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

    A savestate is loaded before probing, but not for the reason it first appeared. Measured on
    Super Smash Bros., eight runs each way, the boot screen and an in-game state agree: frame
    2.21ms against 2.32ms, everything else inside the run-to-run spread. An earlier reading that
    a title screen was three times cheaper came from GoldenEye, and was the *game* being lighter
    rather than the screen -- the two had been changed together.

    What loading a state does buy is a workload that holds still. The probe's passes run over
    several seconds, and a booting game moves through logos, an intro and an attract demo while
    they do; the repair decomposition assumes a stationary cost and misreads badly when that
    fails, which is visible as the derived load collapsing to zero on some boot runs. Pass
    -StateSlot 0 to probe at boot anyway, which is the only option for a game with no state.

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
    [string[]] $Rom = @('X:\Games\Emulators\BizHawk\zz_ROMS\Multiplayer\Super Smash Bros. (USA).z64'),
    # A savestate carries the core configuration it was made under, and the video plugin is a
    # SYNC setting -- so a state saved on Rice is not the state to load while configured for
    # GLideN64. The slot therefore follows the plugin rather than being fixed for the sweep.
    [hashtable] $SlotByPlugin = @{ Rice = 1; GLideN64 = 2 },
    # -1 takes the slot from SlotByPlugin; 0 probes wherever the ROM boots to, with no state
    # loaded; anything higher is that slot. 0 is the only way to compare a title screen against
    # an in-game state for the SAME game, which is the only comparison that isolates the screen
    # from the game.
    [int]      $StateSlot = -1,
    [string[]] $Config = @('Rice:320x240'),
    [int]      $Runs = 1,
    [string]   $OutFile,
    [int]      $TimeoutSec = 180,
    # How long the core runs before it is probed. This is the one wait here that is part of the
    # experiment rather than overhead: everything else -- menus, tab clicks, window and log polling --
    # is the harness sitting around, and is driven by conditions instead of by a duration.
    #
    # Kept at a second and a half rather than trimmed with the rest, on the general principle that a
    # measurement taken of a just-started process is not obviously the measurement wanted. That is a
    # precaution, not a finding: cutting it to 250ms and restoring it made no difference to any figure
    # the probe reports, so nothing here is known to depend on it.
    [int]      $SettleMs = 1500,
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
    # Poll briefly for the switch rather than assuming a duration for it: it is usually immediate,
    # and when it does not happen there is nothing to gain by waiting -- the clicks below use screen
    # coordinates and land whether or not the window took focus.
    for ($i = 0; $i -lt 6; $i++) {
        if ([ProbeSweep.Win]::GetForegroundWindow() -eq $Handle) { return $true }
        Start-Sleep -Milliseconds 20
    }
    return $false
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
    Start-Sleep -Milliseconds 60
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
        Start-Sleep -Milliseconds 75
    }
    throw "window '$Title' did not appear within ${Seconds}s"
}

