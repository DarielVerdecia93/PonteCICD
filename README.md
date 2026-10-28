# Ponte CI/CD

CI/CD local para implantar projetos .NET legados (p. ex. `ENMC.BalcaoUnico.UI.WebApplication`) por branch, sem tocar no repositório
nem na base de dados (a BD é administrada por outra parte: aqui só se configura a respetiva connection string).

Painel: **http://admin-pontecicd/** (ou **http://localhost:5080/**)

© Magia Technologies Labs

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
- Antivírus (Kaspersky): excluir esta pasta, `service\PonteCICD.exe` e `iisexpress.exe`, ou bloqueará os processos do CI.
- Cada deploy:
  1. exporta o commit com `git archive` (código limpo, o repo de trabalho não é tocado);
  2. compila e publica com MSBuild (`/t:WebPublish`), sem `DBScripts` e com os ficheiros extra de `includeInPublish`;
  3. aplica o `Web.local.config` (connection string da BD administrada, URLs...);
  4. pára o site, **guarda a versão atual num zip**, substitui os ficheiros e arranca o site;
  5. verifica que responde; se não, **restaura automaticamente** o zip anterior.
- **Rollback**: restaura qualquer zip. Antes guarda também a versão atual, pelo que um rollback pode ser desfeito.
- Guardam-se os últimos `keepBackups` zips por ambiente. `UploadFiles` (dados de utilizadores) não é tocado nem entra nos zips.
- Os jobs executam-se um de cada vez; cada execução fica em `runs\` (json + log), visível no painel.

## Endereço do painel

O painel responde em **`http://admin-pontecicd/`** (nome editável) e, em alternativa, em `http://localhost:5080/`.

- O instalador pergunta o nome do painel; `install-service.ps1 -HostName <nome>` faz o mesmo. Ambos acrescentam
  `127.0.0.1 <nome>` ao ficheiro `hosts` (linha marcada com `# PonteCICD`, retirada ao desinstalar) e guardam o `listenUrl` no `ci.json`
  (`http://<nome>/;http://localhost:5080/`).
- O nome usa o **porto 80**, que o IIS também usa: o painel corre sobre o http.sys do Windows, que reparte o porto por nome de host,
  pelo que o *Default Web Site* e os restantes sites do IIS continuam a funcionar.
- Só funciona **neste computador** (o nome aponta para 127.0.0.1). Para outros computadores, peça ao responsável da rede um
  registo DNS para o nome apontando para este PC e use HTTPS.
- `listenUrl` aceita vários endereços separados por `;`. Com `localhost`/IP usa-se o Kestrel; com um nome de host, o http.sys.
- Antivírus (Kaspersky e outros) podem bloquear a alteração do ficheiro `hosts`; nesse caso use `http://localhost:5080/`.

## Acesso ao painel (login)

O painel exige início de sessão e **não existe nenhuma conta nem palavra-passe predefinida**: o administrador é criado por si.

- **Pelo instalador:** a página «Administrador do painel» pede o utilizador e a palavra-passe (com confirmação). Numa atualização,
  se já existir um administrador, não se pergunta nada e não se altera.
- **Sem instalador** (instalação manual, `dotnet run`, ou se `secrets\users.json` for apagado): ao abrir o painel aparece a
  **Configuração inicial**, onde cria o administrador. Só é aceite a partir deste computador e só enquanto não houver administrador.
  Em alternativa, pela linha de comandos (uma consola de administrador): `.\service\PonteCICD.exe --set-admin <utilizador>`
  (pede a palavra-passe sem a mostrar) ou `install-service.ps1 -AdminUser <utilizador> -AdminPasswordFile <ficheiro>`.
- **Alterar a palavra-passe:** menu do utilizador (canto superior direito) → **Alterar palavra-passe**. O menu também tem
  *Projeto e acesso ao repositório*, *Frequência de atualização* e *Terminar sessão*.
- **Esqueceu-se da palavra-passe:** pare o serviço (`Stop-Service PonteCICD`), execute
  `.\service\PonteCICD.exe --set-admin <utilizador>` e arranque-o de novo. Isto substitui a conta de administrador.
- A sessão dura 12 h sem atividade. Ao mudar a palavra-passe, as outras sessões abertas terminam.
  Após 5 tentativas falhadas, o utilizador fica bloqueado 1 minuto.
- As palavras-passe nunca são guardadas: só um hash PBKDF2 em `secrets\users.json`. Utilizador: 3 a 32 caracteres
  (letras, números, `.`, `-`, `_`); palavra-passe: mínimo 8 caracteres.
- Por omissão o painel só aceita ligações locais. Se o expuser na rede, use HTTPS (proxy inverso).
- Instalações de versões anteriores podem ter ainda a conta predefinida antiga: o painel avisa até a palavra-passe ser alterada.

## Primeira configuração: projeto e autenticação

Num PC novo, abra o painel, inicie sessão e abra o menu do utilizador → **Projeto e acesso ao repositório** (ou use o aviso «Configurar projeto e acesso»):

1. **URL do repositório** (HTTPS), de **Azure DevOps** (`https://dev.azure.com/org/projeto/_git/repo`) ou de **GitHub** (`https://github.com/org/repo.git`).
2. **Token de acesso (PAT)**:
   - Azure DevOps: *User settings → Personal access tokens*, permissão **Code (Read)**;
   - GitHub: *Settings → Developer settings → Personal access tokens*, com leitura do repositório.
3. **Solução (.sln)** e **projeto web (.csproj)** a publicar (relativos à raiz do repositório); opcionalmente a pasta de pacotes NuGet e o repositório local.
4. **Testar ligação** confirma que o token e o URL funcionam; depois **Guardar**.

