#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Instala (ou desinstala) o Ponte CI/CD como servico do Windows elevado que administra o IIS.

    O servico corre ELEVADO porque cria e governa sites e application pools do IIS
    (New-WebSite/New-WebAppPool, troca de physical path, bindings, warm-up). Para isso a conta
    do servico tem de pertencer ao grupo local de Administradores: este script adiciona-a se faltar.

    Opcoes de conta:
      - Por omissao usa a SUA conta do Windows (assim mantem as suas credenciais de git para o Azure DevOps);
        o Windows pede a palavra-passe, o script nao a guarda. Nao serve se iniciar sessao com conta
        Microsoft (PIN/Hello) sem uma palavra-passe local pratica.
      - Com -LocalSystem o servico corre como LocalSystem: ja e administrador local e NAO pede
        palavra-passe. ATENCAO: o LocalSystem NAO leva as suas credenciais de git; para um repo privado do
        Azure DevOps e preciso configurar o git ao nivel do sistema com um PAT.

    Antes de arrancar verifica que o IIS esta presente (W3SVC, WAS, o modulo WebAdministration e a
    funcionalidade Web-AppInit). Se faltar o IIS avisa (nao aborta: pode estar a instala-lo em paralelo).

    Utilizacao a partir do instalador (sem interacao):
      -Account <conta> -PasswordFile <ficheiro>   conta de utilizador; a palavra-passe e lida do ficheiro
      -InstallIis                                 ativa as funcionalidades do IIS que faltem
      -AdminUser <utilizador> -AdminPasswordFile <ficheiro>
                                                  cria (ou substitui) o administrador do painel; a palavra-passe e lida do ficheiro.
                                                  Sem isto o painel pede para o criar no primeiro acesso (a partir deste computador).
      -HostName <nome>                            nome do painel (omissao: admin-pontecicd), ex.: http://admin-pontecicd/
                                                  Regista-o no ficheiro hosts e em listenUrl; localhost:5080 continua a funcionar.

.EXAMPLE
    .\install-service.ps1                 # corre com a SUA conta (pede a palavra-passe do Windows)
    .\install-service.ps1 -LocalSystem    # corre como LocalSystem (sem palavra-passe)
    .\install-service.ps1 -LocalSystem -InstallIis
    .\install-service.ps1 -Uninstall
#>
param(
    [switch]$Uninstall,
    [switch]$LocalSystem,
    [switch]$InstallIis,
    [string]$Account,
    [string]$PasswordFile,
    [string]$HostName = 'admin-pontecicd',
    [string]$AdminUser,
    [string]$AdminPasswordFile
)

$ErrorActionPreference = 'Stop'
$name = 'PonteCICD'
$exe = Join-Path $PSScriptRoot 'service\PonteCICD.exe'

# ---------------------------------------------------------------- helpers

