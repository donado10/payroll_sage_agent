#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installe — ou met à jour — l'agent Sage d'IGF Paie comme service Windows.

.DESCRIPTION
    Une installation par serveur SQL : le service relie ensuite autant de bases Sage qu'il faut, une
    par « liaison » (add-binding). Relancer ce script depuis un paquet plus récent MET À JOUR l'agent et
    garde ses liaisons.

    Ce que le script fait :
      1. arrête le service s'il existe déjà (mise à jour) ;
      2. copie l'agent dans  %ProgramFiles%\IGF\PayrollSageAgent ;
      3. crée le dossier     %ProgramData%\IGF\PayrollSageAgent  (liaisons, secrets, journaux), ouvert
         SEULEMENT à SYSTEM, aux Administrateurs et au compte du service ;
      4. déclare le service « PayrollSageAgent », démarrage automatique (différé), sous son compte virtuel
         NT SERVICE\PayrollSageAgent, redémarré par Windows s'il s'arrête sur une erreur ;
      5. relie la première base si -Name … est donné (le mot de passe SQL est demandé, masqué) ;
      6. démarre le service.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\install.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\install.ps1 -Name prestige -Url http://41.82.10.5:8080/api/agent `
        -Code ABCD-EFGH-JKLM -SqlServer "SRV-SAGE\SAGE100" -Database PRESTIGE -SqlUser agent_prestige -TrustServerCertificate
#>
[CmdletBinding()]
param(
    [string]$Name,
    [string]$Url,
    [string]$Code,
    [string]$SqlServer,
    [string]$Database,
    [string]$SqlUser,
    [switch]$TrustServerCertificate,
    [string]$Label
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3

$ServiceName = 'PayrollSageAgent'
$DisplayName = 'IGF Paie - Agent Sage'   # services.msc's label, unless -Label (or an earlier one) says otherwise
$Description = "Relie les bases Sage 100 de ce serveur à la plateforme IGF Paie (plan comptable). Appelle la plateforme, n'ouvre aucun port."
$InstallDir = Join-Path $env:ProgramFiles 'IGF\PayrollSageAgent'
$DataDir = Join-Path $env:ProgramData 'IGF\PayrollSageAgent'
$Exe = Join-Path $InstallDir 'PayrollSageAgent.exe'
$Package = $PSScriptRoot
$Source = Join-Path $Package 'app'

function Say([string]$Text) { Write-Host $Text }
function Fail([string]$Text) { Write-Host $Text -ForegroundColor Red; exit 1 }

# The service account's SID, as `sc showsid` computes it: S-1-5-80- and the SHA-1 of the upper-cased name
# in UTF-16, as five little-endian integers. Named by SID, never by name — no translation, no locale.
function Get-ServiceSid([string]$Service) {
    $sha = [System.Security.Cryptography.SHA1]::Create()
    try { $hash = $sha.ComputeHash([System.Text.Encoding]::Unicode.GetBytes($Service.ToUpperInvariant())) }
    finally { $sha.Dispose() }
    $parts = for ($i = 0; $i -lt 5; $i++) { [BitConverter]::ToUInt32($hash, $i * 4) }
    return 'S-1-5-80-' + ($parts -join '-')
}

# Windows PowerShell 5.1 turns a native command's redirected stderr into a terminating error under
# « Stop »: sc.exe runs under « Continue », and its exit code decides.
function Invoke-Sc([string[]]$Arguments) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $output = & sc.exe @Arguments 2>&1 } finally { $ErrorActionPreference = $previous }
    if ($LASTEXITCODE -ne 0) { Fail ("sc.exe " + ($Arguments -join ' ') + " a échoué : " + ($output -join ' ')) }
}

# The MSI owns an installation it made: mixing the two would leave Windows Installer's records wrong.
$byMsi = Get-ItemProperty -Path 'HKLM:\SOFTWARE\IGF\PayrollSageAgent' -Name InstalledBy -ErrorAction SilentlyContinue
if ($byMsi -and $byMsi.InstalledBy -eq 'msi') {
    Fail "L'agent a été installé par le MSI : mettez-le à jour avec le MSI le plus récent (double-clic), pas avec ce script."
}

if (-not (Test-Path (Join-Path $Source 'PayrollSageAgent.exe'))) {
    Fail "Le dossier « app » est introuvable à côté de ce script. Lancez install.ps1 depuis le paquet décompressé."
}
$firstBinding = [bool]$Name
if ($firstBinding -and -not ($Url -and $Code -and $SqlServer -and $Database -and $SqlUser)) {
    Fail "Pour relier une première base, donnez -Name, -Url, -Code, -SqlServer, -Database et -SqlUser (ou aucun, et reliez-la ensuite avec add-binding)."
}

