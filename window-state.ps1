# PowerShell fallback for dsh-window-state.
#
# The plugin prefers the compiled helper in helper/window-state.cs (about 15 MB,
# WinEvent hooks plus a heartbeat). This script is started only when the machine
# has no .NET Framework compiler to build it; it polls instead of hooking but
# writes the same state file with the same semantics.
param(
  [Parameter(Mandatory = $true)][string]$StateFile,
  [int]$IntervalMs = 700,
  [string]$ExeName = 'DeepSeek Harness.exe',
  [string]$TitleHint = 'DeepSeek Harness'
)

$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class DshWindowNative {
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int command);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hWnd);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder path, ref uint size);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    public static string ImagePath(uint processId) {
        IntPtr handle = OpenProcess(0x1000, false, processId);
        if (handle == IntPtr.Zero) return null;
        try {
            StringBuilder buffer = new StringBuilder(1024);
            uint size = (uint)buffer.Capacity;
            if (!QueryFullProcessImageNameW(handle, 0, buffer, ref size)) return null;
            return buffer.ToString(0, (int)size);
        } finally { CloseHandle(handle); }
    }
}
'@

$SM_XVIRTUALSCREEN = 76
$SM_YVIRTUALSCREEN = 77
$SM_CXVIRTUALSCREEN = 78
$SM_CYVIRTUALSCREEN = 79
$SW_MAXIMIZE = 3
$SWP_NOZORDER = 0x0004
$SWP_NOACTIVATE = 0x0010
$SWP_NOOWNERZORDER = 0x0200

$script:imagePathCache = @{}
$script:lastSignature = ''

function Get-WindowImagePath([uint32]$processId) {
  if ($script:imagePathCache.ContainsKey($processId)) { return $script:imagePathCache[$processId] }
  $path = [DshWindowNative]::ImagePath($processId)
  $script:imagePathCache[$processId] = $path
  return $path
}

