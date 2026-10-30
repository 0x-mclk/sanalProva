# Sertlestirmeleri MEVCUT bir dev/test veritabanina uygular (hedef kullanici: sanalprova_app): History = migration gecmisi (DENY INSERT/UPDATE/DELETE),
# AppendOnly = yalnizca-ekleme tablolari (ai_cost_reconciliations, organization_agreement_acceptances; DENY UPDATE/DELETE), All = ikisi.
# GUVENLI VARSAYILAN: -Apply verilmezse hicbir sey DEGISTIRMEZ; yalnizca durumu okur (meta veri) ve yapilacak islemi gosterir.
# -Apply ile bile calistirmadan once onay ister (-Confirm:$false ile atlanir; yalnizca bilincli kullanin).
# Kullanim:
#   ./scripts/apply-history-hardening.ps1 -Database sanalprova_test                          # kuru calisma (History)
#   ./scripts/apply-history-hardening.ps1 -Database sanalprova_test -Hardening AppendOnly   # kuru calisma
#   ./scripts/apply-history-hardening.ps1 -Database sanalprova_test -Hardening All -Apply   # uygular (onay sorar)
# Yalnizca SANALPROVA ornegi ve sanalprova_dev / sanalprova_test kabul edilir; SQLEXPRESS ornekleri ve diger veritabanlari reddedilir.
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)][ValidateSet('sanalprova_dev', 'sanalprova_test')][string]$Database,
    [ValidateSet('History', 'AppendOnly', 'All')][string]$Hardening = 'History',
    [string]$Instance = '.\SANALPROVA',
    [switch]$Apply
)
$ErrorActionPreference = 'Stop'
$scripts = @()
if ($Hardening -in 'History', 'All') { $scripts += (Join-Path $PSScriptRoot 'mssql/harden-migration-history.sql') }
if ($Hardening -in 'AppendOnly', 'All') { $scripts += (Join-Path $PSScriptRoot 'mssql/harden-append-only.sql'); $scripts += (Join-Path $PSScriptRoot 'mssql/harden-agreement-acceptances.sql') }
foreach ($s in $scripts) { if (-not (Test-Path $s)) { throw "Betik bulunamadi: $s" } }

function Invoke-Sql([string]$Query, [string]$Db = 'master') {
    $out = sqlcmd -S $Instance -E -C -I -b -d $Db -h -1 -W -s '|' -Q "SET NOCOUNT ON; $Query"
    if ($LASTEXITCODE -ne 0) { throw "sqlcmd basarisiz: $out" }
    return $out
}

$info = (Invoke-Sql "SELECT CAST(SERVERPROPERTY('InstanceName') AS nvarchar(128)), IS_SRVROLEMEMBER('sysadmin'), DB_ID(N'$Database')") -split '\|'
if ($info[0].Trim() -ne 'SANALPROVA') { throw "Hedef ornek SANALPROVA degil ($($info[0])). Hicbir sey yapilmadi." }
if ($info[1].Trim() -ne '1') { throw 'Bu islem sunucu yonetici yetkisi ister.' }
if ($info[2].Trim() -eq 'NULL') { throw "Veritabani yok: $Database" }

function Get-State([string]$Table) {
    $exists = (Invoke-Sql "SELECT CASE WHEN OBJECT_ID(N'dbo.$Table', N'U') IS NULL THEN N'yok' ELSE N'var' END" $Database).Trim()
    if ($exists -ne 'var') { return "$Table : tablo yok" }
    $perm = (Invoke-Sql "SELECT COALESCE((SELECT STRING_AGG(p.permission_name + ':' + p.state_desc, ',') FROM sys.database_permissions p JOIN sys.database_principals pr ON pr.principal_id = p.grantee_principal_id WHERE p.major_id = OBJECT_ID(N'dbo.$Table') AND pr.name = N'sanalprova_app'), N'(acik izin yok)')" $Database).Trim()
    return "$Table : sanalprova_app izinleri = $perm"
}
$tables = @()
if ($Hardening -in 'History', 'All') { $tables += '__ef_migrations_history' }
if ($Hardening -in 'AppendOnly', 'All') { $tables += 'ai_cost_reconciliations'; $tables += 'organization_agreement_acceptances' }
Write-Host "Veritabani: $Database | sertlestirme: $Hardening"
foreach ($t in $tables) { Write-Host (Get-State $t) }

if (-not $Apply) {
    Write-Host "KURU CALISMA: hicbir sey degistirilmedi. Uygulamak icin ayni komuta -Apply ekleyin."
    $scripts | ForEach-Object { Write-Host "Calistirilacak betik: $_" }
    return
}
foreach ($t in $tables) { if ((Get-State $t) -like '*tablo yok') { throw "$t yok: once migration uygulanmali." } }
if ($PSCmdlet.ShouldProcess($Database, "sanalprova_app icin sertlestirme uygulanacak: $Hardening")) {
    foreach ($s in $scripts) {
        sqlcmd -S $Instance -E -C -I -b -v "Database=$Database" -i $s
        if ($LASTEXITCODE -ne 0) { throw "Betik basarisiz ($LASTEXITCODE): $s" }
    }
    foreach ($t in $tables) { Write-Host "Sonraki durum: $(Get-State $t)" }
}
