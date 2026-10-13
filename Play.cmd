@echo off
cd /d "%~dp0"
dotnet run --project "ChessVsZombies\ChessVsZombies.csproj" --configuration Release
if errorlevel 1 pause
