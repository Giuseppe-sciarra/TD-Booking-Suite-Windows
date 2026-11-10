# Percorsi e funzioni comuni agli script del server, caricato con:  . "$PSScriptRoot\comune.ps1"
$ErrorActionPreference = "Stop"
$App = Split-Path $PSScriptRoot -Parent
$Data = Join-Path $env:ProgramData "TD Booking Suite"
$Server = Join-Path $App "app\TDBookingServer.exe"
$PgBin = Join-Path $App "pgsql\bin"
$Services = @("TDBookingWeb", "TDBookingWorker")
$env:TDBS_DATA = $Data
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

# A folder the SYSTEM scripts will fill with keys and data must not be one a normal user prepared: a junction
# (redirects icacls and writes elsewhere) is refused, and a real folder owned by someone other than SYSTEM or the
# Administrators is set aside whole, so nothing planted inside it (a certificate authority, a config.json, a fake
# pgdata that makes it look installed) is reused. A real installation is owned by the Administrators and stays.
# -Reset, for a folder made now: owner Administrators and only SYSTEM and Administrators, before any key is written.
function Protect-Folder([string]$Path, [switch]$Reset) {
    if (Test-Path $Path) {
        $item = Get-Item $Path -Force
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "$Path è un collegamento (giunzione): rimuovilo e reinstalla." }
        $owner = (Get-Acl $Path).Owner
        $sid = try { ([Security.Principal.NTAccount]$owner).Translate([Security.Principal.SecurityIdentifier]).Value } catch { $owner }
        if ($sid -notin @("S-1-5-18", "S-1-5-32-544")) {
            $aside = "$Path.non-fidata-" + (Get-Date -Format "yyyyMMdd-HHmmss")
            Move-Item -LiteralPath $Path -Destination $aside -Force
            Write-Output "$Path era di $owner, non di un amministratore: spostata in $aside, se ne crea una nuova"
            $Reset = $true
        }
    }
    else { $Reset = $true }
    New-Item -ItemType Directory -Force $Path | Out-Null
    if ($Reset) {
        Invoke-Checked "icacls.exe" @($Path, "/setowner", "*S-1-5-32-544", "/T", "/C", "/Q")
        Invoke-Checked "icacls.exe" @($Path, "/reset", "/T", "/C", "/Q")
    }
    Invoke-Checked "icacls.exe" @($Path, "/inheritance:r", "/grant:r", "*S-1-5-18:(OI)(CI)F", "*S-1-5-32-544:(OI)(CI)F")
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
    throw "Il servizio TD Booking Suite Web non risponde: guarda $Data\log\TDBookingWeb.log"
}

# The program is its own Windows service (no wrapper): registered here, or pointed at this version when the service
# is already there (an update, also from the 2.x that ran it through WinSW).
function Register-Program([string]$Name, [string]$Title, [string]$Description, [string]$Arguments) {
    # the path in quotes, then the arguments: written by Windows itself, not through a command line that would split it
    $command = "`"$Server`"" + $(if ($Arguments) { " $Arguments" } else { "" })
    if (Get-Service $Name -ErrorAction SilentlyContinue) {
        Set-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$Name" -Name "ImagePath" -Value $command
        Invoke-Checked "sc.exe" @("config", $Name, "start=", "auto", "depend=", "TDBookingDB", "DisplayName=", $Title)
    }
    else { New-Service -Name $Name -BinaryPathName $command -DisplayName $Title -StartupType Automatic -DependsOn "TDBookingDB" | Out-Null }
    Invoke-Checked "sc.exe" @("config", $Name, "obj=", "NT SERVICE\$Name")
    Invoke-Checked "sc.exe" @("description", $Name, $Description)
    # stopped by an error: started again after 10 seconds, 30 seconds, 2 minutes; the count starts over after an hour
    Invoke-Checked "sc.exe" @("failure", $Name, "reset=", "3600", "actions=", "restart/10000/restart/30000/restart/120000")
}
