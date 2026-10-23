#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Instala (o desinstala) Legados CI como servicio de Windows.

    El servicio corre con TU cuenta de Windows: asi usa tus credenciales de git (Azure DevOps)
    y IIS Express funciona igual que cuando lo lanzas tu. La contrasena la pide Windows; el script no la guarda.

.EXAMPLE
    .\install-service.ps1
    .\install-service.ps1 -Uninstall
#>
param([switch]$Uninstall)

$ErrorActionPreference = 'Stop'
$name = 'LegadosCI'
$exe = Join-Path $PSScriptRoot 'service\LegadosCI.exe'

if (Get-Service $name -ErrorAction SilentlyContinue) {
    Stop-Service $name -ErrorAction SilentlyContinue
    & sc.exe delete $name | Out-Null
    Start-Sleep -Seconds 2
    Write-Host "Servicio $name eliminado."
}
if ($Uninstall) { return }

if (-not (Test-Path $exe)) { throw "No existe $exe. Publica antes: dotnet publish src\LegadosCI -c Release -o service" }
Get-Process LegadosCI -ErrorAction SilentlyContinue | Stop-Process -Force   # instancia en modo consola, si la hay

$cred = Get-Credential -UserName ".\$env:USERNAME" -Message 'Cuenta con la que correra Legados CI (la tuya: necesita acceso git a Azure DevOps)'

# Un servicio solo puede arrancar con una cuenta que tenga el derecho "Iniciar sesion como servicio".
$sid = (New-Object Security.Principal.NTAccount ($cred.UserName -replace '^\.\\', "$env:COMPUTERNAME\")).Translate([Security.Principal.SecurityIdentifier]).Value
$tmp = Join-Path $env:TEMP 'legadosci-rights'
New-Item -ItemType Directory -Force $tmp | Out-Null
& secedit /export /cfg "$tmp\current.inf" /areas USER_RIGHTS | Out-Null
$current = (Select-String "$tmp\current.inf" -Pattern '^SeServiceLogonRight\s*=\s*(.*)$').Matches | ForEach-Object { $_.Groups[1].Value }
if ("$current" -notmatch [regex]::Escape("*$sid")) {
    $value = if ($current) { "$current,*$sid" } else { "*$sid" }
    @('[Unicode]', 'Unicode=yes', '[Version]', 'signature="$CHICAGO$"', 'Revision=1', '[Privilege Rights]', "SeServiceLogonRight = $value") |
        Set-Content "$tmp\grant.inf" -Encoding Unicode
    & secedit /configure /db "$tmp\grant.sdb" /cfg "$tmp\grant.inf" /areas USER_RIGHTS | Out-Null
    Write-Host "Concedido 'Iniciar sesion como servicio' a $($cred.UserName)."
}
Remove-Item $tmp -Recurse -Force

New-Service -Name $name -BinaryPathName "`"$exe`"" -DisplayName 'Legados CI' `
    -Description 'Deploy continuo de proyectos legados por rama (panel en http://localhost:5080)' `
    -StartupType Automatic -Credential $cred | Out-Null
& sc.exe failure $name reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null
Start-Service $name
Write-Host "Servicio $name instalado y arrancado. Panel: http://localhost:5080"
