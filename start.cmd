@echo off
setlocal

cd /d "%~dp0"
set "OUT_DIR=%~dp0build\dev-run"

where dotnet >nul 2>nul
if errorlevel 1 (
    echo .NET SDK was not found. Please install .NET SDK 10 first.
    pause
    exit /b 1
)

echo Building SourceGit into "%OUT_DIR%"...
dotnet build "src\SourceGit.csproj" -o "%OUT_DIR%"
if errorlevel 1 (
    echo.
    echo Build failed.
    pause
    exit /b 1
)

echo.
echo Starting SourceGit...
start "" "%OUT_DIR%\SourceGit.exe"

endlocal
