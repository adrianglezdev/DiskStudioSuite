; Instalador de Disk Studio Suite (Inno Setup 6).
; Se instala por usuario (sin administrador) en %LOCALAPPDATA%\Programs\Disk Studio Suite.
; Compilar:  ISCC.exe /DAppVersion=0.3.0 /DSourceExe=..\dist\DiskStudioSuite.exe installer\DiskStudioSuite.iss
;
; Actualización silenciosa desde la app:  DiskStudioSuite-Setup-X.Y.Z.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /RELANZAR

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceExe
  #define SourceExe "..\dist\DiskStudioSuite.exe"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

[Setup]
; El AppId no debe cambiar nunca: así Windows reconoce las versiones nuevas como actualización de la misma app
AppId={{912AA28E-5E5C-4996-8772-5C2E84DDA6C0}
AppName=Disk Studio Suite
AppVersion={#AppVersion}
AppVerName=Disk Studio Suite {#AppVersion}
AppPublisher=AdrianGlezDev
AppPublisherURL=https://github.com/adrianglezdev/DiskStudioSuite
AppSupportURL=https://github.com/adrianglezdev/DiskStudioSuite/issues
AppUpdatesURL=https://github.com/adrianglezdev/DiskStudioSuite/releases
AppCopyright=© 2026 AdrianGlezDev
VersionInfoVersion={#AppVersion}
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=
DefaultDirName={autopf}\Disk Studio Suite
DefaultGroupName=Disk Studio Suite
DisableProgramGroupPage=yes
DisableDirPage=auto
DisableReadyPage=no
UninstallDisplayIcon={app}\DiskStudioSuite.exe
UninstallDisplayName=Disk Studio Suite
SetupIconFile=..\src\Assets\app.ico
WizardStyle=modern
WizardSizePercent=110
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#OutputDir}
OutputBaseFilename=DiskStudioSuite-Setup-{#AppVersion}
CloseApplications=yes
CloseApplicationsFilter=DiskStudioSuite.exe
RestartApplications=no
LicenseFile=..\LICENSE

[Languages]
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
es.DesktopIcon=Crear un acceso directo en el escritorio
en.DesktopIcon=Create a desktop shortcut
es.Launch=Abrir Disk Studio Suite
en.Launch=Open Disk Studio Suite
es.RemoveSettings=¿Quieres borrar también tus ajustes, tareas e historial de Disk Studio Suite?%n%nSi vas a volver a instalarlo, elige «No» para conservarlos.
en.RemoveSettings=Do you also want to delete your Disk Studio Suite settings, tasks and history?%n%nIf you plan to reinstall it, choose “No” to keep them.

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; Flags: unchecked

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; DestName: "DiskStudioSuite.exe"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Disk Studio Suite"; Filename: "{app}\DiskStudioSuite.exe"
Name: "{autodesktop}\Disk Studio Suite"; Filename: "{app}\DiskStudioSuite.exe"; Tasks: desktopicon

[Run]
; Instalación normal: casilla «Abrir Disk Studio Suite» al terminar
Filename: "{app}\DiskStudioSuite.exe"; Description: "{cm:Launch}"; Flags: nowait postinstall skipifsilent
; Actualización desde la app (silenciosa): volver a abrirla
Filename: "{app}\DiskStudioSuite.exe"; Parameters: "--actualizado --reinicio --sin-presentacion"; Flags: nowait; Check: Relaunch

[UninstallRun]
; Quitar el inicio con Windows y las opciones del menú del Explorador
Filename: "{app}\DiskStudioSuite.exe"; Parameters: "--limpiar-integracion"; Flags: waituntilterminated runhidden; RunOnceId: "LimpiarIntegracion"

[Code]
function Relaunch: Boolean;
var
  i: Integer;
begin
  Result := False;
  for i := 1 to ParamCount do
    if CompareText(ParamStr(i), '/RELANZAR') = 0 then Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usPostUninstall) and not UninstallSilent then
    if MsgBox(CustomMessage('RemoveSettings'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      DelTree(ExpandConstant('{userappdata}\Disk Studio Suite'), True, True, True);
end;
