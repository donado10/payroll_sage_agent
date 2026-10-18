@echo off
rem IGF Paie - Agent Sage : ouvre agent.json dans le Bloc-notes, EN ADMINISTRATEUR.
rem (agent.json n'est lisible que par les administrateurs : il peut contenir des mots de passe.)
rem Apres l'avoir enregistre : Redemarrer-agent.bat.
powershell -NoProfile -Command "Start-Process notepad.exe -ArgumentList '\"%~dp0agent.json\"' -Verb RunAs"
