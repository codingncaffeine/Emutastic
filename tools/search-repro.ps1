# UIA repro: launch Emutastic, navigate to NES, type "contra", dump visible results.
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y); [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint cButtons, uint dwExtraInfo);' -Name U32 -Namespace W

$exe = "C:\Users\gamer\source\repos\Emutastic\Emutastic\bin\Release\net8.0-windows10.0.22621.0\Emutastic.exe"
$proc = Start-Process $exe -PassThru
Write-Host "PID $($proc.Id) — waiting for main window..."

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
Start-Sleep -Seconds 5   # library load

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
function Click-Element($el) {
    $pt = $el.GetClickablePoint()
    [W.U32]::SetCursorPos([int]$pt.X, [int]$pt.Y) | Out-Null
    Start-Sleep -Milliseconds 100
    [W.U32]::mouse_event(2, 0, 0, 0, 0); [W.U32]::mouse_event(4, 0, 0, 0, 0)
}
function Dump-Grid($win, $label) {
    $grid = Find-ById $win "GameGridView"
    if (-not $grid) { Write-Host "  ($label) GameGridView not found"; return }
    $items = $grid.FindAll([System.Windows.Automation.TreeScope]::Children,
        [System.Windows.Automation.Condition]::TrueCondition)
    Write-Host "($label) realized grid items: $($items.Count)"
    $n = 0
    foreach ($it in $items) {
        $texts = $it.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::Text)))
        $title = if ($texts.Count -gt 0) { $texts[0].Current.Name } else { "?" }
        Write-Host "  card: '$title'"
        $n++; if ($n -ge 8) { break }
    }
}

# 1. Click "Nintendo (NES)" in the sidebar
$nes = Find-ByName $win "Nintendo (NES)"
if (-not $nes) { Write-Host "FAIL: NES nav not found"; Stop-Process -Id $proc.Id -Force; exit 1 }
Click-Element $nes
Start-Sleep -Seconds 2
Dump-Grid $win "after NES nav"

# 2. Type into SearchBox
$box = Find-ById $win "SearchBox"
if (-not $box) { Write-Host "FAIL: SearchBox not found"; Stop-Process -Id $proc.Id -Force; exit 1 }
$vp = $box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
$vp.SetValue("contra")
Write-Host "Search text set to 'contra'."
Start-Sleep -Seconds 3   # debounce + search + render

Dump-Grid $win "after search"

Stop-Process -Id $proc.Id -Force
Write-Host "Done."
