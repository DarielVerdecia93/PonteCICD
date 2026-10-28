; Instalador de Ponte CI/CD (Inno Setup 6).
; Compilar desde la raiz del repo, tras publicar el servicio en artifacts\service:
;   iscc /DAppVersion=1.2.3 installer\PonteCI.iss
; Genera artifacts\PonteCI-Setup-<version>.exe

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={{40C18D9B-198C-4E3B-9573-4389408F3D04}
AppName=Ponte CI/CD
AppVersion={#AppVersion}
AppPublisher=Ambisig
DefaultDirName=C:\PonteCI
DisableDirPage=no
DefaultGroupName=Ponte CI-CD
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=artifacts
OutputBaseFilename=PonteCI-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=installer\ponteci.ico
UninstallDisplayIcon={app}\service\PonteCI.exe
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
Source: "README.md"; DestDir: "{app}"; Flags: ignoreversion
; Configuração base: só na primeira instalação (as atualizações não pisam os ambientes do utilizador).
Source: "installer\ci.template.json"; DestDir: "{app}"; DestName: "ci.json"; Flags: onlyifdoesntexist uninsneveruninstall

[Icons]
Name: "{group}\Painel Ponte CI/CD"; Filename: "http://localhost:5080"
Name: "{group}\Desinstalar Ponte CI/CD"; Filename: "{uninstallexe}"

[UninstallRun]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\install-service.ps1"" -Uninstall"; Flags: runhidden; RunOnceId: "RemoveService"

[Code]
var
  AccountPage: TInputOptionWizardPage;
  CredPage: TInputQueryWizardPage;

procedure InitializeWizard;
begin
  AccountPage := CreateInputOptionPage(wpSelectTasks,
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

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = CredPage.ID) and (AccountPage.SelectedValueIndex <> 1);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
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
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop PonteCI', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop LegadosCI', '', SW_HIDE, ewWaitUntilTerminated, Code); // nome antigo
  Sleep(3000);
  Result := '';
end;

procedure RunInstallScript;
var
  Args, PwdFile: String;
  Code: Integer;
begin
  Args := '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\install-service.ps1') + '"';
  if WizardIsTaskSelected('installiis') then Args := Args + ' -InstallIis';
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
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
begin
  if CurStep = ssPostInstall then RunInstallScript;
  if (CurStep = ssDone) and WizardIsTaskSelected('openpanel') then
    ShellExec('open', 'http://localhost:5080', '', '', SW_SHOWNORMAL, ewNoWait, Code);
end;
