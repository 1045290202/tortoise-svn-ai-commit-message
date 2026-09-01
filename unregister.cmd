@echo off
rem Unregister the plugin (current user). The DLL is only read to obtain the CLSID.
rem Usage: unregister.cmd [Debug^|Release]  (default: Debug)
rem pwsh 7: defaults to UTF-8, avoids the GBK misdecoding of Chinese output.
set CONFIG=%1
if "%CONFIG%"=="" set CONFIG=Debug
"%ProgramFiles%\PowerShell\7\pwsh.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\register.ps1" -DllPath "%~dp0bin\%CONFIG%\TsvnAiCommitMessage.dll" -Action Unregister
