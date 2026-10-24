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
    'start'   { Start-Site; if (-not (Test-SiteHealth)) { throw 'O site nao responde corretamente.' } }
    'stop'    { Stop-Site }
    'restart' { Stop-Site; Start-Site; if (-not (Test-SiteHealth)) { throw 'O site nao responde corretamente.' } }
    'purge'   {
        Stop-Site
        Remove-Site
        # Quitar los junctions de Preserve de cada release ANTES del borrado recursivo (no atravesar enlaces).
        if (Test-Path $Paths.Releases) {
            foreach ($r in @(Get-ChildItem -LiteralPath $Paths.Releases -Directory -ErrorAction SilentlyContinue)) {
                Remove-PreserveJunctions $r.FullName
            }
        }
        Remove-Item $Paths.Env -Recurse -Force
        Write-Ok "Removido $($Paths.Env)"
    }
}
