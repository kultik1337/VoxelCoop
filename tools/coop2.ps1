# Dev test: two local instances, instance 0 hosts, instance 1 joins by code, host starts the given mode.
param([string]$Mode = "Start Sandbox", [string]$Extra = "-vcoopudp")
$g = "C:\Program Files (x86)\Steam\steamapps\common\VoxelPlayground"; $t = $PSScriptRoot
Get-Process "Voxel Playground" -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep 2
foreach ($i in 0, 1) {
  Start-Process "$g\Voxel Playground.exe" -ArgumentList "--flatscreen","-vcoopdev",$Extra,"-screen-fullscreen","0","-screen-width","960","-screen-height","540" -WorkingDirectory $g
  Start-Sleep 25
}
Start-Sleep 20
& "$t\cmd.ps1" 0 "show" "click btnCreateRoom" | Out-Null
Start-Sleep 4
& "$t\cmd.ps1" 0 "dump" | Out-Null
Start-Sleep 5
$log = Get-Content "$g\BepInEx\LogOutput.log" -Raw
$code = [regex]::Matches($log, 'state=HostingLobby code=(\w+)') | select -Last 1 | % { $_.Groups[1].Value }
"code=$code"
& "$t\cmd.ps1" 1 "join $code" | Out-Null
Start-Sleep 10
& "$t\cmd.ps1" 0 "click $Mode" | Out-Null
Start-Sleep 3
& "$t\cmd.ps1" 0 "click btnPlay" | Out-Null
