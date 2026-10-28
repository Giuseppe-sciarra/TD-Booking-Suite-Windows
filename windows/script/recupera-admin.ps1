# Menu Start, TD Booking Suite, Recupera amministratore: nuova password per l'amministratore del programma.
# Azzera anche Authenticator e passkey: si rifanno al primo accesso. Solo da questo PC, come amministratore di Windows.
. "$PSScriptRoot\comune.ps1"
Request-Admin $PSCommandPath
Write-Host "TD Booking Suite: recupero dell'amministratore del programma`n"
$email = Read-Host "Email dell'amministratore"
if (-not $email) { exit }
try {
    $out = & $Python -m django recover_admin --email $email --confirm-reset-mfa 2>&1 | ForEach-Object { "$_" }
    if ($LASTEXITCODE -ne 0) { throw ($out -join "`n") }
    $link = ($out | Where-Object { $_ -match "^https://" } | Select-Object -Last 1)
    Write-Host "`nAccessi, Authenticator e passkey azzerati. Apro la pagina per scegliere la nuova password: vale 24 ore."
    Write-Host $link
    Start-Process $link
}
catch { Write-Host "`nNon riuscito: $_" -ForegroundColor Red }
Read-Host "`nPremi Invio per chiudere"
