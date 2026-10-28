; Instalador de Ponte CI/CD (Inno Setup 6).
; Compilar desde la raiz del repo, tras publicar el servicio en artifacts\service:
;   iscc /DAppVersion=1.2.3 installer\PonteCICD.iss
; Genera artifacts\PonteCICD-Setup-<version>.exe

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={{40C18D9B-198C-4E3B-9573-4389408F3D04}
AppName=Ponte CI/CD
AppVersion={#AppVersion}
AppPublisher=Magia Technologies Labs
AppCopyright=Copyright (C) Magia Technologies Labs
VersionInfoCompany=Magia Technologies Labs
VersionInfoProductName=Ponte CI/CD
VersionInfoDescription=Instalador do Ponte CI/CD
DefaultDirName=C:\PonteCICD
DisableDirPage=no
DefaultGroupName=Ponte CI-CD
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=artifacts
OutputBaseFilename=PonteCICD-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=installer\pontecicd.ico
UninstallDisplayIcon={app}\pontecicd.ico
UninstallDisplayName=Ponte CI/CD
SourceDir=..

[Languages]
Name: "portuguese"; MessagesFile: "compiler:Languages\Portuguese.isl"

[Tasks]
Name: "installiis"; Description: "Instalar/ativar as funcionalidades do IIS necessárias (se faltarem)"; Flags: checkedonce
Name: "openpanel"; Description: "Abrir o painel no fim da instalação"; Flags: checkedonce

[Files]
Source: "artifacts\service\*"; DestDir: "{app}\service"; Flags: recursesubdirs ignoreversion
Source: "engine\*"; DestDir: "{app}\engine"; Excludes: "Web.local.config"; Flags: recursesubdirs ignoreversion
Source: "install-service.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "installer\pontecicd.ico"; DestDir: "{app}"; Flags: ignoreversion
Source: "README.md"; DestDir: "{app}"; Flags: ignoreversion
; Configuração base: só na primeira instalação (as atualizações não pisam os ambientes do utilizador).
Source: "installer\ci.template.json"; DestDir: "{app}"; DestName: "ci.json"; Flags: onlyifdoesntexist uninsneveruninstall

[Icons]
Name: "{group}\Painel Ponte CI/CD"; Filename: "{code:PanelUrl}"; IconFilename: "{app}\pontecicd.ico"
Name: "{group}\Desinstalar Ponte CI/CD"; Filename: "{uninstallexe}"

[UninstallRun]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\install-service.ps1"" -Uninstall"; Flags: runhidden; RunOnceId: "RemoveService"

[Code]
var
  AccountPage: TInputOptionWizardPage;
  CredPage: TInputQueryWizardPage;
  HostPage: TInputQueryWizardPage;
  AdminPage: TInputQueryWizardPage;

// Nome do painel: http://<nome>/ (regista-se no ficheiro hosts; localhost:5080 continua a funcionar).
function PanelHost: String;
begin
  Result := Lowercase(Trim(HostPage.Values[0]));
end;

function PanelUrl(Param: String): String;
begin
  Result := 'http://' + PanelHost + '/';
end;

function IsValidHostName(const S: String): Boolean;
var
  I: Integer;
  C: Char;
begin
  Result := (Length(S) >= 1) and (Length(S) <= 63) and (S[1] <> '-') and (S[Length(S)] <> '-');
  if not Result then Exit;
  for I := 1 to Length(S) do
  begin
    C := S[I];
    if not (((C >= 'a') and (C <= 'z')) or ((C >= '0') and (C <= '9')) or (C = '-')) then
    begin
      Result := False;
      Exit;
    end;
  end;
end;

procedure InitializeWizard;
begin
  HostPage := CreateInputQueryPage(wpSelectTasks,
    'Endereço do painel', 'Com que nome quer aceder ao painel?',
    'O painel ficará em http://<nome>/ neste computador (usa o porto 80, partilhado com o IIS) e também em http://localhost:5080/.');
  HostPage.Add('Nome do painel:', False);
  HostPage.Values[0] := 'admin-pontecicd';

  // Administrador do painel: definido aqui, sem palavra-passe predefinida em lado nenhum.
  AdminPage := CreateInputQueryPage(HostPage.ID,
    'Administrador do painel', 'Crie o utilizador com que vai entrar no painel',
    'Guarda-se apenas um hash da palavra-passe. Pode alterá-la depois no menu do utilizador do painel.');
  AdminPage.Add('Utilizador (3 a 32 caracteres: letras, números, . - _):', False);
  AdminPage.Add('Palavra-passe (mínimo 8 caracteres):', True);
  AdminPage.Add('Repetir a palavra-passe:', True);
  AdminPage.Values[0] := 'admin';

  AccountPage := CreateInputOptionPage(AdminPage.ID,
    'Conta do serviço', 'Com que conta deve correr o serviço Ponte CI/CD?',
    'O serviço gere sites e application pools do IIS, por isso a conta é adicionada ao grupo de Administradores.',
    True, False);
  AccountPage.Add('LocalSystem (sem palavra-passe; não leva as suas credenciais de git)');
  AccountPage.Add('A minha conta do Windows (usa as suas credenciais de git/Azure DevOps)');
  AccountPage.SelectedValueIndex := 0;

  CredPage := CreateInputQueryPage(AccountPage.ID,
    'Credenciais da conta', 'Indique a conta e a palavra-passe do Windows',
    'A palavra-passe só é usada para registar o serviço e não é guardada.');
  CredPage.Add('Conta (DOMÍNIO\utilizador ou .\utilizador):', False);
  CredPage.Add('Palavra-passe:', True);
  CredPage.Values[0] := '.\' + GetUserNameString;
end;

// Atualização: se já existe um administrador na pasta de instalação, não se pergunta nem se altera.
function AdminAlreadyExists: Boolean;
begin
  Result := FileExists(AddBackslash(WizardDirValue) + 'secrets\users.json');
end;

function IsValidUserName(const S: String): Boolean;
var
  I: Integer;
  C: Char;
begin
  Result := (Length(S) >= 3) and (Length(S) <= 32);
  if not Result then Exit;
  for I := 1 to Length(S) do
  begin
    C := S[I];
    if not (((C >= 'a') and (C <= 'z')) or ((C >= 'A') and (C <= 'Z')) or ((C >= '0') and (C <= '9')) or (C = '.') or (C = '-') or (C = '_')) then
    begin
      Result := False;
      Exit;
    end;
  end;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := ((PageID = CredPage.ID) and (AccountPage.SelectedValueIndex <> 1))
         or ((PageID = AdminPage.ID) and AdminAlreadyExists);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = HostPage.ID) and not IsValidHostName(PanelHost) then
  begin
    MsgBox('O nome do painel só pode ter letras minúsculas, números e hífens (ex.: admin-pontecicd).', mbError, MB_OK);
    Result := False;
  end;
  if CurPageID = AdminPage.ID then
  begin
    if not IsValidUserName(Trim(AdminPage.Values[0])) then
    begin
      MsgBox('O utilizador deve ter 3 a 32 caracteres: letras, números, ponto, hífen ou sublinhado.', mbError, MB_OK);
      Result := False;
    end
    else if Length(AdminPage.Values[1]) < 8 then
    begin
      MsgBox('A palavra-passe deve ter pelo menos 8 caracteres.', mbError, MB_OK);
      Result := False;
    end
    else if AdminPage.Values[1] <> AdminPage.Values[2] then
    begin
      MsgBox('As palavras-passe não coincidem.', mbError, MB_OK);
      Result := False;
    end;
  end;
  if (CurPageID = CredPage.ID) and ((Trim(CredPage.Values[0]) = '') or (CredPage.Values[1] = '')) then
  begin
    MsgBox('Indique a conta e a palavra-passe.', mbError, MB_OK);
    Result := False;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  // Atualização: pára o serviço para poder substituir os ficheiros.
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop PonteCICD', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop LegadosCI', '', SW_HIDE, ewWaitUntilTerminated, Code); // nomes antigos
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop PonteCI', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Sleep(3000);
  Result := '';
