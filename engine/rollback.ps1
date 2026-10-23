<#
.SYNOPSIS
    Restaura un backup de un entorno. Antes guarda un zip de la version actual, asi que tambien se puede deshacer.

.EXAMPLE
    .\rollback.ps1 -Environment develop                                   # el backup mas reciente
    .\rollback.ps1 -Environment develop -Backup develop_20261023-153000_ff3b1d6_deploy.zip
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Environment,
    [string]$Backup
)

trap { Write-Host "ERROR: $($_.Exception.Message)"; exit 1 }
. (Join-Path $PSScriptRoot 'common.ps1')
Use-Environment $Environment

Enter-DeployLock
try {
    $list = @(Get-Backups)
    if (-not $list.Count) { throw 'No hay backups para restaurar.' }
    $target = if ($Backup) { $list | Where-Object { $_.Name -eq (Split-Path $Backup -Leaf) } | Select-Object -First 1 } else { $list[0] }
    if (-not $target) { throw "No existe el backup '$Backup'." }

    $current = Get-DeployInfo
    Write-Step ('Version actual: ' + $(if ($current) { $current.ShortCommit } else { '(ninguna)' }) + " -> restaurar $($target.Name)")

    Write-Step 'Deteniendo el sitio'
    Stop-Site

    Write-Step 'Guardando la version actual'
    $safety = New-SiteBackup 'rollback'

    Write-Step "Restaurando $($target.Name)"
    Restore-SiteBackup $target.Path

    Write-Step 'Arrancando el sitio'
    Start-Site
    if (-not (Test-SiteHealth)) {
        $hint = if ($safety) { " La version anterior al rollback esta en $(Split-Path $safety -Leaf)." } else { '' }
        throw "El sitio restaurado no responde correctamente.$hint"
    }

    Remove-OldBackups
    Write-Step "Rollback completado: ahora en $($target.Commit) - $SiteUrl"
}
finally {
    Exit-DeployLock
}
