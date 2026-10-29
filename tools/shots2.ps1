# Two 1280x720 dev instances for screenshots (no debug tunnel: real LAN path + Steam lobby).
param([string]$Extra = "-vcoopudp")
$g = "C:\Program Files (x86)\Steam\steamapps\common\VoxelPlayground"
Get-Process "Voxel Playground" -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep 2
foreach ($i in 0, 1) {
  Start-Process "$g\Voxel Playground.exe" -ArgumentList "--flatscreen","-vcoopdev",$Extra,"-screen-fullscreen","0","-screen-width","1280","-screen-height","720" -WorkingDirectory $g
  Start-Sleep 25
}
Start-Sleep 20
