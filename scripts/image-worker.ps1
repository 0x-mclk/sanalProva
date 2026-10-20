<#
.SYNOPSIS
  Python image-worker servisini yerel geliştirme için 127.0.0.1:8090 üzerinde başlatır.
  İç anahtarı (X-Internal-Key) backend'in user-secrets'ından okur; anahtar ekrana yazılmaz.

.EXAMPLE
  ./scripts/image-worker.ps1
#>
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'dev-env.ps1')
$root = Split-Path -Parent $PSScriptRoot
$worker = Join-Path $root 'image-worker'
$python = Join-Path $worker '.venv/Scripts/python.exe'

if (-not (Test-Path $python)) {
    throw "Sanal ortam yok. Önce: cd image-worker; python -m venv .venv; .venv\Scripts\python -m pip install -e "".[dev]"""
}

$secret = dotnet user-secrets list --project (Join-Path $root 'backend/src/SanalProva.Api') |
    Select-String '^ImageWorker:InternalKey = ' | Select-Object -First 1
if (-not $secret) { throw "ImageWorker:InternalKey user-secrets'ta yok. './scripts/db.ps1 init' çalıştırın." }

$env:IMAGE_WORKER_INTERNAL_KEY = ($secret.ToString() -split ' = ', 2)[1]
$env:IMAGE_WORKER_SEGMENTATION = 'mock'

Push-Location $worker
try { & $python -m uvicorn app.main:app --host 127.0.0.1 --port 8090 }
finally { Pop-Location }
