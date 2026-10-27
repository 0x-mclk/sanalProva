# Playwright E2E icin izole ortam: SANALPROVA ornegindeki ATILABILIR `sanalprova_e2e` veritabani + gecici dosya deposu.
# sanalprova_dev / sanalprova_test / PostgreSQL / yedekler / gercek storage'a DOKUNMAZ. Yalnizca adi `sanalprova_e2e` olan veritabanini olusturur ve siler.
# Kullanim (PowerShell, repo kokunden):
#   . ./scripts/e2e-env.ps1 up      # DB'yi (yeniden) kurar, migration uygular, ortam degiskenlerini bu oturuma yazar
#   cd frontend; npm run test:e2e
#   . ./scripts/e2e-env.ps1 down    # DB'yi siler, gecici depoyu temizler
# Yonetici baglantisi Windows kimligiyle yapilir (veritabani olusturma sunucu yetkisi ister); API ayni atilabilir veritabanina baglanir.
param([Parameter(Mandatory = $true)][ValidateSet('up', 'down')][string]$Action)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$database = 'sanalprova_e2e'
$server = '.\SANALPROVA'
$connection = "Server=$server;Database=$database;Integrated Security=True;TrustServerCertificate=True;Encrypt=False"
$admin = "Server=$server;Integrated Security=True;TrustServerCertificate=True;Encrypt=False"

function Invoke-Admin([string]$sql) {
    $c = New-Object System.Data.SqlClient.SqlConnection $admin
    $c.Open()
    try { $cmd = $c.CreateCommand(); $cmd.CommandText = $sql; $cmd.CommandTimeout = 120; [void]$cmd.ExecuteNonQuery() } finally { $c.Dispose() }
}

if ($Action -eq 'up') {
    . (Join-Path $PSScriptRoot 'dev-env.ps1')
    Invoke-Admin "IF DB_ID(N'$database') IS NOT NULL BEGIN ALTER DATABASE [$database] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$database]; END"
    Invoke-Admin "CREATE DATABASE [$database] COLLATE Latin1_General_100_CI_AS_SC"
    Invoke-Admin "ALTER DATABASE [$database] SET COMPATIBILITY_LEVEL = 160; ALTER DATABASE [$database] SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE; ALTER DATABASE [$database] SET RECOVERY SIMPLE"
    Push-Location (Join-Path $root 'backend')
    try {
        $env:ConnectionStrings__Main = $connection
        dotnet ef database update --project src/SanalProva.Infrastructure --startup-project src/SanalProva.Api --connection $connection
        if ($LASTEXITCODE -ne 0) { throw "migration basarisiz ($LASTEXITCODE)" }
    } finally { Pop-Location }

    $bytes = New-Object byte[] 24
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create(); $rng.GetBytes($bytes); $rng.Dispose()
    $secret = [Convert]::ToBase64String($bytes).Replace('+', 'a').Replace('/', 'b').Replace('=', 'c')
    $storage = Join-Path ([IO.Path]::GetTempPath()) ("sanalprova-e2e\" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $storage | Out-Null

    # Playwright config bu degiskenleri API surecine aktarir; gizli degerler yalnizca bu oturumdadir (kaynaga/rapora yazilmaz)
    $env:SANALPROVA_E2E_DB = $connection
    $env:SANALPROVA_E2E_STORAGE = $storage
    $env:E2E_PASSWORD = "Aa1!" + $secret
    $env:SANALPROVA_E2E_SIGNING_KEY = $secret + $secret
    Write-Host "E2E ortami hazir: $database, depo $storage"
}
else {
    if ($env:SANALPROVA_E2E_STORAGE -and $env:SANALPROVA_E2E_STORAGE -like '*sanalprova-e2e*') { Remove-Item -Recurse -Force $env:SANALPROVA_E2E_STORAGE -ErrorAction SilentlyContinue }
    Invoke-Admin "IF DB_ID(N'$database') IS NOT NULL BEGIN ALTER DATABASE [$database] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$database]; END"
    foreach ($n in 'SANALPROVA_E2E_DB', 'SANALPROVA_E2E_STORAGE', 'E2E_PASSWORD', 'SANALPROVA_E2E_SIGNING_KEY') { Remove-Item "Env:$n" -ErrorAction SilentlyContinue }
    Write-Host "E2E ortami silindi."
}
