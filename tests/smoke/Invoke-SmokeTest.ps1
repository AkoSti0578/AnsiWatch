<#
.SYNOPSIS
    End-to-end test van de MSI op een (wegwerp-)Windows-machine, bijvoorbeeld een CI-runner.
    Installeert stil met een lokaal serviceaccount, controleert service, ACL's, Event Log en
    conversie, en deïnstalleert weer. NIET op een productiemachine draaien.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Msi,
    [string] $WatchPath = 'C:\AnsiWatchSmoke\export',
    [string] $LogDir = (Join-Path $PSScriptRoot '..\..\artifacts\smoke')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $LogDir, $WatchPath | Out-Null
$Msi = (Resolve-Path $Msi).Path
$failures = [System.Collections.Generic.List[string]]::new()

function Check([string] $Name, [scriptblock] $Test) {
    try {
        if (& $Test) { Write-Host "  [OK]   $Name" -ForegroundColor Green }
        else { Write-Host "  [FOUT] $Name" -ForegroundColor Red; $failures.Add($Name) }
    } catch {
        Write-Host "  [FOUT] $Name – $($_.Exception.Message)" -ForegroundColor Red; $failures.Add($Name)
    }
}

function Wait-Until([scriptblock] $Condition, [int] $TimeoutSeconds = 30) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        if (& $Condition) { return $true }
        Start-Sleep -Milliseconds 200
    }
    return [bool](& $Condition)
}

# Lokaal testaccount (geen admin nodig: alleen rechten op de map).
$user = 'svc_ansiwatch'
$password = 'Aw!' + [Guid]::NewGuid().ToString('N').Substring(0, 16) + 'x9'
if (-not (Get-LocalUser -Name $user -ErrorAction SilentlyContinue)) {
    New-LocalUser -Name $user -Password (ConvertTo-SecureString $password -AsPlainText -Force) -PasswordNeverExpires | Out-Null
} else {
    Set-LocalUser -Name $user -Password (ConvertTo-SecureString $password -AsPlainText -Force)
}
icacls $WatchPath /grant "${env:COMPUTERNAME}\${user}:(OI)(CI)M" | Out-Null

