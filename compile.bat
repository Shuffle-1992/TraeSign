@echo off
setlocal
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set LIBDIR=C:\Windows\Microsoft.NET\Framework64\v4.0.30319

taskkill /IM TraeSign.exe /F >nul 2>&1

"%CSC%" /nologo /target:winexe /optimize+ ^
  /out:TraeSign.exe ^
  /lib:"%LIBDIR%" ^
  /reference:System.Windows.Forms.dll ^
  /reference:System.Drawing.dll ^
  /reference:System.Web.Extensions.dll ^
  /win32manifest:app.manifest ^
  TraeCheckinApp.cs CalendarControl.cs

if errorlevel 1 (
  echo COMPILE FAILED
  exit /b 1
)
echo COMPILE OK