function Invoke-Click {
    param([int] $X, [int] $Y)
    [void][ProbeSweep.Win]::SetCursorPos($X, $Y)
    Start-Sleep -Milliseconds 30
    [ProbeSweep.Win]::mouse_event(0x0002, 0, 0, 0, [System.IntPtr]::Zero)  # LEFTDOWN
    [ProbeSweep.Win]::mouse_event(0x0004, 0, 0, 0, [System.IntPtr]::Zero)  # LEFTUP
    Start-Sleep -Milliseconds 60
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
    # No settle after expanding or invoking: the caller's next step already polls for whatever this
    # produced -- the following menu item, a window, or a log marker -- so sleeping here just adds a
    # fixed cost to every step of every run. The retry loop below covers a dropdown that is slow to
    # appear, at 60ms rather than by guessing a duration up front.
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        try {
            foreach ($r in (Get-ProcessRoots -ProcId $ProcId)) {
                $hit = $r.FindFirst($Tree::Descendants, $cond)
                if (-not $hit) { continue }
                if ($Expand) {
                    $ec = Get-Pattern $hit ([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
                    if ($ec) { $ec.Expand(); return }
                }
                $inv = Get-Pattern $hit ([System.Windows.Automation.InvokePattern]::Pattern)
                if ($inv) { $inv.Invoke(); return }
            }
        } catch { }
        Start-Sleep -Milliseconds 60
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

<#
    One filtered cross-process call, not a walk.

    This used to enumerate every descendant and compare names here. The tool's tree arrives over the
    MSAA bridge, where a full walk is hundreds of milliseconds and each property read is its own
    round trip -- and the tab search pays for one of these after every click. Handing the condition
    to UI Automation lets it filter on its side and return the one element.
#>
function Find-ByName {
    param($Window, [string] $Name, [switch] $OnScreenOnly)
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $Name)
    if ($OnScreenOnly) {
        $cond = New-Object System.Windows.Automation.AndCondition(
            $cond,
            (New-Object System.Windows.Automation.PropertyCondition($AE::IsOffscreenProperty, $false)))
    }
    return $Window.FindFirst($Tree::Descendants, $cond)
}

<#
    The tab control, reached with one walker step instead of enumerating the window's descendants.
#>
function Get-TabControl {
    param($Window)
    return [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetFirstChild($Window)
}

<#
    Is this tab page the selected one? Scoped to the tab control's immediate CHILDREN, which is the
    difference between a fast answer and a slow one.

    A descendant search has to walk the whole subtree before it can conclude "not here", and during
    the strip scan that negative is the answer after every click but the last -- so the search was
    costing a full tree walk per click, over the MSAA bridge, several times per run. The realized
    tab pages are direct children of the tab control, so asking there looks at four elements.
#>
function Find-TabPage {
    param($TabControl, [string] $Name)
    $cond = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $Name)),
        (New-Object System.Windows.Automation.PropertyCondition($AE::IsOffscreenProperty, $false)))
    return $TabControl.FindFirst($Tree::Children, $cond)
}

<#
    Click along the tab strip until the wanted page appears in the tree. The strip is the band
    between the tab control's top and the selected page's top, so its position is measured
    rather than assumed -- the form is DPI-scaled and the tab widths follow the font.
#>
# Where each tab's label was found, remembered across runs. Every run maximizes the tool window to
# the same place, so the strip lands at the same coordinates and a sweep only pays for the search
# once instead of once per run -- which was the single biggest fixed cost in a run.
$script:TabHit = @{}
$script:StripY = $null
$script:StripLeft = $null

function Select-ToolTab {
    param($Window, [string] $Name)
    $tabCtl = Get-TabControl -Window $Window
    if (-not $tabCtl) { throw 'the tool window exposes no tab control' }
    if (Find-TabPage -TabControl $tabCtl -Name $Name) { return }

    # The strip sits between the tab control's top and the selected page's, so finding it needs two
    # rectangles -- measured once and reused, since every run maximizes to the same geometry.
    if ($null -eq $script:StripY) {
        $page = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetFirstChild($tabCtl)
        if (-not $page) { throw 'no tab page is visible; cannot locate the tab strip' }
        $tr = $tabCtl.Current.BoundingRectangle
        $pr = $page.Current.BoundingRectangle
        $script:StripY = [int](($tr.Y + $pr.Y) / 2)
        $script:StripLeft = [int] $tr.X
    }
    $stripY = $script:StripY
    $left = $script:StripLeft

    if ($script:TabHit.ContainsKey($Name)) {
        Invoke-Click -X $script:TabHit[$Name] -Y $stripY
        if (Find-TabPage -TabControl $tabCtl -Name $Name) { return }
        # Geometry moved, so nothing cached about it is trustworthy any more.
        $script:TabHit.Remove($Name)
        $script:StripY = $null
        $script:StripLeft = $null
    }

    # 50px steps: the observed labels are ~120px apart at this DPI, so this cannot step over one,
    # and it halves the clicks of the original 30px sweep. Each miss is now four elements to check
    # rather than a whole tree, so the scan costs about what the clicks themselves do.
    for ($x = $left + 10; $x -lt $left + 1200; $x += 50) {
        Invoke-Click -X $x -Y $stripY
        if (Find-TabPage -TabControl $tabCtl -Name $Name) {
            Write-Verbose "selected tab '$Name' at x=$x"
            $script:TabHit[$Name] = $x
            return
        }
    }
    throw "could not select the '$Name' tab"
}

