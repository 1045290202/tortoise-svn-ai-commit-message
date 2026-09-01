@echo off
rem Register the plugin (current user, no admin) using the Rider Debug build output.
rem pwsh 7: defaults to UTF-8, avoids the GBK misdecoding of Chinese output.
"%ProgramFiles%\PowerShell\7\pwsh.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\register.ps1" -DllPath "%~dp0bin\Debug\TsvnAiCommitMessage.dll"
pause
