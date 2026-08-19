#ifndef StagingDir
  #error StagingDir deve ser informado pelo script de release.
#endif

#ifndef OutputDir
  #error OutputDir deve ser informado pelo script de release.
#endif

#ifndef AppVersion
  #define AppVersion "0.0.0-local"
#endif

#define AppName "FH6 Open Assist"
#define AppExecutable "FH6OpenAssist.exe"
#define ViGEmBusUrl "https://github.com/nefarius/ViGEmBus/releases/latest"

[Setup]
AppId={{8AA33E37-D16E-4F31-B5A9-087148C4F89E}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=FH6 Open Assist
AppPublisherURL=https://github.com/FH6-Open-Assist/FH6-Open-Assist
AppSupportURL=https://github.com/FH6-Open-Assist/FH6-Open-Assist/issues
AppUpdatesURL=https://github.com/FH6-Open-Assist/FH6-Open-Assist/releases/latest
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
PrivilegesRequired=admin
Uninstallable=yes
OutputDir={#OutputDir}
OutputBaseFilename=FH6-Open-Assist-Setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
LicenseFile=..\LICENSE
UninstallDisplayIcon={app}\{#AppExecutable}
VersionInfoCompany=FH6 Open Assist
VersionInfoDescription=Instalador do FH6 Open Assist
VersionInfoProductName={#AppName}

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "Criar um atalho na Área de Trabalho"; GroupDescription: "Atalhos adicionais:"; Flags: unchecked
Name: "vigembuslink"; Description: "Abrir a página oficial do ViGEmBus após a instalação"; GroupDescription: "Dependência opcional para o modo em segundo plano:"; Flags: unchecked

[Files]
Source: "{#StagingDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "portable.marker"

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExecutable}"; WorkingDir: "{app}"
Name: "{group}\Desinstalar {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExecutable}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{#ViGEmBusUrl}"; Flags: shellexec nowait skipifsilent; Tasks: vigembuslink

[Code]
var
  ViGEmStatusPage: TOutputMsgWizardPage;

function ViGEmBusDetected: Boolean;
begin
  Result :=
    RegKeyExists(HKLM64, 'SYSTEM\CurrentControlSet\Services\ViGEmBus') or
    RegKeyExists(HKLM32, 'SYSTEM\CurrentControlSet\Services\ViGEmBus');
end;

procedure InitializeWizard;
var
  StatusMessage: String;
begin
  if ViGEmBusDetected then
    StatusMessage :=
      'Foi encontrada uma entrada de registro do serviço ViGEmBus. ' +
      'Essa verificação é apenas indicativa e não garante que o driver esteja operacional.'
  else
    StatusMessage :=
      'Não foi encontrada uma entrada de registro do serviço ViGEmBus. ' +
      'A instalação do FH6 Open Assist pode continuar normalmente. ' +
      'O ViGEmBus é necessário somente para o modo em segundo plano e pode ser obtido pelo link oficial opcional.';

  ViGEmStatusPage := CreateOutputMsgPage(
    wpSelectDir,
    'Verificação indicativa do ViGEmBus',
    'Dependência opcional do modo em segundo plano',
    StatusMessage);
end;
