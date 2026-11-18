# 실제 로컬 Mock HTTP 및 signed 수집 API를 검증한다. 기존 DB/이력/계정은 보존한다.
# 선행: 최신 이미지를 빌드하고 tooling upgrade-auto-production 후 서비스를 기동한다.
param()

$ErrorActionPreference = 'Stop'
$factoryRoot = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $factoryRoot
try {
  docker compose `
    -f deploy/compose.yaml `
    -f deploy/compose.collection-verification.yaml `
    -f deploy/compose.analysis-verification.yaml `
    run --rm tooling verify-auto-production

  if ($LASTEXITCODE -ne 0) {
    throw 'AUTO verification failed; inspect the checks before retrying.'
  }
}
finally {
  Pop-Location
}
