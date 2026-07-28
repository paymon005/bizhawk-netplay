<#
.SYNOPSIS
    Drive EmuHawk through the netplay tool's Capability Probe unattended, across any number
    of N64 video configurations, and collect the results.

.DESCRIPTION
    BizHawk 2.11 has no command-line or Lua hook for opening an external tool, so this drives
    the real UI. Per run it:

        patch config.ini -> launch EmuHawk with a ROM -> File > Load State > N ->
        Tools > External Tool > BizHawk Netplay -> Diagnostics tab -> Capability Probe ->
        read the Log tab -> kill EmuHawk

    Three things about this UI are worth knowing before changing any of it:

    1. The tool window is invisible to UI Automation's desktop-children enumeration, though
       Win32 EnumWindows sees it and AutomationElement.FromHandle works on it. So windows are
       found via Win32, never via UIA's root.

    2. The tool form is surfaced through the MSAA-to-UIA bridge, which exposes almost nothing:
       buttons and radios arrive as Pane, no control is keyboard-focusable, the tab strip is
       not in the tree at all, and only the *selected* tab page's controls exist. Searching for
       ControlType.Button or ControlType.TabItem finds nothing, and neither InvokePattern nor
       SetFocus is available. What the bridge does report accurately is Name and
       BoundingRectangle -- so this clicks real screen coordinates and reads the log out of an
       element's Name.

    3. Consequently tabs are selected by clicking along the strip until the wanted page shows
       up in the tree. Cheap, and self-correcting across DPI and font differences, which a
       hard-coded offset would not be.

    A savestate is loaded before probing because the frame cost is whatever the game is doing:
    at a title screen N64 measures ~0.7ms a frame against ~1.6-2.1ms in play, which would make
    every number here incomparable with one taken by hand.

    EmuHawk is killed rather than closed, deliberately: a clean exit rewrites config.ini and
    would undo the settings written for the next run.

.PARAMETER Config
    One or more "<plugin>:<width>x<height>" specs, e.g. Rice:320x240. Plugin names match
    BizHawk's PluginType enum (Rice, Glide, GlideMk2, FormerlyJabo, GLideN64, Angrylion).

.EXAMPLE
    .\probe-sweep.ps1 -Config Rice:320x240 -Runs 3

.EXAMPLE
    .\probe-sweep.ps1 -Config Rice:320x240,Rice:1280x960,GLideN64:320x240 -Runs 5 -OutFile sweep.txt
