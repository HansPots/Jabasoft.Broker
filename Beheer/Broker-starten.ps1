# Start Jabasoft.Broker en blijft draaien zolang hij draait.
#
# Dit is wat de aanmeldtaak "Jabasoft Broker" uitvoert (zie
# Broker-installeren.ps1), onzichtbaar via conhost --headless. Het script
# wacht op de broker: zo hoort het brokerproces bij de taak en ziet
# Taakplanner hem als "Wordt uitgevoerd" tot hij stopt.
#
# Eerst bouwen, zodat altijd de laatste code draait - net als het
# "dotnet run" waarmee Jabasoft hem start. Is er niets veranderd, dan duurt
# dat maar een paar seconden.
#
# Wat er gebeurt, staat in %LOCALAPPDATA%\Jabasoft\Broker\opstart.log.

$basisUrl = 'http://localhost:5310'
$project = Split-Path $PSScriptRoot -Parent
$logmap = Join-Path $env:LOCALAPPDATA 'Jabasoft\Broker'
$log = Join-Path $logmap 'opstart.log'

New-Item -ItemType Directory -Force -Path $logmap | Out-Null
if ((Test-Path $log) -and (Get-Item $log).Length -gt 1MB) { Remove-Item $log }

function Schrijf([string]$tekst) {
    "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')  $tekst" | Add-Content -Path $log -Encoding UTF8
}

# Draait hij al (gestart door Jabasoft, of een eerdere start)? Dan klaar.
try {
    Invoke-WebRequest "$basisUrl/health" -UseBasicParsing -TimeoutSec 2 | Out-Null
    Schrijf 'Broker draait al - niets te doen.'
    exit 0
} catch { }

Schrijf 'Bouwen...'
$uitvoer = & dotnet build (Join-Path $project 'Jabasoft.Broker.csproj') -c Debug --nologo -v q | Out-String
if ($LASTEXITCODE -ne 0) {
    Schrijf "Bouwen mislukt (code $LASTEXITCODE):`r`n$uitvoer"
    exit 1
}

$exe = Join-Path $project 'bin\Debug\net10.0\Jabasoft.Broker.exe'
if (-not (Test-Path $exe)) {
    Schrijf "Niet gevonden: $exe"
    exit 1
}

# Zelfde omgeving als AiBrokerProcessLauncher in Jabasoft.Base: de
# projectmap als werkmap (BeeldTools zoekt ImageFunn ernaast) en
# Development als omgeving.
$env:ASPNETCORE_ENVIRONMENT = 'Development'
Set-Location $project

Schrijf 'Gestart.'
& $exe --urls $basisUrl | Out-Null
Schrijf "Gestopt (code $LASTEXITCODE)."
