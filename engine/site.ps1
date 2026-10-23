<#
.SYNOPSIS
    Arranca, para o reinicia el sitio de un entorno, o elimina sus ficheros.

.EXAMPLE
    .\site.ps1 -Environment develop -Action restart
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Environment,
    [Parameter(Mandatory)][ValidateSet('start', 'stop', 'restart', 'purge')][string]$Action
)

trap { Write-Host "ERROR: $($_.Exception.Message)"; exit 1 }
. (Join-Path $PSScriptRoot 'common.ps1')
Use-Environment $Environment

switch ($Action) {
    'start'   { Start-Site; if (-not (Test-SiteHealth)) { throw 'El sitio no responde correctamente.' } }
    'stop'    { Stop-Site }
    'restart' { Stop-Site; Start-Site; if (-not (Test-SiteHealth)) { throw 'El sitio no responde correctamente.' } }
    'purge'   { Stop-Site; Remove-Item $Paths.Env -Recurse -Force; Write-Ok "Eliminado $($Paths.Env)" }
}
