# Prova l'MSI come farebbe un cliente, su GitHub Actions: installazione silenziosa come Server con postazioni, il
# programma risponde in HTTPS con il suo certificato, il worker fa un backup con il suo account; aggiornamento vero
# (un MSI con un altro codice prodotto) di un server della 2.8, che non aveva il tipo nel registro: resta server, con
# porta, dati e backup; poi Modifica dell'installazione verso Un PC solo (solo su questo PC, niente firewall);
# disinstallazione che lascia i dati.
param(
    [string]$Msi = (Get-ChildItem (Join-Path (Split-Path $PSScriptRoot -Parent) "dist\*.msi") | Select-Object -First 1).FullName,
    [string]$Upgrade = (Join-Path (Split-Path $PSScriptRoot -Parent) "build\aggiornamento.msi")
)
$ErrorActionPreference = "Stop"
$data = Join-Path $env:ProgramData "TD Booking Suite"
$app = Join-Path $env:ProgramFiles "TD Booking Suite\server"
function Show-Logs {
    Get-ChildItem (Join-Path $data "log") -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "----- $($_.Name)"; Get-Content $_.FullName -Tail 60 }
}
function Test-Mode([string]$Expected) {
    $listen = @()
    for ($i = 0; $i -lt 60 -and -not $listen; $i++) { Start-Sleep 1; $listen = @(Get-NetTCPConnection -LocalPort 9443 -State Listen -ErrorAction SilentlyContinue | Select-Object -ExpandProperty LocalAddress) }
    $config = Get-Content (Join-Path $data "config.json") -Raw | ConvertFrom-Json
    $rule = Get-NetFirewallRule -DisplayName "TD Booking Suite" -ErrorAction SilentlyContinue
    $remembered = (Get-ItemProperty "HKLM:\Software\TD Booking Suite" -Name Modalita).Modalita
    $shared = Get-Content (Join-Path $env:ProgramData "TD Booking Suite Postazione\server.txt") -Raw
    Write-Host "Modalità $($config.modalita), in ascolto su $($listen -join ', '), regola del firewall: $([bool]$rule), ricordata: $remembered"
    if ($config.modalita -ne $Expected -or $remembered -ne $Expected) { throw "Modalità $($config.modalita) invece di $Expected" }
    if ($shared -notmatch "ca=[0-9A-F]{4}( [0-9A-F]{4}){15}") { throw "server.txt senza l'impronta dell'autorità: $shared" }
    if ($Expected -eq "standalone") {
        if ($rule) { throw "Un PC solo non deve aprire il firewall" }
        if (-not $listen -or ($listen | Where-Object { $_ -ne "127.0.0.1" })) { throw "Un PC solo deve ascoltare solo su 127.0.0.1" }
        if ($shared -notmatch "server=https://localhost:9443") { throw "La postazione di questo PC non punta a localhost: $shared" }
        if ($shared -notmatch "modalita=standalone") { throw "server.txt non dice che è Un PC solo: la finestra potrebbe aprire un altro server" }
    }
    else {
        if (-not $rule) { throw "Il server deve aprire la sua porta nel firewall" }
        if ($listen -notcontains "0.0.0.0") { throw "Il server deve ascoltare sulla rete" }
    }
}
function Install-Msi([string[]]$Arguments, [string]$Log) {
    $p = Start-Process msiexec.exe -ArgumentList ($Arguments + @("/qn", "/l*v", $Log)) -Wait -PassThru
    if ($p.ExitCode -ne 0) { Get-Content $Log -Tail 200; Show-Logs; throw "msiexec $($Arguments -join ' ') non riuscito, codice $($p.ExitCode)" }
}

