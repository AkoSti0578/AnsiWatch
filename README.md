# AnsiWatch

AnsiWatch is een Windows-service die één of meer mappen bewaakt op `.txt`-bestanden in **UTF-16LE** en die ze **binnen ongeveer een seconde** in place omzet naar **ANSI** (standaard Windows-1252). Alleen de codering verandert. De tekst, de regeleinden (CRLF/LF), de bestandsnaam, de locatie, de ACL's en (instelbaar) de tijdstempels blijven gelijk.

```
extern proces ──► D:\Export\order.txt (UTF-16LE, FF FE …)
                         │  FileSystemWatcher + vangnet-scan
                         ▼
                 debounce 400 ms ─► exclusief openen + grootte stabiel?
                         ▼
          detectie ─► controle niet-representeerbare tekens ─► .ansiwatch.tmp
                         ▼                                         │ verificatie (SHA-256)
                 File.Replace (atomair) ◄─────────────────────────┘
                         ▼
                 D:\Export\order.txt (ANSI 1252, zonder BOM)
```

## Inhoud

- [Kenmerken](#kenmerken)
- [Installatie](#installatie)
- [Rechten van het serviceaccount](#rechten-van-het-serviceaccount)
- [Configuratie](#configuratie)
- [Logging en Event Log](#logging-en-event-log)
- [Troubleshooting](#troubleshooting)
- [Bouwen vanaf de broncode](#bouwen-vanaf-de-broncode)
- [Architectuur](#architectuur)

## Kenmerken

- **Veilige detectie.** AnsiWatch converteert alleen echte UTF-16LE: primair op basis van de BOM `FF FE`, en optioneel ook zonder BOM via een heuristiek (veel `0x00` op oneven posities). ANSI, UTF-8, UTF-16BE, UTF-32 en binaire bestanden blijven byte voor byte ongemoeid. De eigen output wordt daardoor nooit opnieuw geconverteerd, dus er ontstaat geen loop.
- **Wacht tot het bestand klaar is.** Na een debounce per bestand opent AnsiWatch het bestand met `FileShare.None`, met back-off tot 30 seconden. Er wordt pas geconverteerd als de grootte tussen twee metingen gelijk blijft.
- **Inhoud exact behouden.** Er wordt niets getrimd, regeleinden blijven ongewijzigd en de output krijgt geen BOM. Na het schrijven decodeert AnsiWatch het resultaat terug en vergelijkt het via een SHA-256-hash met de brontekst (`VerifyAfterWrite`).
- **Niet-representeerbare tekens** (bijvoorbeeld `Ł` of emoji) worden vóór het schrijven gecontroleerd met `EncoderFallback.ExceptionFallback`. Er zijn drie opties: Skip (eventueel met quarantaine), Replace (`?`) of BestFit. De waarschuwing vermeldt regel en positie.
- **Atomair schrijven.** AnsiWatch schrijft naar `<naam>.<id>.ansiwatch.tmp` in dezelfde map en vervangt daarna het origineel met `File.Replace`. Gaat er iets mis, dan blijft het origineel intact en wordt het tijdelijke bestand opgeruimd.
- **Streaming.** Grote bestanden (getest met meer dan 50 MB) worden in blokken van 64 KB verwerkt, met een constant en klein geheugengebruik.
- **Robuust op shares.** Naast de FileSystemWatcher (64 KB buffer, volledige rescan bij overflow) draait een periodieke vangnet-scan. Offline paden worden gelogd en periodiek opnieuw geprobeerd; de service crasht daar niet door.
- **Live configuratie.** Wijzigingen in `config.json` worden zonder herstart overgenomen. Een ongeldige config wordt geweigerd en de vorige configuratie blijft actief.
- **Configuratietool** met live status, een rechtencontrole voor het serviceaccount (effectieve NTFS-rechten via de Windows Authz-API) en een testconversie.
- **MSI** voor interactieve en stille installatie (RMM), met gMSA-ondersteuning, service recovery en vertraagd automatisch starten.

## Installatie

Vereisten: 64-bits Windows 10/11 of Windows Server 2016 of nieuwer. Een .NET-runtime is niet nodig, want beide exe's zijn self-contained.

### Interactief

1. Dubbelklik op `AnsiWatch-x.y.z.msi`.
2. Kies de installatiemap (standaard `C:\Program Files\AnsiWatch`).
3. Vul in het scherm **Serviceaccount** de volgende velden in:
   - **Account**: `DOMEIN\gebruiker`, `.\gebruiker` (lokaal) of `DOMEIN\gmsa$` (gMSA). Laat je het veld leeg, dan draait de service als LocalSystem.
   - **Wachtwoord**: dit veld is verborgen en komt niet in de MSI-log. Een gMSA heeft geen wachtwoord nodig.
   - Optioneel een **te bewaken map**. Daarmee wordt bij de eerste installatie een `config.json` aangemaakt.

   Bij **Volgende** worden de aanmeldgegevens direct gecontroleerd. Een typfout zie je dus vóór de installatie.
4. Na de installatie kun je de configuratietool direct openen. Daarna vind je die in het startmenu als **AnsiWatch configuratie**.

### Stil (RMM / GPO / Intune)

```bat
msiexec /i AnsiWatch-1.0.0.msi SERVICEACCOUNT="DOMEIN\svc_ansiwatch" SERVICEPASSWORD="..." WATCHPATH="D:\Export" /qn /l*v C:\Windows\Temp\ansiwatch-install.log
```

| Property | Betekenis |
|---|---|
| `SERVICEACCOUNT` | `DOMEIN\gebruiker`, `.\gebruiker`, `gebruiker@domein.nl`, `DOMEIN\gmsa$`, `LocalSystem` of leeg (= LocalSystem). |
| `SERVICEPASSWORD` | Wachtwoord. Het is gemarkeerd als `Hidden` en wordt niet gelogd. Bij een gMSA laat je het weg. |
| `WATCHPATH` | Optioneel. Maakt bij een eerste installatie `config.json` aan. Meerdere paden scheid je met `;`. Een bestaande config wordt nooit overschreven. |
| `WATCHSUBFOLDERS` | `1` neemt ook submappen van `WATCHPATH` mee. |
| `SKIPACCOUNTVALIDATION` | `1` slaat de wachtwoordcontrole vooraf over, bijvoorbeeld wanneer geen domeincontroller bereikbaar is. |

Voorbeeld met een gMSA en meerdere paden:

```bat
msiexec /i AnsiWatch-1.0.0.msi SERVICEACCOUNT="CONTOSO\gmsa_ansiwatch$" WATCHPATH="D:\Export;\\fs01\export\out" WATCHSUBFOLDERS=1 /qn
```

Exitcodes: `0` betekent geslaagd, `3010` geslaagd maar herstart nodig, en `1603` dat de installatie mislukt is. Bij `1603` staat de reden in de log. Zoek daarvoor naar `AnsiWatch:`, bijvoorbeeld *"Onjuiste gebruikersnaam of wachtwoord"*.

### Wat de installer doet

- Installeert `AnsiWatch.Service.exe` en `AnsiWatch.Config.exe` in `%ProgramFiles%\AnsiWatch`, en zet een startmenu-snelkoppeling.
- Registreert de service **AnsiWatch** (*"AnsiWatch – UTF-16LE naar ANSI converter"*) als **automatisch (vertraagd starten)**.
- Stelt **service recovery** in: herstarten bij de eerste, tweede en volgende fout, telkens na 60 seconden. Dat geldt ook bij een stop met een foutcode.
- Kent het serviceaccount het recht **Aanmelden als service** (*Log on as a service*) toe. Het account zelf wordt nooit aangemaakt, gewijzigd of verwijderd.
- Zet de ACL op `%ProgramData%\AnsiWatch`: Administrators, SYSTEM en het serviceaccount krijgen volledige rechten.
- Registreert de Event Log-bron **AnsiWatch** in het Application-logboek.

### Upgrade en deïnstallatie

- **Upgrade**: installeer de nieuwe MSI over de oude heen (MajorUpgrade). `config.json` en de logs blijven behouden. Het eerder gebruikte account wordt automatisch voorgesteld. Het **wachtwoord** moet je opnieuw meegeven, omdat AnsiWatch wachtwoorden nooit opslaat. Bij een gMSA of LocalSystem is dat niet nodig.
  ```bat
  msiexec /i AnsiWatch-1.1.0.msi SERVICEPASSWORD="..." /qn
  ```
- **Deïnstallatie**: gebruik *Apps en onderdelen* of `msiexec /x AnsiWatch-1.0.0.msi /qn`. `%ProgramData%\AnsiWatch` (config en logs) blijft bewust staan. Verwijder die map handmatig als je alles kwijt wilt.

## Rechten van het serviceaccount

De service converteert in place. Het account heeft daarom op **elke bewaakte map** de volgende rechten nodig:

| Recht | Waarom |
|---|---|
| Map weergeven / lezen | Bestanden vinden en lezen (watcher en scan). |
| Bestanden maken / schrijven | Het tijdelijke `.ansiwatch.tmp`-bestand. |
| Verwijderen (of *Subfolders and files verwijderen*) | `File.Replace` van het origineel. |
| Kenmerken schrijven | Tijdstempels herstellen. |

In de praktijk betekent dit **NTFS: Wijzigen (Modify)** op de map, met overerving naar bestanden.

**Bij netwerkshares (UNC):**

- Geef het account **zowel sharerechten (minimaal Wijzigen) als NTFS-rechten (Wijzigen)**. De strengste van de twee geldt.
- Gebruik **UNC-paden** (`\\server\share\map`), geen driveletters. Een service ziet de netwerkschijven van een ingelogde gebruiker niet.
- **LocalSystem** benadert shares als het computeraccount (`DOMEIN\COMPUTERNAAM$`). Gebruik bij voorkeur een domeinaccount of een **gMSA**.
- Optioneel: rechten op de back-upmap en de quarantainemap (Wijzigen).

De configuratietool controleert dit met **Mappen → Paden en rechten controleren**. Die controle berekent de *effectieve* rechten van het serviceaccount (inclusief groepen en deny-regels) en niet die van de ingelogde beheerder. Sharerechten worden daarbij niet meegenomen.

De spec gaat uit van een (admin)account. AnsiWatch werkt echter ook met een gewoon account met de rechten hierboven, en dat is veiliger.

## Configuratie

Het configuratiebestand is `%ProgramData%\AnsiWatch\config.json`. Je bewerkt het bij voorkeur met **AnsiWatch configuratie**, maar met de hand kan ook. Wijzigingen worden binnen enkele seconden overgenomen, zonder herstart.

```json
{
  "WatchPaths": [
    { "Path": "D:\\Export", "IncludeSubfolders": false, "Enabled": true },
    { "Path": "\\\\fs01\\export\\out", "IncludeSubfolders": true, "Enabled": true }
  ],
  "CodePage": 1252,
  "UnmappableCharacterAction": "Skip",
  "DetectWithoutBom": false,
  "PreserveTimestamps": true,
  "BackupFolder": "E:\\AnsiWatch\\backup",
  "BackupRetentionDays": 30,
  "QuarantineFolder": "E:\\AnsiWatch\\quarantaine",
  "RescanIntervalSeconds": 30,
  "LogLevel": "Information",
  "DebounceMilliseconds": 400,
  "LockTimeoutSeconds": 30,
  "MaxParallelism": 4,
  "VerifyAfterWrite": true
}
```

| Instelling | Standaard | Betekenis |
|---|---|---|
| `WatchPaths` | – | Lijst met mappen. Per map: `Path` (lokaal of UNC), `IncludeSubfolders` en `Enabled`. |
| `CodePage` | `1252` | Doel-codepage, bijvoorbeeld 1250 (Centraal-Europees) of 1251 (Cyrillisch). Unicode-codepages (65001, 1200, …) worden geweigerd. |
| `UnmappableCharacterAction` | `Skip` | Zie de tabel hieronder. |
| `DetectWithoutBom` | `false` | Herkent ook UTF-16LE zonder BOM met een heuristiek: ten minste 60 % `0x00` op oneven en hoogstens 2 % op even posities, en een even lengte. |
| `PreserveTimestamps` | `true` | Herstelt na de conversie de originele CreationTime en LastWriteTime. |
| `BackupFolder` | leeg | Hier komt vóór de conversie een kopie van het origineel, in `yyyy-MM-dd\`-submappen. |
| `BackupRetentionDays` | `30` | Back-ups ouder dan dit aantal dagen worden elk uur opgeruimd. `0` betekent nooit opruimen. |
| `QuarantineFolder` | leeg | Alleen bij `Skip`: niet-converteerbare bestanden worden hierheen verplaatst, met een `.ansiwatch-rapport.log` ernaast. |
| `RescanIntervalSeconds` | `30` | Interval van de vangnet-scan (5–86400). Deze scan is nodig voor SMB-shares. |
| `LogLevel` | `Information` | Kies uit `Verbose`, `Debug`, `Information`, `Warning`, `Error` of `Fatal`. Wijzigingen gaan live in. |
| `DebounceMilliseconds` | `400` | Wachttijd na het laatste event voor een bestand. |
| `LockTimeoutSeconds` | `30` | Maximale wachttijd op een gelockt of groeiend bestand. Daarna volgt een nieuwe poging met back-off (1, 2, 4 … 30 minuten). |
| `MaxParallelism` | `4` | Aantal gelijktijdige conversies. Een wijziging gaat pas in na een herstart van de service. |
| `VerifyAfterWrite` | `true` | Decodeert de output terug en vergelijkt die met de bron voordat het origineel vervangen wordt. |

**UnmappableCharacterAction**

| Waarde | Gedrag |
|---|---|
| `Skip` | Het bestand wordt **niet** geconverteerd en er volgt een waarschuwing (event 2003) met regel, positie en codepoint. Met een `QuarantineFolder` wordt het bestand daarnaartoe verplaatst (event 2004). |
| `Replace` | Het bestand wordt geconverteerd en niet-representeerbare tekens worden `?`. Je krijgt een waarschuwing (event 2001) met regel en positie. |
| `BestFit` | Het bestand wordt geconverteerd met de Windows best-fit-tabel (`Ł`→`L`, `ő`→`o`). Wat geen best-fit heeft, wordt `?`. Je krijgt een waarschuwing (event 2002). |

### Configuratietool (AnsiWatch.Config.exe)

De tool start altijd als administrator (via het manifest) en heeft vier tabbladen:

- **Mappen**: mappen toevoegen met een mapkiezer of als UNC-pad, verwijderen, (de)activeren en submappen aan- of uitzetten. Je ziet de live status per pad (*Actief* of *Offline*) en kunt de rechten van het serviceaccount controleren.
- **Instellingen**: alle instellingen uit de tabel hierboven, met validatie.
- **Service**: status, account en opstarttype, de knoppen **Start**, **Stop** en **Herstart**, en **Log openen**, **Logmap openen**, **Event Viewer** en **config.json openen**. Daarnaast live tellers en de laatste 50 conversies en waarschuwingen (uit `status.json`).
- **Testconversie**: kies een bestand en zie de gedetecteerde codering, de verwachte uitkomst, alle niet-representeerbare tekens (regel, positie, U+xxxx), regeleinden, grootte vóór en na, en een voorbeeld van het ANSI-resultaat plus hex. Daarbij gelden de instellingen zoals ze op dat moment in het venster staan. **Er wordt niets opgeslagen.**

### Commandoregel

```bat
AnsiWatch.Service.exe convert D:\test.txt --dry-run      :: testconversie, niets wijzigen
AnsiWatch.Service.exe convert D:\test.txt --action Replace
AnsiWatch.Service.exe status                              :: live status van de draaiende service
AnsiWatch.Service.exe init-config --watch-path "D:\Export" [--include-subfolders] [--force]
AnsiWatch.Service.exe                                     :: buiten de SCM: draait in de console (diagnose)
```

## Logging en Event Log

- **Logbestand**: `%ProgramData%\AnsiWatch\logs\ansiwatch-JJJJMMDD.log`. Er komt dagelijks een nieuw bestand bij en er worden 30 bestanden bewaard. Per conversie zie je het pad, de grootte, de duur en het resultaat, bijvoorbeeld:
  ```
  2026-11-24 10:15:02.481 +01:00 [INF] (2000) Geconverteerd: D:\Export\order.txt | 1048578 → 524288 bytes | 612 ms (waarvan 204 ms wachten) | resultaat: Converted | geverifieerd
  ```
- **Windows Event Log** (*Application*, bron **AnsiWatch**): hier komen start, stop, configuratie, waarschuwingen en fouten. Gewone conversies staan alleen in het logbestand, zodat het Event Log niet volloopt.

| ID | Niveau | Betekenis |
|---|---|---|
| 1000 | Informatie | Service gestart (versie, account, configpad). |
| 1001 | Informatie | Service gestopt. |
| 1002 | Informatie | Configuratie (opnieuw) geladen. |
| 1003 | Fout | `config.json` is ongeldig. **De vorige configuratie blijft actief.** |
| 1004 | Waarschuwing | Configuratiewaarschuwing, bijvoorbeeld geen actieve paden of een gewijzigde MaxParallelism. |
| 1900 | Fout/Kritiek | Onverwachte fout in de service. Windows herstart de service volgens de herstelinstellingen. |
| 2000 | Informatie | Bestand geconverteerd (alleen in het logbestand). Op niveau Waarschuwing: een fallback (bijv. Move i.p.v. Replace) of tijdstempels konden niet hersteld worden. |
| 2001 | Waarschuwing | Geconverteerd met `?`-vervangingen (Replace), met regel en positie. |
| 2002 | Waarschuwing | Geconverteerd met best-fit (BestFit). |
| 2003 | Waarschuwing | **Niet** geconverteerd: niet-representeerbare tekens (Skip). Het bestand is ongewijzigd. |
| 2004 | Waarschuwing | Niet geconverteerd en naar quarantaine verplaatst. |
| 2005 | Waarschuwing | Het bestand bleef langer dan `LockTimeoutSeconds` in gebruik. AnsiWatch probeert het later opnieuw. |
| 2006 | Waarschuwing | De BOM zegt UTF-16LE maar de inhoud is ongeldig (oneven lengte, losse surrogaten). Het bestand is ongewijzigd. |
| 2007 | Fout | Conversie mislukt (bijv. geen schrijfrechten of een verificatiefout). **Het origineel is intact.** |
| 2010 | Debug | Overgeslagen: geen UTF-16LE of een leeg bestand (alleen in het logbestand, op niveau Debug). |
| 3000 | Informatie | Bewaking van een pad gestart. |
| 3001 | Waarschuwing | Pad niet bereikbaar (share offline, map bestaat niet). AnsiWatch probeert het elke rescan-interval opnieuw. |
| 3002 | Informatie | Pad weer bereikbaar. De bewaking is hervat en er volgt een volledige scan. |
| 3003 | Waarschuwing | Buffer-overflow van de watcher. Er volgt een volledige rescan, dus er gaat niets verloren. |
| 3004 | Waarschuwing | Fout in de watcher, meestal een weggevallen netwerkverbinding. De bewaking wordt opnieuw opgebouwd. |
| 3005 | Informatie | Een pad wordt niet meer bewaakt (configwijziging). |
| 3006 | Informatie | De vangnet-scan vond bestanden die de watcher gemist had. Op shares is dat normaal. |
| 4000 | Informatie | Verlopen back-ups opgeruimd. |
| 4001 | Waarschuwing | Opruimen van back-ups mislukt. |
| 4002 | Informatie | Verweesde `.ansiwatch.tmp`-bestanden opgeruimd (na een crash of stroomstoring). |

Monitoringtip voor RMM: alarmeer op bron `AnsiWatch` met ID's **1003, 1900, 2003, 2004, 2005, 2007 en 3001**.

## Troubleshooting

| Symptoom | Oorzaak en oplossing |
|---|---|
| Installatie faalt met 1603: *Onjuiste gebruikersnaam of wachtwoord* | Controleer `SERVICEACCOUNT` en `SERVICEPASSWORD`. Gebruik `DOMEIN\gebruiker` of `.\gebruiker`. |
| Installatie faalt met *wachtwoord opnieuw opgeven* (upgrade) | Geef bij een upgrade `SERVICEPASSWORD` opnieuw mee. |
| Installatie faalt met 1920/1923 (*service kon niet starten*) | Het account mag zich niet als service aanmelden, bijvoorbeeld door een GPO die *Log on as a service* overschrijft. Voeg het account toe in die GPO. |
| Een bestand wordt niet geconverteerd | Doe een **Testconversie** in de configuratietool. Mogelijke oorzaken: geen BOM (zet `DetectWithoutBom` aan), al ANSI of UTF-8 (wordt bewust overgeslagen), niet-representeerbare tekens (event 2003), of het bestand is nog gelockt (2005). |
| Offline-melding (3001) voor een share | Is de share bereikbaar vanaf de server? Heeft het **serviceaccount** (niet jij) rechten? Gebruik een UNC-pad, geen driveletter. |
| Event 2007 *Geen toegang* | Het serviceaccount mist het recht Wijzigen op de map, of het bestand is read-only en het account mag geen kenmerken schrijven. |
| Conversie duurt langer dan 1 seconde | Op shares zonder change-notificaties (sommige NAS'en) vindt de vangnet-scan het bestand. Verlaag dan `RescanIntervalSeconds`. Een schrijver die het bestand lang open houdt, vertraagt de conversie bewust. |
| Config-wijziging wordt niet overgenomen | Kijk of event 1003 is gelogd: ongeldige JSON of een ongeldige waarde. De vorige config blijft dan actief. Gebruik de configuratietool, want die valideert vóór het opslaan. |
| Geen events in het Event Log | De bron *AnsiWatch* ontbreekt. Herstel de MSI (*Apps → AnsiWatch → Herstellen*) of start de service één keer als administrator. Het logbestand werkt altijd. |
| Tijdelijke `*.ansiwatch.tmp`-bestanden | Deze blijven alleen achter na een harde crash of stroomuitval. De service ruimt ze automatisch op als ze ouder zijn dan 15 minuten. |
| Diagnose in de console | `sc stop AnsiWatch` en daarna `"C:\Program Files\AnsiWatch\AnsiWatch.Service.exe"` in een elevated prompt. Zet `LogLevel` op `Debug` voor details. |

## Bouwen vanaf de broncode

Vereisten: Windows met de **.NET 8 SDK** en PowerShell 7 of Windows PowerShell 5.1. WiX hoef je niet te installeren, want WiX Toolset **v7.0.1** komt als NuGet-SDK mee.

```powershell
.\build.ps1 -AcceptWixEula
```

Resultaat: `artifacts\AnsiWatch-<versie>.msi` plus een `.sha256`-bestand en de testresultaten.

- **WiX v7 en de OSMF-EULA.** WiX Toolset v7 vereist acceptatie van de [Open Source Maintenance Fee-EULA](https://wixtoolset.org/osmf/). Organisaties met omzet betalen een maintenance fee. `-AcceptWixEula` (of de omgevingsvariabele `ANSIWATCH_ACCEPT_WIX_EULA=1`) geeft `AcceptEula=wix7` door. Zonder die schakelaar stopt de build vóór de MSI-stap. Wil je de EULA niet accepteren, zet dan WiX 5.0.2 in `global.json` en `Directory.Packages.props`. Die versie kent geen OSMF-EULA en gebruikt dezelfde `.wxs`-syntax.
- **Versie.** Die staat op één plek: `<Version>` in `Directory.Build.props`. Exe's, MSI-ProductVersion en de bestandsnaam volgen die waarde. De UpgradeCode ligt vast in `Package.wxs`.
- Overige opties: `-SkipTests`, `-Configuration Debug` en `-OutputDir`.
- **Tests alleen** (werkt ook op Linux en macOS): `dotnet test tests/AnsiWatch.Tests`.
- **CI**: `.github/workflows/build.yml` bouwt op `windows-latest` en voert daarna `tests/smoke/Invoke-SmokeTest.ps1` uit. Die test installeert de MSI stil met een lokaal serviceaccount, controleert service, recovery, ACL's, Event Log en een echte conversie, en deïnstalleert weer.

## Architectuur

| Project | Inhoud |
|---|---|
| `AnsiWatch.Core` | Detectie (`EncodingDetector`), streaming transcoder (`AnsiTranscoder`), bestandsconverter (`FileConverter`: wachten/locken, back-up, tijdelijk bestand, verificatie, `File.Replace`, quarantaine), testconversie (`FilePreview`), configuratiemodel en validatie, event-ID's. Volledig testbaar zonder Windows. |
| `AnsiWatch.Service` | .NET 8 Worker Service (`UseWindowsService`): `ConfigMonitor` (`reloadOnChange` met validatie), `WatchManager`/`PathWatcher` (FileSystemWatcher + vangnet-scan + offline-herstel), `ConversionQueue` (`Channel<T>`, debounce, ontdubbeling per pad, beperkte parallelliteit), `FileStateCache` (scan slaat ongewijzigde bestanden over, back-off voor gelockte bestanden), Serilog (bestand + Event Log), `status.json`. |
| `AnsiWatch.Config` | WinForms-configuratietool (.NET 8, `requireAdministrator`). |
| `AnsiWatch.Installer` | WiX v7-MSI met een eigen serviceaccountdialoog. |
| `AnsiWatch.Installer.CustomActions` | DTF custom action: normaliseert het account (`.\`, UPN, gMSA, ingebouwd) en controleert het wachtwoord vooraf met `LogonUser`. |
| `AnsiWatch.Tests` | xUnit: byte-voor-byte-vergelijking met `Encoding.GetEncoding(1252).GetBytes(tekst)`, Nederlandse tekens, €, Ł en emoji, CRLF/LF, leeg bestand, bestand met alleen een BOM, een bestand van 52 MB, bestanden die al ANSI of UTF-8 zijn, gelockte en groeiende bestanden, faalinjectie vóór `Replace`, random round-trips, plus end-to-end tests met de echte watcher (conversie < 1,5 s, 200 bestanden, live config-reload, offline pad, quarantaine). |
