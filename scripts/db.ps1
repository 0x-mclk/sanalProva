<#
.SYNOPSIS
  SQL Server 2025 Express (ornek SANALPROVA) icin yerel gelistirme/test veritabani yardimcisi.
  (Eski PostgreSQL yoneticisinin yerini alir; PostgreSQL verisine ve yedeklerine dokunmaz.)

.DESCRIPTION
  Eylemler:
    status    Salt okunur: ornek, surum ve sanalprova_* veritabanlari (test/e2e/lab icin tablo ve migration sayisi).
    migrate   Bekleyen migration'lari listeler; -Apply ile uygular (hedef onceden var olmali; EF veritabani YARATMAZ/dogrulanir).
    reset     Tum tablolardaki VERIYI siler (sema ve migration gecmisi korunur); -Apply olmadan yalnizca satir sayilarini gosterir.
    script    Idempotent migration betigi uretir (scripts/gen-migration-script.ps1); veritabanina baglanmaz.

  GUVENLIK SINIRLARI (kodla zorlanir):
    * Yalnizca ornek adi SANALPROVA kabul edilir (SQLEXPRESS ve diger ornekler reddedilir).
    * Yalnizca sanalprova_test, sanalprova_e2e ve sanalprova_lab_<8 hex> hedeflenebilir. sanalprova_dev M5'e kadar bos kalir ve REDDEDILIR.
    * migrate/reset varsayilan olarak hicbir sey degistirmez (-Apply gerekir); reset ayrica onay ister (-Confirm:$false ile atlanir).
    * Sunucuya Windows kimligiyle baglanilir; parola/gizli deger yazdirilmaz. Hedef, calistirmadan once sunucudan (DB_NAME, InstanceName) dogrulanir.

.EXAMPLE
  ./scripts/db.ps1 status
  ./scripts/db.ps1 migrate -Database sanalprova_test            # kuru calisma
  ./scripts/db.ps1 migrate -Database sanalprova_test -Apply
  ./scripts/db.ps1 reset   -Database sanalprova_test -Apply
  ./scripts/db.ps1 script
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory, Position = 0)][string]$Action,
    [string]$Database,
    [string]$Instance = '.\SANALPROVA',
    [string]$Output,
    [switch]$Apply
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

if ($Action -in 'init', 'start', 'stop', 'reseed-demo') {
    throw "'$Action' eski PostgreSQL komutudur ve kaldirildi. SQL Server 2025 Express (SANALPROVA) servisi Windows tarafindan yonetilir. Kullanilabilir eylemler: status, migrate, reset, script. Ayrinti: README.md ('Veritabani')."
}
if ($Action -notin 'status', 'migrate', 'reset', 'script') { throw "Bilinmeyen eylem: $Action (status | migrate | reset | script)" }

# Ornek siniri: baglanmadan once reddedilir
if ($Instance -notmatch '(?i)^(\.|\(local\)|localhost|127\.0\.0\.1)\\SANALPROVA$') {
    throw "Yalnizca SANALPROVA ornegi kabul edilir ('$Instance' reddedildi). SQLEXPRESS ve diger ornekler kullanilmaz."
}

function Invoke-Sql([string]$Query, [string]$Db = 'master') {
    $out = sqlcmd -S $Instance -E -C -I -b -d $Db -h -1 -W -s '|' -Q "SET NOCOUNT ON; $Query"
    if ($LASTEXITCODE -ne 0) { throw "sqlcmd basarisiz (veritabani: $Db): $out" }
    # Tek satirli sonuc dizi olarak kalsin (PowerShell tek elemanli diziyi acar): virgul dis diziyi korur
    return ,@($out | Where-Object { $_ -and $_.Trim() })
}

function Assert-Target([string]$Name) {
    if ($Name -eq 'sanalprova_dev') { throw "sanalprova_dev reddedildi: M5'e kadar bos kalir; bu betik ona dokunmaz." }
    if ($Name -notmatch '^sanalprova_(test|e2e|lab_[0-9a-f]{8})$') { throw "Hedef veritabani izin listesinde degil: '$Name' (sanalprova_test | sanalprova_e2e | sanalprova_lab_<8hex>)." }
    $info = (Invoke-Sql "SELECT CAST(SERVERPROPERTY('InstanceName') AS nvarchar(128)) + '|' + ISNULL(CAST(DB_ID(N'$Name') AS nvarchar(20)), 'yok')")[0] -split '\|'
    if ($info[0] -ne 'SANALPROVA') { throw "Baglanilan ornek SANALPROVA degil ($($info[0])). Hicbir sey yapilmadi." }
    if ($info[1] -eq 'yok') { throw "Veritabani yok: $Name. Test icin entegrasyon testleri/provision.sql, E2E icin '. ./scripts/e2e-env.ps1 up' kullanin (bu betik veritabani yaratmaz)." }
    $actual = (Invoke-Sql 'SELECT DB_NAME()' $Name)[0].Trim()
    if ($actual -ne $Name) { throw "Sunucu farkli bir veritabani bildirdi ($actual). Hicbir sey yapilmadi." }
}

