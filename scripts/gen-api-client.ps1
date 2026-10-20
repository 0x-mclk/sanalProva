<#
.SYNOPSIS
  Backend'in OpenAPI dokümanından frontend tip tanımlarını üretir.

.DESCRIPTION
  1) API'yi geçici olarak 127.0.0.1:5081 üzerinde (Development) başlatır,
  2) /openapi/v1.json dosyasını frontend/openapi/openapi.json olarak kaydeder,
  3) openapi-typescript ile frontend/src/shared/api/schema.d.ts dosyasını üretir,
  4) API'yi durdurur.
  Backend entegrasyon testi, kayıtlı openapi.json ile canlı dokümanın aynı olduğunu doğrular (sözleşme kayması uyarısı).

.EXAMPLE
  ./scripts/gen-api-client.ps1
#>
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'dev-env.ps1')
$root = Split-Path -Parent $PSScriptRoot
$port = 5081
$spec = Join-Path $root 'frontend/openapi/openapi.json'
New-Item -ItemType Directory -Force (Split-Path $spec) | Out-Null

$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:Seed__Enabled = 'false'
$process = Start-Process -FilePath 'dotnet' -PassThru -WindowStyle Hidden `
    -ArgumentList @('run', '--project', (Join-Path $root 'backend/src/SanalProva.Api'), '--no-launch-profile', '--urls', "http://127.0.0.1:$port")
try {
    $ready = $false
    for ($i = 0; $i -lt 60 -and -not $ready; $i++) {
        Start-Sleep -Seconds 1
        try { $ready = (Invoke-WebRequest -UseBasicParsing "http://127.0.0.1:$port/health/live" -TimeoutSec 2).StatusCode -eq 200 } catch { }
    }
    if (-not $ready) { throw "API $port portunda başlamadı." }

    Invoke-WebRequest -UseBasicParsing "http://127.0.0.1:$port/openapi/v1.json" -OutFile $spec
    Push-Location (Join-Path $root 'frontend')
    try { npx openapi-typescript openapi/openapi.json -o src/shared/api/schema.d.ts; if ($LASTEXITCODE -ne 0) { throw 'openapi-typescript başarısız.' } }
    finally { Pop-Location }
    Write-Host "Üretildi: frontend/openapi/openapi.json ve frontend/src/shared/api/schema.d.ts"
}
finally {
    # dotnet run alt sürecini de kapat
    Get-CimInstance Win32_Process -Filter "Name = 'SanalProva.Api.exe'" | Where-Object { $_.CommandLine -like "*$port*" -or $_.ParentProcessId -eq $process.Id } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
}