# 1. An existing service is stopped first: its files are about to be replaced.
$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service) {
    Say "Agent déjà installé : mise à jour (les liaisons sont gardées)."
    if ($service.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force
        (Get-Service -Name $ServiceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
    }
}

# The label services.msc shows: -Label, else the one this service already has (an update must not reset
# it), else the default. The internal name stays PayrollSageAgent: its account and rights hang on it.
$serviceLabel = $DisplayName
if ($Label) { $serviceLabel = $Label.Trim() }
elseif ($service) { $serviceLabel = $service.DisplayName }

# 2. The program.
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item -Path (Join-Path $Source '*') -Destination $InstallDir -Recurse -Force
foreach ($file in @('uninstall.ps1', 'LISEZMOI.txt', 'compte-sql-lecture.sql', 'acces-windows.sql', 'exemple-liaison.json', 'exemple-plusieurs-liaisons.json', 'Configurer-agent.bat', 'Redemarrer-agent.bat')) {
    $path = Join-Path $Package $file
    if (Test-Path $path) { Copy-Item -Path $path -Destination $InstallDir -Force }
}
# Files from a downloaded zip carry « this came from the Internet »; the service has no one to ask.
Get-ChildItem -Path $InstallDir -Recurse -File | Unblock-File
Say "Programme : $InstallDir"

# 3. The service, then its account exists: declared once, re-configured on every run.
if (-not $service) {
    New-Service -Name $ServiceName -BinaryPathName "`"$Exe`"" -DisplayName $serviceLabel -StartupType Automatic | Out-Null
}
Invoke-Sc @('config', $ServiceName, 'obj=', "NT SERVICE\$ServiceName", 'start=', 'delayed-auto', 'DisplayName=', $serviceLabel)
# Saved where `set-label` and the MSI read it, so moving to the MSI keeps it too.
New-Item -Path 'HKLM:\SOFTWARE\IGF\PayrollSageAgent' -Force | Out-Null
Set-ItemProperty -Path 'HKLM:\SOFTWARE\IGF\PayrollSageAgent' -Name Label -Value $serviceLabel
Invoke-Sc @('description', $ServiceName, $Description)
Invoke-Sc @('sidtype', $ServiceName, 'unrestricted')
# Restart after 1 min, 1 min, then 5 min; the count resets after a day without failure. failureflag 1:
# a stop with an error code counts as a failure too, not only a crash.
Invoke-Sc @('failure', $ServiceName, 'reset=', '86400', 'actions=', 'restart/60000/restart/60000/restart/300000')
Invoke-Sc @('failureflag', $ServiceName, '1')

# 4. The data folder: bindings, their protected secrets, the logs. DPAPI protects the secrets for THIS
# machine, so the folder's rights are what keeps other users out of them.
$serviceSid = Get-ServiceSid $ServiceName
New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
& icacls.exe $DataDir /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' "*${serviceSid}:(OI)(CI)M" /Q | Out-Null
if ($LASTEXITCODE -ne 0) { Fail "Les droits du dossier $DataDir n'ont pas pu être posés (icacls)." }
# Users, Authenticated Users, Everyone: no explicit right left on the folder either.
& icacls.exe $DataDir /remove:g '*S-1-5-32-545' '*S-1-5-11' '*S-1-1-0' /Q | Out-Null
if (Get-ChildItem -Path $DataDir -Force -ErrorAction SilentlyContinue) {
    & icacls.exe (Join-Path $DataDir '*') /reset /T /C /Q | Out-Null
}
Say "Données : $DataDir (SYSTEM, Administrateurs, NT SERVICE\$ServiceName)"

# 5. The first binding, if asked. The SQL password is typed at the agent's own hidden prompt.
if ($firstBinding) {
    $arguments = @('add-binding', '--name', $Name, '--url', $Url, '--code', $Code,
        '--sql-server', $SqlServer, '--database', $Database, '--sql-user', $SqlUser)
    if ($TrustServerCertificate) { $arguments += '--trust-server-certificate' }
    & $Exe @arguments
    if ($LASTEXITCODE -ne 0) {
        Say "La première liaison n'a pas été créée (code $LASTEXITCODE). Le service est installé ; reliez la base avec add-binding (LISEZMOI.txt)."
    }
}

# 6. Start.
Start-Service -Name $ServiceName
(Get-Service -Name $ServiceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
# agent.json, the agent's config beside it: created empty on a first install, kept on an update.
& $Exe init-config | Out-Host

Say ""
Say "Service « $serviceLabel » démarré."
Say "Journaux     : $(Join-Path $DataDir 'logs')"
Say "Liaisons     : & `"$Exe`" list-bindings"
Say "Relier       : & `"$Exe`" add-binding --name ... --url ... --code ... --sql-server ... --database ... --sql-user ..."
Say "Désinstaller : & `"$(Join-Path $InstallDir 'uninstall.ps1')`""
