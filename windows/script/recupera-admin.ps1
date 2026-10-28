# Menu Start, TD Booking Suite, Recupera accesso: password dimenticata dell'amministratore del programma o del
# titolare (o di una persona dell'attività). Azzera anche Authenticator e passkey: si rifanno al primo accesso.
# Solo da questo PC, come amministratore di Windows: chi può farlo vede già tutti i file del programma.
. "$PSScriptRoot\comune.ps1"
Request-Admin $PSCommandPath
Write-Host "TD Booking Suite: recupero di un accesso`n"
Write-Host "Vale per l'amministratore del programma e per il titolare o una persona dell'attività."
$email = Read-Host "Username o email dell'account"
if (-not $email) { exit }
try {
    $ErrorActionPreference = "Continue"
    $out = & $Python -m django recupera_accesso --email $email --conferma 2>&1 | ForEach-Object { "$_" }
    $ErrorActionPreference = "Stop"
    if ($LASTEXITCODE -ne 0) { throw (($out | Select-Object -Last 1) -replace "^CommandError: ", "") }
    $link = ($out | Where-Object { $_ -match "https://" } | Select-Object -Last 1) -replace "^\S+ ", ""
    Write-Host "`nAccessi, Authenticator e passkey azzerati. Apro la pagina per scegliere la nuova password: vale 24 ore."
    Write-Host $link
    Start-Process $link
}
catch { Write-Host "`nNon riuscito: $_" -ForegroundColor Red }
Read-Host "`nPremi Invio per chiudere"
