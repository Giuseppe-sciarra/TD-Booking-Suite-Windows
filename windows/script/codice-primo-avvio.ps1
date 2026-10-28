# Menu Start, TD Booking Suite, Codice del primo avvio: mostra il codice che la pagina Primo avvio chiede.
# Il codice sta nella cartella dei dati, che leggono solo gli amministratori di Windows: per questo un utente
# normale del PC server non può creare l'amministratore del programma prima di te.
. "$PSScriptRoot\comune.ps1"
Request-Admin $PSCommandPath
Write-Host "TD Booking Suite: codice del primo avvio`n"
$file = Join-Path $Data "licenza\primo-avvio.txt"
if (Test-Path $file) {
    Write-Host "Scrivi questo codice nella pagina Primo avvio:`n"
    Write-Host ("    " + (Get-Content $file -Raw).Trim()) -ForegroundColor Cyan
}
else {
    Write-Host "Nessun codice da mostrare. Se il programma non è ancora configurato, aprilo una volta (il codice si crea"
    Write-Host "alla prima apertura) e poi torna qui. Se l'amministratore esiste già, per una password dimenticata usa"
    Write-Host "Recupera accesso dal menu Start."
}
Read-Host "`nPremi Invio per chiudere"
