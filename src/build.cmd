@echo off
rem ============================================================
rem  Build NarakaClientSwitcher.exe
rem  Uses the C# compiler shipped with .NET Framework 4.x
rem  (preinstalled on Windows 10 / 11 - no SDK required)
rem ============================================================
setlocal
title Build NarakaClientSwitcher

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo [ERROR] csc.exe not found. .NET Framework 4.x is required ^(included in Windows 10/11^).
  pause
  exit /b 1
)

set "ROOT=%~dp0"
set "OUT=%ROOT%..\dist\NarakaClientSwitcher.exe"
if not exist "%ROOT%..\dist" mkdir "%ROOT%..\dist"

echo Compiler: %CSC%
echo Output  : %OUT%
echo.

"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 ^
  /win32icon:"%ROOT%app.ico" ^
  /out:"%OUT%" ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Xml.dll ^
  "%ROOT%Program.cs"

if errorlevel 1 (
  echo.
  echo [FAILED] Compilation error.
  pause
  exit /b 1
)

echo.
echo [OK] Built: %OUT%
pause
