Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

$Root = Split-Path $PSScriptRoot -Parent
$Cfg = Get-Content (Join-Path $Root 'ci.json') -Raw -Encoding UTF8 | ConvertFrom-Json

$Mirror = Join-Path $Root 'mirror.git'
# work\ guarda el codigo fuente y la compilacion. El proyecto web genera rutas muy largas (obj\...\AspnetCompileMerge\...)
# y MSBuild/aspnet_compiler fallan pasados 260 caracteres: workDir en ci.json permite usar una ruta corta.
$WorkDir = if ($Cfg.PSObject.Properties['workDir'] -and $Cfg.workDir) { $Cfg.workDir } else { Join-Path $Root 'work' }
$LockFile = Join-Path $WorkDir '.lock'
New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null

function Write-Step([string]$Message) { Write-Host ''; Write-Host "==> $Message" }
function Write-Ok([string]$Message) { Write-Host "    $Message" }
function Write-Warn([string]$Message) { Write-Host "    AVISO: $Message" }
function Get-Timestamp { Get-Date -Format 'yyyyMMdd-HHmmss' }

# ---------------------------------------------------------------- environment

# Loads an environment from ci.json and defines $EnvCfg, $Paths and $SiteUrl in the caller's scope.
function Use-Environment([string]$Name) {
    $e = @($Cfg.environments | Where-Object { $_.name -eq $Name })
    if (-not $e.Count) { throw "No existe el entorno '$Name' en ci.json." }
    $script:EnvCfg = $e[0]
    $envRoot = Join-Path $Root "envs\$Name"
    $script:Paths = @{
        Env      = $envRoot
        Site     = Join-Path $envRoot 'site'
        Backups  = Join-Path $envRoot 'backups'
        LocalXdt = Join-Path $envRoot 'Web.local.config'
    }
    foreach ($d in 'Site', 'Backups') { New-Item -ItemType Directory -Force -Path $Paths[$d] | Out-Null }
    $healthPath = if ($EnvCfg.PSObject.Properties['healthPath'] -and $EnvCfg.healthPath) { $EnvCfg.healthPath } else { '/' }
    $script:SiteUrl = "http://localhost:$($EnvCfg.port)"
    $script:HealthUrl = $SiteUrl + $healthPath
}

# ---------------------------------------------------------------- lock (one build/swap at a time)

function Enter-DeployLock {
    $deadline = (Get-Date).AddMinutes(30)
    while ($true) {
        try {
            $script:LockStream = [System.IO.File]::Open($LockFile, 'OpenOrCreate', 'ReadWrite', 'None')
            return
        } catch {
            if ((Get-Date) -gt $deadline) { throw 'Timeout esperando a otra operacion de deploy/rollback.' }
            Start-Sleep -Seconds 5
        }
    }
}

function Exit-DeployLock {
    if ($script:LockStream) { $script:LockStream.Dispose(); $script:LockStream = $null }
}

# ---------------------------------------------------------------- git

function Update-Mirror {
    $env:GIT_TERMINAL_PROMPT = '0'
    $env:GCM_INTERACTIVE = 'never'
    # Ramas del remoto en refs/heads/*, ramas del repo local en refs/local/* (mismo esquema que el servicio).
    if (-not (Test-Path $Mirror)) {
        & git clone --bare --quiet $Cfg.repoUrl $Mirror 2>&1 | ForEach-Object { "$_" } | Out-Host
    } else {
        & git --git-dir $Mirror fetch --prune --quiet origin '+refs/heads/*:refs/heads/*' 2>&1 | ForEach-Object { "$_" } | Out-Host
    }
    if ($LASTEXITCODE) { throw 'No se pudo actualizar el mirror git (credenciales o red).' }
    if ($Cfg.PSObject.Properties['localRepoPath'] -and $Cfg.localRepoPath) {
        & git --git-dir $Mirror fetch --prune --quiet $Cfg.localRepoPath '+refs/heads/*:refs/local/*' 2>&1 | ForEach-Object { "$_" } | Out-Host
        if ($LASTEXITCODE) { throw "No se pudieron leer las ramas del repo local $($Cfg.localRepoPath)." }
    }
}

function Resolve-Commit([string]$Ref) {
    $commit = & git -C $Mirror rev-parse --verify --quiet "$Ref^{commit}"
    if ($LASTEXITCODE -or -not $commit) { throw "No existe la referencia '$Ref'." }
    return "$commit".Trim()
}

# ---------------------------------------------------------------- tools

