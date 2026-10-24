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
    if (-not $list.Count) { throw 'Nao ha backups para restaurar.' }
    $target = if ($Backup) { $list | Where-Object { $_.Name -eq (Split-Path $Backup -Leaf) } | Select-Object -First 1 } else { $list[0] }
    if (-not $target) { throw "Nao existe o backup '$Backup'." }

    $current = Get-DeployInfo
    Write-Step ('Versao atual: ' + $(if ($current) { $current.ShortCommit } else { '(nenhuma)' }) + " -> restaurar $($target.Name)")

    Write-Step 'A assegurar o site no IIS'
    Ensure-Site

    Write-Step 'A guardar a versao atual'
    $safety = New-SiteBackup 'rollback'

    Write-Step "A restaurar $($target.Name)"
    # Restore-SiteBackup extrae el zip a una release nueva, re-crea los junctions de Preserve y hace Switch-Release.
    Restore-SiteBackup $target.Path

    Write-Step 'A aquecer a release restaurada'
    $warm = Invoke-WarmRelease
    if (-not ($warm -and (Test-SiteHealth))) {
        $hint = if ($safety) { " A versao anterior ao rollback esta em $(Split-Path $safety -Leaf)." } else { '' }
        throw "O site restaurado nao responde corretamente.$hint"
    }

    Remove-OldReleases
    Remove-OldBackups
    Write-Step "Rollback concluido: agora em $($target.Commit) - $SiteUrl"
}
finally {
    Exit-DeployLock
}