<#
    The log's text arrives as the Name of its element -- the bridge offers no TextPattern or
    ValuePattern here. It is the only multi-line name in the window, which is what identifies it.
#>
function Find-LogElement {
    param($Window)
    $best = $null; $bestLen = -1
    foreach ($e in (Get-Descendants -Window $Window)) {
        $n = $e.Current.Name
        if (-not $n -or $n -notmatch "`n") { continue }
        if ($n.Length -gt $bestLen) { $bestLen = $n.Length; $best = $e }
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
            Start-Sleep -Milliseconds 100
        }
        if ($proc.MainWindowHandle -eq [System.IntPtr]::Zero) { throw 'EmuHawk main window never appeared' }
        Show-WindowMaximized -Handle $proc.MainWindowHandle

        # The core is loaded when the caption says so: EmuHawk titles itself
        # "<game> [<system>] - BizHawk" once a ROM is up, and plain "BizHawk" before. Waiting on that
        # beats a fixed settle in both directions -- it returns as soon as the ROM is up on a fast
        # start, and still waits on a slow one instead of racing ahead into an empty core.
        $romDeadline = (Get-Date).AddSeconds($Seconds)
        while ((Get-Date) -lt $romDeadline) {
            $proc.Refresh()
            if ($proc.MainWindowTitle -match '\[.+\] - BizHawk') { break }
            Start-Sleep -Milliseconds 100
        }
        $proc.Refresh()
        if ($proc.MainWindowTitle -notmatch '\[.+\] - BizHawk') { throw 'the ROM never finished loading' }
        Start-Sleep -Milliseconds 200   # the caption lands a touch before the menu is usable

        if ($Slot -gt 0) {
            Invoke-MenuItem -ProcId $proc.Id -Name 'File'       -Expand $true
            Invoke-MenuItem -ProcId $proc.Id -Name 'Load State' -Expand $true
            Invoke-MenuItem -ProcId $proc.Id -Name "$Slot"      -Expand $false
        }
        # Let the core run before measuring it -- see -SettleMs. Opening the tool below happens on
        # top of this, so the effective warm-up is a little longer than the number.
        Start-Sleep -Milliseconds $SettleMs

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
        # Locate the log element once, then poll only its own text. Re-finding it each time meant a
        # full descendant walk every poll, against a UI thread the probe has frozen anyway.
        $deadline = (Get-Date).AddSeconds($Seconds)
        $text = ''
        $log = $null
        while ((Get-Date) -lt $deadline) {
            if (-not $log) { try { $log = Find-LogElement -Window $tool } catch { } }
            if ($log) { try { $text = $log.Current.Name } catch { $log = $null } }
            if ($text -match '=== done ===') { break }
            Start-Sleep -Milliseconds 150
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
            # Wait for the process to actually go, rather than for a duration guessed to outlast it:
            # the next run refuses to start while an EmuHawk is up.
            try { Stop-Process -Id $proc.Id -Force -ErrorAction Stop } catch { }
            try { [void] $proc.WaitForExit(5000) } catch { }
        }
    }
}

# ---------------------------------------------------------------------------------- main

$exe     = Join-Path $BizHawkHome 'EmuHawk.exe'
$cfg     = Join-Path $BizHawkHome 'config.ini'
$toolDll = Join-Path $BizHawkHome 'ExternalTools\BizHawkNetplay.Tool.dll'

foreach ($p in (@($exe, $cfg, $toolDll) + $Rom)) {
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
    if ($StateSlot -ge 0) { return $StateSlot }
    if ($SlotByPlugin -and $SlotByPlugin.ContainsKey($Plugin)) { return [int] $SlotByPlugin[$Plugin] }
    return 0
}

Write-Host ("{0} rom(s) x {1} config(s) x {2} run(s)" -f $Rom.Count, $Config.Count, $Runs) -ForegroundColor DarkGray
Write-Host ''

