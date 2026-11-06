/**
 * Remembers the DSH desktop window geometry and restores it on the next launch.
 *
 * The desktop shell (`@deepseek-ai/dsh-desktop`) creates its main window at a
 * hard-coded 1280x820 and never writes the user's size back, and the Cordis Host
 * this bundle loads into is a child process of that shell: it has no handle on
 * Electron's `BrowserWindow` and no IPC channel that could ask the shell to
 * resize it.
 *
 * So the Host half starts one small native helper that stays with the window for
 * the whole run. The helper is compiled once from the C# source shipped in this
 * bundle (`helper/window-state.cs`) with the .NET Framework compiler that ships
 * with Windows, then cached next to the state file:
 *
 * 1. it fast-polls until the main window exists and applies the geometry recorded
 *    by the previous run, before the shell shows the window;
 * 2. it installs WinEvent hooks scoped to the DSH process, so a move, resize,
 *    maximize, show or hide writes the state file immediately instead of waiting
 *    for a timer;
 * 3. a slow heartbeat (2 s by default) covers events a hook did not deliver and
 *    notices the window going away.
 *
 * A PowerShell implementation (`window-state.ps1`) remains as the fallback for
 * machines without `csc.exe`; it polls instead of using hooks but writes the same
 * state file.
 *
 * Both helpers only run inside the packaged Windows desktop application; other
 * platforms and plain CLI hosts skip activation without spawning anything.
 *
 * @module dsh-window-state
 */
import { spawn, spawnSync } from 'node:child_process'
import { existsSync, statSync } from 'node:fs'
import { homedir } from 'node:os'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

/** Directory holding this bundle's runtime files. */
const moduleDirectory = dirname(fileURLToPath(import.meta.url))

/** C# source compiled on first use. */
const helperSource = join(moduleDirectory, 'helper', 'window-state.cs')

/** PowerShell fallback used when the compiler is unavailable. */
const legacyScript = join(moduleDirectory, 'window-state.ps1')

/** Cached compiled helper name inside the harness home. */
const helperExeName = 'window-state-helper.exe'

/** File inside the harness home that stores the last window rectangle. */
const stateFileName = 'desktop-window-state.json'

/** Default heartbeat interval in milliseconds. */
const defaultIntervalMs = 2000

/** Shortest interval accepted from configuration (and by the helper). */
const minimumIntervalMs = 500

/** Process image whose window is managed. */
const defaultExeName = 'DeepSeek Harness.exe'

/** Title substring used to score windows of that process. */
const defaultTitleHint = 'DeepSeek Harness'

/**
 * Resolve the harness home the same way the runtime does: `$DSH_HOME` when it is
 * a non-blank value, `~/.dsh` otherwise.
 * @returns the absolute harness home directory.
 */
function resolveHarnessHome() {
  const configured = process.env.DSH_HOME
  if (typeof configured === 'string' && configured.trim() !== '') return configured.trim()
  return join(homedir(), '.dsh')
}

/**
 * Locate Windows PowerShell 5.1 so the fallback does not depend on `PATH`.
 * @returns the absolute executable path, or the bare command name as a fallback.
 */
function resolvePowerShell() {
  const systemRoot = process.env.SystemRoot ?? 'C:\\Windows'
  const candidate = join(systemRoot, 'System32', 'WindowsPowerShell', 'v1.0', 'powershell.exe')
  return existsSync(candidate) ? candidate : 'powershell.exe'
}

/**
 * Locate the .NET Framework C# compiler shipped with Windows.
 * @returns the absolute `csc.exe` path, or `undefined` when absent.
 */
function resolveCompiler() {
  const windir = process.env.WINDIR ?? process.env.SystemRoot ?? 'C:\\Windows'
  const candidates = [
    join(windir, 'Microsoft.NET', 'Framework64', 'v4.0.30319', 'csc.exe'),
    join(windir, 'Microsoft.NET', 'Framework', 'v4.0.30319', 'csc.exe'),
  ]
  return candidates.find((candidate) => existsSync(candidate))
}

/**
 * Read a positive integer option.
 * @param value - untrusted configuration value.
 * @param fallback - value used when the option is absent or invalid.
 * @param minimum - smallest accepted value.
 * @returns the accepted value or the fallback.
 */
function readCount(value, fallback, minimum) {
  return Number.isSafeInteger(value) && value >= minimum ? value : fallback
}

/**
 * Read an optional non-blank string option.
 * @param value - untrusted configuration value.
 * @returns the trimmed string, or `undefined` when it is absent or blank.
 */
function readText(value) {
  return typeof value === 'string' && value.trim() !== '' ? value.trim() : undefined
}

