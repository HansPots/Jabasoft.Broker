# Laat Jabasoft.Broker bij het aanmelden starten, los van Jabasoft, zodat
# Goose en Hermes altijd werken - met tokenregistratie, want die doet de
# broker zelf.
#
#   - Taakplanner: taak "Jabasoft Broker", bij aanmelden, onder je eigen
#     account (LocalDB werkt alleen per gebruiker, daarom geen Windows-service).
#   - Bureaublad: snelkoppelingen "Broker starten" en "Broker stoppen".
#   - Opstarten: het pictogram in het systeemvak (Tray\Jabasoft.BrokerTray),
#     met Starten / Stoppen / Opnieuw starten / Opstartlog openen.
#
# Ongedaan maken:  .\Broker-installeren.ps1 -Verwijderen
#
# Geen beheerdersrechten nodig: de taak is van en voor jezelf.

param([switch]$Verwijderen)

$taaknaam = 'Jabasoft Broker'
$beheer = $PSScriptRoot
$project = Split-Path $beheer -Parent
$bureaublad = [Environment]::GetFolderPath('Desktop')
$snelStart = Join-Path $bureaublad 'Broker starten.lnk'
$snelStop = Join-Path $bureaublad 'Broker stoppen.lnk'
$snelTray = Join-Path ([Environment]::GetFolderPath('Startup')) 'Jabasoft Broker-pictogram.lnk'
$trayProject = Join-Path $project 'Tray\Jabasoft.BrokerTray.csproj'
$trayExe = Join-Path $project 'Tray\bin\Debug\net10.0-windows\Jabasoft.BrokerTray.exe'

if ($Verwijderen) {
    Unregister-ScheduledTask -TaskName $taaknaam -Confirm:$false -ErrorAction SilentlyContinue
    Get-Process -Name 'Jabasoft.BrokerTray' -ErrorAction SilentlyContinue | Stop-Process -Force
    Remove-Item $snelStart, $snelStop, $snelTray -ErrorAction SilentlyContinue
    Write-Output 'Aanmeldtaak en snelkoppelingen verwijderd.'
    return
}

$powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$conhost = Join-Path $env:SystemRoot 'System32\conhost.exe'

# conhost --headless: een consoleprogramma zonder venster, dus geen
# flitsend PowerShell-scherm bij aanmelden of bij een klik.
function Onzichtbaar([string]$script) {
    "--headless `"$powershell`" -NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $beheer $script)`""
}

$gebruiker = "$env:USERDOMAIN\$env:USERNAME"

$actie = New-ScheduledTaskAction -Execute $conhost -Argument (Onzichtbaar 'Broker-starten.ps1') -WorkingDirectory $project
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $gebruiker
$principal = New-ScheduledTaskPrincipal -UserId $gebruiker -LogonType Interactive -RunLevel Limited

# Geen tijdslimiet (standaard stopt Taakplanner een taak na 72 uur, en
# daarmee de broker), niet dubbel starten, en ook op batterij.
$instellingen = New-ScheduledTaskSettingsSet `
    -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -MultipleInstances IgnoreNew `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries

Register-ScheduledTask -TaskName $taaknaam -Action $actie -Trigger $trigger -Principal $principal `
    -Settings $instellingen -Force `
    -Description 'Start Jabasoft.Broker bij aanmelden, zodat Goose en Hermes ook zonder Jabasoft werken.' | Out-Null

$shell = New-Object -ComObject WScript.Shell

# Starten = de taak uitvoeren: dezelfde weg als bij aanmelden, dus ook
# onzichtbaar en door Taakplanner gevolgd.
$link = $shell.CreateShortcut($snelStart)
$link.TargetPath = $conhost
$link.Arguments = "--headless schtasks.exe /run /tn `"$taaknaam`""
$link.WorkingDirectory = $project
$link.IconLocation = "$env:SystemRoot\System32\imageres.dll,101"
$link.Description = 'Start Jabasoft.Broker'
$link.Save()

$link = $shell.CreateShortcut($snelStop)
$link.TargetPath = $conhost
$link.Arguments = Onzichtbaar 'Broker-stoppen.ps1'
$link.WorkingDirectory = $project
$link.IconLocation = "$env:SystemRoot\System32\imageres.dll,100"
$link.Description = 'Stopt Jabasoft.Broker'
$link.Save()

# Het pictogram in het systeemvak: bouwen (draait hij al, dan eerst stoppen,
# anders zit het exe-bestand vast), in de map Opstarten zetten en starten.
Get-Process -Name 'Jabasoft.BrokerTray' -ErrorAction SilentlyContinue | Stop-Process -Force
& dotnet build $trayProject -c Debug --nologo -v q | Out-Null
if ($LASTEXITCODE -eq 0 -and (Test-Path $trayExe)) {
    $link = $shell.CreateShortcut($snelTray)
    $link.TargetPath = $trayExe
    $link.WorkingDirectory = Split-Path $trayExe -Parent
    $link.Description = 'Pictogram om Jabasoft.Broker te starten en te stoppen'
    $link.Save()
    Start-Process $trayExe
    Write-Output 'Pictogram in het systeemvak gestart en bij aanmelden ingesteld.'
} else {
    Write-Output "Pictogram bouwen mislukt (code $LASTEXITCODE) - aanmeldtaak en snelkoppelingen zijn wel gemaakt."
}

Write-Output "Aanmeldtaak '$taaknaam' en snelkoppelingen op het bureaublad gemaakt."
