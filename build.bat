@echo off
setlocal
set CSC=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%SystemRoot%\Microsoft.NET\Framework\v4.0.30319\csc.exe

if not exist "%CSC%" (
    echo [ERROR] .NET Framework 4.x compiler csc.exe not found!
    exit /b 1
)

echo Compiling UltraContrast.exe...
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /utf8output ^
    /win32icon:"%~dp0app.ico" ^
    /r:System.dll ^
    /r:System.Drawing.dll ^
    /r:System.Windows.Forms.dll ^
    /r:System.Management.dll ^
    /out:"%~dp0UltraContrast.exe" ^
    "%~dp0UltraContrast.cs"

if errorlevel 1 (
    echo [FAILED] Build failed.
    exit /b 1
)

echo [OK] Built "%~dp0UltraContrast.exe"
endlocal
