# Builds the netplay tool in Release and stages the two shippable DLLs in dist/
# (BizHawkNetplay.Tool.dll + BizHawkNetplay.Core.dll) for upload to a GitHub Release.
# dist/ is gitignored: binaries live on Releases, not in the repo tree.
#
#   .\build-dist.ps1                       # just build + stage into dist/
#   .\build-dist.ps1 -Tag v0.4.0           # also create/upload a GitHub Release (needs gh CLI)
#
# BizHawk assemblies are only needed to compile against; nothing from the install is
# copied into dist. Override the install location with -BizHawkHome if it differs.
param(
    [string]$BizHawkHome = "",
    [string]$Tag = "",
    [string]$Notes = "Prebuilt BizHawk Netplay external tool. See the README for install steps."
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$dist = Join-Path $root "dist"
$relOut = Join-Path $root "src\BizHawkNetplay.Tool\bin\Release"

$buildArgs = @("build", (Join-Path $root "src\BizHawkNetplay.Tool"), "-c", "Release", "-p:DeployToExternalTools=false")
if ($BizHawkHome -ne "") { $buildArgs += "-p:BizHawkHome=$BizHawkHome" }

Write-Host "Building Release..." -ForegroundColor Cyan
& dotnet @buildArgs
if ($LASTEXITCODE -ne 0) { throw "build failed" }

New-Item -ItemType Directory -Force -Path $dist | Out-Null
$dlls = @("BizHawkNetplay.Tool.dll", "BizHawkNetplay.Core.dll")
foreach ($dll in $dlls) {
    Copy-Item (Join-Path $relOut $dll) (Join-Path $dist $dll) -Force
    Write-Host "  -> dist\$dll" -ForegroundColor Green
}
Write-Host "dist/ staged." -ForegroundColor Cyan

if ($Tag -ne "") {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        throw "gh CLI not found. Install it, or upload dist\*.dll to a Release manually."
    }
    $assets = $dlls | ForEach-Object { Join-Path $dist $_ }
    Write-Host "Creating GitHub Release $Tag..." -ForegroundColor Cyan
    & gh release create $Tag @assets --title $Tag --notes $Notes
    if ($LASTEXITCODE -ne 0) { throw "gh release create failed" }
    Write-Host "Published release $Tag with the two DLLs attached." -ForegroundColor Green
}
else {
    Write-Host "To publish: .\build-dist.ps1 -Tag vX.Y.Z   (or upload dist\*.dll to a Release by hand)" -ForegroundColor DarkGray
}
