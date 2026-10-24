<#
.SYNOPSIS
    Compila la rama de un entorno y la despliega, guardando antes un zip de la version actual.
    Si la nueva version no responde, restaura automaticamente la anterior.

.EXAMPLE
    .\deploy.ps1 -Environment develop
    .\deploy.ps1 -Environment develop -Ref 1a2b3c4      # un commit concreto en vez de la punta de la rama
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Environment,
    [string]$Ref,
    [switch]$NoFetch,
    [switch]$NoAutoRollback
)

trap { Write-Host "ERROR: $($_.Exception.Message)"; exit 1 }
. (Join-Path $PSScriptRoot 'common.ps1')
Use-Environment $Environment

Enter-DeployLock
try {
    # ------------------------------------------------------------ 1. source
    Write-Step "A preparar o codigo-fonte ($($EnvCfg.branch))"
    if (-not $NoFetch) { Update-Mirror }
    if (-not $Ref) {
        $isLocal = $EnvCfg.PSObject.Properties['source'] -and $EnvCfg.source -eq 'local'
        $Ref = if ($isLocal) { "refs/local/$($EnvCfg.branch)" } else { "refs/heads/$($EnvCfg.branch)" }
    }
    $commit = Resolve-Commit $Ref
    $short = $commit.Substring(0, 7)
    $subject = (& git -C $Mirror log -1 --format=%s $commit | Out-String).Trim()
    $author = (& git -C $Mirror log -1 --format=%an $commit | Out-String).Trim()

    $srcRoot = Join-Path $WorkDir 'src'
    Clear-Directory $srcRoot -Keep 'packages'
    $tar = Join-Path $WorkDir 'src.tar'
    & git -C $Mirror archive --format=tar -o $tar $commit
    if ($LASTEXITCODE) { throw 'Falha no git archive.' }
    & tar -xf $tar -C $srcRoot
    if ($LASTEXITCODE) { throw 'Nao foi possivel extrair o codigo-fonte.' }
    Remove-Item $tar -Force
    Write-Ok "Commit $short - $subject ($author)"

    # packages\ no esta versionado: se siembra (incremental) desde una copia local y se restaura lo que falte.
    $packages = Join-Path $srcRoot 'packages'
    if ($Cfg.packagesSource -and (Test-Path $Cfg.packagesSource)) {
        & robocopy $Cfg.packagesSource $packages /E /R:2 /W:2 /NP /NFL /NDL /NJH /NJS | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "Nao foi possivel copiar os pacotes a partir de $($Cfg.packagesSource)." }
    }
    $msbuild = Get-MSBuildPath
    Write-Ok "MSBuild: $msbuild"
    & $msbuild (Join-Path $srcRoot $Cfg.solutionFile) /nologo /v:quiet /t:Restore /p:RestorePackagesConfig=true | Out-Host
    if ($LASTEXITCODE) { Write-Warn 'A restauracao de pacotes NuGet falhou; tenta-se compilar com os pacotes existentes.' }

    # ------------------------------------------------------------ 2. build + publish
    Write-Step "A compilar e publicar ($($EnvCfg.configuration))"
    $stage = Join-Path $WorkDir 'stage'
    Clear-Directory $stage
    $buildLog = Join-Path $WorkDir 'build.log'
    # /t:WebPublish publica solo el proyecto web (las referencias solo se compilan).
    & $msbuild (Join-Path $srcRoot $Cfg.projectFile) /nologo /m /v:minimal /clp:ErrorsOnly `
        "/flp:LogFile=$buildLog;Verbosity=normal;Encoding=UTF-8" `
        /t:WebPublish /p:Configuration=$($EnvCfg.configuration) /p:Platform=AnyCPU `
        /p:WebPublishMethod=FileSystem /p:PublishUrl="$stage" /p:DeleteExistingFiles=true `
        /p:CustomAfterMicrosoftCommonTargets="$(Join-Path $PSScriptRoot 'publish.targets')" `
        /p:DeployExcludeFolders="$(@($Cfg.excludeFromPublish) -join '%3B')" `
        /p:DeployIncludeFiles="$(@($Cfg.includeInPublish) -join '%3B')" `
        /p:ExcludeApp_Data=false /p:AutoParameterizationWebConfigConnectionStrings=false | Out-Host
    if ($LASTEXITCODE) { throw "A compilacao falhou (codigo $LASTEXITCODE). Log completo: $buildLog. O site nao foi alterado." }
    if (-not (Test-Path (Join-Path $stage 'Web.config'))) { throw 'A publicacao nao gerou o Web.config.' }
    foreach ($p in $Cfg.preserve) { Remove-Item (Join-Path $stage $p) -Recurse -Force -ErrorAction SilentlyContinue }
    Write-Ok 'Publicacao gerada.'

    # ------------------------------------------------------------ 3. local config
    $xdt = $Paths.LocalXdt
    if (-not (Test-Path $xdt)) { $xdt = Join-Path $Root 'Web.local.config' }
    if (Test-Path $xdt) {
        Write-Step "A aplicar $xdt"
        $base = Join-Path $WorkDir 'Web.base.config'
        Copy-Item (Join-Path $stage 'Web.config') $base -Force
        & $msbuild (Join-Path $PSScriptRoot 'transform.proj') /nologo /v:minimal /clp:ErrorsOnly `
            /p:TasksDll="$(Get-TransformTasksDll $msbuild)" `
            /p:Source="$base" /p:Transform="$xdt" /p:Destination="$(Join-Path $stage 'Web.config')" | Out-Host
        if ($LASTEXITCODE) { throw 'Falha na transformacao do Web.config. O site nao foi alterado.' }
        Remove-Item $base -Force
    } else {
        Write-Warn 'Nao ha Web.local.config (nem do ambiente nem global): usa-se o Web.config do repo tal como esta.'
    }

    # La compilacion dinamica de ASP.NET (global.asax, vistas) se hace en una carpeta del entorno y no en
    # %TEMP%\Temporary ASP.NET Files, donde Smart App Control bloquea las DLL generadas.
    $aspnetTemp = Join-Path $Paths.Env 'aspnet-temp'
    New-Item -ItemType Directory -Force -Path $aspnetTemp | Out-Null
    $webConfigPath = Join-Path $stage 'Web.config'
    $webConfig = New-Object System.Xml.XmlDocument
    $webConfig.PreserveWhitespace = $true
    $webConfig.Load($webConfigPath)
    $compilation = $webConfig.SelectSingleNode('/configuration/system.web/compilation')
    if ($compilation) {
        $compilation.SetAttribute('tempDirectory', $aspnetTemp)
        $webConfig.Save($webConfigPath)
    }

    $deployInfo = @{
        Commit        = $commit
        ShortCommit   = $short
        Subject       = $subject
        Author        = $author
        Branch        = $EnvCfg.branch
        Configuration = $EnvCfg.configuration
        DeployedAt    = (Get-Date).ToString('s')
    }

    # ------------------------------------------------------------ 4. release + backup + swap (sin corte)
    $previous = Get-DeployInfo
    $prevRelease = if (Test-Path $Paths.Current) { (Get-Content -LiteralPath $Paths.Current -Raw -Encoding ASCII).Trim() } else { '' }

    Write-Step "A publicar release $short"
    $releaseId = New-Release $stage $deployInfo

    Write-Step 'A guardar a versao atual'
    $backup = New-SiteBackup 'deploy'

    Write-Step 'A assegurar o site no IIS'
    Ensure-Site

    Write-Step "A ativar release $short (troca de caminho fisico + recycle sobreposto)"
    Switch-Release $releaseId

    Write-Step 'A aquecer a release nova'
    $warm = Invoke-WarmRelease

    if (-not ($warm -and (Test-SiteHealth))) {
        if ($NoAutoRollback) { throw "A implantacao de $short nao responde." }
        if ($prevRelease) {
            Write-Step "Rollback de emergencia para a release anterior ($prevRelease)"
            Switch-Release $prevRelease
            Test-SiteHealth | Out-Null
            throw "A implantacao de $short falhou a verificacao de saude; a release anterior foi reativada."
        } elseif ($backup) {
            Write-Step "Rollback automatico para $(Split-Path $backup -Leaf)"
            Restore-SiteBackup $backup
            Test-SiteHealth | Out-Null
            throw "A implantacao de $short falhou a verificacao de saude; a versao anterior foi restaurada."
        } else {
            throw "A implantacao de $short nao responde e nao ha versao anterior para restaurar."
        }
    }

    Remove-OldReleases
    Remove-OldBackups
    Clear-Directory $stage

    $from = if ($previous) { $previous.ShortCommit } else { '(vazio)' }
    Write-Step "Implantacao concluida: $from -> $short em $SiteUrl"
}
finally {
    Exit-DeployLock
}
