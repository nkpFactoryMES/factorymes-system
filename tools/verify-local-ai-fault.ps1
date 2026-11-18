# 로컬 모델 서버만 중지·재기동한다. finally에서 복구하고 DB/모델 볼륨은 보존한다.
$ErrorActionPreference = 'Stop'
$factoryRoot = Split-Path -Parent $PSScriptRoot

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
  docker compose @localCompose build tooling *> verification-results/local-ai-tooling-build.log
  $localBuildCode = $LASTEXITCODE
  Get-Content verification-results/local-ai-tooling-build.log -Tail 6
  if ($localBuildCode -ne 0) { throw 'Final LOCAL tooling build failed' }
  Set-Content verification-results/local-ai-stage.json '{"stage":"starting"}'
  Set-Content verification-results/local-ai-ack.txt '' -NoNewline
  $localRun = Start-Process -FilePath docker -ArgumentList @(
    'compose',
    '-f',
    'deploy/compose.yaml',
    '-f',
    'deploy/compose.local-ai.yaml',
    '-f',
    'deploy/compose.collection-verification.yaml',
    '-f',
    'deploy/compose.analysis-verification.yaml',
    'run',
    '--rm',
    '--name',
    'factorymes-local-ai-fault',
    '-e',
    'MES_LOCAL_AI_FAULT_VERIFICATION=1',
    'tooling',
    'verify-local-ai'
  ) -WindowStyle Hidden -PassThru `
    -RedirectStandardOutput "$PWD/verification-results/local-ai-fault.log" `
    -RedirectStandardError "$PWD/verification-results/local-ai-fault-error.log"
  $localHandled = ''
  $localUntil = (Get-Date).AddMinutes(8)
  try {
    while (-not $localRun.HasExited -and (Get-Date) -lt $localUntil) {
      $localPhase = $null
      try {
        $localPhase = (Get-Content verification-results/local-ai-stage.json -Raw | ConvertFrom-Json).stage
      }
      catch { }
      if ($localPhase -and $localPhase -ne 'starting' -and $localPhase -ne $localHandled) {
        switch ($localPhase) {
          'stop-local-ai' {
            docker compose @localCompose stop local-ai
          }
          'restart-local-ai' {
            docker compose @localCompose up -d --no-deps --no-build local-ai
          }
          default { throw 'Unknown local AI verification phase' }
        }
        Confirm-Exit "Local inference controller $localPhase"
        Set-Content verification-results/local-ai-ack.txt $localPhase -NoNewline
        $localHandled = $localPhase
      }
      Start-Sleep -Milliseconds 250
    }
    if (-not $localRun.HasExited) { throw 'Local AI fault verification timed out' }
    $localRun.WaitForExit()
    Get-Content verification-results/local-ai-fault.log -Tail 20
    Get-Content verification-results/local-ai-fault-error.log -Tail 8
    if ($localRun.ExitCode -ne 0) { throw 'Local AI fault verification failed' }
  }
  finally {
    if (-not $localRun.HasExited) { docker stop factorymes-local-ai-fault }
    docker compose @localCompose up -d --no-deps --no-build --wait local-ai
    Confirm-Exit 'Restore local GPU runtime'
  }
}
finally {
  Pop-Location
}