#>
[CmdletBinding()]
param(
    [string]   $BizHawkHome = 'X:\Games\Emulators\BizHawk',
    [string]   $Rom = 'X:\Games\Emulators\BizHawk\zz_ROMS\Multiplayer\Super Smash Bros. (USA).z64',
    # A savestate carries the core configuration it was made under, and the video plugin is a
    # SYNC setting -- so a state saved on Rice is not the state to load while configured for
    # GLideN64. The slot therefore follows the plugin rather than being fixed for the sweep.
    [hashtable] $SlotByPlugin = @{ Rice = 1; GLideN64 = 2 },
    [int]      $StateSlot = 0,
    [string[]] $Config = @('Rice:320x240'),
    [int]      $Runs = 1,
    [string]   $OutFile,
    [int]      $TimeoutSec = 180,
    [switch]   $KeepOpen
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

Add-Type -Namespace ProbeSweep -Name Win -MemberDefinition @'
public delegate bool EnumProc(System.IntPtr h, System.IntPtr p);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, System.IntPtr p);
[DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(System.IntPtr h, out int pid);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(System.IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(System.IntPtr h);
[DllImport("user32.dll")] public static extern bool ShowWindow(System.IntPtr h, int c);
[DllImport("user32.dll")] public static extern bool SetWindowPos(System.IntPtr h, System.IntPtr a, int x, int y, int cx, int cy, uint f);
[DllImport("user32.dll")] public static extern System.IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern bool BringWindowToTop(System.IntPtr h);
[DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(System.IntPtr h, System.IntPtr pid);
[DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, System.IntPtr e);
'@

$AE   = [System.Windows.Automation.AutomationElement]
$Tree = [System.Windows.Automation.TreeScope]
$CT   = [System.Windows.Automation.ControlType]
$True_ = [System.Windows.Automation.Condition]::TrueCondition

# PluginType, read off BizHawk.Emulation.Cores rather than assumed.
$PluginIds = @{
    'Rice' = 0; 'Glide' = 1; 'GlideMk2' = 2; 'FormerlyJabo' = 3; 'GLideN64' = 4; 'Angrylion' = 5
}

# ----------------------------------------------------------------------- window plumbing

function Get-Pattern {
    param($Element, $Pattern)
    $o = $null
    if ($Element -and $Element.TryGetCurrentPattern($Pattern, [ref] $o)) { return $o }
    return $null
}

<#
    Windows Explorer refuses SetForegroundWindow from a process that is not already
    foreground. Briefly sharing input state with the current foreground thread lifts that,
    which is what makes the subsequent synthetic clicks land where they are aimed.
#>
function Set-Foreground {
    param([System.IntPtr] $Handle)
    if ([ProbeSweep.Win]::GetForegroundWindow() -eq $Handle) { return $true }
    $fgThread = [ProbeSweep.Win]::GetWindowThreadProcessId([ProbeSweep.Win]::GetForegroundWindow(), [System.IntPtr]::Zero)
    $me = [ProbeSweep.Win]::GetCurrentThreadId()
    [void][ProbeSweep.Win]::AttachThreadInput($me, $fgThread, $true)
    [void][ProbeSweep.Win]::BringWindowToTop($Handle)
    [void][ProbeSweep.Win]::SetForegroundWindow($Handle)
    [void][ProbeSweep.Win]::AttachThreadInput($me, $fgThread, $false)
    Start-Sleep -Milliseconds 300
    return ([ProbeSweep.Win]::GetForegroundWindow() -eq $Handle)
}

<#
    Put a window where it can be seen and clicked. A window restored onto a monitor that is
    no longer attached is invisible and unhittable, so this drags it to the primary origin
    before maximizing rather than trusting its remembered position.
#>
function Show-WindowMaximized {
    param([System.IntPtr] $Handle)
    [void][ProbeSweep.Win]::ShowWindow($Handle, 9)                                        # SW_RESTORE
    [void][ProbeSweep.Win]::SetWindowPos($Handle, [System.IntPtr]::Zero, 0, 0, 0, 0, 0x0005) # NOSIZE|NOZORDER
    [void][ProbeSweep.Win]::ShowWindow($Handle, 3)                                        # SW_MAXIMIZE
    [void](Set-Foreground -Handle $Handle)
    Start-Sleep -Milliseconds 400
}

$script:ScanPid = 0
$script:ScanWant = ''
$script:ScanHits = @()

function Get-WindowByTitle {
    param([int] $ProcId, [string] $Title)
    $script:ScanPid = $ProcId; $script:ScanWant = $Title; $script:ScanHits = @()
    $cb = [ProbeSweep.Win+EnumProc] {
        param($h, $p)
        $wpid = 0
        [void][ProbeSweep.Win]::GetWindowThreadProcessId($h, [ref] $wpid)
        if ($wpid -eq $script:ScanPid) {
            $sb = New-Object System.Text.StringBuilder 512
            [void][ProbeSweep.Win]::GetWindowTextW($h, $sb, 512)
            if ($sb.ToString() -eq $script:ScanWant) { $script:ScanHits += $h }
        }
        return $true
    }
    [void][ProbeSweep.Win]::EnumWindows($cb, [System.IntPtr]::Zero)
    if ($script:ScanHits.Count -gt 0) { return $script:ScanHits[0] }
    return [System.IntPtr]::Zero
}

function Wait-WindowByTitle {
    param([int] $ProcId, [string] $Title, [int] $Seconds)
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        $h = Get-WindowByTitle -ProcId $ProcId -Title $Title
        if ($h -ne [System.IntPtr]::Zero) { return $h }
        Start-Sleep -Milliseconds 300
    }
    throw "window '$Title' did not appear within ${Seconds}s"
}

function Invoke-Click {
    param([int] $X, [int] $Y)
    [void][ProbeSweep.Win]::SetCursorPos($X, $Y)
    Start-Sleep -Milliseconds 120
    [ProbeSweep.Win]::mouse_event(0x0002, 0, 0, 0, [System.IntPtr]::Zero)  # LEFTDOWN
    [ProbeSweep.Win]::mouse_event(0x0004, 0, 0, 0, [System.IntPtr]::Zero)  # LEFTUP
    Start-Sleep -Milliseconds 250
}

# ------------------------------------------------------------------ EmuHawk's own menus

# The main window, unlike the tool form, is exposed properly: menu items arrive as MenuItem
# with Expand/Invoke, so the menus are driven through UI Automation rather than by clicking.
function Get-ProcessRoots {
    param([int] $ProcId)
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $ProcId)
    return $AE::RootElement.FindAll($Tree::Children, $cond)
}

function Invoke-MenuItem {
    param([int] $ProcId, [string] $Name, [bool] $Expand, [int] $Seconds = 20)
    $cond = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $Name)),
        (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::MenuItem)))
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        try {
            foreach ($r in (Get-ProcessRoots -ProcId $ProcId)) {
                $hit = $r.FindFirst($Tree::Descendants, $cond)
                if (-not $hit) { continue }
                if ($Expand) {
                    $ec = Get-Pattern $hit ([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
                    if ($ec) { $ec.Expand(); Start-Sleep -Milliseconds 500; return }
                }
                $inv = Get-Pattern $hit ([System.Windows.Automation.InvokePattern]::Pattern)
                if ($inv) { $inv.Invoke(); Start-Sleep -Milliseconds 300; return }
            }
        } catch { }
        Start-Sleep -Milliseconds 250
    }
    throw "menu item '$Name' not found"
}

