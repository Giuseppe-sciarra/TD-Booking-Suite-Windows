# Toglie servizi, regola del firewall e fiducia nel certificato prima della disinstallazione o di un aggiornamento.
# Dati, backup e licenza restano in C:\ProgramData\TD Booking Suite.
# -Aggiornamento: l'MSI nuovo sostituisce i file, quindi i servizi si fermano soltanto e restano registrati;
# se l'aggiornamento non riesce, Windows rimette i file di prima e i servizi ripartono.
param([switch]$Aggiornamento)
. "$PSScriptRoot\comune.ps1"
$ErrorActionPreference = "Continue"
if ($Aggiornamento) {
    Stop-Program
    Stop-Service TDBookingDB -Force -ErrorAction SilentlyContinue
    exit 0
}
foreach ($name in $Services) {
    if (Get-Service $name -ErrorAction SilentlyContinue) {
        Stop-Service $name -Force
        & (Join-Path $App "servizi\$name.exe") uninstall
    }
}
if (Get-Service TDBookingDB -ErrorAction SilentlyContinue) {
    Stop-Service TDBookingDB -Force
    & (Join-Path $PgBin "pg_ctl.exe") unregister -N TDBookingDB
}
Get-NetFirewallRule -DisplayName "TD Booking Suite" -ErrorAction SilentlyContinue | Remove-NetFirewallRule
$ca = Join-Path $Data "certs\ca.crt"
if (Test-Path $ca) {
    $thumb = (New-Object Security.Cryptography.X509Certificates.X509Certificate2 $ca).Thumbprint
    Remove-Item "Cert:\LocalMachine\Root\$thumb" -ErrorAction SilentlyContinue
}
Remove-Item (Join-Path $env:ProgramData "Microsoft\Windows\Start Menu\Programs\TD Booking Suite\TD Booking Suite nel browser.url") -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:ProgramData "TD Booking Suite Postazione\server.txt") -ErrorAction SilentlyContinue
exit 0
