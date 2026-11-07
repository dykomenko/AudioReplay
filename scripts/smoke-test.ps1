# End-to-end check on a real audio device: starts the built app with an isolated data dir, plays a system
# sound from a separate process, saves a replay and verifies the per-app track. Needs a working output device.
#   .\scripts\smoke-test.ps1 [-Configuration Debug|Release]
param(
    [string]$Configuration = 'Debug',
    [string]$Sound = 'C:\Windows\Media\Alarm01.wav'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$exe = "$root\artifacts\bin\AudioReplay.App\$($Configuration.ToLower())\AudioReplay.exe"
if (-not (Test-Path $exe)) { throw "Build first: dotnet build -c $Configuration ($exe not found)" }

$base = Join-Path $env:TEMP 'audioreplay-smoke'
if (Test-Path $base) { Remove-Item $base -Recurse -Force }
$data = "$base\data"; $out = "$base\out"
New-Item -ItemType Directory -Force $data, $out | Out-Null
@{ SaveDirectory = $out; ReplaySeconds = 20; ShowNotifications = $false } | ConvertTo-Json | Set-Content "$data\settings.json"

$env:AUDIOREPLAY_DATA_DIR = $data
$env:AUDIOREPLAY_DEBUG = '1'
$app = Start-Process $exe -PassThru
try {
    Start-Sleep 3
    $player = Start-Process pwsh -PassThru -WindowStyle Hidden -ArgumentList '-NoProfile', '-Command', "(New-Object Media.SoundPlayer '$Sound').PlaySync()"
    $player.WaitForExit()
    Start-Sleep 1
    & $exe --save
    Start-Sleep 3

    $clip = Get-ChildItem $out -Directory | Select-Object -First 1
    if (-not $clip) { throw 'FAIL: no replay folder was created' }
    $track = Get-ChildItem $clip.FullName -Filter '*_pwsh.wav'
    if (-not $track) { throw "FAIL: no pwsh track in $($clip.FullName)" }
    $sizes = Get-ChildItem $clip.FullName -Filter *.wav | Select-Object -ExpandProperty Length -Unique
    if (@($sizes).Count -ne 1) { throw 'FAIL: tracks have different lengths' }
    $vol = ffmpeg -hide_banner -i $track.FullName -af volumedetect -f null - 2>&1 | Select-String 'max_volume'
    if ($vol -match 'max_volume: -inf') { throw 'FAIL: pwsh track is silent' }

    Write-Host "OK: $($clip.Name) -> $((Get-ChildItem $clip.FullName).Name -join ', ')"

    # Second press shortly after the first must extend the same clip (merge window), not create a new one.
    $lengthBefore = $track.Length
    $player = Start-Process pwsh -PassThru -WindowStyle Hidden -ArgumentList '-NoProfile', '-Command', "(New-Object Media.SoundPlayer '$Sound').PlaySync()"
    $player.WaitForExit()
    Start-Sleep 1
    & $exe --save
    Start-Sleep 3

    if (@(Get-ChildItem $out -Directory).Count -ne 1) { throw 'FAIL: second save created a new clip instead of extending the first' }
    $track = Get-ChildItem $clip.FullName -Filter '*_pwsh.wav'
    if ($track.Length -le $lengthBefore) { throw 'FAIL: clip did not grow after the second save' }
    $sizes = Get-ChildItem $clip.FullName -Filter *.wav | Select-Object -ExpandProperty Length -Unique
    if (@($sizes).Count -ne 1) { throw 'FAIL: tracks have different lengths after merge' }
    $bursts = @(ffmpeg -hide_banner -i $track.FullName -af 'silencedetect=n=-50dB:d=0.5' -f null - 2>&1 | Select-String 'silence_end').Count
    if ($bursts -lt 2) { throw "FAIL: expected two separate sounds in the merged pwsh track, found $bursts" }
    Write-Host ("OK: merged into one clip, {0:N1} s -> {1:N1} s, sounds found: {2}" -f (($lengthBefore - 44) / 192000), (($track.Length - 44) / 192000), $bursts)
}
finally {
    & $exe --exit
    Start-Sleep 1
    if (-not $app.HasExited) { $app.Kill() }
}
