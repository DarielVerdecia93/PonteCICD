# Ponte CI/CD

CI/CD local para implantar projetos .NET legados (p. ex. `ENMC.BalcaoUnico.UI.WebApplication`) por branch, sem tocar no repositório
nem na base de dados (a BD é administrada por outra parte: aqui só se configura a respetiva connection string).

Painel: **http://localhost:5080**

## Como funciona

- Cada **ambiente** = uma branch + uma porta (8090, 8091, ...) + uma configuração de compilação (`Desenvolvimento`, `Qualidade`...).
  A branch escolhe-se num seletor visual (pesquisa, agrupada por `feature/`, `bugfix/`...), do **remoto** ou do **repo local**.
- Deteção de alterações:
  - remoto: `git ls-remote` a cada `pollIntervalSec` (20 s); só descarrega se alguma branch se moveu;
  - repo local (`localRepoPath`): vigia-se o `.git`, pelo que um commit local (sem push) é detetado em poucos segundos.
- Com *auto-deploy*, quando a branch muda espera-se `quietSec` (15 s) sem mais commits e implanta-se **o último**.
  Se um commit falhar, não se tenta de novo até ao commit seguinte ou até carregar em *Implantar*.
- O painel atualiza-se em direto (Server-Sent Events): passo atual do deploy, contagem decrescente do auto-deploy,
  commits pendentes e avisos no fim de cada execução.
- A compilação dinâmica do ASP.NET faz-se em `envs\<ambiente>\aspnet-temp` em vez de `%TEMP%`, onde
  o **Smart App Control** bloqueia as DLL geradas (erro 0x800711C7).
- Antivírus (Kaspersky): excluir esta pasta, `service\PonteCI.exe` e `iisexpress.exe`, ou bloqueará os processos do CI.
- Cada deploy:
  1. exporta o commit com `git archive` (código limpo, o repo de trabalho não é tocado);
  2. compila e publica com MSBuild (`/t:WebPublish`), sem `DBScripts` e com os ficheiros extra de `includeInPublish`;
  3. aplica o `Web.local.config` (connection string da BD administrada, URLs...);
  4. pára o site, **guarda a versão atual num zip**, substitui os ficheiros e arranca o site;
  5. verifica que responde; se não, **restaura automaticamente** o zip anterior.
