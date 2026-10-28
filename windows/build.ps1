# Costruisce l'MSI di TD Booking Suite per Windows. Gira su GitHub Actions (windows-latest) o su un PC Windows con
# Python 3.14, .NET SDK 8 e PowerShell 7:   pwsh windows/build.ps1
# Risultato: dist\TD-Booking-Suite-<versione>.msi
param(
    [string]$Version = "",
    [string]$PgVersion = "17.11",     # PostgreSQL per Windows, binari EDB
    [string]$WinSWVersion = "2.12.0",
    [string]$WixVersion = "5.0.2"
)
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$root = Split-Path $PSScriptRoot -Parent
$build = Join-Path $root "build"
$cache = Join-Path $build "cache"
$stage = Join-Path $build "stage"
$server = Join-Path $stage "server"
$postazione = Join-Path $stage "postazione"
$dist = Join-Path $root "dist"
if (-not $Version) { $Version = (Get-Content (Join-Path $root "suite\VERSION")).Trim() }
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

if (-not (Test-Path (Join-Path $root "tdwin\licenza-chiave-pubblica.txt"))) {
    # a release without the key would accept no licence at all: its customers would stop after the trial
    if ($env:GITHUB_REF -like "refs/tags/*") { throw "Manca tdwin/licenza-chiave-pubblica.txt: un rilascio senza la chiave non accetterebbe nessuna licenza." }
    Write-Host "::warning::Manca tdwin/licenza-chiave-pubblica.txt: questa build accetta solo la prova di 7 giorni, nessuna licenza."
}

# 1. Python embeddable, the same version as the Python running this build, with the libraries next to it
$pyVersion = (& python -c "import platform;print(platform.python_version())").Trim()
$pyTag = (& python -c "import sys;print(f'{sys.version_info[0]}{sys.version_info[1]}')").Trim()
$pyZip = Get-Download "https://www.python.org/ftp/python/$pyVersion/python-$pyVersion-embed-amd64.zip" "python-$pyVersion-embed-amd64.zip"
Expand-Archive $pyZip (Join-Path $server "python")
Set-Content (Join-Path $server "python\python$pyTag._pth") -Encoding ASCII -Value @("python$pyTag.zip", ".", "Lib\site-packages", "..\app", "..\app\suite", "import site")
Invoke-Native python @("-m", "pip", "install", "--disable-pip-version-check", "--no-cache-dir", "--only-binary=:all:", "--target", (Join-Path $server "python\Lib\site-packages"), "-r", (Join-Path $root "windows\requirements.txt"))

# 2. the program: core and Windows edition, without tests, compiled ahead because the services cannot write there
$app = Join-Path $server "app"
New-Item -ItemType Directory -Force (Join-Path $app "suite") | Out-Null
foreach ($item in "core", "tdbooking", "templates", "static", "scripts", "VERSION", "LICENSE", "THIRD-PARTY.md") {
    Copy-Item (Join-Path $root "suite\$item") (Join-Path $app "suite") -Recurse
}
Copy-Item (Join-Path $root "tdwin") $app -Recurse
Get-ChildItem $app -Recurse -Directory -Filter "__pycache__" | Remove-Item -Recurse -Force
Invoke-Native python @((Join-Path $root "windows\build_static.py"))
Copy-Item (Join-Path $root "static-collected") $app -Recurse
Invoke-Native python @("-m", "compileall", "-q", "-j", "0", $app)
New-Item -ItemType Directory -Force (Join-Path $server "script"), (Join-Path $server "servizi") | Out-Null
Copy-Item (Join-Path $root "windows\script\*.ps1") (Join-Path $server "script")

# 3. PostgreSQL: server and tools only, without pgAdmin, StackBuilder, documentation and headers
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

# 4. services: WinSW with its configuration, one copy per service
$winsw = Get-Download "https://github.com/winsw/winsw/releases/download/v$WinSWVersion/WinSW-x64.exe" "WinSW-$WinSWVersion-x64.exe"
foreach ($name in "TDBookingWeb", "TDBookingWorker") {
    Copy-Item $winsw (Join-Path $server "servizi\$name.exe")
    Copy-Item (Join-Path $root "windows\servizi\$name.xml") (Join-Path $server "servizi")
}

# 5. the workstation program
Invoke-Native dotnet @("build", (Join-Path $root "windows\client\TDBookingSuite.csproj"), "-c", "Release", "-nologo", "-p:Version=$Version", "-o", $postazione)
Get-ChildItem $postazione -Include "*.pdb", "Microsoft.Web.WebView2.Wpf.dll" -Recurse | Remove-Item -Force
Copy-Item (Join-Path $root "windows\client\app.ico") $postazione

# 6. the MSI
Invoke-Native dotnet @("tool", "update", "--global", "wix", "--version", $WixVersion)
Invoke-Native wix @("extension", "add", "-g", "WixToolset.Util.wixext/$WixVersion", "WixToolset.UI.wixext/$WixVersion")
$msi = Join-Path $dist "TD-Booking-Suite-$Version.msi"
Invoke-Native wix @("build", (Join-Path $root "windows\wix\Package.wxs"), "-arch", "x64", "-culture", "it-IT",
    "-ext", "WixToolset.Util.wixext", "-ext", "WixToolset.UI.wixext",
    "-bindpath", "server=$server", "-bindpath", "postazione=$postazione", "-d", "Version=$Version", "-o", $msi)
Write-Host "Pronto: $msi ($([math]::Round((Get-Item $msi).Length / 1MB)) MB)"
if ($env:GITHUB_ACTIONS) {
    # a second build of the same files has another product code: smoke-test.ps1 uses it as a real upgrade
    Invoke-Native wix @("build", (Join-Path $root "windows\wix\Package.wxs"), "-arch", "x64", "-culture", "it-IT",
        "-ext", "WixToolset.Util.wixext", "-ext", "WixToolset.UI.wixext",
        "-bindpath", "server=$server", "-bindpath", "postazione=$postazione", "-d", "Version=$Version", "-o", (Join-Path $build "aggiornamento.msi"))
}
