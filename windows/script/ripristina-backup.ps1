# Menu Start, TD Booking Suite, Ripristina backup: rimette i dati di un file .tdbk.
# Il database di prima non viene cancellato: resta da parte con la data nel nome.
. "$PSScriptRoot\comune.ps1"
Request-Admin $PSCommandPath
Add-Type -AssemblyName System.Windows.Forms
Write-Host "TD Booking Suite: ripristino di un backup`n"
$dialog = New-Object Windows.Forms.OpenFileDialog
$dialog.Title = "Scegli il backup da ripristinare"
$dialog.Filter = "Backup TD Booking Suite (*.tdbk)|*.tdbk"
$dialog.InitialDirectory = Join-Path $Data "backup"
if ($dialog.ShowDialog() -ne [Windows.Forms.DialogResult]::OK) { exit }
$file = $dialog.FileName
Write-Host "File: $file"
Write-Host "Il programma si ferma per qualche minuto, anche sulle postazioni. I dati di adesso restano da parte."
if ((Read-Host "Scrivi SI per continuare") -ne "SI") { exit }
try {
    Stop-Program
    Invoke-Django @("ripristina_backup", $file) -Owner -Interactive
    Invoke-Django @("migrate", "--noinput") -Owner
    Invoke-Django @("grant_runtime") -Owner
    Start-Program
    $config = Read-Json "config.json"
    Write-Host "`nRipristino completato. Se il backup viene da un altro PC, la licenza va rifatta con il nuovo codice installazione."
    Start-Process "$($config.origine)/"
}
catch {
    Write-Host "`nERRORE: $_" -ForegroundColor Red
    Write-Host "Riavvio il programma con i dati di prima."
    Start-Program
}
Read-Host "`nPremi Invio per chiudere"
