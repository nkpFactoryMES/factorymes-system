param(
  [Parameter(Mandatory)]
  [ValidateSet(
    'web',
    'mes-backend',
    'integration-gateway',
    'equipment-connector',
    'ai-connector',
    'jobs-worker',
    'equipment-simulator',
    'modbus-simulator',
    'chat-receiver'
  )]
  [string]$Service,
  [Parameter(Mandatory)]
  [string]$Image
)

$ErrorActionPreference = 'Stop'
$systemRoot = Split-Path -Parent $PSScriptRoot
$repoNames = @{
  'web' = 'factorymes-web'
  'mes-backend' = 'factorymes-backend'
  'integration-gateway' = 'factorymes-integration-gateway'
  'equipment-connector' = 'factorymes-equipment-connector'
  'ai-connector' = 'factorymes-ai-connector'
  'jobs-worker' = 'factorymes-jobs-worker'
  'equipment-simulator' = 'factorymes-equipment-simulator'
  'modbus-simulator' = 'factorymes-modbus-simulator'
  'chat-receiver' = 'factorymes-chat-receiver'
}
$localPattern = '^factorymes/' + [regex]::Escape($Service) + ':[A-Za-z0-9._-]+$'
$remotePattern = '^ghcr\.io/nkpfactorymes/' + [regex]::Escape($repoNames[$Service]) +
  '@sha256:[0-9a-f]{64}$'
if ($Image -notmatch $localPattern -and $Image -notmatch $remotePattern) {
  throw 'Image must match this service local image or its reviewed GHCR digest.'
}

function Snapshot {
  $ids = @(docker ps --filter 'label=com.docker.compose.project=factorymes' --format '{{.ID}}')
  if ($LASTEXITCODE -ne 0) {
    throw 'Cannot inspect the existing stack.'
  }
  if ($ids.Count -eq 0) {
    throw 'Start the local stack before deploying one service.'
  }
  $rows = docker inspect @ids | ConvertFrom-Json
  if ($LASTEXITCODE -ne 0) {
    throw 'Cannot inspect stack containers.'
  }
  return @($rows | Where-Object {
    $_.Config.Labels.'com.docker.compose.service' -ne 'tooling'
  } | ForEach-Object {
    [pscustomobject]@{
      service = $_.Config.Labels.'com.docker.compose.service'
      containerId = $_.Id
      imageId = $_.Image
    }
  })
}

Push-Location -LiteralPath $systemRoot
try {
  $before = Snapshot
  if ($Image -match $remotePattern) {
    docker pull $Image
  }
  else {
    docker image inspect $Image --format '{{.Id}}' | Out-Null
  }
  if ($LASTEXITCODE -ne 0) {
    throw 'Reviewed image is unavailable.'
  }

  New-Item -ItemType Directory -Force -Path .deploy | Out-Null
  New-Item -ItemType Directory -Force -Path verification-results | Out-Null
  $before | ConvertTo-Json -Depth 5 |
    Set-Content -LiteralPath (Join-Path '.deploy' ($Service + '-before.json'))
  $override = Join-Path '.deploy' ($Service + '.json')
  @{ services = @{ $Service = @{ image = $Image } } } | ConvertTo-Json -Depth 5 |
    Set-Content -LiteralPath $override -Encoding utf8
  docker compose -f deploy/compose.yaml -f deploy/compose.local-ai.yaml `
    -f deploy/compose.delivery-adapters.yaml -f $override `
    up -d --no-build --no-deps --wait $Service
  if ($LASTEXITCODE -ne 0) {
    throw 'Selected service did not become healthy. Inspect the recorded prior image.'
  }

  $after = Snapshot
  foreach ($other in $before | Where-Object service -ne $Service) {
    $current = $after | Where-Object service -eq $other.service
    if (-not $current -or $current.containerId -ne $other.containerId -or
        $current.imageId -ne $other.imageId) {
      throw "Unexpected deployment impact: $($other.service)"
    }
  }
  [ordered]@{
    service = $Service
    requestedImage = $Image
    before = $before
    after = $after
    unrelatedContainersUnchanged = $true
  } | ConvertTo-Json -Depth 7 |
    Set-Content -LiteralPath (Join-Path 'verification-results' ('deployment-' + $Service + '.json'))
  Write-Output "Updated only $Service; unrelated container images and identities are unchanged."
}
finally {
  Pop-Location
}