- **Rollback**: restaura qualquer zip. Antes guarda também a versão atual, pelo que um rollback pode ser desfeito.
- Guardam-se os últimos `keepBackups` zips por ambiente. `UploadFiles` (dados de utilizadores) não é tocado nem entra nos zips.
- Os jobs executam-se um de cada vez; cada execução fica em `runs\` (json + log), visível no painel.

## Criar um novo ambiente

No painel, **+ Novo ambiente**:

1. Escolha a branch (separador **Remoto** ou **Meu repo local**; pesquisa por branch, autor ou mensagem).
2. **Nome do ambiente** (preenchido a partir da branch), **Configuração de compilação** e, opcionalmente,
   **Pasta base do ambiente (IIS)** e **Porta** (vazio = automática).
3. **Auto-deploy** implanta automaticamente a cada novo commit da branch.
4. **Criar e implantar** cria o ambiente e lança o primeiro deploy.

## Estrutura

```
ci.json                    configuração global + ambientes (o painel edita-o)
Web.local.config           (opcional) transformação XDT comum a todos os ambientes
engine\                    motor PowerShell: deploy.ps1, rollback.ps1, site.ps1
envs\<ambiente>\site       site implantado (servido pelo IIS)
envs\<ambiente>\backups    zips <ambiente>_<data>_<commit>_<motivo>.zip
envs\<ambiente>\Web.local.config   (opcional) transformação própria do ambiente
mirror.git                 mirror do remoto
runs\                      histórico de execuções
service\                   PonteCI publicado (serviço do Windows)
src\PonteCI              código do serviço (.NET 10)
```

## Base de dados administrada

Copie `engine\Web.local.config.example` como `Web.local.config` (comum) ou como
`envs\<ambiente>\Web.local.config` (por ambiente) e coloque a connection string. Aplica-se no deploy seguinte.

## Instalar com o instalador (recomendado)

Descarregue `PonteCI-Setup-<versão>.exe` da página de [Releases](https://github.com/DarielVerdecia93/Legados-CI/releases)
e execute-o como administrador. O instalador:

1. copia o serviço (autocontido: **não precisa de .NET instalado**), o motor `engine\` e o `install-service.ps1`
   para `C:\PonteCI` (pasta editável);
2. cria um `ci.json` base apenas na primeira instalação (as atualizações **não** tocam nos ambientes existentes);
3. opcionalmente ativa as funcionalidades do IIS que faltem;
4. pergunta a conta do serviço — `LocalSystem` (sem palavra-passe) ou a sua conta do Windows (usa as suas credenciais de git) —,
   adiciona-a aos Administradores, regista o serviço `PonteCI`, configura o reinício automático e arranca-o;
5. abre o painel em **http://localhost:5080**.

Para atualizar, execute o instalador da nova versão por cima. Para desinstalar, use «Aplicações» do Windows
(remove o serviço; `ci.json`, `envs\`, `runs\` e `mirror.git` ficam na pasta).

Depois configure no `ci.json` o `repoUrl`, `solutionFile`, `projectFile`, etc., e crie ambientes no painel.

## Publicar uma release (mantenedores)

A release é gerada pelo GitHub Actions ([release.yml](.github/workflows/release.yml)): corre os testes, publica o serviço,
compila o instalador com Inno Setup e anexa o `.exe` à release.

```powershell
git tag v1.0.0
git push origin v1.0.0
```

Para compilar o instalador localmente (requer [Inno Setup 6](https://jrsoftware.org/isinfo.php)):

```powershell
dotnet publish src\PonteCI -c Release -r win-x64 --self-contained -o artifacts\service
iscc /DAppVersion=1.0.0 installer\PonteCI.iss      # gera artifacts\PonteCI-Setup-1.0.0.exe
```

## Instalar como serviço (manual)

O serviço corre **elevado**, porque cria e gere sites e application pools do IIS. O script de instalação
adiciona a conta do serviço ao grupo local de Administradores, concede-lhe o direito «Iniciar sessão como serviço»,
verifica o IIS e configura o reinício automático em caso de falha.

### Pré-requisitos

- Windows com **IIS** instalado (serviços `W3SVC` e `WAS`, módulo `WebAdministration` e *Application Initialization*).
  Se faltar, o script avisa e o Ponte CI/CD arranca em modo degradado até o IIS estar disponível. Para instalar (como administrador):

  ```powershell
  Enable-WindowsOptionalFeature -Online -FeatureName IIS-WebServerRole,IIS-WebServer,IIS-ManagementScriptingTools,IIS-ApplicationInit -All
  ```

- **.NET SDK 10**, **git** e MSBuild/Visual Studio Build Tools (para compilar os projetos legados).

### Passos

1. Publicar o serviço (na raiz do repositório):

   ```powershell
   dotnet publish src\PonteCI -c Release -o service
   ```

2. Abrir o PowerShell **como administrador** e instalar:

   ```powershell
   .\install-service.ps1
   ```

   O Windows pede a palavra-passe da sua conta (o script não a guarda). O serviço corre com o seu utilizador,
   para reutilizar as suas credenciais de git (Azure DevOps).

   Se iniciar sessão com conta Microsoft (PIN/Hello) e não tiver palavra-passe local, use a conta do sistema:

   ```powershell
   .\install-service.ps1 -LocalSystem
   ```

   O `LocalSystem` **não** leva as suas credenciais de git. Para um repo privado do Azure DevOps, configure o git
   ao nível do sistema com um PAT (`git config --system credential.helper store`) ou use uma `repoUrl`
   com o token numa variável de ambiente.

3. Abrir o painel em **http://localhost:5080**.

### Gerir o serviço

```powershell
Get-Service PonteCI                 # estado
Restart-Service PonteCI             # reiniciar
.\install-service.ps1 -Uninstall      # desinstalar
```

Sem o instalar, também se pode executar numa consola: `.\service\PonteCI.exe`.

## Atualizar o serviço após alterar o seu código

```powershell
Stop-Service PonteCI
dotnet publish src\PonteCI -c Release -o service
Start-Service PonteCI
```

## Utilização a partir da consola (sem painel)

```powershell
.\engine\deploy.ps1   -Environment develop               # ponta da branch
.\engine\deploy.ps1   -Environment develop -Ref 1a2b3c4  # commit concreto
.\engine\rollback.ps1 -Environment develop               # último backup
.\engine\site.ps1     -Environment develop -Action restart
```