# ------------------------------------------------------------------------ the tool form

function Get-Descendants {
    param($Window)
    $out = @()
    foreach ($e in $Window.FindAll($Tree::Descendants, $True_)) { $out += $e }
    return $out
}

function Find-ByName {
    param($Window, [string] $Name, [switch] $OnScreenOnly)
    foreach ($e in (Get-Descendants -Window $Window)) {
        if ($e.Current.Name -ne $Name) { continue }
        if ($OnScreenOnly -and $e.Current.IsOffscreen) { continue }
        return $e
    }
    return $null
}

<#
    Click along the tab strip until the wanted page appears in the tree. The strip is the band
    between the tab control's top and the selected page's top, so its position is measured
    rather than assumed -- the form is DPI-scaled and the tab widths follow the font.
#>
function Select-ToolTab {
    param($Window, [string] $Name)
    if (Find-ByName -Window $Window -Name $Name -OnScreenOnly) { return }

    $kids = Get-Descendants -Window $Window
    if ($kids.Count -eq 0) { throw 'the tool window exposes no children' }
    $tabCtl = $kids[0]
    $page = $null
    foreach ($n in @('Connection', 'Players', 'Diagnostics', 'Log')) {
        $candidate = Find-ByName -Window $Window -Name $n -OnScreenOnly
        if ($candidate) { $page = $candidate; break }
    }
    if (-not $page) { throw 'no tab page is visible; cannot locate the tab strip' }

    $tr = $tabCtl.Current.BoundingRectangle
    $pr = $page.Current.BoundingRectangle
    $stripY = [int](($tr.Y + $pr.Y) / 2)
    $left = [int] $tr.X

    for ($x = $left + 10; $x -lt $left + 1200; $x += 30) {
        Invoke-Click -X $x -Y $stripY
        if (Find-ByName -Window $Window -Name $Name -OnScreenOnly) {
            Write-Verbose "selected tab '$Name' at x=$x"
            return
        }
    }
    throw "could not select the '$Name' tab"
}

<#
    The log's text arrives as the Name of its element -- the bridge offers no TextPattern or
    ValuePattern here. It is the only multi-line name in the window, which is what identifies it.
#>
function Get-LogText {
    param($Window)
    $best = ''
    foreach ($e in (Get-Descendants -Window $Window)) {
        $n = $e.Current.Name
        if (-not $n -or $n -notmatch "`n") { continue }
        if ($n.Length -gt $best.Length) { $best = $n }
    }
    return $best
}

# ---------------------------------------------------------------------- config.ini edits

function Set-N64Config {
    param([string] $Path, [int] $PluginId, [int] $Width, [int] $Height, [string] $ToolDll)

    $c = Get-Content $Path -Raw

    # Each key appears exactly once (in the N64 core's settings and sync-settings blocks), so a
    # targeted replace beats round-tripping the whole JSON, which reorders and reformats it.
    foreach ($pair in @(@('VideoSizeX', $Width), @('VideoSizeY', $Height), @('VideoPlugin', $PluginId))) {
        $key = $pair[0]; $val = $pair[1]
        $rx = '("' + $key + '":\s*)-?\d+'
        # Assert the key was found, not that the text changed: a run repeating the previous
        # run's setting is the normal case and rewrites the file to itself.
        if (-not [regex]::IsMatch($c, $rx)) { throw "could not find $key in $Path" }
        $c = [regex]::Replace($c, $rx, ('${1}' + $val))
    }

    # BizHawk trusts an external tool by SHA512 of the DLL. Rebuilding changes the hash, so
    # without this every launch after a build stops on a trust prompt.
    $hash = (Get-FileHash -Algorithm SHA512 -Path $ToolDll).Hash
    $jsonPath = $ToolDll -replace '\\', '\\'
    $pattern = '("' + [regex]::Escape($jsonPath) + '":\s*")[^"]*(")'
    if ([regex]::IsMatch($c, $pattern)) {
        $c = [regex]::Replace($c, $pattern, ('${1}SHA512:' + $hash + '${2}'))
    }
    else {
        $entry = '"TrustedExtTools": {' + "`r`n    `"$jsonPath`": `"SHA512:$hash`","
        $c = $c -replace '"TrustedExtTools":\s*\{', $entry
    }

    Set-Content -Path $Path -Value $c -Encoding UTF8 -NoNewline
}