O token fica **cifrado nesta máquina** (DPAPI, em `secrets\git-credential.bin`) e nunca é escrito no `ci.json` nem no URL. É enviado ao git
como cabeçalho HTTP limitado ao servidor do repositório, tanto pelo serviço como pelo motor de deploy. Funciona com qualquer conta do serviço
(incluindo `LocalSystem`), sem precisar de configurar o git à mão. Requer git 2.31 ou superior.

## Criar um novo ambiente

No painel, **+ Novo ambiente**:

1. Escolha a branch (separador **Remoto** ou **Meu repo local**; pesquisa por branch, autor ou mensagem).
2. **Nome do ambiente** (preenchido a partir da branch), **Configuração de compilação** e, opcionalmente,
   **Pasta base do ambiente (IIS)** e **Porta** (vazio = automática).
3. **Nome DNS** (opcional): ver abaixo.
4. **Auto-deploy** implanta automaticamente a cada novo commit da branch.
5. **Criar e implantar** cria o ambiente e lança o primeiro deploy.

### Nome DNS por ambiente (sem porto no endereço)

Cada ambiente corre no seu porto (ex.: 4058). Com **Nome DNS** (ex.: `sitio.teste.br`) o mesmo site do IIS recebe **dois enlaces**:
`*:4058` (acesso direto e verificação de saúde, como sempre) e `*:80:sitio.teste.br`. O endereço passa a ser
**`http://sitio.teste.br/`**, sem mostrar o porto; o painel mostra ambos os links.

- O nome tem de existir no **DNS** (registo A para o IP deste servidor) ou, em testes, no ficheiro `hosts` de quem aceder.
- Usa o porto 80 do IIS, partilhado por nome de host: o *Default Web Site*, o painel e os outros ambientes não são afetados.
- Cada nome só pode ser de um ambiente. Se já estiver associado a outro site do IIS, o deploy avisa e o ambiente
  continua a responder no seu porto.
- Alterar ou retirar o nome reimplanta o ambiente, para atualizar os enlaces. Só HTTP (para HTTPS é preciso certificado e enlace próprio).

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
service\                   PonteCICD publicado (serviço do Windows)
src\PonteCICD              código do serviço (.NET 10)
```

## Base de dados administrada

Copie `engine\Web.local.config.example` como `Web.local.config` (comum) ou como
`envs\<ambiente>\Web.local.config` (por ambiente) e coloque a connection string. Aplica-se no deploy seguinte.

## Instalar com o instalador (recomendado)

Descarregue `PonteCICD-Setup-<versão>.exe` da página de [Releases](https://github.com/DarielVerdecia93/PonteCICD/releases)
e execute-o como administrador. O instalador:

1. copia o serviço (autocontido: **não precisa de .NET instalado**), o motor `engine\` e o `install-service.ps1`
   para `C:\PonteCICD` (pasta editável);
2. cria um `ci.json` base apenas na primeira instalação (as atualizações **não** tocam nos ambientes existentes);
3. pergunta o **nome do painel** e o **utilizador e palavra-passe do administrador** do painel (sem contas predefinidas; numa atualização não se repete);
   opcionalmente ativa as funcionalidades do IIS que faltem;
4. pergunta a conta do serviço — `LocalSystem` (sem palavra-passe) ou a sua conta do Windows (usa as suas credenciais de git) —,
   adiciona-a aos Administradores, regista o serviço `PonteCICD`, configura o reinício automático e arranca-o;
5. abre o painel em **http://admin-pontecicd/** (ou no nome que tiver escolhido).

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
dotnet publish src\PonteCICD -c Release -r win-x64 --self-contained -o artifacts\service
iscc /DAppVersion=1.0.0 installer\PonteCICD.iss      # gera artifacts\PonteCICD-Setup-1.0.0.exe
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
   dotnet publish src\PonteCICD -c Release -o service
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

3. Abrir o painel em **http://admin-pontecicd/** (ou **http://localhost:5080/**).

### Gerir o serviço

```powershell
Get-Service PonteCICD                 # estado
Restart-Service PonteCICD             # reiniciar
.\install-service.ps1 -Uninstall      # desinstalar
```

Sem o instalar, também se pode executar numa consola: `.\service\PonteCICD.exe`.

### Executar em desenvolvimento (`dotnet run`) com o serviço instalado

O serviço instalado já ocupa os endereços do painel (`http://admin-pontecicd/` e `http://localhost:5080/`), por isso uma segunda
cópia não pode usá-los. Duas opções:

- parar o serviço: `Stop-Service PonteCICD` (e `Start-Service PonteCICD` no fim); ou
- usar outro endereço, sem mexer no `ci.json`:

  ```powershell
  dotnet run --project src\PonteCICD -- --ListenUrl=http://localhost:5081
  ```

Numa consola **sem administrador** o nome do painel (`admin-pontecicd`) não pode ser registado: a aplicação avisa e escuta só em
`localhost`. Para o nome, use uma consola de administrador. Também o IIS só pode ser gerido com administrador.

## Atualizar o serviço após alterar o seu código

```powershell
Stop-Service PonteCICD
dotnet publish src\PonteCICD -c Release -o service
Start-Service PonteCICD
```

## Utilização a partir da consola (sem painel)

```powershell
.\engine\deploy.ps1   -Environment develop               # ponta da branch
.\engine\deploy.ps1   -Environment develop -Ref 1a2b3c4  # commit concreto
.\engine\rollback.ps1 -Environment develop               # último backup
.\engine\site.ps1     -Environment develop -Action restart
```
