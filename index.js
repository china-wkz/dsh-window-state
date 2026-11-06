/**
 * Remembers the DSH desktop window geometry and restores it on the next launch.
 *
 * The desktop shell (`@deepseek-ai/dsh-desktop`) creates its main window at a
 * hard-coded 1280x820 and never writes the user's size back, and the Cordis Host
 * this bundle loads into is a child process of that shell: it has no handle on
 * Electron's `BrowserWindow` and no IPC channel that could ask the shell to
 * resize it. So the Host half starts one hidden Windows helper
 * (`window-state.ps1`) that stays with the window for the whole run:
 *
 * 1. it waits for the main window (process image `DeepSeek Harness.exe`,
 *    Chromium's `Chrome_WidgetWin_1` class, best-scoring match by size and title),
 *    then holds that handle so later dialogs cannot take it over;
 * 2. it applies the geometry recorded by the previous run once, before the shell
 *    shows the window;
 * 3. afterwards it samples the window rectangle and rewrites the state file
 *    whenever the user moves, resizes, maximizes, or restores the window;
 * 4. it exits when the window disappears, and the Host effect's disposer kills
 *    it when the plugin unloads.
 *
 * Consequently the state is always the latest size, so even an abrupt exit
 * (killed process, power loss) leaves a usable record behind.
 *
 * The helper only runs inside the packaged Windows desktop application; other
 * platforms and plain CLI hosts skip activation without spawning anything.
 *
 * @module dsh-window-state
 */
import { spawn } from 'node:child_process'
import { existsSync } from 'node:fs'
import { homedir } from 'node:os'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

/** Directory holding this bundle's runtime files, including the Windows helper. */
const moduleDirectory = dirname(fileURLToPath(import.meta.url))

/** Hidden Windows helper that owns the Win32 window inspection. */
const helperPath = join(moduleDirectory, 'window-state.ps1')

/** File inside the harness home that stores the last window rectangle. */
const stateFileName = 'desktop-window-state.json'

/** Default sampling interval in milliseconds. */
const defaultIntervalMs = 700

/** Shortest sampling interval accepted from configuration. */
const minimumIntervalMs = 200

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
 * Locate Windows PowerShell 5.1 so the helper does not depend on `PATH`.
 * @returns the absolute executable path, or the bare command name as a fallback.
 */
function resolvePowerShell() {
  const systemRoot = process.env.SystemRoot ?? 'C:\\Windows'
  const candidate = join(systemRoot, 'System32', 'WindowsPowerShell', 'v1.0', 'powershell.exe')
  return existsSync(candidate) ? candidate : 'powershell.exe'
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

  if (!existsSync(helperPath)) {
    say('warn', 'window state: window-state.ps1 is missing from the bundle')
    return
  }

  const stateFile =
    readText(config.stateFile) ?? join(resolveHarnessHome(), stateFileName)
  const intervalMs = readCount(config.intervalMs, defaultIntervalMs, minimumIntervalMs)
  const exeName = readText(config.exeName)
  const titleHint = readText(config.titleHint)

  ctx.effect(() => {
    const child = spawn(
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
        helperPath,
        '-StateFile',
        stateFile,
        '-IntervalMs',
        String(intervalMs),
        ...(exeName === undefined ? [] : ['-ExeName', exeName]),
        ...(titleHint === undefined ? [] : ['-TitleHint', titleHint]),
      ],
      {
        cwd: moduleDirectory,
        windowsHide: true,
        stdio: ['ignore', 'ignore', 'pipe'],
      },
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

    say('info', `window state: active (state: ${stateFile})`)

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
