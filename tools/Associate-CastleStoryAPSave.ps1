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
$alreadyAssociated = $false
if ($byFolder.ContainsKey($SaveName)) {
    $existingTag = $byFolder[$SaveName]
    if ([string]$existingTag.token -ne [string]$session.token -or
        [string]$existingTag.mission -ne [string]$mission.id -or
        [string]$existingTag.mode -ne [string]$session.mode -or
        ([string]$existingTag.seed -ne [string]$session.seed) -or
        ([string]$existingTag.slot -ne [string]$session.slot_name) -or
        ($existingTag.has_slot_identity -and
            ([int]$existingTag.team -ne [int]$session.team -or
             [int]$existingTag.slot_id -ne [int]$session.slot)) -or
        (-not [string]::IsNullOrWhiteSpace([string]$existingTag.map_id) -and
         [string]$existingTag.map_id -ne $missionMap)) {
        throw "Cette sauvegarde est deja associee a une autre seed, mission, carte ou mode. Aucun ecrasement autorise."
    }
    $alreadyAssociated = $true
    Write-Warning "Association identique deja presente : verification et synchronisation des index uniquement."
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
if (-not $alreadyAssociated) { $registry.saves += @{
    folder = $SaveName
    token = [string]$session.token
    seed = [string]$session.seed
    slot = [string]$session.slot_name
    team = [int]$session.team
    slot_id = [int]$session.slot
    has_slot_identity = $true
    mode = [string]$session.mode
    mission = [string]$mission.id
    map_id = $missionMap
    created_utc = (Get-Date).ToUniversalTime().ToString("o")
} }
$json = ConvertTo-Json -InputObject $registry -Depth 12
# Stage both files and create durable backups BEFORE modifying either index.
# File.Replace(source,destination,$null) failed under Windows PowerShell 5.1
# with "Le chemin d'acces n'a pas une forme conforme." Use ordinary
# absolute-path file copies instead; they are recoverable from backups.
# Write the durable mirror FIRST so a failed primary copy can be repaired
# on the next invocation by merging both registries.
[void](New-Item -Force -ItemType Directory -Path $config)
$targets = @($mirrorPath, $tagPath)
$transactionId = [guid]::NewGuid().ToString("N")
$staged = @{}
foreach ($target in $targets) {
    $temp = "$target.tmp-$transactionId"
    [System.IO.File]::WriteAllText($temp, $json, [System.Text.Encoding]::UTF8)
    $staged[$target] = $temp
    if ([System.IO.File]::Exists($target)) {
        $backup = "$target.bak-$transactionId"
        [System.IO.File]::Copy($target, $backup, $false)
    }
}
foreach ($target in $targets) {
    $temp = [string]$staged[$target]
    [System.IO.File]::Copy($temp, $target, $true)
    if ([System.IO.File]::ReadAllText($target, [System.Text.Encoding]::UTF8) -cne $json) {
        throw "Verification du registre echouee : $target. Restaurer la sauvegarde .bak-$transactionId."
    }
    [System.IO.File]::Delete($temp)
}
Write-Host "Association enregistree et index synchronises sans modifier la sauvegarde du jeu."
