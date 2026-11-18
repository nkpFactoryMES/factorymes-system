# 무료 로컬 모델 설치/실행/검증. DB·모델 볼륨과 업무 이력은 삭제하지 않는다.
param(
  [ValidateSet('Setup', 'Start', 'Verify', 'Provider', 'Browser', 'Fault', 'Gateway')]
  [string]$Mode = 'Verify'
)

$ErrorActionPreference = 'Stop'
$factoryRoot = Split-Path -Parent $PSScriptRoot
$repositoriesRoot = Split-Path -Parent $factoryRoot
$localCompose = @(
  '-f',
  'deploy/compose.yaml',
  '-f',
  'deploy/compose.local-ai.yaml'
)

function Confirm-Exit([string]$step) {
  if ($LASTEXITCODE -ne 0) {
    throw "$step failed"
  }
}

Push-Location -LiteralPath $factoryRoot
try {
  switch ($Mode) {
    'Setup' {
      # 최초 다운로드 때만 외부망을 연결한다. 실패해도 finally에서 추론망만 남긴다.
      try {
        docker compose @localCompose -f deploy/compose.local-ai-download.yaml `
          up -d --no-deps --wait local-ai
        Confirm-Exit 'Download runtime startup'
        docker compose @localCompose -f deploy/compose.local-ai-download.yaml `
          exec -T local-ai ollama pull qwen2.5:1.5b
        Confirm-Exit 'Free local model download'
      }
      finally {
        docker compose @localCompose up -d --no-deps --wait local-ai
        Confirm-Exit 'Remove model download network'
      }
    }
    'Start' {
      docker compose @localCompose up -d --build --wait
      Confirm-Exit 'Local AI stack startup'
    }
    'Verify' {
      docker compose @localCompose `
        -f deploy/compose.collection-verification.yaml `
        -f deploy/compose.analysis-verification.yaml `
        run --rm tooling verify-local-ai
      Confirm-Exit 'GPU maintenance API and Oracle verification'
      docker compose @localCompose exec -T local-ai ollama ps
      Confirm-Exit 'GPU model residency'
    }
    'Provider' {
      # 독립 테스트의 소유 저장소에서 고정 패키지를 준비한다.
      $ownerRoot = Join-Path $repositoriesRoot 'factorymes-ai-connector'
      & (Join-Path $ownerRoot 'build/restore-packages.ps1')
      $project = Join-Path $ownerRoot 'tests/local-ai-provider/LocalProviderTests.csproj'
      dotnet run --project $project -c Release `
        -- verification-results/local-provider-contracts.json
      Confirm-Exit 'Independent provider response and fault checks'
    }
    'Fault' {
      & (Join-Path $PSScriptRoot 'verify-local-ai-fault.ps1')
    }
    'Gateway' {
      # 독립 테스트의 소유 저장소에서 고정 패키지를 준비한다.
      $ownerRoot = Join-Path $repositoriesRoot 'factorymes-integration-gateway'
      & (Join-Path $ownerRoot 'build/restore-packages.ps1')
      $project = Join-Path $ownerRoot 'tests/local-ai-gateway/GatewayContractTests.csproj'
      dotnet run --project $project -c Release `
        -- verification-results/local-ai-gateway-contract.json
      Confirm-Exit 'Independent Gateway contract checks'
    }
    'Browser' {
      . (Join-Path $PSScriptRoot 'import-local-environment.ps1')
      Push-Location -LiteralPath tests/browser
      try {
        node verify-analyses-local.cjs
        Confirm-Exit 'Local AI administrator browser checks'
      }
      finally {
        Pop-Location
      }
    }
  }
}
finally {
  Pop-Location
}



