# Builds single-file executables into ./dist
#   dist\AudioReplay.exe             small (~1 MB), needs the .NET 9 Desktop Runtime
#   dist\AudioReplay-standalone.exe  bundles .NET (~46 MB), runs on a clean Windows
#   .\scripts\publish.ps1                      both
#   .\scripts\publish.ps1 -Variant small       only the small one
param(
    [ValidateSet('both', 'small', 'standalone')]
    [string]$Variant = 'both',
    [string]$Configuration = 'Release'
)

$root = Split-Path $PSScriptRoot -Parent
$project = "$root\src\AudioReplay.App\AudioReplay.App.csproj"
New-Item -ItemType Directory -Force "$root\dist" | Out-Null

function Publish([string]$name, [string[]]$extra) {
    $tmp = "$root\artifacts\publish\$name"
    if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
    # Out-Host keeps the build log out of the function's return value.
    dotnet publish $project -c $Configuration -r win-x64 -p:PublishSingleFile=true -p:DebugType=none @extra -o $tmp | Out-Host
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    return $tmp
}

if ($Variant -in 'both', 'small') {
    $out = Publish 'small' @('--no-self-contained')
    Copy-Item "$out\AudioReplay.exe" "$root\dist\AudioReplay.exe" -Force
}

if ($Variant -in 'both', 'standalone') {
    $out = Publish 'standalone' @('--self-contained', '-p:EnableCompressionInSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true')
    Copy-Item "$out\AudioReplay.exe" "$root\dist\AudioReplay-standalone.exe" -Force
}

Write-Host "`nDone:"
Get-ChildItem "$root\dist" -Filter *.exe | ForEach-Object { '  {0}  ({1:N1} MB)' -f $_.FullName, ($_.Length / 1MB) }