end;

procedure RunInstallScript;
var
  Args, PwdFile, AdminPwdFile: String;
  PwdLines: TArrayOfString;
  Code: Integer;
begin
  Args :='-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\install-service.ps1') + '" -HostName ' + PanelHost;
  if WizardIsTaskSelected('installiis') then Args := Args + ' -InstallIis';
  // Administrador do painel (só na primeira instalação): a palavra-passe segue num ficheiro temporário (UTF-8), apagado no fim.
  AdminPwdFile := '';
  if not AdminAlreadyExists then
  begin
    AdminPwdFile := ExpandConstant('{tmp}\lci-admin.txt');
    SetArrayLength(PwdLines, 1);
    PwdLines[0] := AdminPage.Values[1];
    SaveStringsToUTF8File(AdminPwdFile, PwdLines, False);
    Args := Args + ' -AdminUser "' + Trim(AdminPage.Values[0]) + '" -AdminPasswordFile "' + AdminPwdFile + '"';
  end;
  PwdFile := '';
  if AccountPage.SelectedValueIndex = 1 then
  begin
    PwdFile := ExpandConstant('{tmp}\lci-pwd.txt');
    SaveStringToFile(PwdFile, CredPage.Values[1], False);
    Args := Args + ' -Account "' + Trim(CredPage.Values[0]) + '" -PasswordFile "' + PwdFile + '"';
  end
  else
    Args := Args + ' -LocalSystem';

  WizardForm.StatusLabel.Caption := 'A registar e arrancar o serviço Ponte CI/CD (pode demorar se o IIS for instalado)...';
  if not Exec('powershell.exe', Args, ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then
    MsgBox('O registo do serviço falhou (código ' + IntToStr(Code) + '). Execute como administrador:' + #13#10 +
      '  ' + ExpandConstant('{app}\install-service.ps1'), mbError, MB_OK);
  if PwdFile <> '' then DeleteFile(PwdFile);
  if AdminPwdFile <> '' then DeleteFile(AdminPwdFile);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
begin
  if CurStep = ssPostInstall then RunInstallScript;
  if (CurStep = ssDone) and WizardIsTaskSelected('openpanel') then
    ShellExec('open', PanelUrl(''), '', '', SW_SHOWNORMAL, ewNoWait, Code);
end;
