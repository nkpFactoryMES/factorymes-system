param(
  [ValidateSet('Start', 'Stop', 'Status')]
  [string]$Action = 'Status',
  [switch]$WithoutLocalAi
)

$ErrorActionPreference = 'Stop'
$systemRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'import-local-environment.ps1')
$compose = @('-f', 'deploy/compose.yaml')
if (-not $WithoutLocalAi) {
  $compose += @('-f', 'deploy/compose.local-ai.yaml')
}
$compose += @('-f', 'deploy/compose.delivery-adapters.yaml')

Push-Location -LiteralPath $systemRoot
try {
  switch ($Action) {
    'Start' {
      & (Join-Path $PSScriptRoot 'bootstrap-workspace.ps1') -ExistingOnly
      docker compose @compose up -d --build --wait
    }
    'Stop' {
      docker compose @compose stop
    }
    'Status' {
      docker compose @compose ps
    }
  }
  if ($LASTEXITCODE -ne 0) {
    throw "FactoryMES stack $Action failed."
  }
}
finally {
  Pop-Location
}
