# 2.14.0: firma del codice (SHA-256 con marca temporale RFC 3161) dei file dati, solo se la build ha un certificato:
#   - .pfx: i secret WINDOWS_CERT_PFX (il file in base64) e WINDOWS_CERT_PASSWORD;
#   - Azure Artifact Signing (ex Trusted Signing): AZURE_TENANT_ID, AZURE_CLIENT_ID, AZURE_CLIENT_SECRET,
#     ARTIFACT_SIGNING_ENDPOINT, ARTIFACT_SIGNING_ACCOUNT, ARTIFACT_SIGNING_PROFILE.
# Senza, i file restano come sono e lo dice in una riga.   & windows/firma.ps1 -Nome "TD Licenze" -Files a.exe, b.dll
param(
    [string]$Nome = "TD Booking Suite",   # what Windows shows for the file, the MSI's UAC prompt included
    [Parameter(Mandatory)][string[]]$Files
)
$ErrorActionPreference = "Stop"
$Files = $Files | ForEach-Object { (Resolve-Path $_).Path }
if ($env:WINDOWS_CERT_PFX) {
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.*\x64\signtool.exe" | Sort-Object FullName | Select-Object -Last 1
    if (-not $signtool) { throw "signtool.exe non trovato: serve il Windows SDK" }
    $pfx = Join-Path ([IO.Path]::GetTempPath()) ([IO.Path]::GetRandomFileName())
    try {
        [IO.File]::WriteAllBytes($pfx, [Convert]::FromBase64String($env:WINDOWS_CERT_PFX))
        & $signtool.FullName sign /fd SHA256 /f $pfx /p $env:WINDOWS_CERT_PASSWORD /tr "http://timestamp.digicert.com" /td SHA256 /d $Nome @Files
        if ($LASTEXITCODE -ne 0) { throw "signtool non riuscito, codice $LASTEXITCODE" }
    }
    finally { Remove-Item $pfx -Force -ErrorAction SilentlyContinue }
}
elseif ($env:ARTIFACT_SIGNING_ACCOUNT) {
    Install-Module TrustedSigning -RequiredVersion 0.5.0 -Force -Scope CurrentUser
    Invoke-TrustedSigning -Endpoint $env:ARTIFACT_SIGNING_ENDPOINT -CodeSigningAccountName $env:ARTIFACT_SIGNING_ACCOUNT -CertificateProfileName $env:ARTIFACT_SIGNING_PROFILE `
        -Files ($Files -join ",") -FileDigest SHA256 -TimestampRfc3161 "http://timestamp.acs.microsoft.com" -TimestampDigest SHA256 -Description $Nome
}
else { Write-Host "::notice::Non firmato, manca il certificato (secret WINDOWS_CERT_PFX o ARTIFACT_SIGNING_ACCOUNT): $(($Files | Split-Path -Leaf) -join ', ')"; return }
Write-Host "Firmati: $(($Files | Split-Path -Leaf) -join ', ')"