function Get-MSBuildPath {
    if ($Cfg.msbuild) { return $Cfg.msbuild }
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path $vswhere)) { throw 'No se encuentra vswhere.exe; define "msbuild" en ci.json.' }
    # La instancia elegida debe tener los targets de aplicaciones web (no todas las BuildTools los traen).
    $instances = & $vswhere -all -products * -requires Microsoft.Component.MSBuild -sort -property installationPath
    foreach ($inst in $instances) {
        $exe = Join-Path $inst 'MSBuild\Current\Bin\MSBuild.exe'
        $web = Get-ChildItem (Join-Path $inst 'MSBuild\Microsoft\VisualStudio') -Filter 'Microsoft.WebApplication.targets' -Recurse -ErrorAction SilentlyContinue
        if ((Test-Path $exe) -and $web) { return $exe }
    }
    throw 'No hay ninguna instalacion de Visual Studio/BuildTools con los targets de aplicaciones web (carga "ASP.NET y desarrollo web").'
}

function Get-TransformTasksDll([string]$MSBuild) {
    $msbuildRoot = (Resolve-Path (Join-Path (Split-Path $MSBuild) '..\..')).Path
    $dll = Get-ChildItem (Join-Path $msbuildRoot 'Microsoft\VisualStudio') -Recurse -Filter 'Microsoft.Web.Publishing.Tasks.dll' -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $dll) { throw "No se encuentra Microsoft.Web.Publishing.Tasks.dll bajo $msbuildRoot." }
    return $dll.FullName
}

function Invoke-Robocopy([string]$Source, [string]$Destination, [string[]]$Exclude = @()) {
    $rcArgs = @($Source, $Destination, '/MIR', '/R:2', '/W:2', '/NP', '/NFL', '/NDL', '/NJH', '/NJS')
    $excluded = @($Exclude | ForEach-Object { Join-Path $Destination $_ })
    if ($excluded.Count) { $rcArgs += '/XD'; $rcArgs += $excluded }
    & robocopy @rcArgs | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy fallo (codigo $LASTEXITCODE) copiando $Source -> $Destination" }
    $global:LASTEXITCODE = 0
}

