<#
.SYNOPSIS
    Bouwt AnsiWatch volledig: tests, self-contained single-file publish (win-x64) en de MSI.

.DESCRIPTION
    Resultaat: artifacts\AnsiWatch-<versie>.msi (versie uit Directory.Build.props).

    WiX Toolset v7 vereist dat u de Open Source Maintenance Fee (OSMF) EULA accepteert
    (https://wixtoolset.org/osmf/). Geef daarvoor -AcceptWixEula op, of zet de omgevingsvariabele
    ANSIWATCH_ACCEPT_WIX_EULA=1 (handig voor CI). Zonder acceptatie stopt de build vóór de MSI-stap.

.PARAMETER Configuration
    Build-configuratie (standaard Release).

.PARAMETER SkipTests
    Unit- en integratietests overslaan.

.PARAMETER AcceptWixEula
    Accepteer de WiX v7 OSMF EULA voor deze build.

.PARAMETER OutputDir
    Uitvoermap (standaard .\artifacts).

.EXAMPLE
    .\build.ps1 -AcceptWixEula
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string] $Configuration = 'Release',
    [switch] $SkipTests,
    [switch] $AcceptWixEula,
    [string] $OutputDir = (Join-Path $PSScriptRoot 'artifacts')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

function Write-Step([string] $Text) {
    Write-Host ''
    Write-Host "==> $Text" -ForegroundColor Cyan
}

function Invoke-DotNet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($args -join ' ') is mislukt (exitcode $LASTEXITCODE)."
    }
}

$root = $PSScriptRoot
Push-Location $root
try {
    $sw = [Diagnostics.Stopwatch]::StartNew()

    Write-Step 'Controleren van de .NET SDK'
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw '.NET SDK niet gevonden. Installeer de .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0'
    }
    Invoke-DotNet --version

    if (-not $IsWindows -and $PSVersionTable.PSEdition -eq 'Core') {
        Write-Warning 'De MSI (WiX) kan alleen op Windows gebouwd worden; tests en publish werken wel.'
    }

    $version = (& dotnet msbuild "$root\src\AnsiWatch.Core\AnsiWatch.Core.csproj" -nologo -getProperty:Version).Trim()
    if (-not $version) { throw 'Versie kon niet gelezen worden uit Directory.Build.props.' }
    Write-Host "Versie: $version"

    $publishDir = Join-Path $OutputDir 'publish'
    if (Test-Path $OutputDir) { Remove-Item $OutputDir -Recurse -Force }
    New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

    Write-Step 'Restore'
    Invoke-DotNet restore "$root\AnsiWatch.sln"

    if (-not $SkipTests) {
        Write-Step 'Tests (xUnit)'
        Invoke-DotNet test "$root\tests\AnsiWatch.Tests\AnsiWatch.Tests.csproj" -c $Configuration --no-restore `
            --logger 'console;verbosity=normal' --logger "trx;LogFileName=AnsiWatch.Tests.trx" `
            --results-directory (Join-Path $OutputDir 'test-results')
    }

    $publishArgs = @('-c', $Configuration, '-r', 'win-x64', '--self-contained', 'true',
        '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:EnableCompressionInSingleFile=true', '-o', $publishDir)

    Write-Step 'Publish AnsiWatch.Service (self-contained, win-x64, single-file)'
    Invoke-DotNet publish "$root\src\AnsiWatch.Service\AnsiWatch.Service.csproj" @publishArgs

    Write-Step 'Publish AnsiWatch.Config (self-contained, win-x64, single-file)'
    Invoke-DotNet publish "$root\src\AnsiWatch.Config\AnsiWatch.Config.csproj" @publishArgs

    Get-ChildItem $publishDir -Filter '*.pdb' | Remove-Item -Force
    foreach ($exe in 'AnsiWatch.Service.exe', 'AnsiWatch.Config.exe') {
        if (-not (Test-Path (Join-Path $publishDir $exe))) { throw "$exe ontbreekt in $publishDir." }
    }

    Write-Step 'MSI bouwen (WiX Toolset v7)'
    $eulaAccepted = $AcceptWixEula -or ($env:ANSIWATCH_ACCEPT_WIX_EULA -in @('1', 'true', 'yes', 'wix7'))
    if (-not $eulaAccepted) {
        throw @'
WiX Toolset v7 vereist acceptatie van de Open Source Maintenance Fee (OSMF) EULA:
https://wixtoolset.org/osmf/  (organisaties met omzet betalen een maintenance fee).
Lees de voorwaarden en start daarna:  .\build.ps1 -AcceptWixEula
'@
    }

    $msiOut = Join-Path $OutputDir 'msi'
    Invoke-DotNet build "$root\src\AnsiWatch.Installer\AnsiWatch.Installer.wixproj" -c $Configuration `
        "-p:AnsiWatchPublishDir=$publishDir\" '-p:AcceptEula=wix7' -o $msiOut

    $msi = Get-ChildItem $msiOut -Filter "AnsiWatch-$version*.msi" -Recurse | Select-Object -First 1
    if (-not $msi) { throw "AnsiWatch-$version.msi niet gevonden in $msiOut." }
    $target = Join-Path $OutputDir "AnsiWatch-$version.msi"
    Copy-Item $msi.FullName $target -Force

    $hash = (Get-FileHash $target -Algorithm SHA256).Hash
    Set-Content -Path "$target.sha256" -Value "$hash  AnsiWatch-$version.msi" -Encoding ascii

    $sw.Stop()
    Write-Host ''
    Write-Host "Klaar in $([int]$sw.Elapsed.TotalSeconds) s:" -ForegroundColor Green
    Write-Host "  MSI     : $target ($([math]::Round((Get-Item $target).Length / 1MB, 1)) MB)"
    Write-Host "  SHA-256 : $hash"
    Write-Host ''
    Write-Host 'Stille installatie (RMM):'
    Write-Host "  msiexec /i AnsiWatch-$version.msi SERVICEACCOUNT=`"DOMEIN\svc_ansiwatch`" SERVICEPASSWORD=`"...`" WATCHPATH=`"D:\Export`" /qn /l*v install.log"
}
finally {
    Pop-Location
}
