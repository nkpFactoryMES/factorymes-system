param(
  [switch]$SkipPackageDownload
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'tools/FactoryMES.Tooling/FactoryMES.Tooling.csproj'
if (-not $SkipPackageDownload) {
  & (Join-Path $repoRoot 'build/restore-packages.ps1')
}

dotnet restore $project --locked-mode
if ($LASTEXITCODE -ne 0) {
  throw 'Locked tooling restore failed.'
}
dotnet build $project -c Release --no-restore
if ($LASTEXITCODE -ne 0) {
  throw 'Tooling build failed.'
}
$env:MES_REPORT_DIRECTORY = Join-Path $repoRoot 'verification-results'
dotnet run --project $project -c Release --no-build -- verify-modbus-protocol
if ($LASTEXITCODE -ne 0) {
  throw 'Modbus protocol smoke verification failed.'
}
