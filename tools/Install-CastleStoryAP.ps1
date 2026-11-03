#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)]
    [string]$GameDirectory
)

$ErrorActionPreference = "Stop"
$game = (Resolve-Path -LiteralPath $GameDirectory).Path
$source = Join-Path $PSScriptRoot "BepInEx\plugins"
$target = Join-Path $game "BepInEx\plugins"
$config = Join-Path $game "BepInEx\config\castle_story_archipelago"
$srcDll = Join-Path $source "CastleStoryArchipelago.dll"
$srcBridge = Join-Path $source "castle_story_ap\castle-story-ap-bridge.exe"
$srcLogo = Join-Path $source "castle_story_ap\archipelago-logo.png"
$dstDll = Join-Path $target "CastleStoryArchipelago.dll"
$dstBridge = Join-Path $target "castle_story_ap\castle-story-ap-bridge.exe"
$dstLogo = Join-Path $target "castle_story_ap\archipelago-logo.png"

foreach ($path in @($srcDll, $srcBridge, $srcLogo)) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Kit incomplet : $path. Extrais entierement castle-story-archipelago-test-kit."
    }
}
if (-not (Test-Path -LiteralPath (Join-Path $game "BepInEx\core"))) {
    throw "Mauvais dossier : $game ne contient pas BepInEx\core."
}

# Never replace an in-use assembly or start a second game instance.
$gameProcesses = @(Get-CimInstance Win32_Process | Where-Object {
    $_.ExecutablePath -and
    $_.ExecutablePath.StartsWith(($game.TrimEnd('\') + '\'), [StringComparison]::OrdinalIgnoreCase) -and
    $_.Name -notin @("castle-story-ap-bridge.exe")
})
if ($gameProcesses.Count -gt 0) {
    $names = ($gameProcesses | ForEach-Object { $_.Name }) -join ", "
    throw "Ferme Castle Story avant la mise a jour. Processus detects : $names"
}

# Stop ONLY the client running from this exact game folder; never another AP bridge.
$bridges = @(Get-CimInstance Win32_Process -Filter "Name='castle-story-ap-bridge.exe'" |
    Where-Object { $_.ExecutablePath -and
        [string]::Equals($_.ExecutablePath, $dstBridge, [StringComparison]::OrdinalIgnoreCase) })
foreach ($process in $bridges) {
    Write-Host "Arret de l'ancien bridge PID $($process.ProcessId)"
    Stop-Process -Id $process.ProcessId -Force -ErrorAction Stop
}

New-Item -Force -ItemType Directory (Split-Path $dstBridge), $config | Out-Null
$backup = Join-Path $config ("upgrade-backup-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
New-Item -ItemType Directory -Path $backup | Out-Null
foreach ($path in @($dstDll, $dstBridge,
    (Join-Path $config "session.json"), (Join-Path $config "bridge_status.json"))) {
    if (Test-Path -LiteralPath $path) {
        Copy-Item -LiteralPath $path -Destination $backup -Force
    }
}
foreach ($pair in @(@($srcDll, $dstDll), @($srcBridge, $dstBridge), @($srcLogo, $dstLogo))) {
    Copy-Item -LiteralPath $pair[0] -Destination $pair[1] -Force
    $srcHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $pair[0]).Hash
    $dstHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $pair[1]).Hash
    if ($srcHash -ne $dstHash) { throw "Echec de verification SHA256 : $($pair[1])" }
    Write-Host ("OK : " + (Split-Path $pair[1] -Leaf) + " SHA256=" + $dstHash.Substring(0, 12))
}

# Migrate legacy connection.json (which could contain a PLAINTEXT password)
# to a preferences file containing ONLY the server and slot. Do NOT back up
# the old file. The new bridge uses an anonymous stdin pipe for passwords.
$legacyConnection = Join-Path $config "connection.json"
$prefsPath = Join-Path $config "connection_preferences.json"
if (Test-Path -LiteralPath $legacyConnection) {
    try {
        $old = Get-Content -LiteralPath $legacyConnection -Raw | ConvertFrom-Json
        if (-not (Test-Path -LiteralPath $prefsPath) -and $old.server -and $old.slot) {
            @{ server = [string]$old.server; slot = [string]$old.slot } |
                ConvertTo-Json -Compress |
                Set-Content -LiteralPath $prefsPath -Encoding UTF8
        }
    } catch {
        Write-Warning "Ancien fichier de connexion illisible. Il sera supprime sans recuperer le mot de passe."
    } finally {
        Remove-Item -LiteralPath $legacyConnection -Force -ErrorAction Stop
        Write-Host "Ancien connection.json supprime (mot de passe non conserve)."
    }
}

# Remove ONLY stale session/cache; preserve seed progress, checks.json,
# bonuses, savegames and native map inventory.
foreach ($name in @("session.json", "bridge_status.json")) {
    $path = Join-Path $config $name
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
}

Write-Host ""
Write-Host "Mise a jour terminee. Sauvegarde : $backup"
Write-Host "Relance Castle Story, ouvre le bouton Archipelago, puis reconnecte-toi."
Write-Host "Le jeu doit afficher un bridge 0.7.6 et une session fraiche."
