# Percorsi e funzioni comuni agli script del server, caricato con:  . "$PSScriptRoot\comune.ps1"
$ErrorActionPreference = "Stop"
$App = Split-Path $PSScriptRoot -Parent
$Data = Join-Path $env:ProgramData "TD Booking Suite"
$Python = Join-Path $App "python\python.exe"
$PgBin = Join-Path $App "pgsql\bin"
$Services = @("TDBookingWeb", "TDBookingWorker")
$env:TDBS_DATA = $Data
$env:DJANGO_SETTINGS_MODULE = "tdwin.settings"
$env:PYTHONDONTWRITEBYTECODE = "1"
$env:PYTHONUTF8 = "1"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

function Invoke-Checked([string]$Exe, [string[]]$Arguments) {
    # Windows PowerShell 5.1 turns the stderr of a program into errors: read it as text and judge the exit code
    $previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try { & $Exe @Arguments 2>&1 | ForEach-Object { "$_" } }
    finally { $ErrorActionPreference = $previous }
    if ($LASTEXITCODE -ne 0) { throw "$([IO.Path]::GetFileName($Exe)) $($Arguments -join ' ') non riuscito, codice $LASTEXITCODE" }
}

function Read-Json([string]$Name) { Get-Content (Join-Path $Data $Name) -Raw -Encoding UTF8 | ConvertFrom-Json }

# migrations, grants and restore need the database owner: only for that command, never for the services
function Invoke-Django([string[]]$Arguments, [switch]$Owner, [switch]$Interactive) {
    if ($Owner) { $a = Read-Json "db-admin.json"; $env:DATABASE_URL = $a.url; $env:APP_DB_PASSWORD = $a.app_password }
    try {
        if ($Interactive) {
            & $Python -m django @Arguments
            if ($LASTEXITCODE -ne 0) { throw "Operazione non riuscita, codice $LASTEXITCODE" }
        }
        else { Invoke-Checked $Python (@("-m", "django") + $Arguments) }
    }
    finally { if ($Owner) { Remove-Item Env:DATABASE_URL, Env:APP_DB_PASSWORD -ErrorAction SilentlyContinue } }
}

function Request-Admin([string]$Script) {
    $principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        Start-Process powershell.exe -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$Script`""
        exit
    }
}

function Stop-Program {
    foreach ($name in $Services) { if (Get-Service $name -ErrorAction SilentlyContinue) { Stop-Service $name -Force -ErrorAction SilentlyContinue } }
}
function Start-Program {
    Start-Service TDBookingDB
    foreach ($name in $Services) { Start-Service $name }
}

function Wait-Database([string]$Port) {
    for ($i = 0; $i -lt 60; $i++) {
        & (Join-Path $PgBin "pg_isready.exe") -h 127.0.0.1 -p $Port -q
        if ($LASTEXITCODE -eq 0) { return }
        Start-Sleep -Seconds 1
    }
    throw "Il database non parte: guarda il Visualizzatore eventi, registro Applicazione, origine PostgreSQL."
}

function Wait-Web([int]$Port) {
    for ($i = 0; $i -lt 90; $i++) {
        $client = New-Object Net.Sockets.TcpClient
        try { $client.Connect("127.0.0.1", $Port); return } catch { Start-Sleep -Seconds 1 } finally { $client.Dispose() }
    }
    throw "Il servizio TD Booking Suite Web non risponde: guarda $Data\log\TDBookingWeb.err.log"
}
