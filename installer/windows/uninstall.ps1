#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Désinstalle l'agent Sage d'IGF Paie de ce serveur.

.DESCRIPTION
    Arrête le service, délie chaque base auprès de la plateforme (au mieux : une plateforme injoignable
    n'empêche rien — révoquez alors la liaison depuis son écran), efface les liaisons et leurs secrets,
    supprime le service et le programme.

    -KeepLogs garde le dossier des journaux, pour nous les envoyer après coup.
    -KeepData ne délie RIEN et garde liaisons, secrets et journaux : c'est le passage au MSI (désinstaller
    ce qu'a posé install.ps1, puis installer le MSI, qui reprend les liaisons telles quelles).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File "C:\Program Files\IGF\PayrollSageAgent\uninstall.ps1"
#>
[CmdletBinding()]
param([switch]$KeepLogs, [switch]$KeepData)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3

$ServiceName = 'PayrollSageAgent'
$InstallDir = Join-Path $env:ProgramFiles 'IGF\PayrollSageAgent'
$DataDir = Join-Path $env:ProgramData 'IGF\PayrollSageAgent'
$Exe = Join-Path $InstallDir 'PayrollSageAgent.exe'

function Say([string]$Text) { Write-Host $Text }

$byMsi = Get-ItemProperty -Path 'HKLM:\SOFTWARE\IGF\PayrollSageAgent' -Name InstalledBy -ErrorAction SilentlyContinue
if ($byMsi -and $byMsi.InstalledBy -eq 'msi') {
    Write-Host "L'agent a été installé par le MSI : désinstallez-le depuis « Applications installées » (ou msiexec /x)." -ForegroundColor Red
    exit 1
}

# This script may live in the folder it is about to remove.
Set-Location -Path $env:SystemRoot

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service -and $service.Status -ne 'Stopped') {
    Stop-Service -Name $ServiceName -Force
    (Get-Service -Name $ServiceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
    Say "Service arrêté."
}

# Each binding: told to the platform, then its file and its secrets deleted.
$bindings = Join-Path $DataDir 'bindings'
if (-not $KeepData -and (Test-Path $Exe) -and (Test-Path $bindings)) {
    foreach ($file in Get-ChildItem -Path $bindings -Filter '*.json' -File) {
        & $Exe remove-binding --name $file.BaseName
    }
}

if ($service) {
    & sc.exe delete $ServiceName | Out-Null
    if ($LASTEXITCODE -eq 0) { Say "Service supprimé." } else { Say "Le service n'a pas pu être supprimé (sc.exe delete, code $LASTEXITCODE)." }
}

if ($KeepData) {
    Say "Liaisons, secrets et journaux gardés : $DataDir — le MSI les reprendra."
} elseif (Test-Path $DataDir) {
    if ($KeepLogs) {
        Get-ChildItem -Path $DataDir -Force | Where-Object { $_.Name -ne 'logs' } | Remove-Item -Recurse -Force
        Say "Liaisons et secrets effacés ; journaux gardés : $(Join-Path $DataDir 'logs')"
    } else {
        Remove-Item -Path $DataDir -Recurse -Force
        Say "Liaisons, secrets et journaux effacés."
    }
}

if (Test-Path $InstallDir) {
    try {
        Remove-Item -Path $InstallDir -Recurse -Force
        Say "Programme supprimé."
    } catch {
        Say "Le programme n'a pas pu être supprimé entièrement : effacez $InstallDir à la main."
    }
}

# The saved label — unless the MSI is about to take over (-KeepData), which reads it back.
if (-not $KeepData -and (Test-Path 'HKLM:\SOFTWARE\IGF\PayrollSageAgent')) {
    Remove-Item -Path 'HKLM:\SOFTWARE\IGF\PayrollSageAgent' -Recurse -Force
    if ((Test-Path 'HKLM:\SOFTWARE\IGF') -and -not (Get-ChildItem -Path 'HKLM:\SOFTWARE\IGF')) { Remove-Item -Path 'HKLM:\SOFTWARE\IGF' -Force }
}

# The IGF folders, if nothing else lives there.
foreach ($parent in @((Split-Path $InstallDir -Parent), (Split-Path $DataDir -Parent))) {
    if ((Test-Path $parent) -and -not (Get-ChildItem -Path $parent -Force)) { Remove-Item -Path $parent -Force }
}
Say "L'agent Sage est désinstallé."