switch ($Action) {
    'status' {
        $server = (Invoke-Sql "SELECT CAST(SERVERPROPERTY('InstanceName') AS nvarchar(128)) + '|' + CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(64)) + '|' + CAST(SERVERPROPERTY('Edition') AS nvarchar(128))")[0] -split '\|'
        Write-Host "Ornek: $($server[0])  Surum: $($server[1])  Surum tipi: $($server[2])"
        $rows = Invoke-Sql "SELECT name, state_desc, CAST(is_read_committed_snapshot_on AS int), compatibility_level FROM sys.databases WHERE name LIKE N'sanalprova[_]%' ORDER BY name"
        if (-not $rows) { Write-Host 'sanalprova_* veritabani yok.' }
        foreach ($row in $rows) {
            $c = $row -split '\|'
            $name = $c[0].Trim()
            $line = "  {0,-24} {1,-8} RCSI={2} uyumluluk={3}" -f $name, $c[1].Trim(), $c[2].Trim(), $c[3].Trim()
            if ($name -eq 'sanalprova_dev') { Write-Host "$line  [korumali: baglanilmadi]" }
            elseif ($name -match '^sanalprova_(test|e2e|lab_[0-9a-f]{8})$') {
                $stats = (Invoke-Sql "SELECT CAST((SELECT COUNT(*) FROM sys.tables WHERE is_ms_shipped = 0) AS nvarchar(20)) + '|' + CAST(CASE WHEN OBJECT_ID(N'dbo.__ef_migrations_history', N'U') IS NULL THEN 0 ELSE (SELECT COUNT(*) FROM dbo.__ef_migrations_history) END AS nvarchar(20))" $name)[0] -split '\|'
                Write-Host "$line  tablo=$($stats[0].Trim()) migration=$($stats[1].Trim())"
            }
            else { Write-Host "$line  [izin listesi disi: baglanilmadi]" }
        }
    }
    'migrate' {
        Assert-Target $Database
        . (Join-Path $PSScriptRoot 'dev-env.ps1')
        $connection = "Server=$Instance;Database=$Database;Integrated Security=True;TrustServerCertificate=True;Encrypt=False"
        Push-Location (Join-Path $root 'backend')
        try {
            dotnet tool restore | Out-Null
            $env:ConnectionStrings__Main = $connection
            $efArgs = @('--project', 'src/SanalProva.Infrastructure', '--startup-project', 'src/SanalProva.Api', '--connection', $connection)
            if ($Apply) {
                dotnet ef database update @efArgs
                if ($LASTEXITCODE -ne 0) { throw "migration basarisiz ($LASTEXITCODE)" }
            }
            else {
                Write-Host "Kuru calisma (${Database}): bekleyen migration'lar '(Pending)' ile gosterilir. Uygulamak icin -Apply ekleyin."
                dotnet ef migrations list @efArgs
                if ($LASTEXITCODE -ne 0) { throw "migration listesi alinamadi ($LASTEXITCODE)" }
            }
        }
        finally { Pop-Location }
    }
    'reset' {
        Assert-Target $Database
        $counts = Invoke-Sql "SELECT t.name, SUM(p.rows) FROM sys.tables t JOIN sys.partitions p ON p.object_id = t.object_id AND p.index_id IN (0, 1) WHERE t.is_ms_shipped = 0 AND t.name <> N'__ef_migrations_history' GROUP BY t.name HAVING SUM(p.rows) > 0 ORDER BY t.name" $Database
        $total = 0
        foreach ($row in $counts) { $total += [long](($row -split '\|')[1]) }
        Write-Host "${Database}: veri iceren tablo=$($counts.Count), toplam satir=$total (sema ve migration gecmisi korunur)."
        if (-not $Apply) { Write-Host 'Kuru calisma: hicbir sey silinmedi. Silmek icin -Apply ekleyin.'; break }
        if ($PSCmdlet.ShouldProcess($Database, 'Tum tablolardaki veriyi sil')) {
            $reset = @'
SET XACT_ABORT ON;
BEGIN TRANSACTION;
DECLARE @a nvarchar(max) = N'', @b nvarchar(max) = N'', @c nvarchar(max) = N'';
SELECT @a += N'ALTER TABLE ' + QUOTENAME(SCHEMA_NAME(schema_id)) + N'.' + QUOTENAME(name) + N' NOCHECK CONSTRAINT ALL;' FROM sys.tables WHERE is_ms_shipped = 0 AND name <> N'__ef_migrations_history';
SELECT @b += N'DELETE FROM ' + QUOTENAME(SCHEMA_NAME(schema_id)) + N'.' + QUOTENAME(name) + N';' FROM sys.tables WHERE is_ms_shipped = 0 AND name <> N'__ef_migrations_history';
SELECT @c += N'ALTER TABLE ' + QUOTENAME(SCHEMA_NAME(schema_id)) + N'.' + QUOTENAME(name) + N' WITH CHECK CHECK CONSTRAINT ALL;' FROM sys.tables WHERE is_ms_shipped = 0 AND name <> N'__ef_migrations_history';
EXEC sys.sp_executesql @a;
EXEC sys.sp_executesql @b;
EXEC sys.sp_executesql @c;
COMMIT TRANSACTION;
'@
            Invoke-Sql $reset $Database | Out-Null
            $left = Invoke-Sql "SELECT COUNT(*) FROM sys.tables t JOIN sys.partitions p ON p.object_id = t.object_id AND p.index_id IN (0, 1) WHERE t.is_ms_shipped = 0 AND t.name <> N'__ef_migrations_history' AND p.rows > 0" $Database
            Write-Host "Tamam: ${Database} verisi silindi (kalan dolu tablo: $($left[0].Trim()))."
        }
    }
    'script' {
        if ($Output) { & (Join-Path $PSScriptRoot 'gen-migration-script.ps1') -Output $Output } else { & (Join-Path $PSScriptRoot 'gen-migration-script.ps1') }
    }
}