function Find-MainWindow {
  param([switch]$RequireLarge)

  $script:bestHandle = [IntPtr]::Zero
  $script:bestScore = -1
  $script:imagePathCache = @{}

  $callback = {
    param([IntPtr]$hWnd, [IntPtr]$lParam)

    $classBuffer = New-Object System.Text.StringBuilder 256
    [void][DshWindowNative]::GetClassNameW($hWnd, $classBuffer, 256)
    if ($classBuffer.ToString() -ne 'Chrome_WidgetWin_1') { return $true }

    [uint32]$processId = 0
    [void][DshWindowNative]::GetWindowThreadProcessId($hWnd, [ref]$processId)
    if ($processId -eq 0) { return $true }

    $imagePath = Get-WindowImagePath -processId $processId
    if ([string]::IsNullOrEmpty($imagePath)) { return $true }
    if ([System.IO.Path]::GetFileName($imagePath) -ne $ExeName) { return $true }

    $titleBuffer = New-Object System.Text.StringBuilder 512
    [void][DshWindowNative]::GetWindowTextW($hWnd, $titleBuffer, 512)
    $title = $titleBuffer.ToString()

    $rect = New-Object DshWindowNative+RECT
    if (-not [DshWindowNative]::GetWindowRect($hWnd, [ref]$rect)) { return $true }
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    $area = [int64]$width * [int64]$height
    if ($area -le 0) { return $true }

    # Before the saved bounds are applied, the main window is still at the shell's
    # creation size, so ignore transient narrow windows such as the welcome or
    # update dialogs that share the application process.
    if ($RequireLarge -and ($width -lt 900 -or $height -lt 500)) { return $true }

    if ($title.IndexOf($TitleHint, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
      $score = [int64]1000000000000 + $area
    } elseif ($area -ge 200000) {
      $score = $area
    } else {
      $score = -1
    }

    if ($score -gt $script:bestScore) {
      $script:bestScore = $score
      $script:bestHandle = $hWnd
    }
    return $true
  }

  [void][DshWindowNative]::EnumWindows([DshWindowNative+EnumWindowsProc]$callback, [IntPtr]::Zero)
  return $script:bestHandle
}

function Get-Bounds([IntPtr]$handle) {
  $rect = New-Object DshWindowNative+RECT
  if (-not [DshWindowNative]::GetWindowRect($handle, [ref]$rect)) { return $null }
  return [pscustomobject]@{
    x      = [int]$rect.Left
    y      = [int]$rect.Top
    width  = [int]($rect.Right - $rect.Left)
    height = [int]($rect.Bottom - $rect.Top)
  }
}

function Get-WindowDpi([IntPtr]$handle) {
  try {
    $dpi = [DshWindowNative]::GetDpiForWindow($handle)
    if ($dpi -ge 96) { return [int]$dpi }
  } catch { }
  return 96
}

function Restore-Bounds([IntPtr]$handle, $state) {
  $x = [int]$state.rect.x
  $y = [int]$state.rect.y
  $width = [int]$state.rect.width
  $height = [int]$state.rect.height

  if ($width -lt 520) { $width = 520 }
  if ($height -lt 600) { $height = 600 }

  $virtualX = [DshWindowNative]::GetSystemMetrics($SM_XVIRTUALSCREEN)
  $virtualY = [DshWindowNative]::GetSystemMetrics($SM_YVIRTUALSCREEN)
  $virtualWidth = [DshWindowNative]::GetSystemMetrics($SM_CXVIRTUALSCREEN)
  $virtualHeight = [DshWindowNative]::GetSystemMetrics($SM_CYVIRTUALSCREEN)

  if ($virtualWidth -gt 0 -and $width -gt $virtualWidth) { $width = $virtualWidth }
  if ($virtualHeight -gt 0 -and $height -gt $virtualHeight) { $height = $virtualHeight }

  if ($virtualWidth -gt 0 -and $virtualHeight -gt 0) {
    if ($x -lt $virtualX) { $x = $virtualX }
    if ($y -lt $virtualY) { $y = $virtualY }
    if ($x + $width -gt $virtualX + $virtualWidth) { $x = $virtualX + $virtualWidth - $width }
    if ($y + $height -gt $virtualY + $virtualHeight) { $y = $virtualY + $virtualHeight - $height }
  }

  $flags = [uint32]($SWP_NOZORDER -bor $SWP_NOACTIVATE -bor $SWP_NOOWNERZORDER)
  [void][DshWindowNative]::SetWindowPos($handle, [IntPtr]::Zero, $x, $y, $width, $height, $flags)
}

function Write-State($state) {
  $directory = [System.IO.Path]::GetDirectoryName($StateFile)
  if (-not [string]::IsNullOrEmpty($directory) -and -not (Test-Path -LiteralPath $directory)) {
    [void][System.IO.Directory]::CreateDirectory($directory)
  }
  $state.savedAt = (Get-Date).ToUniversalTime().ToString('o')
  $json = $state | ConvertTo-Json -Depth 6 -Compress
  $temporary = "$StateFile.tmp"
  [System.IO.File]::WriteAllText($temporary, $json, (New-Object System.Text.UTF8Encoding($false)))
  [System.IO.File]::Copy($temporary, $StateFile, $true)
  Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue
}

function Save-Bounds([IntPtr]$handle, $state) {
  $bounds = Get-Bounds -handle $handle
  if ($null -eq $bounds) { return }
  if ($bounds.width -lt 200 -or $bounds.height -lt 200) { return }

  $zoomed = [DshWindowNative]::IsZoomed($handle)
  if (-not $zoomed) {
    $state.rect = $bounds
    $state.dpi = Get-WindowDpi -handle $handle
  }
  $state.maximized = [bool]$zoomed

  $signature = "$($state.rect.x),$($state.rect.y),$($state.rect.width),$($state.rect.height),$($state.maximized),$($state.dpi)"
  if ($signature -eq $script:lastSignature) { return }
  $script:lastSignature = $signature
  Write-State $state
}

$state = $null
if (Test-Path -LiteralPath $StateFile) {
  try {
    $state = Get-Content -LiteralPath $StateFile -Raw | ConvertFrom-Json
  } catch {
    $state = $null
  }
}
if ($null -eq $state -or $null -eq $state.rect) {
  $state = [pscustomobject]@{ rect = $null; maximized = $false; dpi = 96; savedAt = $null }
}

$applied = $false
$maximizePending = $false
$missingTicks = 0
$targetHandle = [IntPtr]::Zero

while ($true) {
  if ($targetHandle -eq [IntPtr]::Zero -or -not [DshWindowNative]::IsWindow($targetHandle)) {
    $targetHandle = Find-MainWindow -RequireLarge:(-not $applied)
  }

  $handle = $targetHandle
  if ($handle -eq [IntPtr]::Zero) {
    $missingTicks = $missingTicks + 1
    if ($missingTicks -ge 120) { break }
    Start-Sleep -Milliseconds $IntervalMs
    continue
  }

  $missingTicks = 0

  if (-not $applied) {
    $applied = $true
    if ($null -ne $state.rect) {
      Restore-Bounds -handle $handle -state $state
      if ([bool]$state.maximized) { $maximizePending = $true }
    }
    Start-Sleep -Milliseconds 250
    continue
  }

  if ($maximizePending -and [DshWindowNative]::IsWindowVisible($handle)) {
    if (-not [DshWindowNative]::IsZoomed($handle)) {
      [void][DshWindowNative]::ShowWindow($handle, $SW_MAXIMIZE)
    }
    $maximizePending = $false
  }

  if (-not [DshWindowNative]::IsIconic($handle)) {
    Save-Bounds -handle $handle -state $state
  }

  Start-Sleep -Milliseconds $IntervalMs
}
