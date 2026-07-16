@echo off
color 0A
echo ==========================================================
echo   MODO EXAMEN: REINICIO TOTAL DE CHROME (FORCE KILL)
echo ==========================================================
echo.
echo [PASO 1] Terminando TODOS los procesos de Chrome...
echo (Si tienes trabajos sin guardar, CANCELA AHORA cerrando esta ventana)
timeout /t 3
taskkill /F /IM chrome.exe /T >nul 2>&1
echo Procesos terminados.
echo.

echo [PASO 2] Esperando liberacion de archivos...
timeout /t 2 >nul

echo [PASO 3] Iniciando Chrome en Puerto 9222...
echo.
start "" "C:\Program Files\Google\Chrome\Application\chrome.exe" --remote-debugging-port=9222 --user-data-dir="%LOCALAPPDATA%\Google\Chrome\User Data" --restore-last-session
echo.

echo [PASO 4] Verificando conexion...
timeout /t 3 >nul
explorer "http://localhost:9222/json"

echo.
echo ==========================================================
echo INSTRUCCIONES:
echo 1. Se ha abierto una pestana con texto tecnico (JSON).
echo    - SI VES TEXTO (id, title, url...), FUNCIONA! CIERRA ESA PESTANA y ve a tu examen.
echo    - SI DICE "NO SE PUEDE CONECTAR", algo fallo (reinicia PC).
echo.
echo 2. Ahora abre la App WindowsLiveCaptionsReader y usa el boton SCAN.
echo ==========================================================
pause
