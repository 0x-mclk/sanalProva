# Docker Compose icin rastgele parolalarla .env uretir (Windows). Degerler ASLA yazdirilmaz; .env git'e girmez.
# Varsa .env'i EZMEZ (-Force ile yeniden uretir; mevcut birimlerdeki veritabani parolalariyla ayrisir: onceki `docker compose down -v` gerekir).
# Kullanim: ./scripts/compose-env.ps1 [-Force]
param([switch]$Force)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$target = Join-Path $root '.env'
if ((Test-Path $target) -and -not $Force) { Write-Host '.env zaten var (ezilmedi).'; return }
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
function New-Secret([int]$bytes = 16) {
    $b = New-Object byte[] $bytes
    $rng.GetBytes($b)
    return 'Aa1' + (($b | ForEach-Object { $_.ToString('x2') }) -join '')   # Aa1 oneki: buyuk+kucuk+rakam karmasiklik kurali
}
$lines = @(
    "MSSQL_SA_PASSWORD=$(New-Secret 16)",
    "SP_MIGRATOR_PW=$(New-Secret 16)",
    "SP_APP_PW=$(New-Secret 16)",
    "STORAGE_SIGNING_KEY=$(New-Secret 24)",
    "SETUP_TOKEN_KEY=$(New-Secret 24)",
    "IMAGE_WORKER_KEY=$(New-Secret 16)"
)
$rng.Dispose()
[IO.File]::WriteAllLines($target, $lines, (New-Object System.Text.UTF8Encoding($false)))
Write-Host '.env olusturuldu (degerler yazdirilmadi).'
