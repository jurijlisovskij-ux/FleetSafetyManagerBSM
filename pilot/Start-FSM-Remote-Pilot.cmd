@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Start-FSM-Remote-Pilot.ps1"
if errorlevel 1 pause

