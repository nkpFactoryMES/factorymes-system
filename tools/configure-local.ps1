param(
  [Parameter(Mandatory)]
  [string]$SecretsDirectory,
  [string]$AppSubnet,
  [string]$WebAddress,
  [switch]$DiscoverExistingNetwork
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$secretRoot = [IO.Path]::GetFullPath($SecretsDirectory)
if ($secretRoot.Contains("`n") -or $secretRoot.Contains('"')) {
  throw 'Secret path contains unsupported environment-file characters.'
}
if ($DiscoverExistingNetwork) {
  $network = docker network inspect factorymes_app | ConvertFrom-Json
  if ($LASTEXITCODE -ne 0) {
    throw 'Existing FactoryMES app network was not found.'
  }
  $container = docker inspect factorymes-web-1 | ConvertFrom-Json
  if ($LASTEXITCODE -ne 0) {
    throw 'Existing FactoryMES web container was not found.'
  }
  $AppSubnet = $network[0].IPAM.Config[0].Subnet
  $WebAddress = $container[0].NetworkSettings.Networks.factorymes_app.IPAddress
}

$networkParts = $AppSubnet -split '/'
$networkAddress = $null
$webIp = $null
if ($networkParts.Count -ne 2 -or
    -not [Net.IPAddress]::TryParse($networkParts[0], [ref]$networkAddress) -or
    -not [Net.IPAddress]::TryParse($WebAddress, [ref]$webIp) -or
    $networkAddress.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork -or
    $webIp.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork -or
    [int]$networkParts[1] -lt 8 -or [int]$networkParts[1] -gt 28) {
  throw 'Supply a valid local IPv4 subnet and web proxy address.'
}
$networkBytes = $networkAddress.GetAddressBytes()
$webBytes = $webIp.GetAddressBytes()
$prefix = [int]$networkParts[1]
for ($bit = 0; $bit -lt $prefix; $bit++) {
  $index = [int][Math]::Floor($bit / 8)
  $mask = 1 -shl (7 - ($bit % 8))
  if (($networkBytes[$index] -band $mask) -ne ($webBytes[$index] -band $mask)) {
    throw 'Web proxy address must belong to the configured local subnet.'
  }
}

# 실제 경로·주소는 ignored .env에만 저장하며 stdout에는 값을 표시하지 않는다.
$lines = @(
  ('FACTORYMES_SECRETS_DIR="' + $secretRoot.Replace('\', '/') + '"')
  ('FACTORYMES_APP_SUBNET=' + $AppSubnet)
  ('FACTORYMES_WEB_ADDRESS=' + $WebAddress)
)
Set-Content -LiteralPath (Join-Path $repoRoot '.env') -Value $lines -Encoding utf8
Write-Output 'Configured local-only environment in ignored .env.'
