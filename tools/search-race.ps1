# UIA race tests: per-keystroke typing, nav+type races, unscoped path.
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y); [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint cButtons, uint dwExtraInfo); [DllImport("user32.dll")] public static extern bool SetForegroundWindow(System.IntPtr hWnd);' -Name U32 -Namespace W

$exe = "C:\Users\gamer\source\repos\Emutastic\Emutastic\bin\Release\net8.0-windows10.0.22621.0\Emutastic.exe"
$proc = Start-Process $exe -PassThru
$root = [System.Windows.Automation.AutomationElement]::RootElement
$win = $null
for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep -Milliseconds 500
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
    $win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
    if ($win) { break }
}
if (-not $win) { Write-Host "FAIL: window not found"; exit 1 }
Start-Sleep -Seconds 5
[W.U32]::SetForegroundWindow([IntPtr]$win.Current.NativeWindowHandle) | Out-Null

function Find-ByName($parent, $name) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $name)
    return $parent.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}
function Find-ById($parent, $id) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    return $parent.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}
function Click-Rect($el) {
    $r = $el.Current.BoundingRectangle
    $x = [int]($r.X + $r.Width / 2); $y = [int]($r.Y + $r.Height / 2)
    [W.U32]::SetCursorPos($x, $y) | Out-Null
    Start-Sleep -Milliseconds 60
    [W.U32]::mouse_event(2, 0, 0, 0, 0); [W.U32]::mouse_event(4, 0, 0, 0, 0)
}
function Grid-Titles($win) {
    $grid = Find-ById $win "GameGridView"
    if (-not $grid) { return "<no grid>" }
    $items = $grid.FindAll([System.Windows.Automation.TreeScope]::Children,
        [System.Windows.Automation.Condition]::TrueCondition)
    $titles = New-Object System.Collections.Generic.List[string]
    foreach ($it in $items) {
        $texts = $it.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::Text)))
        if ($texts.Count -gt 0) { $titles.Add($texts[0].Current.Name) }
        if ($titles.Count -ge 6) { break }
    }
    return "[realized $($items.Count)] " + ($titles -join " | ")
}
function Type-Search($win, $text, [int]$gapMs) {
    $box = Find-ById $win "SearchBox"
    Click-Rect $box
    Start-Sleep -Milliseconds 150
    foreach ($ch in $text.ToCharArray()) {
        [System.Windows.Forms.SendKeys]::SendWait("$ch")
        Start-Sleep -Milliseconds $gapMs
    }
}
function Clear-Search($win) {
    $box = Find-ById $win "SearchBox"
    $vp = $box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $vp.SetValue("")
    Start-Sleep -Milliseconds 700
}

$nes = Find-ByName $win "Nintendo (NES)"
$favs = $null
$btns = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)))
foreach ($b in $btns) { if ($b.Current.Name -like "*Favorites*") { $favs = $b; break } }

# ── Test A: NES view, realistic typing 60ms gaps ──
Click-Rect $nes; Start-Sleep -Seconds 2
Type-Search $win "contra" 60
Start-Sleep -Seconds 2
Write-Host "A (NES, 60ms typing): $(Grid-Titles $win)"
Clear-Search $win

# ── Test B: NES view, fast typing 15ms gaps ──
Type-Search $win "contra" 15
Start-Sleep -Seconds 2
Write-Host "B (NES, 15ms typing): $(Grid-Titles $win)"
Clear-Search $win

# ── Test C: navigate to Favorites then NES, then type IMMEDIATELY ──
if ($favs) { Click-Rect $favs; Start-Sleep -Seconds 1 }
Click-Rect $nes
Type-Search $win "contra" 40
Start-Sleep -Seconds 2
Write-Host "C (nav->type immediately): $(Grid-Titles $win)"
Clear-Search $win

# ── Test D: All Games view (unscoped, cached index) ──
$allGames = $null
foreach ($b in $btns) { if ($b.Current.Name -like "*All Games*") { $allGames = $b; break } }
if ($allGames) {
    Click-Rect $allGames; Start-Sleep -Seconds 3
    Type-Search $win "contra" 60
    Start-Sleep -Seconds 3
    Write-Host "D (All Games unscoped): $(Grid-Titles $win)"
    Clear-Search $win
}

# ── Test E: type, Esc, retype quickly ──
Click-Rect $nes; Start-Sleep -Seconds 1
Type-Search $win "contra" 40
Start-Sleep -Milliseconds 300
[System.Windows.Forms.SendKeys]::SendWait("{ESC}")
Start-Sleep -Milliseconds 120
Type-Search $win "contra" 40
Start-Sleep -Seconds 2
Write-Host "E (type/esc/retype): $(Grid-Titles $win)"

Stop-Process -Id $proc.Id -Force
Write-Host "Done."
