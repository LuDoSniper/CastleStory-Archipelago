#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$GameDirectory,
    [Parameter(Mandatory=$true)][string]$SaveName,
    [string]$MissionId
)
$ErrorActionPreference = "Stop"
$root = (Resolve-Path -LiteralPath $GameDirectory).Path
if ($SaveName -eq "." -or $SaveName -eq ".." -or
    $SaveName.IndexOfAny([char[]]@('\','/',':','*','?','"','<','>','|')) -ge 0) {
    throw "SaveName doit etre le nom du dossier de sauvegarde, pas un chemin."
}
$saveRoot = Join-Path $root "Info\Saves"
$saveDir = Join-Path $saveRoot $SaveName
if (-not (Test-Path -LiteralPath (Join-Path $saveDir "Heightmap.png"))) {
    throw "Sauvegarde introuvable : $saveDir/Heightmap.png"
}
$config = Join-Path $root "BepInEx\config\castle_story_archipelago"
$sessionPath = Join-Path $config "session.json"
if (-not (Test-Path -LiteralPath $sessionPath)) {
    throw "Aucune seed active connue. Connecte-toi a Archipelago avant de fermer le jeu."
}
$session = Get-Content -LiteralPath $sessionPath -Raw | ConvertFrom-Json
if (-not $session.token -or -not $session.seed -or
    -not $session.campaign -or $session.campaign.ready -ne $true) {
    throw "Le manifeste Archipelago de session.json est absent ou non pret."
}
$missions = @($session.campaign.missions | Where-Object { $null -ne $_ })
if ($MissionId) { $missions = @($missions | Where-Object { $_.id -eq $MissionId }) }
if ($missions.Count -ne 1) {
    throw "Cette seed comporte $($missions.Count) missions possibles. Fournis -MissionId pour eviter une attribution arbitraire."
}
$mission = $missions[0]
$missionMap = if ($mission.map_id) { [string]$mission.map_id } else { [string]$mission.map }
if (-not $missionMap -or -not $mission.mode -or
    [string]$mission.mode -notin @("Invasion","Conquest")) {
    throw "Mission map/mode invalide : ne pas deviner les metadonnees."
}
$tagPath = Join-Path $config "save_tags.json"
$mirrorPath = Join-Path $saveRoot ".archipelago-save-tags.json"
$registry = @{ schema = 1; saves = @() }
if (Test-Path -LiteralPath $tagPath) {
    $existing = Get-Content -LiteralPath $tagPath -Raw | ConvertFrom-Json
    if ($null -eq $existing.saves) { throw "Registre existant illisible; il ne sera pas ecrase." }
    $registry.saves = @($existing.saves)
} elseif (Test-Path -LiteralPath $mirrorPath) {
    $existing = Get-Content -LiteralPath $mirrorPath -Raw | ConvertFrom-Json
    if ($null -eq $existing.saves) { throw "Registre de secours illisible." }
    $registry.saves = @($existing.saves)
}
$prior = @($registry.saves | Where-Object { $_.folder -eq $SaveName })
if ($prior.Count -gt 0) {
    throw "Cette sauvegarde a deja un tag. Aucun remplacement automatique n'est autorise."
}
Write-Host ""
Write-Host "SAUVEGARDE : $SaveName"
Write-Host "SEED        : $($session.seed)"
Write-Host "SLOT        : $($session.slot_name)"
Write-Host "MISSION     : $($mission.id)"
Write-Host "MODE / MAP  : $($mission.mode) / $missionMap"
Write-Warning "Associe uniquement une vraie sauvegarde Archipelago de CETTE seed. Une save classique ne doit jamais etre associee."
$confirmation = Read-Host "Tape exactement ASSOCIER pour confirmer"
if ($confirmation -cne "ASSOCIER") {
    Write-Host "Annule sans modification."
    return
}
$registry.saves += @{
    folder = $SaveName
    token = [string]$session.token
    seed = [string]$session.seed
    slot = [string]$session.slot_name
    mode = [string]$session.mode
    mission = [string]$mission.id
    map_id = $missionMap
    created_utc = (Get-Date).ToUniversalTime().ToString("o")
}
$json = ConvertTo-Json -InputObject $registry -Depth 12
New-Item -Force -ItemType Directory $config | Out-Null
foreach ($target in @($tagPath, $mirrorPath)) {
    if (Test-Path -LiteralPath $target) {
        Copy-Item -LiteralPath $target -Destination ($target + ".bak-" + (Get-Date -Format "yyyyMMdd-HHmmss")) -Force
    }
    $temp = $target + ".tmp"
    [System.IO.File]::WriteAllText($temp, $json, [System.Text.Encoding]::UTF8)
    if (Test-Path -LiteralPath $target) {
        [System.IO.File]::Replace($temp, $target, $null)
    } else { Move-Item -LiteralPath $temp -Destination $target }
}
Write-Host "Association enregistree sans modifier la sauvegarde du jeu."
