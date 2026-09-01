@echo off
rem Unregister the plugin (current user). The Debug DLL is only read to obtain the CLSID.
rem pwsh 7: defaults to UTF-8, avoids the GBK misdecoding of Chinese output.
"%ProgramFiles%\PowerShell\7\pwsh.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\register.ps1" -DllPath "%~dp0bin\Debug\TsvnAiCommitMessage.dll" -Action Unregister
pause
