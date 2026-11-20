@echo off
chcp 65001 >nul
echo 正在刷新 Windows 图标缓存...
ie4uinit.exe -show >nul 2>&1
timeout /t 2 >nul
echo.
echo 如果任务栏固定项仍显示旧图标，请先取消固定，
echo 再从这个目录的 LocalDeck.exe 重新固定。
echo.
pause
