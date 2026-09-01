@echo off
rem Register the plugin (current user, no admin) using the Rider Debug build output.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\register.ps1" -DllPath "%~dp0bin\Debug\TsvnAiCommitMessage.dll"
pause
