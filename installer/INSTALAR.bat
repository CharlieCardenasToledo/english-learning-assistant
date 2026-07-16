@echo off
title English Learning Assistant - Instalador
color 0B

echo ========================================
echo  English Learning Assistant
echo  Instalador v1.0
echo ========================================
echo.

REM Verificar permisos de administrador
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo [!] Este instalador requiere permisos de administrador.
    echo [!] Reiniciando con permisos elevados...
    powershell -Command "Start-Process '%~f0' -Verb RunAs"
    exit /b
)

echo [1/4] Creando directorio de instalacion...
set "INSTALL_DIR=%ProgramFiles%\EnglishLearningAssistant"
if not exist "%INSTALL_DIR%" mkdir "%INSTALL_DIR%"

echo [2/4] Copiando archivos...
copy /Y "EnglishLearningAssistant.exe" "%INSTALL_DIR%\" >nul
copy /Y "README.md" "%INSTALL_DIR%\" >nul 2>&1
copy /Y "README.es.md" "%INSTALL_DIR%\" >nul 2>&1
copy /Y "LANZAR_MODO_EXAMEN.bat" "%INSTALL_DIR%\" >nul 2>&1

echo [3/4] Creando acceso directo en el escritorio...
powershell -Command "$WS = New-Object -ComObject WScript.Shell; $SC = $WS.CreateShortcut('%USERPROFILE%\Desktop\English Learning Assistant.lnk'); $SC.TargetPath = '%INSTALL_DIR%\EnglishLearningAssistant.exe'; $SC.WorkingDirectory = '%INSTALL_DIR%'; $SC.Description = 'AI-powered English Learning Assistant'; $SC.Save()"

echo [4/4] Creando entrada en el menu inicio...
powershell -Command "$WS = New-Object -ComObject WScript.Shell; $SC = $WS.CreateShortcut('%APPDATA%\Microsoft\Windows\Start Menu\Programs\English Learning Assistant.lnk'); $SC.TargetPath = '%INSTALL_DIR%\EnglishLearningAssistant.exe'; $SC.WorkingDirectory = '%INSTALL_DIR%'; $SC.Description = 'AI-powered English Learning Assistant'; $SC.Save()"

echo.
echo ========================================
echo  Instalacion completada!
echo ========================================
echo.
echo La aplicacion ha sido instalada en:
echo %INSTALL_DIR%
echo.
echo Accesos directos creados en:
echo - Escritorio
echo - Menu Inicio
echo.

REM Preguntar si instalar Ollama
if exist "OllamaSetup.exe" (
    echo.
    echo ========================================
    echo  Ollama (Motor de IA)
    echo ========================================
    echo.
    echo Ollama es necesario para que funcione el asistente de IA.
    echo.
    choice /C SN /M "Deseas instalar Ollama ahora"
    if errorlevel 2 goto skip_ollama
    if errorlevel 1 goto install_ollama
    
    :install_ollama
    echo.
    echo Instalando Ollama...
    start /wait OllamaSetup.exe
    echo.
    echo Descargando modelo de IA (llama3.2)...
    echo Esto puede tardar varios minutos...
    ollama pull llama3.2
    goto after_ollama
    
    :skip_ollama
    echo.
    echo [!] Ollama NO fue instalado.
    echo [!] Deberas instalarlo manualmente desde: https://ollama.ai/download
    echo.
)

:after_ollama
echo.
echo ========================================
echo  Primeros Pasos
echo ========================================
echo.
echo 1. Activa los Subtitulos en Vivo de Windows (Win + Ctrl + L)
echo 2. Ejecuta "English Learning Assistant" desde el escritorio
echo 3. Presiona Ctrl + Espacio para abrir el asistente
echo.
pause