# ------------------------------------------------------------------------------- one run

function Invoke-OneProbe {
    param([string] $Exe, [string] $RomPath, [int] $Slot, [int] $Seconds)

    $proc = Start-Process -FilePath $Exe -ArgumentList "`"$RomPath`"" -PassThru
    try {
        $deadline = (Get-Date).AddSeconds($Seconds)
        while ((Get-Date) -lt $deadline) {
            $proc.Refresh()
            if ($proc.HasExited) { throw "EmuHawk exited during startup (code $($proc.ExitCode))" }
            if ($proc.MainWindowHandle -ne [System.IntPtr]::Zero) { break }
            Start-Sleep -Milliseconds 250
        }
        if ($proc.MainWindowHandle -eq [System.IntPtr]::Zero) { throw 'EmuHawk main window never appeared' }
        Show-WindowMaximized -Handle $proc.MainWindowHandle
        Start-Sleep -Seconds 4   # let the core finish loading the ROM

        if ($Slot -gt 0) {
            Invoke-MenuItem -ProcId $proc.Id -Name 'File'       -Expand $true
            Invoke-MenuItem -ProcId $proc.Id -Name 'Load State' -Expand $true
            Invoke-MenuItem -ProcId $proc.Id -Name "$Slot"      -Expand $false
            Start-Sleep -Seconds 2
        }

        Invoke-MenuItem -ProcId $proc.Id -Name 'Tools'           -Expand $true
        Invoke-MenuItem -ProcId $proc.Id -Name 'External Tool'   -Expand $true
        Invoke-MenuItem -ProcId $proc.Id -Name 'BizHawk Netplay' -Expand $false

        $toolHandle = Wait-WindowByTitle -ProcId $proc.Id -Title 'BizHawk Netplay' -Seconds 30
        Show-WindowMaximized -Handle $toolHandle
        $tool = $AE::FromHandle($toolHandle)

        Select-ToolTab -Window $tool -Name 'Diagnostics'
        $button = Find-ByName -Window $tool -Name 'Capability Probe' -OnScreenOnly
        if (-not $button) { throw 'the Capability Probe button is not on the Diagnostics tab' }
        $r = $button.Current.BoundingRectangle
        Invoke-Click -X ([int]($r.X + $r.Width / 2)) -Y ([int]($r.Y + $r.Height / 2))

        Select-ToolTab -Window $tool -Name 'Log'

        # The probe runs synchronously on the UI thread and freezes it for seconds at a time,
        # so poll for its own end marker (and tolerate reads failing meanwhile).
        $deadline = (Get-Date).AddSeconds($Seconds)
        $text = ''
        while ((Get-Date) -lt $deadline) {
            try { $text = Get-LogText -Window $tool } catch { }
            if ($text -match '=== done ===') { break }
            Start-Sleep -Milliseconds 500
        }
        if ($text -notmatch '=== done ===') { throw "the probe did not finish within ${Seconds}s" }

        $lines = $text -split "`r?`n"
        $start = -1; $end = -1
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -match '=== capability probe ===') { $start = $i }
            if ($lines[$i] -match '=== done ===') { $end = $i }
        }
        if ($start -lt 0 -or $end -le $start) { return $text }
        return ($lines[$start..$end] -join "`r`n")
    }
    finally {
        if (-not $KeepOpen) {
            try { Stop-Process -Id $proc.Id -Force -ErrorAction Stop } catch { }
            Start-Sleep -Milliseconds 800
        }
    }
}

# ---------------------------------------------------------------------------------- main

$exe     = Join-Path $BizHawkHome 'EmuHawk.exe'
$cfg     = Join-Path $BizHawkHome 'config.ini'
$toolDll = Join-Path $BizHawkHome 'ExternalTools\BizHawkNetplay.Tool.dll'

foreach ($p in @($exe, $cfg, $toolDll, $Rom)) {
    if (-not (Test-Path $p)) { throw "not found: $p" }
}
if (Get-Process EmuHawk -ErrorAction SilentlyContinue) {
    throw 'EmuHawk is already running. Close it first -- this script owns the process it launches.'
}

$backup = "$cfg.probe-sweep-backup"
if (-not (Test-Path $backup)) {
    Copy-Item $cfg $backup
    Write-Host "config.ini backed up to $backup" -ForegroundColor DarkGray
}

function Resolve-Slot {
    param([string] $Plugin)
    if ($StateSlot -gt 0) { return $StateSlot }
    if ($SlotByPlugin -and $SlotByPlugin.ContainsKey($Plugin)) { return [int] $SlotByPlugin[$Plugin] }
    return 0
}

Write-Host "ROM:  $Rom" -ForegroundColor DarkGray
Write-Host ("{0} config(s) x {1} run(s)" -f $Config.Count, $Runs) -ForegroundColor DarkGray
Write-Host ''

$results = @()
$transcript = New-Object System.Text.StringBuilder

foreach ($spec in $Config) {
    if ($spec -notmatch '^([A-Za-z0-9]+):(\d+)x(\d+)$') { throw "bad -Config spec '$spec' (want e.g. Rice:320x240)" }
    $plugin = $Matches[1]; $w = [int] $Matches[2]; $h = [int] $Matches[3]
    if (-not $PluginIds.ContainsKey($plugin)) { throw "unknown plugin '$plugin'; try $($PluginIds.Keys -join ', ')" }

    $slot = Resolve-Slot -Plugin $plugin
    for ($run = 1; $run -le $Runs; $run++) {
        Write-Host ("[{0} {1}x{2} slot {3}] run {4}/{5} ... " -f $plugin, $w, $h, $slot, $run, $Runs) -NoNewline
        Set-N64Config -Path $cfg -PluginId $PluginIds[$plugin] -Width $w -Height $h -ToolDll $toolDll

        try {
            $out = Invoke-OneProbe -Exe $exe -RomPath $Rom -Slot $slot -Seconds $TimeoutSec
            Write-Host 'ok' -ForegroundColor Green
        }
        catch {
            Write-Host "FAILED: $($_.Exception.Message)" -ForegroundColor Red
            try { Stop-Process -Name EmuHawk -Force -ErrorAction Stop } catch { }
            Start-Sleep -Milliseconds 800
            continue
        }

        Write-Host $out
        Write-Host ''
        [void] $transcript.AppendLine("--- $plugin ${w}x${h} run $run ---").AppendLine($out).AppendLine()

        $row = [ordered] @{ Plugin = $plugin; Res = "${w}x${h}"; Run = $run }
        if ($out -match 'save=([\d.]+)ms load=([\d.]+)ms frame=([\d.]+)ms live=([\d.]+)ms') {
            $row.Save = [double] $Matches[1]; $row.Load = [double] $Matches[2]
            $row.Frame = [double] $Matches[3]; $row.Live = [double] $Matches[4]
        }
        if ($out -match 'maxDepth=(\d+)') { $row.Depth = [int] $Matches[1] }
        if ($out -match 'per-frame ([\d.]+)ms')              { $row.RFrame = [double] $Matches[1] }
        if ($out -match '\+save ([\d.]+)ms, load ([\d.]+)ms') {
            $row.RSave = [double] $Matches[1]; $row.RLoad = [double] $Matches[2]
        }
        if ($out -match '\(([+-][\d.]+)%\)') { $row.ModelErr = $Matches[1] + '%' }
        if ($out -match 'ROLLBACK OK') { $row.Verdict = 'rollback' } else { $row.Verdict = 'lockstep' }
        $results += [pscustomobject] $row
    }
}

if ($results.Count -gt 0) {
    Write-Host '===== summary =====' -ForegroundColor Cyan
    # Out-String with an explicit width: the host console is narrower than this table, and
    # Format-Table silently drops the right-hand columns rather than wrapping them.
    Write-Host ($results | Format-Table -AutoSize | Out-String -Width 240)
}
if ($OutFile) {
    Set-Content -Path $OutFile -Value $transcript.ToString() -Encoding UTF8
    Write-Host "transcript written to $OutFile" -ForegroundColor DarkGray
}
Write-Host "config.ini was modified; restore with: Copy-Item '$backup' '$cfg'" -ForegroundColor DarkGray