Write-Host "Installo $Msi come Server con postazioni"
Install-Msi @("/i", "`"$Msi`"", "ADDLOCAL=Server,Postazione", "MODALITA=server", "PORTA=9443") "install.log"
try {
    foreach ($name in "TDBookingDB", "TDBookingWeb", "TDBookingWorker") {
        $service = Get-Service $name
        $account = (Get-CimInstance Win32_Service -Filter "Name='$name'").StartName
        Write-Host "$name $($service.Status) come $account"
        if ($service.Status -ne "Running" -or $account -ne "NT SERVICE\$name") { throw "$name non è partito con il suo account" }
    }
    # trusted through the local authority added by the setup: no certificate check is skipped
    $page = Invoke-WebRequest "https://localhost:9443/primo-avvio/" -UseBasicParsing
    if ($page.StatusCode -ne 200 -or $page.Content -notmatch "Primo avvio") { throw "Pagina del primo avvio non trovata" }
    if (-not (Test-Path (Join-Path $env:ProgramFiles "TD Booking Suite\postazione\TDBookingSuite.exe"))) { throw "Manca il programma della postazione" }
    # the web service must not read the database owner's password
    $acl = (Get-Acl (Join-Path $data "db-admin.json")).Access | Where-Object { $_.IdentityReference -like "*TDBookingWeb*" }
    if ($acl) { throw "Il servizio web può leggere db-admin.json" }
    Test-Mode "server"

    # a backup made by the worker service, with its own rights
    $env:TDBS_DATA = $data; $env:DJANGO_SETTINGS_MODULE = "tdwin.settings"
    & (Join-Path $app "python\python.exe") -m django shell --no-imports -c "from tdwin import backup;from tdwin.models import ImpostazioniBackup as I;from django.utils import timezone;backup.set_password('password-di-prova-123');I.objects.filter(pk=1).update(richiesto=timezone.now())"
    if ($LASTEXITCODE -ne 0) { throw "Impostazione del backup non riuscita" }
    $file = $null
    for ($i = 0; $i -lt 90 -and -not $file; $i++) { Start-Sleep 2; $file = Get-ChildItem (Join-Path $data "backup\tdbs-*.tdbk") -ErrorAction SilentlyContinue | Select-Object -First 1 }
    if (-not $file) { Show-Logs; throw "Il worker non ha fatto il backup" }
    Write-Host "Backup del worker: $($file.Name), $($file.Length) byte"

    Write-Host "Aggiorno un server come quelli della 2.8, che non avevano il tipo nel registro, senza dire tipo e porta"
    Remove-ItemProperty "HKLM:\Software\TD Booking Suite" -Name Modalita
    Install-Msi @("/i", "`"$Upgrade`"") "upgrade.log"
    foreach ($name in "TDBookingDB", "TDBookingWeb", "TDBookingWorker") { if ((Get-Service $name).Status -ne "Running") { throw "Dopo l'aggiornamento $name è fermo" } }
    if (-not (Test-Path (Join-Path $data "backup\prima-dell-aggiornamento.dump"))) { throw "Manca la copia del database prima dell'aggiornamento" }
    if (-not (Test-Path $file.FullName)) { throw "L'aggiornamento ha perso i backup" }
    Test-Mode "server"
    $installed = Get-ItemProperty "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*" | Where-Object { $_.DisplayName -eq "TD Booking Suite" }
    if (($installed | Measure-Object).Count -ne 1) { throw "Dopo l'aggiornamento risultano installate $(($installed | Measure-Object).Count) versioni" }

    Write-Host "Modifica dell'installazione: diventa Un PC solo"
    Install-Msi @("/i", "`"$Upgrade`"", "MODALITA=standalone") "modifica.log"
    Test-Mode "standalone"
    Invoke-WebRequest "https://localhost:9443/primo-avvio/" -UseBasicParsing | Out-Null
}
catch { Show-Logs; throw }

Write-Host "Disinstallo"
Install-Msi @("/x", "`"$Upgrade`"") "uninstall.log"
foreach ($name in "TDBookingDB", "TDBookingWeb", "TDBookingWorker") { if (Get-Service $name -ErrorAction SilentlyContinue) { throw "$name è rimasto dopo la disinstallazione" } }
if (-not (Test-Path (Join-Path $data "config.json"))) { throw "La disinstallazione ha cancellato i dati" }

Write-Host "Reinstallo come Un PC solo da zero sopra i dati rimasti: vanno da parte, licenza e codice installazione restano"
$before = Get-Content (Join-Path $data "config.json") -Raw | ConvertFrom-Json
Set-Content (Join-Path $data "licenza\segno-di-prova.txt") "resta"
$caBefore = (Get-FileHash (Join-Path $data "certs\ca.crt")).Hash
Install-Msi @("/i", "`"$Upgrade`"", "MODALITA=standalone", "RICOMINCIA=1") "da-zero.log"
try {
    $aside = @(Get-ChildItem $env:ProgramData -Directory | Where-Object Name -like "TD Booking Suite (dati del *")
    if ($aside.Count -ne 1) { throw "I dati di prima non sono stati messi da parte: $($aside.Count) cartelle" }
    if (-not (Test-Path (Join-Path $aside[0].FullName "pgdata\PG_VERSION"))) { throw "Nella cartella messa da parte manca il database di prima" }
    $after = Get-Content (Join-Path $data "config.json") -Raw | ConvertFrom-Json
    if ($after.install_id -ne $before.install_id) { throw "Il codice installazione è cambiato: la licenza non varrebbe più" }
    if ($after.porta -ne $before.porta) { throw "La porta è cambiata: $($before.porta) prima, $($after.porta) ora" }
    if (-not (Test-Path (Join-Path $data "licenza\segno-di-prova.txt"))) { throw "La cartella della licenza non è stata tenuta" }
    if ($after.env.DJANGO_SECRET_KEY -eq $before.env.DJANGO_SECRET_KEY) { throw "Da zero ma con le chiavi di prima" }
    if ((Get-FileHash (Join-Path $data "certs\ca.crt")).Hash -ne $caBefore) { throw "Il certificato è cambiato: le postazioni non aprirebbero più il server" }
    Test-Mode "standalone"
    # an empty program: the first start asks for its administrator
    $page = Invoke-WebRequest "https://localhost:$($after.porta)/" -UseBasicParsing
    if ($page.Content -notmatch "Primo avvio") { throw "Da zero non compare il primo avvio" }
}
catch { Show-Logs; throw }
Install-Msi @("/x", "`"$Upgrade`"") "uninstall-2.log"
Write-Host "Prova completa riuscita"
