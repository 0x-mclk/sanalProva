# Idempotent migration betigi uretir (dagitim/yedekli yukseltme icin): yalnizca uygulanmamis migration'lari calistirir, tekrar calistirmak zararsizdir.
# Veritabanina BAGLANMAZ ve hicbir sey uygulamaz; ciktiyi migrator hesabiyla (sqlcmd -I ile) sizin uygulamaniz gerekir.
# Kullanim:  . ./scripts/dev-env.ps1; ./scripts/gen-migration-script.ps1 [-Output .local/migrations/sanalprova-idempotent.sql]
param([string]$Output = ".local/migrations/sanalprova-idempotent.sql")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
New-Item -ItemType Directory -Force -Path (Split-Path -Parent (Join-Path $root $Output)) | Out-Null
$env:ConnectionStrings__Main = "Server=tcp:127.0.0.1,1;Database=design_only_never_connected;User ID=x;Password=x;Encrypt=False"
Push-Location (Join-Path $root "backend")
try {
    dotnet ef migrations script --idempotent --project src/SanalProva.Infrastructure --startup-project src/SanalProva.Api --output (Join-Path $root $Output)
    if ($LASTEXITCODE -ne 0) { throw "dotnet ef basarisiz ($LASTEXITCODE)" }
} finally { Pop-Location }
Write-Host "Uretildi: $Output  (uygulama: sqlcmd -S <sunucu> -d <veritabani> -U sanalprova_migrator -I -i $Output)"
