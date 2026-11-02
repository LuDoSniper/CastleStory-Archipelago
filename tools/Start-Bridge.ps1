param(
    [string] $Server = "",
    [string] $Slot = "",
    [string] $GameDirectory = "",
    [string] $Password = ""
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $GameDirectory) {
    $steam = Join-Path ${env:ProgramFiles(x86)} "Steam\steamapps\common\Castle Story"
    if (Test-Path $steam) { $GameDirectory = $steam }
}
if (-not $GameDirectory -or -not (Test-Path $GameDirectory)) {
    throw "Game directory not found. Pass -GameDirectory 'C:\path\to\Castle Story'."
}
$bridgeDir = Join-Path $GameDirectory "BepInEx\config\castle_story_archipelago"
if (-not (Test-Path (Join-Path $GameDirectory "BepInEx"))) {
    throw "BepInEx not found in $GameDirectory. Install BepInEx 5 first."
}
New-Item -Force -ItemType Directory $bridgeDir | Out-Null
$python = if (Get-Command py -ErrorAction SilentlyContinue) { "py" } elseif (Get-Command python -ErrorAction SilentlyContinue) { "python" } else { throw "Install Python 3.11+ first." }
& $python -m pip install -r (Join-Path $root "client\requirements.txt")
if ($LASTEXITCODE -ne 0) { throw "Python dependencies could not be installed." }
Write-Host "Starting Castle Story Archipelago bridge (Ctrl+C to stop)"
$argsList = @((Join-Path $root "client\bridge.py"), "--bridge-dir", $bridgeDir)
if ($Server -and $Slot) { $argsList += @("--server", $Server, "--slot", $Slot) }
if ($Password) { $argsList += @("--password", $Password) }
Write-Host "With no -Server or -Slot, connect from the in-game Archipelago menu."
& $python @argsList
if ($LASTEXITCODE -ne 0) { throw "Bridge exited with error code $LASTEXITCODE." }
