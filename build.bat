@echo off
rem ============================================================================
rem  NCM Converter - build.bat
rem
rem  Compiles src\NCMConverter.cs into NCMConverter.exe using the C# compiler
rem  that already ships with every copy of Windows (.NET Framework 4.x).
rem
rem  No downloads. No Visual Studio. No Python. No NuGet. No Internet.
rem
rem  Usage: double-click this file, it drops NCMConverter.exe next to it.
rem ============================================================================

setlocal enabledelayedexpansion

set "ROOT=%~dp0"
set "SRC=%ROOT%src\NCMConverter.cs"
set "OUT=%ROOT%NCMConverter.exe"

if not exist "%SRC%" (
    echo [ERROR] Source not found: %SRC%
    echo.
    pause
    exit /b 1
)

rem ---- 1. Locate the built-in C# compiler -------------------------------
set "CSC="

for %%D in (
    "%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
    "%WINDIR%\Microsoft.NET\Framework\v4.0.30319"
) do (
    if not defined CSC if exist "%%~D\csc.exe" set "CSC=%%~D\csc.exe"
)

rem Fallback: any versioned .NET Framework directory that carries csc.exe
if not defined CSC (
    for /d %%D in ("%WINDIR%\Microsoft.NET\Framework64\v*") do (
        if not defined CSC if exist "%%~D\csc.exe" set "CSC=%%~D\csc.exe"
    )
)
if not defined CSC (
    for /d %%D in ("%WINDIR%\Microsoft.NET\Framework\v*") do (
        if not defined CSC if exist "%%~D\csc.exe" set "CSC=%%~D\csc.exe"
    )
)

rem Last resort: developer tools already on PATH
if not defined CSC (
    for /f "delims=" %%P in ('where csc.exe 2^>nul') do (
        if not defined CSC set "CSC=%%P"
    )
)

if not defined CSC (
    echo [ERROR] Could not find csc.exe.
    echo .NET Framework 4.x is a Windows component and is normally present.
    echo Enable it via: Control Panel ^> Programs ^> Turn Windows features on
    echo or download the .NET Framework 4.8 Runtime from Microsoft.
    echo.
    pause
    exit /b 1
)

echo Compiler : %CSC%
echo Source   : %SRC%
echo Output   : %OUT%
echo.

rem ---- 2. Compile --------------------------------------------------------
"%CSC%" /nologo /optimize+ /warn:1 /nowarn:1607 /codepage:65001 ^
    /target:winexe /platform:anycpu ^
    /out:"%OUT%" "%SRC%" ^
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll

if errorlevel 1 (
    echo.
    echo [ERROR] Compilation failed - see messages above.
    echo.
    pause
    exit /b 1
)

echo.
echo [OK] Built: %OUT%
echo Double-click NCMConverter.exe to start. Drag files into the window.
echo.
pause
