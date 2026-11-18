# Local integration verification. Temporarily stops Mock gateway/worker and restarts Backend.
# Existing Oracle data/secrets are retained; finally restores gateway and worker.
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $projectRoot
function Confirm-Exit([string]$Step) {
    if ($LASTEXITCODE -ne 0) { throw "$Step failed" }
}
$stagePath = (Join-Path $projectRoot 'verification-results/analysis-recovery-stage.json')
$ackPath = (Join-Path $projectRoot 'verification-results/analysis-recovery-ack.txt')
$stdoutPath = (Join-Path $projectRoot 'verification-results/analysis-recovery-run.log')
$stderrPath = (Join-Path $projectRoot 'verification-results/analysis-recovery-error.log')
Set-Content -LiteralPath $stagePath -Value '{"stage":"starting"}'
Set-Content -LiteralPath $ackPath -Value '' -NoNewline
$process = Start-Process -FilePath docker -ArgumentList @(
  'compose', '-f', 'deploy/compose.yaml', '-f', 'deploy/compose.analysis-verification.yaml',
  'run', '--rm', '--name', 'factorymes-recovery-verification', 'tooling', 'verify-analysis-recovery'
) -WorkingDirectory $projectRoot -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath
$handled = ''
$until = (Get-Date).AddMinutes(6)
try {
  while (-not $process.HasExited -and (Get-Date) -lt $until) {
    $phase = $null
    try { $phase = (Get-Content -LiteralPath $stagePath -Raw | ConvertFrom-Json).stage } catch { }
    if ($phase -and $phase -ne $handled -and $phase -ne 'starting') {
      switch ($phase) {
        'stop-first' { docker compose -f deploy/compose.yaml stop integration-gateway }
        'restart-recover' {
          docker compose -f deploy/compose.yaml start integration-gateway
          docker compose -f deploy/compose.yaml up -d --force-recreate --no-deps --no-build --wait mes-backend
        }
        'stop-exhaust' { docker compose -f deploy/compose.yaml stop integration-gateway }
        'restore-exhaust' { docker compose -f deploy/compose.yaml start integration-gateway }
        'pause-worker' { docker compose -f deploy/compose.yaml stop jobs-worker }
        'done' { }
        default { throw "Unknown recovery test phase $phase" }
      }
      Confirm-Exit "Recovery controller $phase"
      Set-Content -LiteralPath $ackPath -Value $phase -NoNewline
      $handled = $phase
    }
    Start-Sleep -Milliseconds 150
    $process.Refresh()
  }
  if (-not $process.HasExited) { throw 'Recovery verification timeout; inspect logs' }
  $process.WaitForExit()
  Get-Content -LiteralPath $stdoutPath
  Get-Content -LiteralPath $stderrPath
  if ($process.ExitCode -ne 0) { throw 'Recovery tooling failed' }
} finally {
  if (-not $process.HasExited) {
    docker stop factorymes-recovery-verification
    $process.WaitForExit(10000) | Out-Null
  }
  docker compose -f deploy/compose.yaml start integration-gateway jobs-worker
  Confirm-Exit 'Restore local gateway and scheduler after controlled fault test'
}
