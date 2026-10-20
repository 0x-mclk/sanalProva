<#
.SYNOPSIS
  Yalnızca bu projeye ait yerel geliştirme PostgreSQL'ini yönetir.

.DESCRIPTION
  Taşınabilir PostgreSQL ikili dosyalarını (.local/pgsql) ve veri klasörünü (.local/pgdata) kullanır.
  Windows servisi kurmaz, yönetici izni gerektirmez. Sunucu yalnızca 127.0.0.1:5433 üzerinde dinler.

  init   : Veri klasörünü oluşturur, rastgele parolalarla 'sanalprova_app' rolünü ve
           'sanalprova_dev' + 'sanalprova_test' veritabanlarını açar, bağlantı dizelerini
           dotnet user-secrets'a yazar (kaynak koda hiçbir parola girmez).
  start  : Sunucuyu başlatır.
  stop   : Sunucuyu durdurur.
  status : Durumu gösterir.
  reseed-demo : Geliştirme veritabanındaki demo katalog, prova oturumları ve görsel dosyalarını siler;
           API bir sonraki başlatmada demo kataloğu yeniden yükler. YALNIZCA sanalprova_dev'e dokunur.

.EXAMPLE
  ./scripts/db.ps1 init
  ./scripts/db.ps1 start
#>
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('init', 'start', 'stop', 'status', 'reseed-demo')]
    [string]$Command
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'dev-env.ps1')
$root = Split-Path -Parent $PSScriptRoot
$bin = Join-Path $root '.local/pgsql/bin'
$data = Join-Path $root '.local/pgdata'
$log = Join-Path $root '.local/postgres.log'
$port = 5433
$apiProject = Join-Path $root 'backend/src/SanalProva.Api'

if (-not (Test-Path (Join-Path $bin 'pg_ctl.exe'))) {
    throw "PostgreSQL ikili dosyaları bulunamadı: $bin. README'deki 'Yerel PostgreSQL' adımlarına bakın."
}

function New-Secret([int]$bytes = 24) {
    $buffer = New-Object byte[] $bytes
    # Windows PowerShell 5.1 (.NET Framework) uyumlu kriptografik rastgele üretici
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($buffer) } finally { $rng.Dispose() }
    # URL ve bağlantı dizesi için güvenli karakterler
    return [Convert]::ToBase64String($buffer).TrimEnd('=').Replace('+', 'A').Replace('/', 'B')
}

function Invoke-Psql([string]$sql, [string]$superPassword, [string]$database = 'postgres') {
    $env:PGPASSWORD = $superPassword
    try {
        & (Join-Path $bin 'psql.exe') -h 127.0.0.1 -p $port -U postgres -d $database -v ON_ERROR_STOP=1 -q -c $sql
        if ($LASTEXITCODE -ne 0) { throw "psql başarısız oldu." }
    }
    finally { Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue }
}

function Start-Db {
    & (Join-Path $bin 'pg_ctl.exe') status -D $data *> $null
    if ($LASTEXITCODE -eq 0) { Write-Host "PostgreSQL zaten çalışıyor (port $port)."; return }
    # Start-Process: postgres alt süreci bu oturumun çıktı akışını devralmasın (aksi halde komut asılı kalır)
    $process = Start-Process -FilePath (Join-Path $bin 'pg_ctl.exe') -PassThru -WindowStyle Hidden `
        -ArgumentList @('start', '-D', "`"$data`"", '-l', "`"$log`"", '-w', '-o', "`"-p $port -c listen_addresses=127.0.0.1`"")
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw "PostgreSQL başlatılamadı. Log: $log" }
}

switch ($Command) {
    'init' {
        if (Test-Path $data) { throw "Veri klasörü zaten var: $data. Sıfırdan kurmak için önce durdurup klasörü elle silin." }

        $superPassword = New-Secret
        $appPassword = New-Secret
        $pwFile = Join-Path $env:TEMP "sanalprova-pg-$([guid]::NewGuid()).txt"
        try {
            Set-Content -Path $pwFile -Value $superPassword -NoNewline -Encoding ascii
            & (Join-Path $bin 'initdb.exe') -D $data -U postgres --pwfile=$pwFile --auth=scram-sha-256 `
                --encoding=UTF8 --locale=C --lc-messages=C --locale-provider=builtin --builtin-locale=C.UTF-8 | Out-Host
            if ($LASTEXITCODE -ne 0) { throw "initdb başarısız oldu." }
        }
        finally { Remove-Item $pwFile -ErrorAction SilentlyContinue }

        Start-Db
        Invoke-Psql "CREATE ROLE sanalprova_app LOGIN PASSWORD '$appPassword';" $superPassword
        Invoke-Psql "CREATE DATABASE sanalprova_dev OWNER sanalprova_app;" $superPassword
        Invoke-Psql "CREATE DATABASE sanalprova_test OWNER sanalprova_app;" $superPassword

        $common = "Host=127.0.0.1;Port=$port;Username=sanalprova_app;Password=$appPassword"
        $signingKey = New-Secret 32
        $workerKey = New-Secret 24
        $devPassword = "Prova-" + (New-Secret 9)

        Push-Location $apiProject
        try {
            dotnet user-secrets set "ConnectionStrings:Main" "$common;Database=sanalprova_dev" | Out-Null
            dotnet user-secrets set "ConnectionStrings:Test" "$common;Database=sanalprova_test" | Out-Null
            dotnet user-secrets set "Storage:SigningKey" $signingKey | Out-Null
            dotnet user-secrets set "ImageWorker:InternalKey" $workerKey | Out-Null
            dotnet user-secrets set "Seed:DevPassword" $devPassword | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "dotnet user-secrets başarısız oldu." }
        }
        finally { Pop-Location }

        $superFile = Join-Path $root '.local/postgres-superuser.txt'
        Set-Content -Path $superFile -Value $superPassword -NoNewline -Encoding ascii

        Write-Host ""
        Write-Host "Yerel geliştirme veritabanı hazır:"
        Write-Host "  Sunucu      : 127.0.0.1:$port (yalnızca bu makine)"
        Write-Host "  Veritabanı  : sanalprova_dev, sanalprova_test (sahip: sanalprova_app)"
        Write-Host "  Gizli değer : dotnet user-secrets (backend/src/SanalProva.Api)"
        Write-Host "  Süper kullanıcı parolası: $superFile (git'e girmez)"
        Write-Host "  Demo personel parolası  : $devPassword"
    }
    'start' { Start-Db }
    'stop' { & (Join-Path $bin 'pg_ctl.exe') stop -D $data -m fast }
    'status' { & (Join-Path $bin 'pg_ctl.exe') status -D $data }
    'reseed-demo' {
        Start-Db
        $superPassword = (Get-Content (Join-Path $root '.local/postgres-superuser.txt') -Raw).Trim()
        # Müşteri oturumları (DB cascade: fotoğraf, seçim, iş, sonuç, onay) ve katalog; kategoriler/kullanıcılar kalır
        Invoke-Psql "DELETE FROM tryon_sessions; DELETE FROM outfit_set_components; DELETE FROM product_images; DELETE FROM product_variants; DELETE FROM products; DELETE FROM stored_files;" $superPassword 'sanalprova_dev'
        $storage = Join-Path $root 'storage'
        if (Test-Path $storage) { Get-ChildItem $storage -Directory | Remove-Item -Recurse -Force -Confirm:$false }
        Write-Host "Demo katalog ve oturumlar silindi. API'yi yeniden başlatınca demo katalog yeniden yüklenir."
    }
}
