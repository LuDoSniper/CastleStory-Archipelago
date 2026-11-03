#Requires -Version 5.1
$ErrorActionPreference = "Stop"
$script = (Resolve-Path (Join-Path $PSScriptRoot "../tools/Associate-CastleStoryAPSave.ps1")).Path
$game = Join-Path ([System.IO.Path]::GetTempPath()) ("castle-ap-save-test-" + [guid]::NewGuid().ToString("N"))
$save = "ArchiLan-Test-01"
$saveDir = Join-Path $game "Info/Saves/$save"
$config = Join-Path $game "BepInEx/config/castle_story_archipelago"
$tag = Join-Path $config "save_tags.json"
$mirror = Join-Path $game "Info/Saves/.archipelago-save-tags.json"
function Put-Text([string]$path, [string]$text) {
    [IO.File]::WriteAllText($path, $text, [Text.Encoding]::UTF8)
}
function Expect-Throws([scriptblock]$action, [string]$why) {
    $failed = $false
    try { & $action | Out-Null } catch { $failed = $true }
    if (-not $failed) { throw "Expected refusal: $why" }
}
function Check-Validate([int]$expected) {
    $result = & $script -GameDirectory $game -SaveName $save -ValidateOnly 3>&1 | Out-String
    if ($result -notmatch "Verification reussie : $expected tag") {
        throw "Expected $expected existing tags. Got: $result"
    }
}
try {
    [void](New-Item -Force -ItemType Directory -Path $saveDir)
    [void](New-Item -Force -ItemType Directory -Path $config)
    $heightmap = Join-Path $saveDir "Heightmap.png"
    Put-Text $heightmap "dummy-heightmap-do-not-modify"
    $session = @{
        token = "test-token-001"; seed = "seed-001"; slot_name = "Tester"
        mode = "Invasion"
        campaign = @{
            ready = $true
            missions = @(@{id = "inv-one"; mode = "Invasion"; map_id = "official:test-map"})
        }
    }
    Put-Text (Join-Path $config "session.json") (ConvertTo-Json -InputObject $session -Depth 12)

    # Bug report: PS 5.1 reads empty JsonUtility arrays as null in some
    # contexts; never misclassify a schema-1 empty registry as corruption.
    foreach ($json in @('{"schema":1,"saves":[]}', '{"schema":1,"saves":null}', '{"schema":1}')) {
        Put-Text $tag $json
        if (Test-Path $mirror) { Remove-Item -LiteralPath $mirror -Force }
        Check-Validate 0
        if (([IO.File]::ReadAllText($tag)) -ne $json) {
            throw "ValidateOnly unexpectedly modified primary registry"
        }
    }

    # Do not drop valid entries in mirror just because primary is empty.
    $other = '{"schema":1,"saves":[{"folder":"OtherSave","token":"another-token","seed":"other","slot":"S","mode":"Invasion","mission":"inv-two","map_id":"official:other"}]}'
    Put-Text $mirror $other
    Put-Text $tag '{"schema":1,"saves":[]}'
    Check-Validate 1

    # Malformed / incompatible format must still be rejected and unchanged.
    foreach ($bad in @('{"schema":1,"saves":"corrupt"}', '{"schema":2,"saves":[]}', 'this is not JSON')) {
        Put-Text $tag $bad
        Expect-Throws { & $script -GameDirectory $game -SaveName $save -ValidateOnly } $bad
        if (([IO.File]::ReadAllText($tag)) -ne $bad) {
            throw "Unrecognized registry overwritten"
        }
    }

    # If same folder has conflicting seeds/mission, refuse instead of
    # overwriting primary or its mirror.
    Put-Text $tag '{"schema":1,"saves":[{"folder":"OtherSave","token":"conflicting-token","mission":"inv-different"}]}'
    Expect-Throws { & $script -GameDirectory $game -SaveName $save -ValidateOnly } "conflicting tags"

    # Full write simulation with mocked interactive confirmation.
    Put-Text $tag '{"schema":1,"saves":[]}'
    function global:Read-Host { param([string]$Prompt) return "ASSOCIER" }
    try { & $script -GameDirectory $game -SaveName $save | Out-Null }
    finally { Remove-Item Function:\Read-Host -ErrorAction SilentlyContinue }
    foreach ($path in @($tag, $mirror)) {
        $data = ConvertFrom-Json -InputObject ([IO.File]::ReadAllText($path))
        if (@($data.saves).Count -ne 2) { throw "Registry write dropped earlier save tag: $path" }
        $added = @($data.saves | Where-Object { $_.folder -eq $save })
        if ($added.Count -ne 1 -or $added[0].token -ne "test-token-001" -or
            $added[0].mission -ne "inv-one") { throw "Saved association invalid: $path" }
    }
    # A retry after a partial write must be idempotent, not reject the same
    # seed or duplicate its tag. This reproduces the user's failed Replace.
    Check-Validate 2
    function global:Read-Host { param([string]$Prompt) return "ASSOCIER" }
    try { & $script -GameDirectory $game -SaveName $save | Out-Null }
    finally { Remove-Item Function:\Read-Host -ErrorAction SilentlyContinue }
    foreach ($path in @($tag, $mirror)) {
        $data = ConvertFrom-Json -InputObject ([IO.File]::ReadAllText($path))
        if (@($data.saves).Count -ne 2) { throw "Retry duplicated/lost a tag: $path" }
    }
    if (([IO.File]::ReadAllText($heightmap)) -ne "dummy-heightmap-do-not-modify") {
        throw "Native game save was modified"
    }
    Write-Host "PASS: empty registries, mirrored tags, refusals, confirmed association, native-save safety."
} finally {
    if (Test-Path $game) { Remove-Item -LiteralPath $game -Recurse -Force }
}
