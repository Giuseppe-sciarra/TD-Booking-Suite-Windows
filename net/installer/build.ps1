# Costruisce l'MSI di TD Booking Suite 3 (il programma in C#). Gira su GitHub Actions (windows-latest) o su un PC
# Windows con .NET SDK 8 e PowerShell 7:   pwsh net/installer/build.ps1
# Risultato: dist\TD-Booking-Suite-<versione>.msi. Stesso MSI di prima per Windows (stesso codice di aggiornamento):
# installato sopra una 2.x la aggiorna e tiene dati, chiavi, licenza e certificati.
param(
    [string]$Version = "",
    [string]$PgVersion = "17.11",     # PostgreSQL per Windows, binari EDB
    [string]$WixVersion = "5.0.2"
)
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$net = Split-Path $PSScriptRoot -Parent
$root = Split-Path $net -Parent
$build = Join-Path $root "build"
$cache = Join-Path $build "cache"
$stage = Join-Path $build "stage"
$server = Join-Path $stage "server"
$postazione = Join-Path $stage "postazione"
$dist = Join-Path $root "dist"
if (-not $Version) { $Version = ([xml](Get-Content (Join-Path $net "Directory.Build.props"))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1 }
Remove-Item $stage, $dist -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $cache, $server, $postazione, $dist | Out-Null

function Get-Download([string]$Url, [string]$Name) {
    $file = Join-Path $cache $Name
    if (-not (Test-Path $file)) {
        Write-Host "Scarico $Url"
        Invoke-WebRequest -Uri $Url -OutFile "$file.part" -MaximumRetryCount 3 -RetryIntervalSec 5
        Move-Item "$file.part" $file
    }
    # shortcut: downloads come over HTTPS from the official sites; the hash is printed so a change shows in the log
    Write-Host "  SHA256 $((Get-FileHash $file -Algorithm SHA256).Hash)  $Name"
    return $file
}
function Invoke-Native([string]$Exe, [string[]]$Arguments) {
    & $Exe @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Exe $($Arguments -join ' ') non riuscito, codice $LASTEXITCODE" }
}

if (-not (Test-Path (Join-Path $net "src\TD.Core\Win\licenza-chiave-pubblica.txt"))) { throw "Manca net/src/TD.Core/Win/licenza-chiave-pubblica.txt: senza la chiave il programma non accetterebbe nessuna licenza." }

# 1. the program: server and worker are the same exe, with .NET inside (nothing to install on the PC)
Invoke-Native dotnet @("publish", (Join-Path $net "src\TD.Server\TD.Server.csproj"), "-c", "Release", "-r", "win-x64", "--self-contained", "true", "-nologo", "-p:Version=$Version", "-p:DebugType=none", "-o", (Join-Path $server "app"))
New-Item -ItemType Directory -Force (Join-Path $server "script") | Out-Null
Copy-Item (Join-Path $PSScriptRoot "script\*.ps1") (Join-Path $server "script")
foreach ($item in "LICENSE", "THIRD-PARTY.md") { Copy-Item (Join-Path $root "suite\$item") $server -ErrorAction SilentlyContinue }

# 2. PostgreSQL: server and tools only, without pgAdmin, StackBuilder, documentation and headers
$pgZip = $null
foreach ($n in 1..3) {
    try { $pgZip = Get-Download "https://get.enterprisedb.com/postgresql/postgresql-$PgVersion-$n-windows-x64-binaries.zip" "postgresql-$PgVersion-$n.zip"; break }
    catch { Write-Host "  postgresql-$PgVersion-$n non disponibile" }
}
if (-not $pgZip) { throw "PostgreSQL $PgVersion per Windows non trovato: controlla la versione su https://www.enterprisedb.com/download-postgresql-binaries" }
$pgTemp = Join-Path $build "pg"
Remove-Item $pgTemp -Recurse -Force -ErrorAction SilentlyContinue
Expand-Archive $pgZip $pgTemp
$pgsql = Join-Path $server "pgsql"
Move-Item (Join-Path $pgTemp "pgsql") $pgsql
foreach ($extra in "pgAdmin 4", "StackBuilder", "doc", "include", "symbols") { Remove-Item (Join-Path $pgsql $extra) -Recurse -Force -ErrorAction SilentlyContinue }

# 3. the window of the program, for the server PC and for every workstation
Invoke-Native dotnet @("publish", (Join-Path $net "src\TD.App\TD.App.csproj"), "-c", "Release", "-r", "win-x64", "--self-contained", "true", "-nologo", "-p:Version=$Version", "-p:DebugType=none", "-o", $postazione)
Copy-Item (Join-Path $net "src\TD.App\app.ico") $postazione -Force

# 3b. TD Cassa di prova, the register that prints nothing, as a zip next to the MSI (not installed by it)
$prova = Join-Path $stage "cassa-prova"
Invoke-Native dotnet @("build", (Join-Path $root "windows\cassa-prova\CassaProva.csproj"), "-c", "Release", "-nologo", "-p:Version=$Version", "-o", $prova)
Copy-Item (Join-Path $root "windows\cassa-prova\LEGGIMI.txt") $prova
Compress-Archive -Path (Join-Path $prova "TD-Cassa-di-prova.exe"), (Join-Path $prova "TD-Cassa-di-prova.exe.config"), (Join-Path $prova "LEGGIMI.txt") -DestinationPath (Join-Path $dist "TD-Cassa-di-prova-$Version.zip") -Force

# our own code signed before it goes into the MSI, when the build has a certificate (windows/firma.ps1)
$firma = Join-Path $root "windows\firma.ps1"
if ($env:WINDOWS_CERT_PFX -or $env:ARTIFACT_SIGNING_ACCOUNT) {
    & $firma -Files @((Join-Path $postazione "TDBookingSuite.exe"), (Join-Path $postazione "TDBookingSuite.dll"), (Join-Path $server "app\TDBookingServer.exe"), (Join-Path $server "app\TDBookingServer.dll"), (Join-Path $server "app\TD.Core.dll"))
}

# 4. the MSI: the same package and windows as the 2.x (windows/wix), so it installs over it as an update
Invoke-Native dotnet @("tool", "update", "--global", "wix", "--version", $WixVersion)
Invoke-Native wix @("extension", "add", "-g", "WixToolset.Util.wixext/$WixVersion", "WixToolset.UI.wixext/$WixVersion")
$wxs = @((Join-Path $root "windows\wix\Package.wxs"), (Join-Path $root "windows\wix\Modalita.wxs"))
$msi = Join-Path $dist "TD-Booking-Suite-$Version.msi"
$common = @("-arch", "x64", "-culture", "it-IT", "-ext", "WixToolset.Util.wixext", "-ext", "WixToolset.UI.wixext", "-bindpath", "server=$server", "-bindpath", "postazione=$postazione", "-d", "Version=$Version")
Invoke-Native wix (@("build") + $wxs + $common + @("-o", $msi))
& $firma -Files $msi  # without a certificate, the one line that says the build is not signed
Write-Host "Pronto: $msi ($([math]::Round((Get-Item $msi).Length / 1MB)) MB)"
if ($env:GITHUB_ACTIONS) {
    # a second build of the same files has another product code: smoke-test.ps1 uses it as a real upgrade
    Invoke-Native wix (@("build") + $wxs + $common + @("-o", (Join-Path $build "aggiornamento.msi")))
}
