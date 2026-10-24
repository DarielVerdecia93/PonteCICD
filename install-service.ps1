#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Instala (o desinstala) Legados CI como servicio de Windows elevado que administra IIS.

    El servicio corre ELEVADO porque crea y gobierna sitios y application pools de IIS
    (New-WebSite/New-WebAppPool, swap de physical path, bindings, warm-up). Para eso la cuenta
    del servicio debe pertenecer al grupo local de Administradores: este script la agrega si falta.

    Opciones de cuenta:
      - Por defecto usa TU cuenta de Windows (asi conserva tus credenciales de git hacia Azure DevOps);
        Windows pide la contrasena, el script no la guarda. No sirve si inicias sesion con cuenta
        Microsoft (PIN/Hello) sin una contrasena local comoda.
      - Con -LocalSystem el servicio corre como LocalSystem: ya es administrador local y NO pide
        contrasena. OJO: LocalSystem NO lleva tus credenciales de git; para un repo privado de Azure
        DevOps hay que configurar git a nivel de sistema con un PAT (ver iis-migration-notes.md).

    Antes de arrancar verifica que IIS este presente (W3SVC, WAS, el modulo WebAdministration y la
    caracteristica Web-AppInit). Si falta IIS avisa (no aborta: puede que lo estes instalando en paralelo).

.EXAMPLE
    .\install-service.ps1                 # corre con TU cuenta (pide contrasena de Windows)
    .\install-service.ps1 -LocalSystem    # corre como LocalSystem (sin contrasena)
    .\install-service.ps1 -Uninstall
#>
param(
    [switch]$Uninstall,
    [switch]$LocalSystem
)

$ErrorActionPreference = 'Stop'
$name = 'LegadosCI'
$exe = Join-Path $PSScriptRoot 'service\LegadosCI.exe'

# ---------------------------------------------------------------- helpers

