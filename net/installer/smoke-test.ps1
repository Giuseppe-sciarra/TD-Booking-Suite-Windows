# Prova l'MSI del programma in C# come farebbe un cliente, su GitHub Actions: installazione silenziosa come Server con
# postazioni, i tre servizi con il loro account, il programma risponde in HTTPS con il suo certificato, primo avvio
# con il codice, un backup fatto dal worker; aggiornamento vero (un MSI con un altro codice prodotto) che tiene dati,
# porta e backup; Modifica verso Un PC solo; disinstallazione che lascia i dati; da zero sopra i dati rimasti.
param(
    [string]$Msi = (Get-ChildItem (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) "dist\*.msi") | Select-Object -First 1).FullName,
    [string]$Upgrade = (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) "build\aggiornamento.msi")
)
$ErrorActionPreference = "Stop"
$data = Join-Path $env:ProgramData "TD Booking Suite"
$app = Join-Path $env:ProgramFiles "TD Booking Suite\server"
$base = "https://localhost:9443"
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
# trusted through the local authority added by the setup: no certificate check is skipped
function Get-Info { Invoke-RestMethod "$base/api/info" }
function Invoke-Api([string]$Path, $Body, [string]$Token = "") {
    $headers = @{}; if ($Token) { $headers["Authorization"] = "Bearer $Token" }
    Invoke-RestMethod "$base$Path" -Method Post -ContentType "application/json" -Headers $headers -Body ($Body | ConvertTo-Json -Compress)
}
function Test-Services([string]$When) {
    foreach ($name in "TDBookingDB", "TDBookingWeb", "TDBookingWorker") {
        $service = Get-Service $name
        $account = (Get-CimInstance Win32_Service -Filter "Name='$name'").StartName
        Write-Host "$name $($service.Status) come $account"
        if ($service.Status -ne "Running" -or $account -ne "NT SERVICE\$name") { throw "$When $name non gira con il suo account" }
    }
}

