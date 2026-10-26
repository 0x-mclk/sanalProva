# Migration gecmisi sertlestirmesini (DENY INSERT/UPDATE/DELETE ... sanalprova_app) MEVCUT bir dev/test veritabanina uygular.
# GUVENLI VARSAYILAN: -Apply verilmezse hicbir sey DEGISTIRMEZ; yalnizca durumu okur (meta veri) ve yapilacak islemi gosterir.
# -Apply ile bile calistirmadan once onay ister (-Confirm:$false ile atlanir; yalnizca bilincli kullanin).
# Kullanim:
#   ./scripts/apply-history-hardening.ps1 -Database sanalprova_test            # kuru calisma
#   ./scripts/apply-history-hardening.ps1 -Database sanalprova_test -Apply     # uygular (onay sorar)
# Yalnizca SANALPROVA ornegi ve sanalprova_dev / sanalprova_test kabul edilir; SQLEXPRESS ornekleri ve diger veritabanlari reddedilir.
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)][ValidateSet('sanalprova_dev', 'sanalprova_test')][string]$Database,
    [string]$Instance = '.\SANALPROVA',
    [switch]$Apply
)
$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot 'mssql/harden-migration-history.sql'
if (-not (Test-Path $script)) { throw "Betik bulunamadi: $script" }

function Invoke-Sql([string]$Query, [string]$Db = 'master') {
    $out = sqlcmd -S $Instance -E -C -I -b -d $Db -h -1 -W -s '|' -Q "SET NOCOUNT ON; $Query"
    if ($LASTEXITCODE -ne 0) { throw "sqlcmd basarisiz: $out" }
    return $out
}

$info = (Invoke-Sql "SELECT CAST(SERVERPROPERTY('InstanceName') AS nvarchar(128)), IS_SRVROLEMEMBER('sysadmin'), DB_ID(N'$Database')") -split '\|'
if ($info[0].Trim() -ne 'SANALPROVA') { throw "Hedef ornek SANALPROVA degil ($($info[0])). Hicbir sey yapilmadi." }
if ($info[1].Trim() -ne '1') { throw 'Bu islem sunucu yonetici yetkisi ister.' }
if ($info[2].Trim() -eq 'NULL') { throw "Veritabani yok: $Database" }

$state = "SELECT COALESCE((SELECT STRING_AGG(p.permission_name + ':' + p.state_desc, ',') FROM sys.database_permissions p JOIN sys.database_principals pr ON pr.principal_id = p.grantee_principal_id WHERE p.major_id = OBJECT_ID(N'dbo.__ef_migrations_history') AND pr.name = N'sanalprova_app'), N'(acik izin yok)')"
$exists = (Invoke-Sql "SELECT CASE WHEN OBJECT_ID(N'dbo.__ef_migrations_history', N'U') IS NULL THEN N'yok' ELSE N'var' END" $Database).Trim()
Write-Host "Veritabani: $Database | gecmis tablosu: $exists"
if ($exists -eq 'var') { Write-Host "Mevcut sanalprova_app izinleri: $((Invoke-Sql $state $Database).Trim())" }

if (-not $Apply) {
    Write-Host "KURU CALISMA: hicbir sey degistirilmedi. Uygulamak icin ayni komuta -Apply ekleyin."
    Write-Host "Calistirilacak betik: $script  (DENY INSERT, UPDATE, DELETE ON dbo.__ef_migrations_history TO sanalprova_app)"
    return
}
if ($exists -ne 'var') { throw 'Gecmis tablosu yok: once migration uygulanmali.' }
if ($PSCmdlet.ShouldProcess($Database, 'sanalprova_app icin __ef_migrations_history uzerinde INSERT/UPDATE/DELETE reddedilecek')) {
    sqlcmd -S $Instance -E -C -I -b -v "Database=$Database" -i $script
    if ($LASTEXITCODE -ne 0) { throw "Betik basarisiz ($LASTEXITCODE)" }
    Write-Host "Sonraki durum: $((Invoke-Sql $state $Database).Trim())"
}