/**
 * Ensure the cached helper executable matches the shipped source, compiling it
 * with `csc.exe` when it is missing or stale.
 * @param exePath - cache location inside the harness home.
 * @param say - best-effort logger.
 * @returns whether a usable executable exists afterwards.
 */
function ensureHelperExecutable(exePath, say) {
  let sourceMtime = 0
  try {
    sourceMtime = statSync(helperSource).mtimeMs
  } catch {
    say('warn', 'window state: helper/window-state.cs is missing from the bundle')
    return false
  }

  try {
    if (existsSync(exePath) && statSync(exePath).mtimeMs >= sourceMtime) return true
  } catch {
    // Fall through to compilation.
  }

  const compiler = resolveCompiler()
  if (compiler === undefined) {
    say('info', 'window state: no csc.exe found, falling back to PowerShell')
    return false
  }

  const result = spawnSync(
    compiler,
    ['/nologo', '/target:exe', '/optimize+', '/platform:anycpu', `/out:${exePath}`, helperSource],
    { windowsHide: true, encoding: 'utf8', timeout: 120_000 },
  )

  if (result.error !== undefined || result.status !== 0) {
    const detail = String(result.stderr ?? result.error?.message ?? '').trim().slice(-500)
    say('warn', `window state: helper compilation failed, falling back to PowerShell: ${detail}`)
    return false
  }

  say('info', `window state: helper compiled at ${exePath}`)
  return existsSync(exePath)
}

/**
 * Activate the window-state helper inside the DSH desktop Host process.
 * @param ctx - the plugin's Cordis context, used to register and dispose the helper.
 * @param config - optional row configuration (`enabled`, `stateFile`, `intervalMs`,
 *   `exeName`, `titleHint`).
 */
export function apply(ctx, config = {}) {
  const logger = ctx.logger
  const say = (level, message) => {
    try {
      logger?.[level]?.(message)
    } catch {
      // Logging is best effort; it must never break activation.
    }
  }

  if (config.enabled === false) {
    say('info', 'window state: disabled by configuration')
    return
  }

  if (process.platform !== 'win32') {
    say('info', 'window state: inactive outside Windows')
    return
  }

  // The desktop shell starts the Host with ELECTRON_RUN_AS_NODE; a terminal-launched
  // `dsh` does not, and it owns no window this plugin should touch.
  if (process.env.ELECTRON_RUN_AS_NODE !== '1') {
    say('info', 'window state: inactive outside the DSH desktop application')
    return
  }

  const harnessHome = resolveHarnessHome()
  const stateFile = readText(config.stateFile) ?? join(harnessHome, stateFileName)
  const intervalMs = readCount(config.intervalMs, defaultIntervalMs, minimumIntervalMs)
  const exeName = readText(config.exeName) ?? defaultExeName
  const titleHint = readText(config.titleHint) ?? defaultTitleHint

  const helperExe = join(harnessHome, helperExeName)
  const compiled = ensureHelperExecutable(helperExe, say)

  ctx.effect(() => {
    const child = compiled
      ? spawn(
          helperExe,
          [
            '--state',
            stateFile,
            '--interval',
            String(intervalMs),
            '--exe',
            exeName,
            '--title',
            titleHint,
          ],
          { cwd: moduleDirectory, windowsHide: true, stdio: ['ignore', 'ignore', 'pipe'] },
        )
      : spawn(
          resolvePowerShell(),
          [
            '-NoLogo',
            '-NoProfile',
            '-NonInteractive',
            '-ExecutionPolicy',
            'Bypass',
            '-WindowStyle',
            'Hidden',
            '-File',
            legacyScript,
            '-StateFile',
            stateFile,
            '-IntervalMs',
            String(intervalMs),
            '-ExeName',
            exeName,
            '-TitleHint',
            titleHint,
          ],
          { cwd: moduleDirectory, windowsHide: true, stdio: ['ignore', 'ignore', 'pipe'] },
        )

    let stderrTail = ''
    let stopping = false
    child.stderr?.setEncoding('utf8')
    child.stderr?.on('data', (chunk) => {
      stderrTail = (stderrTail + String(chunk)).slice(-2000)
    })

    child.on('error', (error) => {
      say('warn', `window state: helper failed to start: ${error.message}`)
    })

    child.on('exit', (code, signal) => {
      if (stopping || code === 0) return
      const detail = stderrTail.trim() === '' ? 'no diagnostic output' : stderrTail.trim()
      say(
        'warn',
        `window state: helper exited (code ${String(code)}, signal ${String(signal)}): ${detail}`,
      )
    })

    say(
      'info',
      `window state: active (${compiled ? 'compiled helper' : 'PowerShell fallback'}, state: ${stateFile})`,
    )

    return () => {
      stopping = true
      try {
        child.kill()
      } catch {
        // The helper may already have exited with the window.
      }
    }
  }, 'dsh-window-state: window watcher')
}
