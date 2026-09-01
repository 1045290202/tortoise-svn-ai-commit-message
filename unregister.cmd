@echo off
rem Unregister the plugin (current user). The Debug DLL is only read to obtain the CLSID.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\register.ps1" -DllPath "%~dp0bin\Debug\TsvnAiCommitMessage.dll" -Action Unregister
pause