$results = @()
$transcript = New-Object System.Text.StringBuilder

foreach ($romPath in $Rom) {
    $game = [System.IO.Path]::GetFileNameWithoutExtension($romPath)

    foreach ($spec in $Config) {
        if ($spec -notmatch '^([A-Za-z0-9]+):(\d+)x(\d+)$') { throw "bad -Config spec '$spec' (want e.g. Rice:320x240)" }
        $plugin = $Matches[1]; $w = [int] $Matches[2]; $h = [int] $Matches[3]
        if (-not $PluginIds.ContainsKey($plugin)) { throw "unknown plugin '$plugin'; try $($PluginIds.Keys -join ', ')" }

        $slot = Resolve-Slot -Plugin $plugin
        $where = if ($slot -gt 0) { "slot $slot" } else { 'boot' }

        for ($run = 1; $run -le $Runs; $run++) {
            Write-Host ("[{0} | {1} {2}x{3} {4}] run {5}/{6} ... " -f $game, $plugin, $w, $h, $where, $run, $Runs) -NoNewline
            Set-N64Config -Path $cfg -PluginId $PluginIds[$plugin] -Width $w -Height $h -ToolDll $toolDll

            try {
                $out = Invoke-OneProbe -Exe $exe -RomPath $romPath -Slot $slot -Seconds $TimeoutSec
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
            [void] $transcript.AppendLine("--- $game | $plugin ${w}x${h} $where run $run ---").AppendLine($out).AppendLine()

            # Short column names, and plugin+resolution folded into one: the host console is about
            # 80 wide and Format-Table drops the right-hand columns rather than wrapping, which
            # silently hid the repair-derived figures this whole script exists to collect. The CSV
            # written alongside carries everything at full precision.
            $row = [ordered] @{
                Game = $game; Config = "$plugin ${w}x${h}"; Where = $where; Run = $run
            }
            if ($out -match 'state=([\d.]+)KiB') { $row.StateKiB = [double] $Matches[1] }
            if ($out -match 'save=([\d.]+)ms load=([\d.]+)ms frame=([\d.]+)ms live=([\d.]+)ms') {
                $row.Save = [double] $Matches[1]; $row.Load = [double] $Matches[2]
                $row.Frame = [double] $Matches[3]; $row.Live = [double] $Matches[4]
            }
            if ($out -match 'maxDepth=(\d+)') { $row.Depth = [int] $Matches[1] }
            if ($out -match 'per-frame ([\d.]+)ms') { $row.RFrame = [double] $Matches[1] }
            if ($out -match '\+save ([\d.]+)ms, load ([\d.]+)ms') {
                $row.RSave = [double] $Matches[1]; $row.RLoad = [double] $Matches[2]
            }
            if ($out -match '1f=([\d.]+)ms (\d+)f=([\d.]+)ms \(\+saves ([\d.]+)ms\)') {
                $row.R1f = [double] $Matches[1]; $row.R8f = [double] $Matches[3]; $row.R8fSaved = [double] $Matches[4]
            }
            if ($out -match 'modelled ([\d.]+)ms') { $row.Modelled = [double] $Matches[1] }
            # The sign is optional: the probe formats a zero error as "0.0%" with no sign, so a
            # pattern requiring one silently dropped exactly the runs where the model was right.
            if ($out -match '\(([+-]?[\d.]+)%\)') { $row.ErrPct = [double] $Matches[1] }
            if ($out -match 'ROLLBACK OK') { $row.Verdict = 'rollback' } else { $row.Verdict = 'lockstep' }
            $results += [pscustomobject] $row
        }
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
    # Full precision, every column, nothing dropped to fit a console.
    $csv = [System.IO.Path]::ChangeExtension($OutFile, '.csv')
    $results | Export-Csv -Path $csv -NoTypeInformation -Encoding UTF8
    Write-Host "table written to $csv" -ForegroundColor DarkGray
}
Write-Host "config.ini was modified; restore with: Copy-Item '$backup' '$cfg'" -ForegroundColor DarkGray