# O servico administra o IIS, pelo que a sua conta tem de ser Administrador local. Adicionamo-la se faltar.
# O grupo chama-se 'Administradores' no Windows em portugues e 'Administrators' em ingles; tentamos ambos
# e, se o modulo Microsoft.PowerShell.LocalAccounts nao estiver disponivel, usamos 'net localgroup'.
function Add-ToLocalAdministrators([string]$member) {
    Write-Host "A garantir que $member pertence ao grupo local de Administradores (o servico administra o IIS)..."
    foreach ($group in @('Administradores', 'Administrators')) {
        try {
            if (Get-LocalGroup -Name $group -ErrorAction SilentlyContinue) {
                $already = Get-LocalGroupMember -Group $group -ErrorAction SilentlyContinue |
                    Where-Object { $_.Name -ieq $member -or $_.Name -ieq ($member -replace '^\.\\', "$env:COMPUTERNAME\") }
                if ($already) {
                    Write-Host "  $member ja e Administrador local (grupo $group)."
                } else {
                    Add-LocalGroupMember -Group $group -Member $member -ErrorAction Stop
                    Write-Host "  $member adicionado ao grupo $group."
                }
                return
            }
        } catch {
            Write-Host "  Nao foi possivel usar o modulo para o grupo '$group': $($_.Exception.Message)"
        }
    }
    foreach ($group in @('Administradores', 'Administrators')) {
        & net localgroup $group $member /add 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) { Write-Host "  $member adicionado ao grupo $group (net localgroup)."; return }
    }
    Write-Host "  AVISO: nao foi possivel confirmar a pertenca a Administradores; adicione-a manualmente se o servico nao conseguir administrar o IIS."
}

# Concede a uma conta o direito "Iniciar sessao como servico" (necessario para arrancar como servico).
function Grant-ServiceLogonRight([string]$account) {
    $sid = (New-Object Security.Principal.NTAccount ($account -replace '^\.\\', "$env:COMPUTERNAME\")).Translate([Security.Principal.SecurityIdentifier]).Value
    $tmp = Join-Path $env:TEMP 'pontecicd-rights'
    New-Item -ItemType Directory -Force $tmp | Out-Null
    & secedit /export /cfg "$tmp\current.inf" /areas USER_RIGHTS | Out-Null
    $current = (Select-String "$tmp\current.inf" -Pattern '^SeServiceLogonRight\s*=\s*(.*)$').Matches | ForEach-Object { $_.Groups[1].Value }
    if ("$current" -notmatch [regex]::Escape("*$sid")) {
        $value = if ($current) { "$current,*$sid" } else { "*$sid" }
        @('[Unicode]', 'Unicode=yes', '[Version]', 'signature="$CHICAGO$"', 'Revision=1', '[Privilege Rights]', "SeServiceLogonRight = $value") |
            Set-Content "$tmp\grant.inf" -Encoding Unicode
        & secedit /configure /db "$tmp\grant.sdb" /cfg "$tmp\grant.inf" /areas USER_RIGHTS | Out-Null
        Write-Host "Concedido 'Iniciar sessao como servico' a $account."
    }
    Remove-Item $tmp -Recurse -Force
}

$hostsFile = Join-Path $env:SystemRoot 'System32\drivers\etc\hosts'
$hostsTag = '# PonteCICD'

# Nome do painel: 127.0.0.1 <nome> no ficheiro hosts (idempotente, marcado para o poder retirar depois).
function Set-HostsEntry([string]$hostName) {
    $lines = @(Get-Content $hostsFile -ErrorAction SilentlyContinue | Where-Object { $_ -notmatch [regex]::Escape($hostsTag) })
    $lines += "127.0.0.1`t$hostName`t$hostsTag"
    Set-Content $hostsFile -Value $lines -Encoding ASCII
    Write-Host "Nome do painel registado no ficheiro hosts: $hostName -> 127.0.0.1"
}
function Remove-HostsEntry {
    if (-not (Test-Path $hostsFile)) { return }
    $lines = @(Get-Content $hostsFile | Where-Object { $_ -notmatch [regex]::Escape($hostsTag) })
    Set-Content $hostsFile -Value $lines -Encoding ASCII
}

# listenUrl do ci.json: o nome (porto 80, partilhado com o IIS por nome de host) + localhost:5080 como alternativa.
function Set-ListenUrl([string]$hostName) {
    $ci = Join-Path $PSScriptRoot 'ci.json'
    $url = "http://$hostName/;http://localhost:5080/"
    if (Test-Path $ci) {
        # Substitui so a linha do listenUrl (sem reserializar o ci.json, que o servico edita).
        $text = Get-Content $ci -Raw -Encoding UTF8
        if ($text -match '"listenUrl"\s*:\s*"[^"]*"') {
            $text = [regex]::Replace($text, '("listenUrl"\s*:\s*")[^"]*(")', { param($m) $m.Groups[1].Value + $url + $m.Groups[2].Value })
            [IO.File]::WriteAllText($ci, $text, (New-Object Text.UTF8Encoding($false)))
        } else {
            Write-Host "AVISO: o ci.json nao tem listenUrl; acrescente: ""listenUrl"": ""$url"""
        }
    }
    Write-Host "Painel em http://$hostName/ (e http://localhost:5080/)"
}

# Ativa as funcionalidades do IIS de que o CI precisa (Windows cliente ou Windows Server).
function Install-Iis {
    Write-Host 'A ativar as funcionalidades do IIS (pode demorar alguns minutos)...'
    $client = @('IIS-WebServerRole', 'IIS-WebServer', 'IIS-ManagementScriptingTools', 'IIS-ApplicationInit')
    $server = @('Web-Server', 'Web-Mgmt-Console', 'Web-Scripting-Tools', 'Web-AppInit')
    if (Get-Command Install-WindowsFeature -ErrorAction SilentlyContinue) {
        Install-WindowsFeature $server | Out-Null
    } else {
        Enable-WindowsOptionalFeature -Online -FeatureName $client -All -NoRestart | Out-Null
    }
}

# Verificacao do IIS antes de arrancar. Nao abortamos: o utilizador pode estar a instalar o IIS em paralelo.
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
        Write-Host 'AVISO: o IIS nao parece estar pronto:' -ForegroundColor Yellow
        if (-not $w3svc)    { Write-Host '  - Falta o servico W3SVC (World Wide Web Publishing).' }
        if (-not $was)      { Write-Host '  - Falta o servico WAS (Windows Process Activation Service).' }
        if (-not $webAdmin) { Write-Host '  - Falta o modulo WebAdministration.' }
        Write-Host '  Instale o IIS (como administrador) com, por exemplo:'
        Write-Host '    Enable-WindowsOptionalFeature -Online -FeatureName IIS-WebServerRole,IIS-WebServer,IIS-ManagementScriptingTools,IIS-ApplicationInit -All'
        Write-Host '    (ou no Windows Server) Install-WindowsFeature Web-Server,Web-Mgmt-Console,Web-Scripting-Tools,Web-AppInit'
        Write-Host '  O Ponte CI/CD arrancara em modo degradado (iisAvailable=false) ate o IIS estar disponivel.'
    } elseif (-not $appInitOn) {
        Write-Host ''
        Write-Host 'AVISO: a funcionalidade Application Initialization (Web-AppInit) nao esta instalada.' -ForegroundColor Yellow
        Write-Host '  Os deploys usarao o fallback de corte breve (brief cutover) em vez do warm-up sem interrupcao.'
        Write-Host '  Para a ativar (como administrador):'
        Write-Host '    Enable-WindowsOptionalFeature -Online -FeatureName IIS-ApplicationInit -All'
        Write-Host '    (ou no Windows Server) Install-WindowsFeature Web-AppInit'
    } else {
        Write-Host 'IIS pronto: W3SVC, WAS, WebAdministration e Application Initialization presentes.'
    }
}

# ---------------------------------------------------------------- (re)instalacao

# Migracao: o servico chamou-se 'LegadosCI' e 'PonteCI' antes de passar a 'PonteCICD'. Remove os antigos para nao ficarem duplicados.
foreach ($legacy in 'LegadosCI', 'PonteCI') {
    if (Get-Service $legacy -ErrorAction SilentlyContinue) {
        Stop-Service $legacy -ErrorAction SilentlyContinue
        & sc.exe delete $legacy | Out-Null
        Write-Host "Servico antigo $legacy removido (substituido por $name)."
    }
    Get-Process $legacy -ErrorAction SilentlyContinue | Stop-Process -Force
}

if (Get-Service $name -ErrorAction SilentlyContinue) {
    Stop-Service $name -ErrorAction SilentlyContinue
    & sc.exe delete $name | Out-Null
    Start-Sleep -Seconds 2
    Write-Host "Servico $name removido."
}
if ($Uninstall) { Remove-HostsEntry; return }

if (-not (Test-Path $exe)) { throw "Nao existe $exe. Publique antes: dotnet publish src\PonteCICD -c Release -o service" }
Get-Process PonteCICD -ErrorAction SilentlyContinue | Stop-Process -Force   # instancia em modo consola, se existir

if ($AdminUser) {
    if (-not $AdminPasswordFile -or -not (Test-Path $AdminPasswordFile)) { throw 'Com -AdminUser e preciso indicar -AdminPasswordFile.' }
    & $exe --set-admin $AdminUser --password-file $AdminPasswordFile
    if ($LASTEXITCODE -ne 0) { throw "Nao foi possivel criar o administrador do painel (codigo $LASTEXITCODE)." }
    Write-Host "Administrador do painel '$AdminUser' guardado."
}
if ($HostName -notmatch '^[a-zA-Z0-9]([a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?$') { throw "Nome do painel invalido: '$HostName' (use letras, numeros e hifens)." }
Set-HostsEntry $HostName
Set-ListenUrl $HostName
if ($InstallIis) { Install-Iis }

if ($LocalSystem) {
    Test-IisReadiness
    New-Service -Name $name -BinaryPathName "`"$exe`"" -DisplayName 'Ponte CI/CD' `
        -Description "Deploy continuo de projetos legados por branch (painel em http://$HostName/)" `
        -StartupType Automatic | Out-Null   # sem -Credential => LocalSystem (ja e administrador local)
    & sc.exe failure $name reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null
    Start-Service $name
    Write-Host ''
    Write-Host "Servico $name instalado como LocalSystem e arrancado. Painel: http://$HostName/ (ou http://localhost:5080/)"
    Write-Host 'NOTA: o LocalSystem nao leva as suas credenciais de git. Se o repo for privado (Azure DevOps/GitHub),'
    Write-Host '      abra o painel, carregue em "Projeto e acesso" e indique o URL do repositorio e o token (PAT).'
    return
}

if ($Account) {
    if (-not $PasswordFile -or -not (Test-Path $PasswordFile)) { throw 'Com -Account e preciso indicar -PasswordFile.' }
    $secure = ConvertTo-SecureString (Get-Content $PasswordFile -Raw).TrimEnd("`r", "`n") -AsPlainText -Force
    $cred = New-Object System.Management.Automation.PSCredential ($Account, $secure)
} else {
    $cred = Get-Credential -UserName ".\$env:USERNAME" -Message 'Conta com que correra o Ponte CI/CD (a sua: precisa de acesso git ao Azure DevOps)'
}
Grant-ServiceLogonRight $cred.UserName
Add-ToLocalAdministrators $cred.UserName
Test-IisReadiness

New-Service -Name $name -BinaryPathName "`"$exe`"" -DisplayName 'Ponte CI/CD' `
    -Description "Deploy continuo de projetos legados por branch (painel em http://$HostName/)" `
    -StartupType Automatic -Credential $cred | Out-Null
& sc.exe failure $name reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null
Start-Service $name
Write-Host "Servico $name instalado e arrancado. Painel: http://$HostName/ (ou http://localhost:5080/)"
