@echo off
chcp 65001 >nul
set CSC="C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\Roslyn\csc.exe"
set FW=C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.1
set OUT=%~dp0AppDataMover.exe

%CSC% /nologo /target:winexe /platform:anycpu /langversion:latest /optimize+ /utf8output ^
  /out:"%OUT%" ^
  /r:"%FW%\System.dll" ^
  /r:"%FW%\System.Core.dll" ^
  /r:"%FW%\System.Xaml.dll" ^
  /r:"%FW%\System.Windows.Forms.dll" ^
  /r:"%FW%\WindowsBase.dll" ^
  /r:"%FW%\PresentationCore.dll" ^
  /r:"%FW%\PresentationFramework.dll" ^
  "%~dp0src\Program.cs" "%~dp0src\Core.cs" "%~dp0src\NativeMethods.cs" "%~dp0src\Mover.cs" "%~dp0src\MainWindow.cs" "%~dp0src\SelfTest.cs"

if %ERRORLEVEL%==0 (
  echo.
  echo BUILD OK: %OUT%
) else (
  echo.
  echo BUILD FAILED
)