# El servicio administra IIS, asi que su cuenta debe ser Administrador local. La agregamos si falta.
# El grupo se llama 'Administradores' en Windows en espanol y 'Administrators' en ingles; probamos ambos
# y, si el modulo Microsoft.PowerShell.LocalAccounts no esta disponible, caemos a 'net localgroup'.
function Add-ToLocalAdministrators([string]$member) {
    Write-Host "Asegurando que $member pertenezca al grupo local de Administradores (el servicio administra IIS)..."
    foreach ($group in @('Administradores', 'Administrators')) {
        try {
            if (Get-LocalGroup -Name $group -ErrorAction SilentlyContinue) {
                $already = Get-LocalGroupMember -Group $group -ErrorAction SilentlyContinue |
                    Where-Object { $_.Name -ieq $member -or $_.Name -ieq ($member -replace '^\.\\', "$env:COMPUTERNAME\") }
                if ($already) {
                    Write-Host "  $member ya es Administrador local (grupo $group)."
                } else {
                    Add-LocalGroupMember -Group $group -Member $member -ErrorAction Stop
                    Write-Host "  Agregado $member al grupo $group."
                }
                return
            }
        } catch {
            Write-Host "  No se pudo usar el modulo para el grupo '$group': $($_.Exception.Message)"
        }
    }
    foreach ($group in @('Administradores', 'Administrators')) {
        & net localgroup $group $member /add 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) { Write-Host "  Agregado $member al grupo $group (net localgroup)."; return }
    }
    Write-Host "  AVISO: no se pudo confirmar la pertenencia a Administradores; agregala manualmente si el servicio no puede administrar IIS."
}

# Concede a una cuenta el derecho "Iniciar sesion como servicio" (necesario para arrancar como servicio).
function Grant-ServiceLogonRight([string]$account) {
    $sid = (New-Object Security.Principal.NTAccount ($account -replace '^\.\\', "$env:COMPUTERNAME\")).Translate([Security.Principal.SecurityIdentifier]).Value
    $tmp = Join-Path $env:TEMP 'legadosci-rights'
    New-Item -ItemType Directory -Force $tmp | Out-Null
    & secedit /export /cfg "$tmp\current.inf" /areas USER_RIGHTS | Out-Null
    $current = (Select-String "$tmp\current.inf" -Pattern '^SeServiceLogonRight\s*=\s*(.*)$').Matches | ForEach-Object { $_.Groups[1].Value }
    if ("$current" -notmatch [regex]::Escape("*$sid")) {
        $value = if ($current) { "$current,*$sid" } else { "*$sid" }
        @('[Unicode]', 'Unicode=yes', '[Version]', 'signature="$CHICAGO$"', 'Revision=1', '[Privilege Rights]', "SeServiceLogonRight = $value") |
            Set-Content "$tmp\grant.inf" -Encoding Unicode
        & secedit /configure /db "$tmp\grant.sdb" /cfg "$tmp\grant.inf" /areas USER_RIGHTS | Out-Null
        Write-Host "Concedido 'Iniciar sesion como servicio' a $account."
    }
    Remove-Item $tmp -Recurse -Force
}

# Verificacion de IIS antes de arrancar. No abortamos: el usuario puede estar instalando IIS en paralelo.
function Test-IisReadiness {
    $w3svc = Get-Service W3SVC -ErrorAction SilentlyContinue
    $was = Get-Service WAS -ErrorAction SilentlyContinue
    $webAdmin = Get-Module -ListAvailable WebAdministration
    $appInit = $null
    try { $appInit = Get-WindowsOptionalFeature -Online -FeatureName IIS-ApplicationInit -ErrorAction SilentlyContinue } catch {}
    if (-not $appInit) {
        try { $appInit = Get-WindowsFeature Web-AppInit -ErrorAction SilentlyContinue } catch {}
    }
    $appInitOn = $false
    if ($appInit) {
        $appInitOn = ($appInit.State -eq 'Enabled') -or ($appInit.Installed -eq $true) -or ($appInit.InstallState -eq 'Installed')
    }

    if (-not $w3svc -or -not $was -or -not $webAdmin) {
        Write-Host ''
        Write-Host 'AVISO: IIS no parece estar listo:' -ForegroundColor Yellow
        if (-not $w3svc)    { Write-Host '  - Falta el servicio W3SVC (World Wide Web Publishing).' }
        if (-not $was)      { Write-Host '  - Falta el servicio WAS (Windows Process Activation Service).' }
        if (-not $webAdmin) { Write-Host '  - Falta el modulo WebAdministration.' }
        Write-Host '  Instala IIS (como admin) con, por ejemplo:'
        Write-Host '    Enable-WindowsOptionalFeature -Online -FeatureName IIS-WebServerRole,IIS-WebServer,IIS-ManagementScriptingTools,IIS-ApplicationInit -All'
        Write-Host '    (o en Windows Server) Install-WindowsFeature Web-Server,Web-Mgmt-Console,Web-Scripting-Tools,Web-AppInit'
        Write-Host '  Legados CI arrancara en modo degradado (iisAvailable=false) hasta que IIS este disponible.'
    } elseif (-not $appInitOn) {
        Write-Host ''
        Write-Host 'AVISO: la caracteristica Application Initialization (Web-AppInit) no esta instalada.' -ForegroundColor Yellow
        Write-Host '  Los deploys usaran el fallback de corte breve (brief cutover) en vez del warm-up sin interrupcion.'
        Write-Host '  Para habilitarla (como admin):'
        Write-Host '    Enable-WindowsOptionalFeature -Online -FeatureName IIS-ApplicationInit -All'
        Write-Host '    (o en Windows Server) Install-WindowsFeature Web-AppInit'
    } else {
        Write-Host 'IIS listo: W3SVC, WAS, WebAdministration y Application Initialization presentes.'
    }
}

# ---------------------------------------------------------------- (re)instalacion

if (Get-Service $name -ErrorAction SilentlyContinue) {
    Stop-Service $name -ErrorAction SilentlyContinue
    & sc.exe delete $name | Out-Null
    Start-Sleep -Seconds 2
    Write-Host "Servicio $name eliminado."
}
if ($Uninstall) { return }

if (-not (Test-Path $exe)) { throw "No existe $exe. Publica antes: dotnet publish src\LegadosCI -c Release -o service" }
Get-Process LegadosCI -ErrorAction SilentlyContinue | Stop-Process -Force   # instancia en modo consola, si la hay

if ($LocalSystem) {
    Test-IisReadiness
    New-Service -Name $name -BinaryPathName "`"$exe`"" -DisplayName 'Legados CI' `
        -Description 'Deploy continuo de proyectos legados por rama (panel en http://localhost:5080)' `
        -StartupType Automatic | Out-Null   # sin -Credential => LocalSystem (ya es administrador local)
    & sc.exe failure $name reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null
    Start-Service $name
    Write-Host ''
    Write-Host "Servicio $name instalado como LocalSystem y arrancado. Panel: http://localhost:5080"
    Write-Host 'NOTA: LocalSystem no lleva tus credenciales de git. Si el repo es privado (Azure DevOps),'
    Write-Host '      configura git a nivel de sistema con un PAT, por ejemplo:'
    Write-Host '        git config --system credential.helper store'
    Write-Host '        (y guarda el PAT la primera vez, o usa la URL https://<PAT>@dev.azure.com/...)'
    return
}

$cred = Get-Credential -UserName ".\$env:USERNAME" -Message 'Cuenta con la que correra Legados CI (la tuya: necesita acceso git a Azure DevOps)'
Grant-ServiceLogonRight $cred.UserName
Add-ToLocalAdministrators $cred.UserName
Test-IisReadiness

New-Service -Name $name -BinaryPathName "`"$exe`"" -DisplayName 'Legados CI' `
    -Description 'Deploy continuo de proyectos legados por rama (panel en http://localhost:5080)' `
    -StartupType Automatic -Credential $cred | Out-Null
& sc.exe failure $name reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null
Start-Service $name
Write-Host "Servicio $name instalado y arrancado. Panel: http://localhost:5080"
