@echo off
setlocal
set "DIR=%~dp0"
set "EXE=%DIR%LocalDeck.exe"
if not exist "%EXE%" set "EXE=%DIR%LocalDeck-portable\LocalDeck.exe"
if not exist "%EXE%" (
    echo LocalDeck.exe not found.
    pause
    exit /b 1
)
powershell -NoProfile -ExecutionPolicy Bypass -Command "Unblock-File -LiteralPath '%EXE%' -ErrorAction SilentlyContinue"
icacls "%EXE%" /setintegritylevel Medium >nul 2>&1
start "" "%EXE%"
