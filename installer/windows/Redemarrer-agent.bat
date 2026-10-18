@echo off
rem IGF Paie - Agent Sage : applique agent.json, redemarre le service, et affiche la reponse.
rem La reponse est aussi ecrite dans agent.result.txt, a cote d'agent.json.
net session >nul 2>&1
if errorlevel 1 (
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
chcp 65001 >nul
"%~dp0PayrollSageAgent.exe" apply-config --restart
echo.
pause
