@echo off
rem FileRelay Release build (.NET Framework 4.8)
rem Requires: .NET SDK (dotnet) or Visual Studio 2019+ (MSBuild)
setlocal
cd /d "%~dp0"

where dotnet >nul 2>nul
if %errorlevel%==0 (
    dotnet build src\FileRelay\FileRelay.csproj -c Release -nologo
) else (
    msbuild src\FileRelay\FileRelay.csproj /restore /p:Configuration=Release /nologo
)
if errorlevel 1 (
    echo.
    echo BUILD FAILED
    exit /b 1
)

echo.
echo Output: %~dp0src\FileRelay\bin\Release\FileRelay.exe
endlocal
