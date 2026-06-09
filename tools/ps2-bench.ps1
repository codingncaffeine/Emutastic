# Launch Release-ps2, navigate to PS2, play the first game, let it benchmark, kill.
# Results are read afterward from perf.log (display=/emu= fps) and emulator.log.
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetCursorPos(int X,int Y); [DllImport("user32.dll")] public static extern void mouse_event(uint f,uint dx,uint dy,uint c,uint e); [DllImport("user32.dll")] public static extern bool SetForegroundWindow(System.IntPtr h);' -Name U -Namespace W

$exe = "C:\Users\gamer\source\repos\Emutastic\Emutastic\bin\Release-ps2\Emutastic.exe"
$proc = Start-Process $exe -PassThru
Write-Host "PID $($proc.Id)"
$root = [System.Windows.Automation.AutomationElement]::RootElement
$win = $null
for ($i=0; $i -lt 60; $i++) {
  Start-Sleep -Milliseconds 500
  $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$proc.Id)
  $win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children,$c)
  if ($win) { break }
}
if (-not $win) { Write-Host "FAIL: no window"; exit 1 }
Start-Sleep -Seconds 5
[W.U]::SetForegroundWindow([IntPtr]$win.Current.NativeWindowHandle) | Out-Null

function ClickName($name) {
  $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty,$name)
  $e = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$c)
  if (-not $e) { return $false }
  $r = $e.Current.BoundingRectangle
  [W.U]::SetCursorPos([int]($r.X+$r.Width/2),[int]($r.Y+$r.Height/2)) | Out-Null
  Start-Sleep -Milliseconds 120; [W.U]::mouse_event(2,0,0,0,0); [W.U]::mouse_event(4,0,0,0,0)
  return $true
}

# Navigate to PlayStation 2
if (ClickName "PlayStation 2") { Write-Host "clicked PS2 nav" } else { Write-Host "FAIL: no PS2 nav"; Stop-Process -Id $proc.Id -Force; exit 1 }
Start-Sleep -Seconds 2

# Click the first game card in the grid
$gc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty,"GameGridView")
$grid = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$gc)
$items = $grid.FindAll([System.Windows.Automation.TreeScope]::Children,[System.Windows.Automation.Condition]::TrueCondition)
Write-Host "grid items: $($items.Count)"
if ($items.Count -lt 1) { Write-Host "FAIL: no PS2 games"; Stop-Process -Id $proc.Id -Force; exit 1 }
$r = $items[0].Current.BoundingRectangle
[W.U]::SetCursorPos([int]($r.X+$r.Width/2),[int]($r.Y+$r.Height/2)) | Out-Null
Start-Sleep -Milliseconds 120; [W.U]::mouse_event(2,0,0,0,0); [W.U]::mouse_event(4,0,0,0,0)
Write-Host "clicked first game"
Start-Sleep -Seconds 2

# Click Play Now
if (ClickName ([char]0x25B6 + "  Play Now")) { Write-Host "clicked Play Now (glyph)" }
elseif (ClickName "Play Now") { Write-Host "clicked Play Now" }
else {
  # scan buttons for one containing 'Play'
  $bc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Button)
  $btns = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants,$bc)
  $hit = $false
  foreach ($b in $btns) { if ($b.Current.Name -like "*Play*") { $r=$b.Current.BoundingRectangle; [W.U]::SetCursorPos([int]($r.X+$r.Width/2),[int]($r.Y+$r.Height/2))|Out-Null; Start-Sleep -Milliseconds 120; [W.U]::mouse_event(2,0,0,0,0);[W.U]::mouse_event(4,0,0,0,0); Write-Host "clicked button '$($b.Current.Name)'"; $hit=$true; break } }
  if (-not $hit) { Write-Host "FAIL: no Play button"; Stop-Process -Id $proc.Id -Force; exit 1 }
}

Write-Host "benchmarking ~45s..."
Start-Sleep -Seconds 45
Stop-Process -Id $proc.Id -Force 2>$null
# kill any orphaned child (heavy core teardown)
Get-Process Emutastic -ErrorAction SilentlyContinue | Stop-Process -Force 2>$null
Write-Host "done."
