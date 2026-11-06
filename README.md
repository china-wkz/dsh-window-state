# dsh-window-state

[English](README.en.md) | **简体中文**

> 记住 DSH Desktop 主窗口的位置、尺寸与最大化状态，下次启动原样恢复。

Remember the DeepSeek Harness desktop window geometry (position, size, maximized) and restore it on the next launch.

![platform](https://img.shields.io/badge/platform-Windows%20x64-0078D4)
![host](https://img.shields.io/badge/DSH%20Desktop-%E2%89%A50.2.2--rc.1-4B6BFB)
[![license](https://img.shields.io/badge/license-MIT-green)](LICENSE)

---

## 这个插件解决什么问题

DSH 桌面端创建主窗口时把尺寸写死了：

```js
// dsh-desktop / lib/main.js
new BrowserWindow({ width: 1280, height: 820, minWidth: 520, minHeight: 600, ... })
```

而且没有任何保存窗口几何的代码。结果就是：你每次调好的大小和位置，关掉再开都会缩回 1280×820。DSH 本身是支持窗口持久化的好地方，但目前为止它还没做——这个插件补上这一段。

安装之后：**你离开时窗口是什么样，下次打开就是什么样。**

## 特性

| 能力 | 说明 |
| --- | --- |
| 位置 + 尺寸 + 最大化 | 三样一起记，换显示器也能落在合理位置 |
| 启动即还原 | 辅助进程在窗口显示前后极短时间内套用上次几何，通常看不到“先小后大”的跳变 |
| 连续记录，不是退出时才存 | 移动/缩放/最大化由 WinEvent 钩子即刻写盘，另有 2 秒心跳补漏；断电、强杀都不会丢 |
| 轻量常驻 | 编译型 helper：约 15MB 内存、空载 CPU 为 0（PowerShell 版约 70MB） |
| 越界钳制 | 拔掉副屏、改分辨率、缩放比变化后，窗口不会被放到看不见的地方 |
| 可读的状态文件 | 纯 JSON，随时能看、能改、能删（删掉即重置） |
| 零系统残留 | 不写注册表、不加开机自启、不装服务；只在你打开 DSH 时存在一个隐藏辅助进程 |
| 窄适用范围 | 只认 `DeepSeek Harness.exe` 的窗口；CLI、无头模式、macOS/Linux 静默跳过 |

## 安装

> 运行环境：**DSH Desktop for Windows**（0.2.2-rc.1 及以上）。浏览器标签页里没有真实窗口，插件在那里不启动。

### 方式一：插件页安装（推荐）

1. 打开 **设置 → 插件**；
2. 点 **添加插件** → **安装第三方插件**；
3. 在「包名或地址」里填入 `dsh-window-state`；
   - 还没发布到 npm 时，改用 `github:china-wkz/dsh-window-state`，直接从仓库安装；
4. 安装完成后 **重启 DSH**。

### 方式二：本地源码目录（开发调试）

把仓库克隆到本地，然后在上面的「包名或地址」里直接填**绝对路径**：

```text
D:/code/dsh-window-state
```

改完代码后重启 DSH 即可生效，不需要打包。

### 方式三：手工写 profile

适合固定版本或离线环境。在 profile 的 `package.json` 里声明依赖与 bundle：

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

> `bundles` 里必须写上包名。只加依赖不写 bundle，包会被装进来但不会被加载。

安装完成后**都需要重启一次 DSH**：profile 的 bundle 列表在启动时组装，重启后插件行才会挂上。

## 验证是否生效

1. 打开 **设置 → 插件**，能看到 **窗口状态记忆** 卡片，开关为开；
2. 拖动窗口、改个大小，然后**完整退出** DSH（托盘里退出，不是关窗口）；
3. 重新打开，窗口应回到你离开时的位置和尺寸；
4. 也可以直接看状态文件里的时间戳有没有在变：

```powershell
Get-Content "$env:USERPROFILE\.dsh\desktop-window-state.json"
```

辅助进程是否在跑：

```powershell
Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" |
  Where-Object { $_.CommandLine -like '*window-state.ps1*' } |
  Select-Object ProcessId, CommandLine
```

## 状态文件

默认路径：`%USERPROFILE%\.dsh\desktop-window-state.json`（遵循 `$DSH_HOME`）

```json
{
  "rect": { "x": 241, "y": 89, "width": 2016, "height": 1248 },
  "maximized": false,
  "dpi": 96,
  "savedAt": "2026-11-07T00:32:41.1234567Z"
}
```

| 字段 | 含义 |
| --- | --- |
| `rect` | 窗口矩形，物理像素，含 Windows 不可见的边框（与 `SetWindowPos` 同一坐标系，因此不会逐次漂移） |
| `maximized` | 上次离开时是否最大化；为 `true` 时 `rect` 记录的是**最大化之前**的尺寸 |
| `dpi` | 保存时的窗口 DPI，用于排查缩放问题 |
| `savedAt` | 最后一次写入时间（UTC） |

**重置**：删掉这个文件，下次启动就不会还原，并重新记录当前尺寸。

**换机器/换分辨率**：不需要手工改，加载时会按当前虚拟屏幕钳制；尺寸小于 520×600 时抬到 DSH 的原生下限。

## 配置

插件行的 `config` 全部可选：

| 字段 | 默认 | 说明 |
| --- | --- | --- |
| `enabled` | `true` | 设 `false` 时完全不启动辅助进程 |
| `stateFile` | `$DSH_HOME/desktop-window-state.json` | 自定义状态文件位置 |
| `intervalMs` | `2000` | 心跳间隔；窗口事件本身会立即写盘，这个只做兜底，最小 `500` |
| `exeName` | `DeepSeek Harness.exe` | 目标进程映像名；桌面端改名后可在这里覆盖 |
| `titleHint` | `DeepSeek Harness` | 窗口标题匹配串，用于在同进程的多个窗口间打分 |

在 profile 的 `cordis.patch.yml` 里覆盖某一项：

```yaml
- id: window-state
  name: dsh-window-state
  config:
    intervalMs: 400
```

## 工作原理

### 为什么需要一个小小的编译型 helper

插件运行在 `dsh-desktop-host` 子进程里——它是桌面外壳 spawn 出来的 Node 进程：

- 拿不到 Electron 的 `BrowserWindow`；
- 桌面外壳没有开放任何“设置窗口尺寸”的 IPC；
- 预加载脚本暴露的 `dshDesktop` API 里也没有窗口几何相关的方法。

所以 Host 半边只做三件事：判断是不是桌面环境、准备并拉起 helper、卸载时收掉它。真正的窗口操作用 Win32 完成，与 DSH 版本解耦。

helper 用 C# 写成（`helper/window-state.cs`），首次运行时由 Host 用 Windows 自带的 .NET Framework 编译器 `csc.exe` 编译一次，产物缓存在 `$DSH_HOME/window-state-helper.exe`；源码有更新会自动重编。这样常驻进程只有约 15MB 内存、空载时 CPU 为 0。机器上找不到 `csc.exe` 时，自动退回 `window-state.ps1`（轮询实现，写同一个状态文件，行为一致，内存约 70MB）。

### 一次启动的时序

```text
Electron 主进程 ──spawn──▶ dsh-desktop-host（插件在这里加载）
                              │
                              ├─ apply() 编译（仅首次）并启动 window-state-helper.exe
                              │
Electron 创建主窗口（隐藏，1280×820）
                              │
helper 以 100ms 快轮询到窗口 ── SetWindowPos ──▶ 套用上次几何（+ 需要时最大化）
                              │
外壳 show()，你看到的就是上次的大小
                              │
安装 WinEvent 钩子（只监听 DSH 进程）
   移动 / 缩放 / 最大化 / 显示 / 隐藏 ──▶ 立即写状态文件
   钩子漏掉的 + 每 2 秒心跳 ──▶ 补一次采样
                              │
窗口消失（退出）或插件卸载 ──▶ helper 结束
```

### 窗口识别规则

只处理同时满足以下条件的顶层窗口：

- 窗口类为 Chromium 的 `Chrome_WidgetWin_1`；
- 所属进程映像名为 `DeepSeek Harness.exe`；
- 未还原前要求尺寸不小于 900×500（避开欢迎页、更新弹窗这类同进程小窗口），并优先标题含 `DeepSeek Harness` 的窗口；
- 命中后**锁定该窗口句柄**，后续弹窗再大也不会被误抓。

### 最大化的语义

最大化被视为“临时摆放”，不是用户选定的尺寸：

- 最大化状态下只更新 `maximized` 标记，`rect` 保留最大化之前的值；
- 下次启动先摆回 `rect`，等窗口真正可见后再最大化。

### 隐藏到托盘

点关闭按钮只是隐藏窗口，进程还在。隐藏期间窗口矩形依然可读，采样照常进行，所以“最小化到托盘再退出”也能记住正确尺寸。

## 已知限制

- **仅 Windows x64 + DSH Desktop**。CLI、headless、其他平台会直接跳过，不启动任何进程。
- **安装后需要重启一次**，插件才会被加载。
- helper 首次运行需要用系统自带的 `csc.exe` 编译一次（约 100–300ms，之后走缓存）；系统缺少编译器时回退到 PowerShell 版。
- 极慢的机器上，辅助进程可能晚于窗口显示才就绪，会看到一次快速的尺寸调整（不会丢数据）。
- 只管理**主窗口**。平台登录页、更新弹窗等附属窗口不记录。
- 依赖桌面外壳当前的进程名与窗口类名；如果未来 DSH 改了这两者，用 `config.exeName` / `config.titleHint` 覆盖即可。
- 如果 DSH 官方后续自己实现了窗口持久化，建议停用本插件，避免两边同时改窗口。

## 排错

| 现象 | 先看什么 |
| --- | --- |
| 重启后窗口没还原 | 插件页开关是否是开；`desktop-window-state.json` 是否存在且 `rect` 合理 |
| `savedAt` 一直不变 | helper 没起来：确认是桌面版（不是浏览器标签页）；系统缺 `csc.exe` 时检查 PowerShell 是否被安全软件拦截 |
| 窗口跑到屏幕外 | 删掉状态文件重置；加载时会做虚拟屏幕钳制，正常不会出现 |
| 装完插件列表里没有 | 需要重启 DSH；确认 profile 的 `bundles` 里有 `dsh-window-state` |

## 与同类项目的区别

这个方向已经有一些实现，做法不同：

- 有的在渲染进程里直接调 `window.moveTo` / `window.resizeTo`；
- 有的做成常驻守护进程 + 开机自启；
- 有的是手动保存/恢复。

本项目的取舍是：**插件生命周期内嵌**（DSH 开才在，关就退）、**不写系统自启**、**显示前还原**、**状态是普通 JSON 文件**。如果你更需要“不依赖客户端版本”的方案，常驻守护类实现可能更合适；如果你不想在系统里多留东西，这个更轻。

## 仓库结构

```text
dsh-window-state/
├── package.json         包清单：dsh.bundle.patch、导出表、文件列表
├── cordis.patch.yml     插入插件行（id: window-state）
├── index.js             Host 半边：环境判定、编译并启动 helper、配置读取
├── helper/
│   └── window-state.cs  编译型 helper：WinEvent 钩子 + 心跳 + Win32 窗口读写
├── window-state.ps1     PowerShell 回退实现（系统没有 csc.exe 时使用）
├── locale/
│   ├── zh.json          插件卡片中文文案
│   └── en.json
├── icon.svg             插件页图标
├── assets/
│   └── icon-180.png     插件市场卡片图
├── README.md            中文说明（本文件）
├── README.en.md         English README
└── LICENSE
```

## 开发

```bash
# JavaScript 语法
node --check index.js

# PowerShell 语法（不执行）
powershell -NoProfile -Command "\$t=\$null;\$e=\$null;[void][System.Management.Automation.Language.Parser]::ParseFile((Resolve-Path 'window-state.ps1'),[ref]\$t,[ref]\$e); if(\$e.Count -eq 0){'ok'}else{\$e.Message}"

# JSON 清单
node -e "for (const f of ['package.json','locale/zh.json','locale/en.json']) JSON.parse(require('fs').readFileSync(f,'utf8')); console.log('ok')"
```

调试时把插件装成**本地目录链接**，改完 `index.js` / `window-state.ps1` 后重启 DSH 即可；辅助进程每次启动都会重新读脚本，不需要重新安装。

手工验证某个尺寸能否被还原：

```powershell
# 写入一个目标几何，然后重启 DSH 观察
'{"rect":{"x":320,"y":160,"width":1720,"height":1080},"maximized":false,"dpi":96,"savedAt":null}' |
  Set-Content "$env:USERPROFILE\.dsh\desktop-window-state.json" -Encoding utf8
```

## 许可

[MIT](LICENSE)
