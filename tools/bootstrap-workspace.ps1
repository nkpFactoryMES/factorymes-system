param(
  [switch]$ExistingOnly
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$workspace = Split-Path -Parent $repoRoot
$manifest = Get-Content -LiteralPath (Join-Path $repoRoot 'build/repositories.lock.json') -Raw |
  ConvertFrom-Json
$resolved = @()

foreach ($repository in $manifest.repositories) {
  $path = Join-Path $workspace $repository.repo
  if (-not (Test-Path -LiteralPath $path)) {
    if ($ExistingOnly) {
      throw "Missing sibling checkout: $($repository.repo)"
    }
    if ($repository.commit -notmatch '^[0-9a-f]{40}$') {
      throw "Remote checkout requires the reviewed commit lock: $($repository.repo)"
    }
    git clone --no-checkout ("https://github.com/" + $manifest.owner + '/' +
      $repository.repo + '.git') $path
    if ($LASTEXITCODE -ne 0) {
      throw "Clone failed: $($repository.repo)"
    }
    git -C $path checkout --detach $repository.commit
    if ($LASTEXITCODE -ne 0) {
      throw "Pinned checkout failed: $($repository.repo)"
    }
  }

  # 기존 개발 checkout을 자동 reset/checkout하지 않는다. 변경 중인 소스를 보존한다.
  & (Join-Path $path 'build/restore-packages.ps1')
  $commit = $null
  if (Test-Path -LiteralPath (Join-Path $path '.git')) {
    $commit = git -C $path rev-parse HEAD
  }
  $resolved += [ordered]@{
    service = $repository.service
    repo = $repository.repo
    expectedCommit = $repository.commit
    actualCommit = $commit
  }
}

$resolved | ConvertTo-Json -Depth 5 |
  Set-Content -LiteralPath (Join-Path $repoRoot '.workspace-lock.json') -Encoding utf8
& (Join-Path $repoRoot 'build/restore-packages.ps1')

