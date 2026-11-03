#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$GameDirectory,
    [Parameter(Mandatory=$true)][string]$SaveName,
    [string]$MissionId,
    [switch]$ValidateOnly
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

function Read-ExistingRegistry {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) {
        return @{ schema = 1; saves = @() }
    }
    try {
        $raw = [System.IO.File]::ReadAllText($Path, [System.Text.Encoding]::UTF8)
        if ([string]::IsNullOrWhiteSpace($raw)) {
            throw "Fichier vide"
        }
        $data = ConvertFrom-Json -InputObject $raw -ErrorAction Stop
        if ($null -eq $data -or $data -isnot [pscustomobject]) {
            throw "Un objet JSON est attendu"
        }
        $schema = $data.PSObject.Properties["schema"]
        if ($null -eq $schema -or [string]$schema.Value -ne "1") {
            throw "Version de schema inconnue"
        }
        foreach ($property in $data.PSObject.Properties) {
            if ($property.Name -notin @("schema", "saves")) {
                throw "Champ non reconnu $($property.Name): conservation necessaire"
            }
        }
        # PowerShell 5.1 and some legacy registries represent an empty
        # "saves" array as $null or omit it entirely. Both mean ZERO tags,
        # not corrupt metadata. Actual malformed or unknown entries still fail.
        $field = $data.PSObject.Properties["saves"]
        $entries = @()
        if ($null -eq $field -or $null -eq $field.Value) {
            Write-Warning "Registre $([IO.Path]::GetFileName($Path)) sans tags : liste vide acceptee."
        } elseif ($field.Value -is [array]) {
            foreach ($entry in $field.Value) { $entries += $entry }
        } elseif ($field.Value -is [pscustomobject]) {
            $entries += $field.Value
        } else {
            throw "La propriete saves n'est ni une liste ni un objet"
        }
        foreach ($entry in $entries) {
            if ($null -eq $entry -or $entry -isnot [pscustomobject] -or
                [string]::IsNullOrWhiteSpace([string]$entry.folder) -or
                [string]::IsNullOrWhiteSpace([string]$entry.token) -or
                [string]::IsNullOrWhiteSpace([string]$entry.mission)) {
                throw "Entree de sauvegarde invalide : aucun tag ne doit etre perdu"
            }
        }
        return @{ schema = 1; saves = @($entries) }
    } catch {
        throw "Registre $Path non lisible en securite : $($_.Exception.Message). Aucun fichier modifie."
    }
}

# Merge BOTH indexes; a freshly created empty primary registry must never
# discard valid tags held by the durable Info/Saves mirror.
$byFolder = [System.Collections.Generic.Dictionary[string,object]]::new(
    [System.StringComparer]::OrdinalIgnoreCase)
foreach ($path in @($mirrorPath, $tagPath)) {
    $existing = Read-ExistingRegistry -Path $path
    foreach ($entry in $existing.saves) {
        $key = [string]$entry.folder
        if ($byFolder.ContainsKey($key)) {
            $previous = $byFolder[$key]
            if ([string]$previous.token -ne [string]$entry.token -or
                [string]$previous.mission -ne [string]$entry.mission) {
                throw "Registres contradictoires pour $key. Aucun fichier ne sera modifie."
            }
        }
        $byFolder[$key] = $entry
    }
}
if ($byFolder.ContainsKey($SaveName)) {
    throw "Cette sauvegarde a deja un tag. Aucun remplacement automatique n'est autorise."
}
$registry = @{ schema = 1; saves = @() }
foreach ($entry in $byFolder.Values) { $registry.saves += $entry }

Write-Host ""
Write-Host "SAUVEGARDE : $SaveName"
Write-Host "SEED        : $($session.seed)"
Write-Host "SLOT        : $($session.slot_name)"
Write-Host "MISSION     : $($mission.id)"
Write-Host "MODE / MAP  : $($mission.mode) / $missionMap"
Write-Warning "Associe uniquement une vraie sauvegarde Archipelago de CETTE seed. Une save classique ne doit jamais etre associee."
if ($ValidateOnly) {
    Write-Host "Verification reussie : $($registry.saves.Count) tag(s) existant(s), aucun fichier modifie."
    return
}
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
