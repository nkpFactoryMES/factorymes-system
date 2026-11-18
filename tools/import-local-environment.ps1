# 비밀·주소는 ignored .env에서만 읽어 native/브라우저 검증 프로세스에 전달한다.
$systemRoot = Split-Path -Parent $PSScriptRoot
$environmentFile = Join-Path $systemRoot '.env'
if (Test-Path -LiteralPath $environmentFile) {
  foreach ($line in Get-Content -LiteralPath $environmentFile) {
    if ($line -match '^FACTORYMES_SECRETS_DIR=(.*)$') {
      $env:MES_SECRET_DIRECTORY = $Matches[1].Trim('"')
    }
  }
}
if (-not $env:MES_BROWSER_EXECUTABLE -and $IsWindows) {
  $candidate = Join-Path ${env:ProgramFiles(x86)} 'Microsoft/Edge/Application/msedge.exe'
  if (Test-Path -LiteralPath $candidate) {
    $env:MES_BROWSER_EXECUTABLE = $candidate
  }
}