Write-Host "== Verkeerd wachtwoord moet de installatie vroegtijdig laten mislukken"
$p = Start-Process msiexec -Wait -PassThru -ArgumentList @('/i', "`"$Msi`"", "SERVICEACCOUNT=.\$user", 'SERVICEPASSWORD=fout', '/qn', '/l*v', "`"$LogDir\install-wrongpw.log`"")
Check 'installatie met fout wachtwoord faalt (1603)' { $p.ExitCode -eq 1603 }
Check 'MSI-log noemt de reden' { Select-String -Path "$LogDir\install-wrongpw.log" -Pattern 'Onjuiste gebruikersnaam of wachtwoord' -Quiet }
Check 'wachtwoord staat niet in de MSI-log' { -not (Select-String -Path "$LogDir\install-wrongpw.log" -Pattern 'SERVICEPASSWORD=fout' -SimpleMatch -Quiet) }

Write-Host "== Stille installatie"
$p = Start-Process msiexec -Wait -PassThru -ArgumentList @('/i', "`"$Msi`"", "SERVICEACCOUNT=.\$user", "SERVICEPASSWORD=$password", "WATCHPATH=$WatchPath", '/qn', '/l*v', "`"$LogDir\install.log`"")
Check "msiexec exitcode 0 (was $($p.ExitCode))" { $p.ExitCode -eq 0 }
Check 'wachtwoord staat niet in de MSI-log' { -not (Select-String -Path "$LogDir\install.log" -Pattern $password -SimpleMatch -Quiet) }

$svc = Get-CimInstance Win32_Service -Filter "Name='AnsiWatch'"
Check 'service bestaat' { $null -ne $svc }
Check 'weergavenaam' { $svc.DisplayName -eq 'AnsiWatch – UTF-16LE naar ANSI converter' }
Check "draait als .\$user ($($svc.StartName))" { $svc.StartName -like "*\$user" }
Check 'automatisch, vertraagd' { $svc.StartMode -eq 'Auto' -and (Get-ItemPropertyValue "HKLM:\SYSTEM\CurrentControlSet\Services\AnsiWatch" -Name DelayedAutostart) -eq 1 }
Check 'service draait' { Wait-Until { (Get-Service AnsiWatch).Status -eq 'Running' } 30 }
Check 'herstelacties: 3x herstart na 60 s' { (sc.exe qfailure AnsiWatch | Out-String) -match 'RESTART -- Delay = 60000' }
Check 'bestanden in Program Files' { (Test-Path "$env:ProgramFiles\AnsiWatch\AnsiWatch.Service.exe") -and (Test-Path "$env:ProgramFiles\AnsiWatch\AnsiWatch.Config.exe") }
Check 'startmenu-snelkoppeling' { Test-Path "$env:ProgramData\Microsoft\Windows\Start Menu\Programs\AnsiWatch configuratie.lnk" }
Check 'initiële config.json met WATCHPATH' { (Get-Content "$env:ProgramData\AnsiWatch\config.json" -Raw | ConvertFrom-Json).WatchPaths[0].Path -eq $WatchPath }
Check 'ACL ProgramData\AnsiWatch: serviceaccount volledige rechten' {
    (Get-Acl "$env:ProgramData\AnsiWatch").Access | Where-Object { $_.IdentityReference -like "*\$user" -and $_.FileSystemRights -match 'FullControl' }
}
Check 'ACL ProgramData\AnsiWatch: Administrators volledige rechten' {
    (Get-Acl "$env:ProgramData\AnsiWatch").Access | Where-Object { $_.IdentityReference -match 'Administrators' -and $_.FileSystemRights -match 'FullControl' }
}
Check 'Event Log: startmelding (1000)' { Wait-Until { Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'AnsiWatch'; Id = 1000 } -MaxEvents 1 -ErrorAction SilentlyContinue } 30 }

Write-Host "== Conversie"
Start-Sleep -Seconds 2
$text = "Één café, crème brûlée, naïef, façade: € 12,50`r`nLF-regel`nlaatste"
$file = Join-Path $WatchPath 'smoke.txt'
$sw = [Diagnostics.Stopwatch]::StartNew()
[IO.File]::WriteAllText($file, $text, [Text.UnicodeEncoding]::new($false, $true))
$expected = [Text.Encoding]::GetEncoding(1252).GetBytes($text)
Check 'UTF-16LE wordt ANSI (byte voor byte)' {
    Wait-Until { try { [Convert]::ToBase64String([IO.File]::ReadAllBytes($file)) -eq [Convert]::ToBase64String($expected) } catch { $false } } 10
}
Write-Host "     conversie na $([int]$sw.Elapsed.TotalMilliseconds) ms"

$bad = Join-Path $WatchPath 'pools.txt'
[IO.File]::WriteAllText($bad, 'Łódź 😀', [Text.UnicodeEncoding]::new($false, $true))
Check 'Event Log: waarschuwing niet-representeerbaar (2003)' {
    Wait-Until { Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'AnsiWatch'; Id = 2003 } -MaxEvents 1 -ErrorAction SilentlyContinue } 15
}
Check 'bestand met Ł/emoji ongewijzigd (Skip)' { [IO.File]::ReadAllBytes($bad)[0] -eq 0xFF }
Check 'logbestand met conversieregel' { Wait-Until { Select-String -Path "$env:ProgramData\AnsiWatch\logs\ansiwatch-*.log" -Pattern 'Geconverteerd: .*smoke.txt' -Quiet } 10 }
Check 'status.json wordt bijgewerkt' { Wait-Until { (Get-Content "$env:ProgramData\AnsiWatch\status.json" -Raw | ConvertFrom-Json).Converted -ge 1 } 15 }

Write-Host "== Deïnstallatie"
$p = Start-Process msiexec -Wait -PassThru -ArgumentList @('/x', "`"$Msi`"", '/qn', '/l*v', "`"$LogDir\uninstall.log`"")
Check "msiexec /x exitcode 0 (was $($p.ExitCode))" { $p.ExitCode -eq 0 }
Check 'service verwijderd' { -not (Get-Service AnsiWatch -ErrorAction SilentlyContinue) }
Check 'Program Files\AnsiWatch verwijderd' { -not (Test-Path "$env:ProgramFiles\AnsiWatch\AnsiWatch.Service.exe") }
Check 'config.json blijft staan' { Test-Path "$env:ProgramData\AnsiWatch\config.json" }
Check 'logs blijven staan' { (Get-ChildItem "$env:ProgramData\AnsiWatch\logs" -Filter *.log).Count -ge 1 }
Check 'gebruikersaccount NIET verwijderd' { $null -ne (Get-LocalUser -Name $user -ErrorAction SilentlyContinue) }

Copy-Item "$env:ProgramData\AnsiWatch\logs\*" $LogDir -ErrorAction SilentlyContinue

if ($failures.Count -gt 0) {
    throw "Smoke-test: $($failures.Count) controle(s) mislukt: $($failures -join '; ')"
}
Write-Host "Smoke-test geslaagd." -ForegroundColor Green
