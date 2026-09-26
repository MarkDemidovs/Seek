@echo off
rem Builds bin\Seek.exe with the C# compiler that ships with Windows (.NET Framework 4.8).
rem Nothing to install.
setlocal
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
set CSC=%FW%\csc.exe
cd /d "%~dp0"
if not exist bin mkdir bin

if not exist assets\seek.ico (
  if not exist assets mkdir assets
  "%CSC%" /nologo /out:bin\IconGen.exe /r:System.Drawing.dll tools\IconGen.cs || exit /b 1
  bin\IconGen.exe assets\seek.ico || exit /b 1
  del bin\IconGen.exe
)

"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ /unsafe ^
  /out:bin\Seek.exe ^
  /win32icon:assets\seek.ico ^
  /win32manifest:src\app.manifest ^
  /resource:assets\seek.ico,Seek.seek.ico ^
  /r:"%FW%\WPF\PresentationFramework.dll" ^
  /r:"%FW%\WPF\PresentationCore.dll" ^
  /r:"%FW%\WPF\WindowsBase.dll" ^
  /r:"%FW%\System.Xaml.dll" ^
  /r:System.Windows.Forms.dll ^
  /r:System.Drawing.dll ^
  src\*.cs || exit /b 1

echo Built bin\Seek.exe
