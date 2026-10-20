<#
.SYNOPSIS
  Geçerli PowerShell oturumunda kullanıcı klasörüne kurulan .NET 10 SDK'yı öne alır.
  Kalıcı bir sistem ayarı değiştirmez.

.EXAMPLE
  . ./scripts/dev-env.ps1
#>
$localDotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'
if (Test-Path (Join-Path $localDotnet 'dotnet.exe')) {
    $env:DOTNET_ROOT = $localDotnet
    if (-not ($env:PATH -split ';' | Where-Object { $_ -eq $localDotnet })) {
        $env:PATH = "$localDotnet;$env:PATH"
    }
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
