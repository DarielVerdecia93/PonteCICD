Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

# IIS completo: toda la gestion de sitio/pool/binding se hace con el modulo WebAdministration
# (proveedor IIS:\). Si no esta disponible, IIS no esta instalado y no podemos operar.
try {
    Import-Module WebAdministration -ErrorAction Stop
} catch {
    throw 'WebAdministration no disponible: instale IIS (Web-Server, Web-Mgmt-Console).'
}

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

# Numero de releases a conservar por entorno (zips de backup aparte, ver keepBackups).
function Get-KeepReleases {
    if ($Cfg.PSObject.Properties['keepReleases'] -and $Cfg.keepReleases) { return [int]$Cfg.keepReleases }
    return 5
}

# ---------------------------------------------------------------- environment

# Loads an environment from ci.json and defines $EnvCfg, $Paths, nombres IIS, $SiteUrl y $HealthUrl en scope del script.
function Use-Environment([string]$Name) {
    $e = @($Cfg.environments | Where-Object { $_.name -eq $Name })
    if (-not $e.Count) { throw "No existe el entorno '$Name' en ci.json." }
    $script:EnvCfg = $e[0]

    # Lectura defensiva de campos nuevos (StrictMode, pueden faltar en ci.json antiguo).
    $sitePath = if ($EnvCfg.PSObject.Properties['sitePath'] -and $EnvCfg.sitePath) { $EnvCfg.sitePath } else { '' }
    # Sin sitePath explicito se usa la convencion por defecto C:\inetpub\wwwroot\deploys\<env> (dentro del arbol de IIS).
    $envBase = if ($sitePath) { $sitePath } else { Join-Path $env:SystemDrive "inetpub\wwwroot\deploys\$Name" }

    $script:SiteName = if ($EnvCfg.PSObject.Properties['siteName'] -and $EnvCfg.siteName) { $EnvCfg.siteName } else { "LegadosCI-$Name" }
    $script:PoolName = if ($EnvCfg.PSObject.Properties['appPoolName'] -and $EnvCfg.appPoolName) { $EnvCfg.appPoolName } else { "LegadosCI-$Name" }
    $script:HostHeader = if ($EnvCfg.PSObject.Properties['hostHeader'] -and $EnvCfg.hostHeader) { $EnvCfg.hostHeader } else { '' }
    $script:Enable32Bit = if ($EnvCfg.PSObject.Properties['enable32Bit'] -and $EnvCfg.enable32Bit) { [bool]$EnvCfg.enable32Bit } else { $false }

    $script:Paths = @{
        Env      = $envBase
        Releases = Join-Path $envBase 'releases'
        Shared   = Join-Path $envBase 'shared'
        Backups  = Join-Path $envBase 'backups'
        Current  = Join-Path $envBase 'current.txt'
        LocalXdt = Join-Path $envBase 'Web.local.config'
    }
    foreach ($d in 'Releases', 'Shared', 'Backups') { New-Item -ItemType Directory -Force -Path $Paths[$d] | Out-Null }

    $healthPath = if ($EnvCfg.PSObject.Properties['healthPath'] -and $EnvCfg.healthPath) { $EnvCfg.healthPath } else { '/' }
    $script:HealthPath = $healthPath
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

# Borra el contenido de $Path (conservando $Keep). Si $Path puede contener junctions de Preserve,
# usar Remove-ReleaseTree sobre cada release en su lugar; aqui se eliminan los junctions de primer
# nivel antes del borrado recursivo para no atravesar ningun punto de reanalisis.
function Clear-Directory([string]$Path, [string[]]$Keep = @()) {
    if (-not (Test-Path -LiteralPath $Path)) { New-Item -ItemType Directory -Path $Path | Out-Null; return }
    foreach ($item in @(Get-ChildItem -LiteralPath $Path -Force | Where-Object { $Keep -notcontains $_.Name })) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            # Es un junction/symlink: quitar solo el enlace, nunca su destino.
            & cmd /c rmdir "$($item.FullName)" 2>&1 | Out-Null
        } elseif ($item.PSIsContainer) {
            # rd con prefijo \\?\ borra arboles con rutas de mas de 260 caracteres (Remove-Item de PowerShell 5.1 no puede).
            & cmd.exe /d /c "rd /s /q `"\\?\$($item.FullName)`" 2>nul"
            $global:LASTEXITCODE = 0
        } else {
            Remove-Item -LiteralPath $item.FullName -Force
        }
        if (Test-Path -LiteralPath $item.FullName) { throw "No se pudo borrar $($item.FullName). Algun proceso lo tiene abierto?" }
    }
}

# ---------------------------------------------------------------- IIS: sitio / pool / binding

# Devuelve la ruta de la release activa (releases\<id> segun current.txt). Si no hay una valida,
# crea releases\_bootstrap con un Web.config minimo y la devuelve (para que New-Website tenga una
# ruta fisica existente en el primer arranque). Invariante "current.txt siempre valido" solo se
# cumple una vez existe la primera release real; antes, physicalPath apunta a _bootstrap y
# current.txt esta ausente (se trata como no-desplegado).
function Get-ActiveReleasePath {
    if (Test-Path $Paths.Current) {
        $id = (Get-Content -LiteralPath $Paths.Current -Raw -Encoding ASCII).Trim()
        if ($id) {
            $rel = Join-Path $Paths.Releases $id
            if (Test-Path (Join-Path $rel 'Web.config')) { return $rel }
        }
    }
    $bootstrap = Join-Path $Paths.Releases '_bootstrap'
    New-Item -ItemType Directory -Force -Path $bootstrap | Out-Null
    $wc = Join-Path $bootstrap 'Web.config'
    if (-not (Test-Path $wc)) {
        Set-Content -LiteralPath $wc -Encoding UTF8 -Value '<?xml version="1.0" encoding="utf-8"?><configuration></configuration>'
    }
    return $bootstrap
}

# Crea shared\<entry> si falta y enlaza releases\<id>\<entry> -> shared\<entry> con un junction (idempotente).
function Add-PreserveJunctions([string]$Release) {
    foreach ($entry in @($Cfg.preserve)) {
        if (-not $entry) { continue }
        $sharedDir = Join-Path $Paths.Shared $entry
        New-Item -ItemType Directory -Force -Path $sharedDir | Out-Null
        $link = Join-Path $Release $entry
        if (Test-Path $link) { continue }   # ya existe (enlace o carpeta): idempotente
        $parent = Split-Path $link -Parent
        if (-not (Test-Path $parent)) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
        & cmd /c mklink /J "$link" "$sharedDir" 2>&1 | Out-Null
        if ($LASTEXITCODE -or -not (Test-Path $link)) { throw "No se pudo enlazar $entry." }
    }
}

# Quita solo los junctions de Preserve de una release (rmdir sobre un junction borra el enlace, no el destino).
function Remove-PreserveJunctions([string]$Release) {
    foreach ($entry in @($Cfg.preserve)) {
        if (-not $entry) { continue }
        $link = Join-Path $Release $entry
        if (Test-Path $link) { & cmd /c rmdir "$link" 2>&1 | Out-Null }
    }
}

# Borra el arbol de una release de forma segura: primero los junctions de Preserve, luego el resto.
function Remove-ReleaseTree([string]$Release) {
    if (-not (Test-Path $Release)) { return }
    Remove-PreserveJunctions $Release
    Remove-Item -LiteralPath $Release -Recurse -Force
}

# Enumera las bindings http y anade *:<port>:<hostheader> solo si no hay ya una equivalente.
# Trata *, 0.0.0.0 y la IP ausente como equivalentes. Nunca borra otras bindings.
function Ensure-Binding {
    $wildcards = @('*', '0.0.0.0', '')
    $existing = @(Get-WebBinding -Name $script:SiteName -Protocol http -ErrorAction SilentlyContinue)
    foreach ($b in $existing) {
        $parts = ($b.bindingInformation -split ':', 3)
        $ip = if ($parts.Count -ge 1) { $parts[0] } else { '' }
        $port = if ($parts.Count -ge 2) { $parts[1] } else { '' }
        $bhost = if ($parts.Count -ge 3) { $parts[2] } else { '' }
        if (($wildcards -contains $ip) -and ($port -eq "$($EnvCfg.port)") -and ($bhost -eq $script:HostHeader)) {
            return  # ya existe una binding equivalente
        }
    }
    New-WebBinding -Name $script:SiteName -Protocol http -Port $EnvCfg.port -HostHeader $script:HostHeader | Out-Null
}

# Crea app pool / sitio / binding si faltan (idempotente) y aplica config de calentamiento + permisos.
function Ensure-Site {
    # 1) carpetas base
    foreach ($d in $Paths.Releases, $Paths.Shared, $Paths.Backups) {
        New-Item -ItemType Directory -Force -Path $d | Out-Null
    }

    # 2) app pool (.NET Framework 4.8, Integrated)
    if (-not (Test-Path "IIS:\AppPools\$script:PoolName")) {
        New-WebAppPool -Name $script:PoolName | Out-Null
    }
    # Valores como STRING ENUM, nunca numericos.
    Set-ItemProperty "IIS:\AppPools\$script:PoolName" -Name managedRuntimeVersion -Value 'v4.0'
    Set-ItemProperty "IIS:\AppPools\$script:PoolName" -Name managedPipelineMode   -Value 'Integrated'
    Set-ItemProperty "IIS:\AppPools\$script:PoolName" -Name startMode             -Value 'AlwaysRunning'
    Set-ItemProperty "IIS:\AppPools\$script:PoolName" -Name enable32BitAppOnWin64 -Value $script:Enable32Bit

    # 3) permisos: el worker (IIS AppPool\<pool>) debe escribir en shared\ y aspnet-temp.
    #    Se aplican DESPUES de crear el pool (la identidad virtual solo existe tras New-WebAppPool).
    $aspnetTemp = Join-Path $Paths.Env 'aspnet-temp'
    New-Item -ItemType Directory -Force -Path $aspnetTemp | Out-Null
    foreach ($target in @($Paths.Shared, $aspnetTemp)) {
        & icacls "$target" /grant "IIS AppPool\$($script:PoolName):(OI)(CI)M" /T /C /Q 2>&1 | Out-Null
        # IIS_IUSRS por SID conocido (independiente del idioma del SO).
        & icacls "$target" /grant '*S-1-5-32-568:(OI)(CI)M' /T /C /Q 2>&1 | Out-Null
    }

    # 4) sitio + binding (requiere un physicalPath existente: la release activa o _bootstrap)
    $physical = Get-ActiveReleasePath
    if (-not (Test-Path "IIS:\Sites\$script:SiteName")) {
        if ($script:HostHeader) {
            New-Website -Name $script:SiteName -PhysicalPath $physical -ApplicationPool $script:PoolName `
                -Port $EnvCfg.port -HostHeader $script:HostHeader -Force | Out-Null
        } else {
            # Host header vacio: se OMITE el parametro para que el binding serialice a "*:<port>:".
            New-Website -Name $script:SiteName -PhysicalPath $physical -ApplicationPool $script:PoolName `
                -Port $EnvCfg.port -Force | Out-Null
        }
    } else {
        Set-ItemProperty "IIS:\Sites\$script:SiteName" -Name applicationPool -Value $script:PoolName
        Set-ItemProperty "IIS:\Sites\$script:SiteName" -Name physicalPath    -Value $physical
        Ensure-Binding
    }

    # 5) Application Initialization (sin cortes): preloadEnabled en la app raiz (path '/'), no en applicationDefaults.
    try {
        Set-WebConfigurationProperty -PSPath 'IIS:\' `
            -Filter "system.applicationHost/sites/site[@name='$($script:SiteName)']/application[@path='/']" `
            -Name preloadEnabled -Value $true
        Set-WebConfigurationProperty -PSPath 'IIS:\' -Location $script:SiteName `
            -Filter 'system.webServer/applicationInitialization' -Name doAppInitAfterRestart -Value $true
        $init = @(Get-WebConfiguration -PSPath 'IIS:\' -Location $script:SiteName `
                -Filter 'system.webServer/applicationInitialization/add' -ErrorAction SilentlyContinue)
        if (-not ($init | Where-Object { $_.initializationPage -eq $script:HealthPath })) {
            Add-WebConfiguration -PSPath 'IIS:\' -Location $script:SiteName `
                -Filter 'system.webServer/applicationInitialization' `
                -Value @{ initializationPage = $script:HealthPath }
        }
    } catch {
        Write-Warn "Application Initialization no disponible: puede haber una pausa breve en el primer acceso. ($($_.Exception.Message))"
    }
}

# Crea releases\<id>, copia stage -> release, enlaza Preserve y escribe deploy-info.json dentro de la release.
function New-Release([string]$Stage, [hashtable]$Info) {
    $id = '{0}_{1}' -f (Get-Timestamp), $Info.ShortCommit
    $release = Join-Path $Paths.Releases $id
    New-Item -ItemType Directory -Force -Path $release | Out-Null
    Invoke-Robocopy $Stage $release
    Add-PreserveJunctions $release
    ([pscustomobject]$Info | ConvertTo-Json) | Set-Content (Join-Path $release 'deploy-info.json') -Encoding UTF8
    Write-Ok "Release creada: $id"
    return $id
}

# Reapunta el sitio a releases\<id>, escribe current.txt (ASCII, sin BOM, sin salto final) y recicla el pool (solapado).
function Switch-Release([string]$ReleaseId) {
    $path = Join-Path $Paths.Releases $ReleaseId
    if (-not (Test-Path (Join-Path $path 'Web.config'))) { throw "La release $ReleaseId no es valida (falta Web.config)." }
    Set-ItemProperty "IIS:\Sites\$script:SiteName" -Name physicalPath -Value $path
    [IO.File]::WriteAllText($Paths.Current, $ReleaseId, [Text.Encoding]::ASCII)
    Restart-WebAppPool $script:PoolName   # recycle solapado (disallowOverlappingRotation=false, default)
    Write-Ok "Release activa: $ReleaseId"
}

# Nudge redundante tras el swap: fuerza/confirma la compilacion de la release nueva (HTTP < 500).
# El gate real es Test-SiteHealth tras el calentamiento de Application Initialization.
function Invoke-WarmRelease {
    $deadline = (Get-Date).AddSeconds($Cfg.healthTimeoutSec)
    Write-Ok "Calentando $HealthUrl (hasta $($Cfg.healthTimeoutSec)s)..."
    while ((Get-Date) -lt $deadline) {
        try {
            $r = Invoke-WebRequest -Uri $HealthUrl -UseBasicParsing -TimeoutSec 120
            Write-Ok "Calentamiento: HTTP $($r.StatusCode)."
            return $true
        } catch {
            $resp = $_.Exception.Response
            if ($resp -and [int]$resp.StatusCode -lt 500) {
                Write-Ok "Calentamiento: HTTP $([int]$resp.StatusCode)."
                return $true
            }
            Start-Sleep -Seconds 3
        }
    }
    Write-Warn 'La release nueva no respondio al calentamiento.'
    return $false
}

function Start-Site {
    Ensure-Site
    if ((Get-WebAppPoolState $script:PoolName).Value -ne 'Started') { Start-WebAppPool $script:PoolName }
    if ((Get-WebsiteState   $script:SiteName).Value -ne 'Started') { Start-Website   $script:SiteName }
    Write-Ok "Sitio IIS '$script:SiteName' iniciado en $SiteUrl"
}

function Stop-Site {
    if (-not (Test-Path "IIS:\Sites\$script:SiteName")) { Write-Ok 'El sitio IIS no existe.'; return }
    if ((Get-WebsiteState    $script:SiteName).Value -eq 'Started') { Stop-Website    $script:SiteName }
    if ((Get-WebAppPoolState $script:PoolName).Value -eq 'Started') { Stop-WebAppPool $script:PoolName }
    Write-Ok "Sitio IIS '$script:SiteName' detenido."
}

# Elimina sitio + pool de IIS, solo cuando los nombres son de LegadosCI (nunca sitios externos).
function Remove-Site {
    Stop-Site
    if ($script:SiteName -like 'LegadosCI-*') {
        if (Test-Path "IIS:\Sites\$script:SiteName") { Remove-Website -Name $script:SiteName }
    } else {
        Write-Warn "El sitio '$script:SiteName' no lo creo LegadosCI; no se elimina de IIS."
    }
    if ($script:PoolName -like 'LegadosCI-*') {
        if (Test-Path "IIS:\AppPools\$script:PoolName") { Remove-WebAppPool -Name $script:PoolName }
    } else {
        Write-Warn "El app pool '$script:PoolName' no lo creo LegadosCI; no se elimina de IIS."
    }
}

# Comprobacion de salud en 3 partes: (a) pool Started (b) sitio Started (c) HTTP < 500 con reintentos.
function Test-SiteHealth {
    if ((Get-WebAppPoolState $script:PoolName).Value -ne 'Started') { Write-Warn 'App pool no esta Started.'; return $false }
    if ((Get-WebsiteState    $script:SiteName).Value -ne 'Started') { Write-Warn 'El sitio no esta Started.'; return $false }
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

function Get-DeployInfo {
    $file = Join-Path (Get-ActiveReleasePath) 'deploy-info.json'
    if (Test-Path $file) { return Get-Content $file -Raw | ConvertFrom-Json }
    return $null
}

# Poda releases antiguas, conservando las $keepReleases mas recientes y SIEMPRE la activa (current.txt)
# y la inmediatamente anterior. Borra junctions antes del arbol.
function Remove-OldReleases {
    $keep = Get-KeepReleases
    $active = if (Test-Path $Paths.Current) { (Get-Content -LiteralPath $Paths.Current -Raw -Encoding ASCII).Trim() } else { '' }
    $all = @(Get-ChildItem -LiteralPath $Paths.Releases -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -ne '_bootstrap' } | Sort-Object Name -Descending)
    if (-not $all.Count) { return }
    # Candidatas protegidas: las $keep mas recientes + la activa + la anterior a la activa.
    $protected = New-Object System.Collections.Generic.HashSet[string]
    foreach ($r in ($all | Select-Object -First $keep)) { [void]$protected.Add($r.Name) }
    if ($active) {
        [void]$protected.Add($active)
        $idx = 0
        for ($i = 0; $i -lt $all.Count; $i++) { if ($all[$i].Name -eq $active) { $idx = $i; break } }
        if ($idx + 1 -lt $all.Count) { [void]$protected.Add($all[$idx + 1].Name) }
    }
    foreach ($r in $all) {
        if (-not $protected.Contains($r.Name)) {
            Remove-ReleaseTree $r.FullName
            Write-Ok "Release antigua eliminada: $($r.Name)"
        }
    }
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

# Zip de la release activa (solo contenido inmutable; nunca Preserve ni destinos de junction).
# Devuelve la ruta del zip, o $null si no hay release activa / esta vacia.
function New-SiteBackup([string]$Reason) {
    $source = Get-ActiveReleasePath
    if ((Split-Path $source -Leaf) -eq '_bootstrap') { Write-Ok 'No hay release activa: no hay nada que respaldar.'; return $null }

    # -Attributes !ReparsePoint impide descender por el junction de Preserve; el filtro de prefijo es 2a defensa.
    $files = @(Get-ChildItem -LiteralPath $source -Recurse -File -Force -Attributes !ReparsePoint | Where-Object {
            $rel = $_.FullName.Substring($source.Length + 1)
            -not (@($Cfg.preserve) | Where-Object { $_ -and $rel -like "$_\*" })
        })
    if (-not $files.Count) { Write-Ok 'La release esta vacia: no hay nada que respaldar.'; return $null }

    $info = Get-DeployInfo
    $commit = if ($info) { $info.ShortCommit } else { 'unknown' }
    $zipPath = Join-Path $Paths.Backups ('{0}_{1}_{2}_{3}.zip' -f $EnvCfg.name, (Get-Timestamp), $commit, $Reason)

    $zip = [System.IO.Compression.ZipFile]::Open($zipPath, 'Create')
    try {
        foreach ($f in $files) {
            $entry = $f.FullName.Substring($source.Length + 1).Replace('\', '/')
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

# Restaura un backup zip: extrae a una release nueva, enlaza Preserve y hace Switch-Release a ella.
function Restore-SiteBackup([string]$ZipPath) {
    $tmp = Join-Path $WorkDir 'restore'
    Clear-Directory $tmp
    [System.IO.Compression.ZipFile]::ExtractToDirectory($ZipPath, $tmp)

    $id = '{0}_restore' -f (Get-Timestamp)
    $release = Join-Path $Paths.Releases $id
    New-Item -ItemType Directory -Force -Path $release | Out-Null
    Invoke-Robocopy $tmp $release
    Add-PreserveJunctions $release
    Clear-Directory $tmp

    Switch-Release $id
    Write-Ok "Restaurado: $(Split-Path $ZipPath -Leaf) -> release $id"
}

function Remove-OldBackups {
    $old = @(Get-Backups | Select-Object -Skip $Cfg.keepBackups)
    foreach ($b in $old) { Remove-Item $b.Path -Force; Write-Ok "Backup antiguo eliminado: $($b.Name)" }
}
