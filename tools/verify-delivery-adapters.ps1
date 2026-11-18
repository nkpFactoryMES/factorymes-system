# SMTP/HTTP는 내부 로컬 수신기에만 보낸다. 실제 이메일·Slack 서비스 발송은 없다.
param(
  [ValidateSet('Verify', 'Browser', 'Provider', 'Receiver')]
  [string]$Mode = 'Verify'
)

$ErrorActionPreference = 'Stop'
$factoryRoot = Split-Path -Parent $PSScriptRoot
$repositoriesRoot = Split-Path -Parent $factoryRoot
$compose = @(
  '-f',
  'deploy/compose.yaml',
  '-f',
  'deploy/compose.local-ai.yaml',
  '-f',
  'deploy/compose.delivery-adapters.yaml',
  '-f',
  'deploy/compose.delivery-adapters-verification.yaml'
)

function Confirm-Exit([string]$step) {
  if ($LASTEXITCODE -ne 0) {
    throw "$step failed"
  }
}

Push-Location -LiteralPath $factoryRoot
try {
  New-Item -ItemType Directory -Force -Path verification-results | Out-Null
  switch ($Mode) {
    'Verify' {
      docker compose @compose build tooling
      Confirm-Exit 'Build current verification image'
      docker compose @compose run --rm tooling verify-delivery-adapters
      Confirm-Exit 'Actual SMTP HTTP and Oracle verification'
    }
    'Browser' {
      . (Join-Path $PSScriptRoot 'import-local-environment.ps1')
      Push-Location -LiteralPath tests/browser
      try {
        node verify-delivery-adapters.cjs
        Confirm-Exit 'Administrator local delivery browser verification'
      }
      finally {
        Pop-Location
      }
    }
    'Provider' {
      # 독립 테스트의 소유 저장소에서 고정 패키지를 준비한다.
      $ownerRoot = Join-Path $repositoriesRoot 'factorymes-backend'
      & (Join-Path $ownerRoot 'build/restore-packages.ps1')
      $project = Join-Path $ownerRoot 'tests/delivery-adapter-provider/DeliveryProviderTests.csproj'
      dotnet run --project $project `
        -c Release -- verification-results/delivery-provider-contracts.json
      Confirm-Exit 'Independent provider contract verification'
    }
    'Receiver' {
      # 독립 테스트의 소유 저장소에서 고정 패키지를 준비한다.
      $ownerRoot = Join-Path $repositoriesRoot 'factorymes-chat-receiver'
      & (Join-Path $ownerRoot 'build/restore-packages.ps1')
      $project = Join-Path $ownerRoot 'tests/delivery-capture/DeliveryCaptureTests.csproj'
      dotnet run --project $project `
        -c Release -- verification-results/delivery-capture-contracts.json
      Confirm-Exit 'Independent receiver wire and journal verification'
    }
  }
}
finally {
  Pop-Location
}
