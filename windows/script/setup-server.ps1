# Configura il server dopo l'installazione o l'aggiornamento. Lo lancia l'MSI come amministratore;
# si può rilanciare a mano per cambiare indirizzo o porta:
#   powershell -ExecutionPolicy Bypass -File setup-server.ps1 -Origine https://ufficio-pc:8443 -Porta 8443
# Non cancella e non sovrascrive mai dati, chiavi e password già esistenti.
param([string]$Porta = "", [string]$Origine = "", [string]$Indirizzi = "")
. "$PSScriptRoot\comune.ps1"
New-Item -ItemType Directory -Force (Join-Path $Data "log") | Out-Null
Start-Transcript -Path (Join-Path $Data "log\setup.log") -Append | Out-Null
try {
    Write-Output "TD Booking Suite: configurazione del server in $App"
    # before any key is written: only Windows and its administrators read the data folder, the services come later
    icacls $Data /inheritance:r /grant:r "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" | Out-Null

    # 1. configuration files: keys and passwords only the first time
    $setup = @("-m", "tdwin.setup_files", "--data", $Data, "--pg-bin", $PgBin)
    if ($Porta) { $setup += @("--porta", $Porta) }
    if ($Origine) { $setup += @("--origine", $Origine) }
    if ($Indirizzi) { $setup += @("--indirizzi", $Indirizzi) }
    Invoke-Checked $Python $setup
    $config = Read-Json "config.json"
    $admin = Read-Json "db-admin.json"
    $dbUrl = [Uri]$admin.url
    $pgPort = [string]$dbUrl.Port
    $env:PGPASSWORD = $dbUrl.UserInfo.Split(":")[1]

    # 2. the database: its own folder, readable only by its service
    $pgData = Join-Path $Data "pgdata"
    if (-not (Test-Path (Join-Path $pgData "PG_VERSION"))) {
        New-Item -ItemType Directory -Force $pgData | Out-Null
        icacls $pgData /inheritance:r /grant:r "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" | Out-Null
        $pwFile = Join-Path $Data "tmp\pg-password.txt"
        New-Item -ItemType Directory -Force (Split-Path $pwFile) | Out-Null
        [IO.File]::WriteAllText($pwFile, $env:PGPASSWORD)
        try { Invoke-Checked (Join-Path $PgBin "initdb.exe") @("-D", $pgData, "-U", "postgres", "-E", "UTF8", "--no-locale", "--auth=scram-sha-256", "--pwfile=$pwFile") }
        finally { Remove-Item $pwFile -Force -ErrorAction SilentlyContinue }
        Add-Content -Path (Join-Path $pgData "postgresql.conf") -Value "`r`n# TD Booking Suite`r`nlisten_addresses = '127.0.0.1'`r`nport = $pgPort`r`n" -Encoding ASCII
        $fresh = $true
    }
    else { $fresh = $false }
    if (-not (Get-Service TDBookingDB -ErrorAction SilentlyContinue)) {
        Invoke-Checked (Join-Path $PgBin "pg_ctl.exe") @("register", "-N", "TDBookingDB", "-D", $pgData, "-S", "auto")
        Invoke-Checked "sc.exe" @("config", "TDBookingDB", "obj=", "NT SERVICE\TDBookingDB")
        Invoke-Checked "sc.exe" @("description", "TDBookingDB", "Database di TD Booking Suite")
    }
    icacls $pgData /grant "NT SERVICE\TDBookingDB:(OI)(CI)F" /T /Q | Out-Null
    Start-Service TDBookingDB
    Wait-Database $pgPort
    $exists = Invoke-Checked (Join-Path $PgBin "psql.exe") @("-h", "127.0.0.1", "-p", $pgPort, "-U", "postgres", "-d", "postgres", "-tAc", "SELECT 1 FROM pg_database WHERE datname='tdbooking'")
    if ("$exists".Trim() -ne "1") { Invoke-Checked (Join-Path $PgBin "createdb.exe") @("-h", "127.0.0.1", "-p", $pgPort, "-U", "postgres", "tdbooking") }
    elseif (-not $fresh) {
        # an update: a copy of the database as it was, before the new version changes it
        Invoke-Checked (Join-Path $PgBin "pg_dump.exe") @("-h", "127.0.0.1", "-p", $pgPort, "-U", "postgres", "-Fc", "-f", (Join-Path $Data "backup\prima-dell-aggiornamento.dump"), "tdbooking")
    }

    # 3. tables of this version, and the program's own database user with data rights only
    Invoke-Django @("migrate", "--noinput") -Owner
    Invoke-Django @("grant_runtime") -Owner
    Invoke-Django @("check_runtime")
    Remove-Item Env:PGPASSWORD

    # 4. services, each with its own virtual account
    foreach ($name in $Services) {
        if (-not (Get-Service $name -ErrorAction SilentlyContinue)) { Invoke-Checked (Join-Path $App "servizi\$name.exe") @("install") }
        Invoke-Checked "sc.exe" @("config", $name, "obj=", "NT SERVICE\$name")
    }

    # 5. who reads what: the web service never sees the database owner's password
    foreach ($folder in "licenza", "backup", "certs", "log", "tmp") { New-Item -ItemType Directory -Force (Join-Path $Data $folder) | Out-Null }
    icacls $Data /inheritance:r /grant:r "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" "NT SERVICE\TDBookingWeb:(OI)(CI)RX" "NT SERVICE\TDBookingWorker:(OI)(CI)RX" | Out-Null
    icacls (Join-Path $Data "db-admin.json") /inheritance:r /grant:r "*S-1-5-18:F" "*S-1-5-32-544:F" "NT SERVICE\TDBookingWorker:R" | Out-Null
    icacls $pgData /inheritance:r /grant:r "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" "NT SERVICE\TDBookingDB:(OI)(CI)F" | Out-Null
    icacls (Join-Path $Data "licenza") /grant "NT SERVICE\TDBookingWeb:(OI)(CI)M" | Out-Null
    icacls (Join-Path $Data "certs") /grant "NT SERVICE\TDBookingWeb:(OI)(CI)M" | Out-Null
    foreach ($folder in "backup", "tmp") { icacls (Join-Path $Data $folder) /grant "NT SERVICE\TDBookingWorker:(OI)(CI)M" | Out-Null }
    icacls (Join-Path $Data "log") /grant "NT SERVICE\TDBookingWeb:(OI)(CI)M" "NT SERVICE\TDBookingWorker:(OI)(CI)M" | Out-Null

    # 6. the network: only private and company networks, see the guide for a server in the cloud
    Get-NetFirewallRule -DisplayName "TD Booking Suite" -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    New-NetFirewallRule -DisplayName "TD Booking Suite" -Direction Inbound -Protocol TCP -LocalPort $config.porta -Action Allow -Profile Domain, Private | Out-Null

    # 7. start, then this PC trusts the program's own certification authority
    Start-Program
    Wait-Web ([int]$config.porta)
    $ca = Join-Path $Data "certs\ca.crt"
    for ($i = 0; $i -lt 30 -and -not (Test-Path $ca); $i++) { Start-Sleep -Seconds 1 }
    Invoke-Checked "certutil.exe" @("-f", "-addstore", "Root", $ca)
    $page = Invoke-WebRequest -Uri "https://localhost:$($config.porta)/primo-avvio/" -UseBasicParsing -TimeoutSec 30
    Write-Output "Il programma risponde su $($config.origine) (HTTP $($page.StatusCode))"

    # 8. the workstation program on this PC opens this server without questions
    $shared = Join-Path $env:ProgramData "TD Booking Suite Postazione"
    New-Item -ItemType Directory -Force $shared | Out-Null
    [IO.File]::WriteAllText((Join-Path $shared "server.txt"), "server=$($config.origine)`r`n")
    $menu = Join-Path $env:ProgramData "Microsoft\Windows\Start Menu\Programs\TD Booking Suite"
    New-Item -ItemType Directory -Force $menu | Out-Null
    [IO.File]::WriteAllText((Join-Path $menu "TD Booking Suite nel browser.url"), "[InternetShortcut]`r`nURL=$($config.origine)/`r`n")
    Write-Output "Configurazione completata."
}
catch {
    Write-Output "ERRORE: $_"
    Stop-Transcript | Out-Null
    exit 1
}
Stop-Transcript | Out-Null
