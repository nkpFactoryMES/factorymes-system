param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'package-lock.json') -Raw |
  ConvertFrom-Json
$feed = Join-Path $repoRoot '.packages'
New-Item -ItemType Directory -Force -Path $feed | Out-Null

foreach ($package in $manifest.packages) {
  $target = Join-Path $feed $package.file
  if (Test-Path -LiteralPath $target) {
    $hash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -eq $package.sha256) {
      continue
    }
    throw "Package hash mismatch: $($package.id)"
  }

  $uri = 'https://github.com/nkpFactoryMES/' + $package.repo + '/releases/download/' +
    $package.tag + '/' + $package.file
  $temporary = $target + '.download'
  Invoke-WebRequest -Uri $uri -OutFile $temporary
  $hash = (Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash.ToLowerInvariant()
  if ($hash -ne $package.sha256) {
    throw "Downloaded package hash mismatch: $($package.id)"
  }
  Move-Item -LiteralPath $temporary -Destination $target
  Write-Output "Restored immutable package $($package.id) $($package.version)"
}
