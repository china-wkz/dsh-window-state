# dsh-window-state

**English** | [简体中文](README.md)

> Remembers the DeepSeek Harness desktop window position, size, and maximized state — and restores all of it on the next launch.

![platform](https://img.shields.io/badge/platform-Windows%20x64-0078D4)
![host](https://img.shields.io/badge/DSH%20Desktop-%E2%89%A50.2.2--rc.1-4B6BFB)
[![license](https://img.shields.io/badge/license-MIT-green)](LICENSE)

---

## The problem it solves

The DSH desktop shell hard-codes its main window size:

```js
// dsh-desktop / lib/main.js
new BrowserWindow({ width: 1280, height: 820, minWidth: 520, minHeight: 600, ... })
```

There is no code anywhere that saves the window geometry, so every restart snaps back to 1280×820 and the position you dragged it to is gone. Window persistence belongs in DSH itself, but it is not there yet — this plugin fills that gap.

After installing it: **the window comes back the way you left it.**

## Features

| Capability | Detail |
| --- | --- |
| Position + size + maximized | All three are remembered, and the restored rectangle is clamped to the current display setup |
| Restored before it shows | The helper applies the saved geometry around the moment the window is created, so you normally never see a small window grow |
| Continuous recording, not on exit | Sampled every 700 ms; a resize is on disk immediately, so a hard kill or power loss does not lose it |
| Off-screen protection | Unplugging a monitor or changing resolution never strands the window outside the visible area |
| Readable state file | Plain JSON — inspect it, edit it, or delete it (deleting = reset) |
| No system residue | No registry keys, no autostart entries, no service. One hidden helper process exists only while DSH runs |
| Deliberately narrow | Only touches the `DeepSeek Harness.exe` window; CLI, headless and non-Windows hosts skip silently |

## Installation

> Requires **DSH Desktop for Windows** (0.2.2-rc.1 or newer). A browser tab has no real window, and the plugin does not start there.

### Option 1 — from the Plugins page (recommended)

1. Open **Settings → Plugins**.
2. Click **Add plugin** → **Install third-party plugin**.
3. Enter `dsh-window-state` as the package name.
   - If the package is not on npm yet, use `github:china-wkz/dsh-window-state` instead to install straight from the repository.
4. **Restart DSH** when the install finishes.

### Option 2 — a local checkout (development)

Clone the repository, then paste the **absolute path** into the same field:

```text
D:/code/dsh-window-state
```

Restart DSH after each change; there is no build step.

### Option 3 — edit the profile by hand

For pinned versions or offline setups, declare the dependency and the bundle in the profile's `package.json`:

```jsonc
// <profile>/package.json
{
  "dependencies": {
    "dsh-window-state": "^0.1.0"
  },
  "dsh": {
    "profile": {
      "bundles": [
        // ...
        "dsh-window-state"
      ]
    }
  }
}
```

> The package name must appear in `bundles`. A dependency alone installs the package but never loads it.

Every route needs **one restart of DSH**: the profile's bundle list is composed at startup, and only then does the plugin row mount.

## Verifying it works

1. **Settings → Plugins** shows the **Window state memory** card with its switch on.
2. Move and resize the window, then **quit DSH completely** (quit from the tray, not just close the window).
3. Reopen it — the window should come back at the position and size you left.
4. You can also watch the timestamp in the state file move:

```powershell
Get-Content "$env:USERPROFILE\.dsh\desktop-window-state.json"
```

Is the helper running?

```powershell
Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" |
  Where-Object { $_.CommandLine -like '*window-state.ps1*' } |
  Select-Object ProcessId, CommandLine
```

## The state file

Default path: `%USERPROFILE%\.dsh\desktop-window-state.json` (honours `$DSH_HOME`)

```json
{
  "rect": { "x": 241, "y": 89, "width": 2016, "height": 1248 },
  "maximized": false,
  "dpi": 96,
  "savedAt": "2026-11-07T00:32:41.1234567Z"
}
```

| Field | Meaning |
| --- | --- |
| `rect` | The window rectangle in physical pixels, including Windows' invisible frame borders. It uses the same coordinates as `SetWindowPos`, so repeated save/restore cycles never drift |
| `maximized` | Whether the window was maximized when you left. When `true`, `rect` holds the size **before** maximizing |
| `dpi` | Window DPI at save time, kept for diagnosing scaling issues |
| `savedAt` | Last write time (UTC) |

**Reset:** delete the file. The next launch restores nothing and starts recording the current size.

**Moving to another machine or resolution:** nothing to edit. The rectangle is clamped to the current virtual screen, and anything smaller than 520×600 is raised to DSH's own minimum.

## Configuration

Every field in the plugin row's `config` is optional:

| Field | Default | Meaning |
| --- | --- | --- |
| `enabled` | `true` | `false` skips the helper entirely |
| `stateFile` | `$DSH_HOME/desktop-window-state.json` | Custom state file location |
| `intervalMs` | `700` | Sampling interval; minimum `200` |
| `exeName` | `DeepSeek Harness.exe` | Target process image name, should the desktop app be renamed |
| `titleHint` | `DeepSeek Harness` | Title substring used to score windows of that process |

Override a field in the profile's `cordis.patch.yml`:

```yaml
- id: window-state
  name: dsh-window-state
  config:
    intervalMs: 400
```

## How it works

### Why there is a small PowerShell helper

The plugin runs inside `dsh-desktop-host`, the Node process the desktop shell spawns:

- it has no handle on Electron's `BrowserWindow`;
- the shell exposes no IPC that would resize one;
- the `dshDesktop` API published by the preload script has nothing about window geometry either.

So the Host half only does three things: decide whether this is the desktop app, spawn the helper, and reap it on unload. The actual window work happens through Win32, independent of the DSH version.

### The startup sequence

```text
Electron main ──spawn──▶ dsh-desktop-host (the plugin loads here)
                              │
                              ├─ apply() starts the hidden window-state.ps1
                              │
Electron creates the main window (hidden, 1280×820)
                              │
helper sees the window ── SetWindowPos ──▶ applies the saved geometry (+ maximize if needed)
                              │
shell calls show(); what you see is the size you left
                              │
sampled every 700 ms: position/size/maximized change → atomic write of the state file
                              │
window gone (quit) or plugin unloaded ──▶ helper exits
```

### How the window is identified

Only top-level windows that satisfy all of these are considered:

- window class is Chromium's `Chrome_WidgetWin_1`;
- owning process image is `DeepSeek Harness.exe`;
- before the restore, at least 900×500 (which skips same-process windows such as the welcome or update dialogs), preferring a title containing `DeepSeek Harness`;
- once matched, **the handle is pinned**, so a later dialog — however large — cannot take over.

### What "maximized" means

Maximizing is treated as a temporary arrangement, not a chosen size:

- while maximized, only the `maximized` flag is updated; `rect` keeps the pre-maximize value;
- the next launch restores `rect` first, then maximizes once the window is actually visible.

### Closing to the tray

The close button only hides the window; the process stays alive. The rectangle is still readable while hidden and sampling continues, so "hide to tray, then quit" remembers the right size too.

## Known limitations

- **Windows x64 + DSH Desktop only.** CLI, headless and other platforms skip activation without starting anything.
- **One restart after installation** before the plugin loads.
- On a very slow machine the helper may become ready after the window is shown, producing one quick resize (no data is lost).
- Only the **main window** is managed. Platform sign-in pages, update dialogs and other auxiliary windows are not recorded.
- Relies on the desktop shell's current process and window class names; if those change, override them with `config.exeName` / `config.titleHint`.
- If DSH ever implements window persistence itself, disable this plugin so the two do not fight over the window.

## Troubleshooting

| Symptom | Check first |
| --- | --- |
| The window is not restored after a restart | Is the plugin switch on? Does `desktop-window-state.json` exist with a plausible `rect`? |
| `savedAt` never changes | The helper is not running: confirm this is the desktop app (not a browser tab) and that security software is not blocking PowerShell |
| The window ends up off-screen | Delete the state file to reset; loading clamps to the virtual screen, so this should not happen normally |
| The plugin is missing from the list | DSH needs a restart; confirm `dsh-window-state` is in the profile's `bundles` |

## How it differs from similar projects

A few implementations of this idea exist, with different approaches:

- some drive `window.moveTo` / `window.resizeTo` straight from the renderer;
- some are a resident daemon with an autostart entry;
- some are manual save/restore.

This project chooses: **scoped to the plugin lifecycle** (present while DSH runs, gone when it quits), **no autostart entry**, **restore before the window is shown**, and **a plain JSON state file**. If you need something that survives client upgrades without changes, a resident daemon may suit you better; if you would rather leave nothing behind on the system, this one is lighter.

## Repository layout

```text
dsh-window-state/
├── package.json         manifest: dsh.bundle.patch, exports, file list
├── cordis.patch.yml     inserts the plugin row (id: window-state)
├── index.js             Host half: environment check, spawns/reaps the helper, reads config
├── window-state.ps1     Win32 window read/write and state persistence (the real logic)
├── locale/
│   ├── zh.json          plugin card copy (Chinese)
│   └── en.json
├── icon.svg             plugin page icon
├── assets/
│   └── icon-180.png     storefront tile
├── README.md            Chinese README
├── README.en.md         this file
└── LICENSE
```

## Development

```bash
# JavaScript syntax
node --check index.js

# PowerShell syntax (parsed, never executed)
powershell -NoProfile -Command "\$t=\$null;\$e=\$null;[void][System.Management.Automation.Language.Parser]::ParseFile((Resolve-Path 'window-state.ps1'),[ref]\$t,[ref]\$e); if(\$e.Count -eq 0){'ok'}else{\$e.Message}"

# JSON manifests
node -e "for (const f of ['package.json','locale/zh.json','locale/en.json']) JSON.parse(require('fs').readFileSync(f,'utf8')); console.log('ok')"
```

For debugging, install the plugin as a **local directory link**; after editing `index.js` / `window-state.ps1`, restart DSH — the helper re-reads the script on every start, so no reinstall is needed.

To check restore behaviour by hand:

```powershell
# write a target geometry, then restart DSH and watch
'{"rect":{"x":320,"y":160,"width":1720,"height":1080},"maximized":false,"dpi":96,"savedAt":null}' |
  Set-Content "$env:USERPROFILE\.dsh\desktop-window-state.json" -Encoding utf8
```

## License

[MIT](LICENSE)
