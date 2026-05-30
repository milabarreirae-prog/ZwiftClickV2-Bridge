; Instalador de Violeta — asistente en violeta, gratis y para siempre.
; Compilar con Inno Setup 6:  ISCC.exe installer\Violeta.iss
; (o usa scripts\build-installer.ps1, que lo hace todo).

#define MyAppName "Violeta"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Hecho con cariño por una mujer trans"
#define MyAppExeName "Violeta.exe"

[Setup]
; AppId único de Violeta (no reutilizar para otros productos).
AppId={{B7E1B6B2-7E2A-4D9C-9F1A-7C0F5E9A1A10}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
VersionInfoDescription=Violeta — puente para tu mando de ciclismo indoor
VersionInfoCopyright=Software libre (MIT). Gratis y para siempre.
DefaultDirName={autopf}\Violeta
DefaultGroupName=Violeta
DisableProgramGroupPage=yes
DisableDirPage=auto
LicenseFile=..\LICENSE
OutputDir=..\dist
OutputBaseFilename=Violeta-Setup-{#MyAppVersion}
SetupIconFile=..\app\Assets\violeta.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
WizardImageFile=assets\wizard.bmp
WizardSmallImageFile=assets\wizard-small.bmp
WizardImageStretch=no
; Instalación por-usuario: sin pedir permisos de administrador.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; Todo el contenido autocontenido publicado en dist\Violeta\.
Source: "..\dist\Violeta\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Desinstalar {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent
