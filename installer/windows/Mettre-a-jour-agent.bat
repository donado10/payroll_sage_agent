@echo off
rem IGF Paie - Agent Sage : installe MAINTENANT la derniere version publiee, sans attendre la plage horaire.
rem Elle part d'une COPIE de l'agent (update\runner-...) : l'installation remplace le programme lui-meme.
net session >nul 2>&1
if errorlevel 1 (
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
chcp 65001 >nul
if not exist "%~dp0update\runner.txt" (
  echo Les mises a jour ne sont pas encore pretes sur ce serveur : installez le MSI 0.5.0 ou plus recent.
  echo.
  pause
  exit /b 1
)
set /p RUNNER=<"%~dp0update\runner.txt"
"%RUNNER%" update --now
echo.
pause
