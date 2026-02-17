@echo off
setlocal EnableExtensions

set "APP_DIR_WIN=%~dp0"
if "%APP_DIR_WIN:~-1%"=="\" set "APP_DIR_WIN=%APP_DIR_WIN:~0,-1%"
set "URL_TO_OPEN=http://127.0.0.1:8765"
set "MAX_WAIT=30"
set "WSL_DIR="
set "WSL_IP="

wsl.exe -d Ubuntu -- echo OK >nul 2>&1
if errorlevel 1 goto distro_unavailable

for /f "usebackq delims=" %%I in (`wsl.exe -d Ubuntu -- wslpath -a "%APP_DIR_WIN%" 2^>nul`) do set "WSL_DIR=%%I"
if not defined WSL_DIR goto wslpath_failed

start "CodexSessionsViewer-WSL" /min wsl.exe -d Ubuntu --cd "%WSL_DIR%" --exec python3 viewer.py
timeout /t 1 /nobreak >nul

set /a WAITED=0
:wait_loop
powershell -NoProfile -ExecutionPolicy Bypass -Command "$c = New-Object Net.Sockets.TcpClient; try { $c.Connect('127.0.0.1', 8765); exit 0 } catch { exit 1 } finally { $c.Dispose() }" >nul 2>&1
if %errorlevel% == 0 goto open_browser

wsl.exe -d Ubuntu -- sh -lc "python3 -c 'import socket,sys;s=socket.socket();s.settimeout(0.3);sys.exit(0 if s.connect_ex((\"127.0.0.1\",8765))==0 else 1)'" >nul 2>&1
if %errorlevel% == 0 (
  for /f %%I in ('wsl.exe -d Ubuntu -- sh -lc "hostname -I"') do set "URL_TO_OPEN=http://%%I:8765"
  goto open_browser
)

set /a WAITED+=1
if %WAITED% geq %MAX_WAIT% goto startup_failed
timeout /t 1 /nobreak >nul
goto wait_loop

:open_browser
start "" "%URL_TO_OPEN%"
goto end

:wslpath_failed
echo Failed to convert Windows path to WSL path.
echo windows_dir: %APP_DIR_WIN%
goto fail_pause

:startup_failed
echo Viewer startup failed.
echo Diagnostic:
wsl.exe -d Ubuntu -- sh -lc "echo distro: $WSL_DISTRO_NAME; echo wsl_dir: \"%WSL_DIR%\"; ls -ld \"%WSL_DIR%\" 2>/dev/null || true; echo python3_path:; command -v python3 || true; echo running_viewer:; pgrep -af \"python3 viewer.py\" || true; echo listening_8765:; ((ss -ltn 2>/dev/null || netstat -lnt 2>/dev/null || true) | grep 8765) || true"
echo.
echo Manual run:
echo   wsl -d Ubuntu --cd "%WSL_DIR%" --exec python3 viewer.py
goto fail_pause

:distro_unavailable
echo Ubuntu distro is not available from this batch context.
echo Run this in PowerShell: wsl -d Ubuntu -- echo OK
goto fail_pause

:fail_pause
echo.
echo Press any key to close this window...
pause >nul
exit /b 1

:end
endlocal