Write-Host "Installo $Msi come Server con postazioni"
Install-Msi @("/i", "`"$Msi`"", "ADDLOCAL=Server,Postazione", "MODALITA=server", "PORTA=9443") "install.log"
try {
    Test-Services "Dopo l'installazione"
    $info = Get-Info
    if ($info.configurato -or -not $info.primoAvvio) { throw "Un programma nuovo deve chiedere il primo avvio: $($info | ConvertTo-Json -Compress)" }
    if (-not (Test-Path (Join-Path $env:ProgramFiles "TD Booking Suite\postazione\TDBookingSuite.exe"))) { throw "Manca il programma della postazione" }
    # the web service must not read the database owner's password
    $acl = (Get-Acl (Join-Path $data "db-admin.json")).Access | Where-Object { $_.IdentityReference -like "*TDBookingWeb*" }
    if ($acl) { throw "Il servizio web può leggere db-admin.json" }
    Test-Mode "server"

    # the first start with the code only a Windows administrator reads, then a backup made by the worker service
    $code = (Get-Content (Join-Path $data "licenza\primo-avvio.txt") -Raw).Trim()
    $password = "Password-di-prova-123"
    $token = (Invoke-Api "/api/primo-avvio" @{ codice = $code; username = "ammin"; email = ""; password = $password; password2 = $password; device = "prova" }).token
    if (-not $token) { throw "Il primo avvio non ha dato la sessione" }
    if (-not (Get-Info).configurato) { throw "Dopo il primo avvio il programma non risulta configurato" }
    Invoke-Api "/api/conferma" @{ password = $password } $token | Out-Null
    Invoke-Api "/api/rpc/backup.azione" @{ azione = "password"; password = "password-di-prova-123"; password2 = "password-di-prova-123" } $token | Out-Null
    Invoke-Api "/api/rpc/backup.azione" @{ azione = "esegui" } $token | Out-Null
    $file = $null
    for ($i = 0; $i -lt 90 -and -not $file; $i++) { Start-Sleep 2; $file = Get-ChildItem (Join-Path $data "backup\tdbs-*.tdbk") -ErrorAction SilentlyContinue | Select-Object -First 1 }
    if (-not $file) { Show-Logs; throw "Il worker non ha fatto il backup" }
    Write-Host "Backup del worker: $($file.Name), $($file.Length) byte"
    # a new installation waits for the licence or the trial: until then the clients' pages are closed
    Invoke-Api "/api/rpc/licenza.azione" @{ azione = "prova" } $token | Out-Null
    # the pages the clients open in the browser come from the same service
    $page = Invoke-WebRequest "$base/privacy/" -UseBasicParsing
    if ($page.StatusCode -ne 200 -or $page.Content -notmatch "Privacy") { throw "La pagina Privacy non risponde" }

    Write-Host "Aggiorno senza dire tipo e porta, con il tipo dimenticato nel registro"
    Remove-ItemProperty "HKLM:\Software\TD Booking Suite" -Name Modalita
    Install-Msi @("/i", "`"$Upgrade`"") "upgrade.log"
    Test-Services "Dopo l'aggiornamento"
    if (-not (Test-Path (Join-Path $data "backup\prima-dell-aggiornamento.dump"))) { throw "Manca la copia del database prima dell'aggiornamento" }
    if (-not (Test-Path $file.FullName)) { throw "L'aggiornamento ha perso i backup" }
    Test-Mode "server"
    if (-not (Get-Info).configurato) { throw "L'aggiornamento ha perso l'amministratore" }
    $installed = Get-ItemProperty "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*" | Where-Object { $_.DisplayName -eq "TD Booking Suite" }
    if (($installed | Measure-Object).Count -ne 1) { throw "Dopo l'aggiornamento risultano installate $(($installed | Measure-Object).Count) versioni" }

    Write-Host "Modifica dell'installazione: diventa Un PC solo"
    Install-Msi @("/i", "`"$Upgrade`"", "MODALITA=standalone") "modifica.log"
    Test-Mode "standalone"
    Get-Info | Out-Null
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
    if ((Get-Info).configurato) { throw "Da zero non compare il primo avvio" }

    # initdb drops the Administrators group; by hand it is not SYSTEM, as in the installer, but this user
    Write-Host "Da zero a mano, da un PowerShell di amministratore, come lo lancia chi assiste il cliente"
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $app "script\setup-server.ps1") -Ricomincia 1
    if ($LASTEXITCODE -ne 0) { throw "Da zero a mano non riuscito, codice $LASTEXITCODE" }
    $aside = @(Get-ChildItem $env:ProgramData -Directory | Where-Object Name -like "TD Booking Suite (dati del *")
    if ($aside.Count -ne 2) { throw "Da zero a mano: $($aside.Count) cartelle messe da parte invece di 2" }
    if ((Get-Content (Join-Path $data "config.json") -Raw | ConvertFrom-Json).install_id -ne $before.install_id) { throw "Da zero a mano ha cambiato il codice installazione" }
    $me = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $mine = (Get-Acl (Join-Path $data "pgdata")).Access | Where-Object { try { $_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value -eq $me } catch { $false } }
    if ($mine) { throw "Dopo il da zero a mano l'utente che l'ha lanciato ha ancora diritti sul database" }
    Test-Mode "standalone"
    if ((Get-Info).configurato) { throw "Da zero a mano non compare il primo avvio" }

    # a start that stopped half way: data aside, configuration made, database never created
    Write-Host "Un da zero rimasto a metà, senza database: l'aggiornamento lo finisce e tiene il codice installazione"
    Stop-Service TDBookingWeb, TDBookingWorker, TDBookingDB -Force
    Get-ChildItem (Join-Path $data "pgdata") -Force | Remove-Item -Recurse -Force
    Install-Msi @("/i", "`"$Msi`"") "finisce.log"
    Test-Services "Dopo l'aggiornamento che finisce"
    if ((Get-Content (Join-Path $data "config.json") -Raw | ConvertFrom-Json).install_id -ne $before.install_id) { throw "Finendo il da zero il codice installazione è cambiato: la licenza non varrebbe più" }
    if (@(Get-ChildItem $env:ProgramData -Directory | Where-Object Name -like "TD Booking Suite (dati del *").Count -ne 2) { throw "L'aggiornamento ha messo da parte altri dati" }
    Test-Mode "standalone"
    if ((Get-Info).configurato) { throw "Finendo il da zero non compare il primo avvio" }
}
catch { Show-Logs; throw }
Install-Msi @("/x", "`"$Msi`"") "uninstall-2.log"
Write-Host "Prova completa riuscita"
