<#
.SYNOPSIS
    Devuelve el estado real de IIS (app pool + sitio) de uno o todos los entornos en JSON, o
    un preflight de disponibilidad de IIS. Lo consume el servicio C# (IisStateProbe).

.EXAMPLE
    .\sitestate.ps1 -Environment develop
    .\sitestate.ps1 -All
    .\sitestate.ps1 -Preflight
#>
[CmdletBinding(DefaultParameterSetName = 'One')]
param(
    [Parameter(ParameterSetName = 'One')][string]$Environment,
    [Parameter(ParameterSetName = 'All')][switch]$All,
    [Parameter(ParameterSetName = 'Preflight')][switch]$Preflight
)

$ErrorActionPreference = 'Stop'
# IisStateProbe lee la salida como UTF-8 (el preflight no pasa por common.ps1, que tambien lo fija).
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false } catch { }

# ---- Preflight: comprueba que IIS esta disponible para operar (no dot-sourcea common.ps1,
#      porque common.ps1 lanza si WebAdministration falta y aqui queremos reportarlo como JSON).
if ($Preflight) {
    $result = [ordered]@{ ok = $false; iisAvailable = $false; webAppInit = $false; message = '' }
    try {
        Import-Module WebAdministration -ErrorAction Stop
        $w3svc = Get-Service -Name 'W3SVC' -ErrorAction SilentlyContinue
        $was = Get-Service -Name 'WAS' -ErrorAction SilentlyContinue
        if (-not $w3svc -or -not $was) {
            $result.message = 'Servicos IIS (W3SVC/WAS) nao encontrados: instale o IIS (Web-Server).'
        } else {
            $result.iisAvailable = $true
            # Web-AppInit (Application Initialization) es opcional: sin el, los deploys usan corte breve.
            $appInit = $null
            try {
                $appInit = Get-WindowsOptionalFeature -Online -FeatureName 'IIS-ApplicationInit' -ErrorAction SilentlyContinue
            } catch { $appInit = $null }
            if ($appInit -and $appInit.State -eq 'Enabled') {
                $result.webAppInit = $true
                $result.ok = $true
                $result.message = 'IIS disponivel (Application Initialization instalado).'
            } else {
                $result.ok = $true
                $result.message = 'IIS disponivel; Application Initialization NAO instalado: as implantacoes usarao um corte breve.'
            }
        }
    } catch {
        $result.message = "WebAdministration nao disponivel: instale o IIS (Web-Server, Web-Mgmt-Console). ($($_.Exception.Message))"
    }
    [pscustomobject]$result | ConvertTo-Json -Compress
    return
}

# ---- Estado de entornos: dot-source common.ps1 para reutilizar Use-Environment / resolucion de nombres.
. (Join-Path $PSScriptRoot 'common.ps1')

function Get-EnvState([string]$Name) {
    Use-Environment $Name
    $poolState = 'Unknown'
    $siteState = 'Unknown'
    try {
        if (Test-Path "IIS:\AppPools\$script:PoolName") { $poolState = (Get-WebAppPoolState $script:PoolName).Value }
        else { $poolState = 'NotFound' }
    } catch { $poolState = 'Unknown' }
    try {
        if (Test-Path "IIS:\Sites\$script:SiteName") { $siteState = (Get-WebsiteState $script:SiteName).Value }
        else { $siteState = 'NotFound' }
    } catch { $siteState = 'Unknown' }
    [pscustomobject][ordered]@{
        name      = $Name
        poolState = $poolState
        siteState = $siteState
        running   = ($poolState -eq 'Started' -and $siteState -eq 'Started')
    }
}

if ($All) {
    $states = @(foreach ($e in $Cfg.environments) { Get-EnvState $e.name })
    ConvertTo-Json @($states) -Compress
} else {
    if (-not $Environment) { throw 'Indique -Environment <nome>, -All ou -Preflight.' }
    (Get-EnvState $Environment) | ConvertTo-Json -Compress
}