function Clear-Directory([string]$Path, [string[]]$Keep = @()) {
    if (-not (Test-Path -LiteralPath $Path)) { New-Item -ItemType Directory -Path $Path | Out-Null; return }
    foreach ($item in @(Get-ChildItem -LiteralPath $Path -Force | Where-Object { $Keep -notcontains $_.Name })) {
        if ($item.PSIsContainer) {
            # rd con prefijo \\?\ borra arboles con rutas de mas de 260 caracteres (Remove-Item de PowerShell 5.1 no puede).
            & cmd.exe /d /c "rd /s /q `"\\?\$($item.FullName)`" 2>nul"
            $global:LASTEXITCODE = 0
        } else {
            Remove-Item -LiteralPath $item.FullName -Force
        }
        if (Test-Path -LiteralPath $item.FullName) { throw "No se pudo borrar $($item.FullName). Algun proceso lo tiene abierto?" }
    }
}

# ---------------------------------------------------------------- site (IIS Express)

function Get-SiteProcess {
    $sitePath = $Paths.Site
    Get-CimInstance Win32_Process -Filter "Name = 'iisexpress.exe'" |
        Where-Object { $_.CommandLine -and $_.CommandLine.IndexOf("`"$sitePath`"", [StringComparison]::OrdinalIgnoreCase) -ge 0 }
}

function Stop-Site {
    $procs = @(Get-SiteProcess)
    if (-not $procs.Count) { Write-Ok 'El sitio no estaba en ejecucion.'; return }
    foreach ($p in $procs) { Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue }
    foreach ($p in $procs) { Wait-Process -Id $p.ProcessId -Timeout 30 -ErrorAction SilentlyContinue }
    Write-Ok "Sitio detenido (PID $(($procs | ForEach-Object ProcessId) -join ', '))."
}

function Start-Site {
    if (@(Get-SiteProcess).Count) { Write-Ok "El sitio ya esta en ejecucion en $SiteUrl"; return }
    if (-not (Test-Path (Join-Path $Paths.Site 'Web.config'))) { throw 'No hay ninguna version desplegada en este entorno.' }
    $busy = Get-NetTCPConnection -LocalPort $EnvCfg.port -State Listen -ErrorAction SilentlyContinue
    if ($busy) { throw "El puerto $($EnvCfg.port) esta ocupado por otro proceso (PID $($busy[0].OwningProcess))." }

    $argLine = "/path:`"$($Paths.Site)`" /port:$($EnvCfg.port) /clr:v4.0 /systray:false"
    Start-Process -FilePath $Cfg.iisExpress -ArgumentList $argLine -WindowStyle Hidden | Out-Null
    Start-Sleep -Seconds 2
    if (-not @(Get-SiteProcess).Count) { throw "IIS Express no arranco. Ejecutalo a mano para ver el error: & `"$($Cfg.iisExpress)`" $argLine" }
    Write-Ok "Sitio iniciado en $SiteUrl"
}

function Test-SiteHealth {
    $deadline = (Get-Date).AddSeconds($Cfg.healthTimeoutSec)
    Write-Ok "Comprobando $HealthUrl (hasta $($Cfg.healthTimeoutSec)s; la primera peticion compila la app)..."
    $lastError = $null
    while ((Get-Date) -lt $deadline) {
        try {
            $r = Invoke-WebRequest -Uri $HealthUrl -UseBasicParsing -TimeoutSec 120
            Write-Ok "Respuesta HTTP $($r.StatusCode)."
            return $true
        } catch {
            $resp = $_.Exception.Response
            if ($resp -and [int]$resp.StatusCode -lt 500) {
                Write-Ok "Respuesta HTTP $([int]$resp.StatusCode)."
                return $true
            }
            $lastError = if ($resp) { "HTTP $([int]$resp.StatusCode)" } else { $_.Exception.Message }
            Start-Sleep -Seconds 3
        }
    }
    Write-Warn "El sitio no responde correctamente: $lastError"
    return $false
}

# ---------------------------------------------------------------- deployed version info

function Get-DeployInfo([string]$Folder = $Paths.Site) {
    $file = Join-Path $Folder 'deploy-info.json'
    if (Test-Path $file) { return Get-Content $file -Raw | ConvertFrom-Json }
    return $null
}

# ---------------------------------------------------------------- backups
# Nombre: <entorno>_<yyyyMMdd-HHmmss>_<commit>_<motivo>.zip, donde <commit> es la version que contiene.

function Get-Backups {
    $pattern = '^(?<env>.+)_(?<date>\d{8}-\d{6})_(?<commit>[0-9a-f]{7,40}|unknown)_(?<reason>[a-z]+)\.zip$'
    Get-ChildItem $Paths.Backups -Filter '*.zip' | ForEach-Object {
        $m = [regex]::Match($_.Name, $pattern)
        if ($m.Success) {
            [pscustomobject]@{
                Name   = $_.Name
                Date   = [datetime]::ParseExact($m.Groups['date'].Value, 'yyyyMMdd-HHmmss', $null)
                Commit = $m.Groups['commit'].Value
                Reason = $m.Groups['reason'].Value
                SizeMB = [math]::Round($_.Length / 1MB, 1)
                Path   = $_.FullName
            }
        }
    } | Sort-Object Date -Descending
}

# Zip of the current site (excluding preserved folders). Returns the zip path, or $null if site is empty.
function New-SiteBackup([string]$Reason) {
    $files = @(Get-ChildItem -LiteralPath $Paths.Site -Recurse -File -Force | Where-Object {
        $rel = $_.FullName.Substring($Paths.Site.Length + 1)
        -not ($Cfg.preserve | Where-Object { $rel -like "$_\*" })
    })
    if (-not $files.Count) { Write-Ok 'El sitio esta vacio: no hay nada que respaldar.'; return $null }

    $info = Get-DeployInfo
    $commit = if ($info) { $info.ShortCommit } else { 'unknown' }
    $zipPath = Join-Path $Paths.Backups ('{0}_{1}_{2}_{3}.zip' -f $EnvCfg.name, (Get-Timestamp), $commit, $Reason)

    $zip = [System.IO.Compression.ZipFile]::Open($zipPath, 'Create')
    try {
        foreach ($f in $files) {
            $entry = $f.FullName.Substring($Paths.Site.Length + 1).Replace('\', '/')
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $f.FullName, $entry, 'Optimal') | Out-Null
        }
    } catch {
        $zip.Dispose(); $zip = $null
        Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
        throw
    } finally {
        if ($zip) { $zip.Dispose() }
    }
    Write-Ok ("Backup creado: {0} ({1} ficheros, {2} MB)" -f (Split-Path $zipPath -Leaf), $files.Count, [math]::Round((Get-Item $zipPath).Length / 1MB, 1))
    return $zipPath
}

# Replaces the site with the content of a backup zip (preserved folders untouched).
function Restore-SiteBackup([string]$ZipPath) {
    $tmp = Join-Path $WorkDir 'restore'
    Clear-Directory $tmp
    [System.IO.Compression.ZipFile]::ExtractToDirectory($ZipPath, $tmp)
    Invoke-Robocopy $tmp $Paths.Site $Cfg.preserve
    Clear-Directory $tmp
    Write-Ok "Restaurado: $(Split-Path $ZipPath -Leaf)"
}

function Remove-OldBackups {
    $old = @(Get-Backups | Select-Object -Skip $Cfg.keepBackups)
    foreach ($b in $old) { Remove-Item $b.Path -Force; Write-Ok "Backup antiguo eliminado: $($b.Name)" }
}
