# Prova l'MSI come farebbe un cliente, su GitHub Actions: installazione silenziosa di server e postazione,
# il programma risponde in HTTPS con il suo certificato, il worker fa un backup con il suo account, disinstallazione.
param([string]$Msi = (Get-ChildItem (Join-Path (Split-Path $PSScriptRoot -Parent) "dist\*.msi") | Select-Object -First 1).FullName)
$ErrorActionPreference = "Stop"
$data = Join-Path $env:ProgramData "TD Booking Suite"
$app = Join-Path $env:ProgramFiles "TD Booking Suite\server"
function Show-Logs {
    Get-ChildItem (Join-Path $data "log") -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "----- $($_.Name)"; Get-Content $_.FullName -Tail 60 }
}
function Install-Msi([string[]]$Arguments, [string]$Log) {
    $p = Start-Process msiexec.exe -ArgumentList ($Arguments + @("/qn", "/l*v", $Log)) -Wait -PassThru
    if ($p.ExitCode -ne 0) { Get-Content $Log -Tail 200; Show-Logs; throw "msiexec $($Arguments -join ' ') non riuscito, codice $($p.ExitCode)" }
}

Write-Host "Installo $Msi"
Install-Msi @("/i", "`"$Msi`"", "ADDLOCAL=Server,Postazione", "PORTA=8443") "install.log"
try {
    foreach ($name in "TDBookingDB", "TDBookingWeb", "TDBookingWorker") {
        $service = Get-Service $name
        $account = (Get-CimInstance Win32_Service -Filter "Name='$name'").StartName
        Write-Host "$name $($service.Status) come $account"
        if ($service.Status -ne "Running" -or $account -ne "NT SERVICE\$name") { throw "$name non è partito con il suo account" }
    }
    # trusted through the local authority added by the setup: no certificate check is skipped
    $page = Invoke-WebRequest "https://localhost:8443/primo-avvio/" -UseBasicParsing
    if ($page.StatusCode -ne 200 -or $page.Content -notmatch "Primo avvio") { throw "Pagina del primo avvio non trovata" }
    if (-not (Test-Path (Join-Path $env:ProgramFiles "TD Booking Suite\postazione\TDBookingSuite.exe"))) { throw "Manca il programma della postazione" }
    # the web service must not read the database owner's password
    $acl = (Get-Acl (Join-Path $data "db-admin.json")).Access | Where-Object { $_.IdentityReference -like "*TDBookingWeb*" }
    if ($acl) { throw "Il servizio web può leggere db-admin.json" }

    # a backup made by the worker service, with its own rights
    $env:TDBS_DATA = $data; $env:DJANGO_SETTINGS_MODULE = "tdwin.settings"
    & (Join-Path $app "python\python.exe") -m django shell --no-imports -c "from tdwin import backup;from tdwin.models import ImpostazioniBackup as I;from django.utils import timezone;backup.set_password('password-di-prova-123');I.objects.filter(pk=1).update(richiesto=timezone.now())"
    if ($LASTEXITCODE -ne 0) { throw "Impostazione del backup non riuscita" }
    $file = $null
    for ($i = 0; $i -lt 90 -and -not $file; $i++) { Start-Sleep 2; $file = Get-ChildItem (Join-Path $data "backup\tdbs-*.tdbk") -ErrorAction SilentlyContinue | Select-Object -First 1 }
    if (-not $file) { Show-Logs; throw "Il worker non ha fatto il backup" }
    Write-Host "Backup del worker: $($file.Name), $($file.Length) byte"

    Write-Host "Reinstallo sopra, come un aggiornamento"
    Install-Msi @("/i", "`"$Msi`"", "ADDLOCAL=Server,Postazione") "upgrade.log"
    if ((Get-Service TDBookingWeb).Status -ne "Running") { throw "Dopo l'aggiornamento il servizio web è fermo" }
    if (-not (Test-Path (Join-Path $data "backup\prima-dell-aggiornamento.dump"))) { throw "Manca la copia del database prima dell'aggiornamento" }
    Invoke-WebRequest "https://localhost:8443/primo-avvio/" -UseBasicParsing | Out-Null
}
catch { Show-Logs; throw }

Write-Host "Disinstallo"
Install-Msi @("/x", "`"$Msi`"") "uninstall.log"
foreach ($name in "TDBookingDB", "TDBookingWeb", "TDBookingWorker") { if (Get-Service $name -ErrorAction SilentlyContinue) { throw "$name è rimasto dopo la disinstallazione" } }
if (-not (Test-Path (Join-Path $data "config.json"))) { throw "La disinstallazione ha cancellato i dati" }
Write-Host "Prova completa riuscita"
