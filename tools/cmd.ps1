param([int]$Index = 0, [Parameter(ValueFromRemainingArguments)][string[]]$Lines)
# Sends dev commands to the Nth running game instance (sorted by start time).
$g = "C:\Program Files (x86)\Steam\steamapps\common\VoxelPlayground"
$p = Get-Process "Voxel Playground" | sort StartTime | select -Index $Index
Set-Content "$g\BepInEx\vcoop_cmd_$($p.Id).txt" ($Lines -join "`n")
"sent to pid $($p.Id)"
