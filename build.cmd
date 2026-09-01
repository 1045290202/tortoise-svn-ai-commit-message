@echo off
rem 一键构建 + 注册（当前用户，免管理员）。位数默认 x64，与 64 位 TortoiseSVN 匹配。
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\build.ps1" ^
    -InterfaceSrc "%~dp0src\IBugTraqProvider.cs" ^
    -PluginSrc "%~dp0src\AiCommitMessageProvider.cs" ^
    -OutDir "%~dp0build" ^
    -PluginAssembly TsvnAiCommitMessage ^
    -Platform %1
if errorlevel 1 exit /b 1
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\register.ps1" -DllPath "%~dp0build\TsvnAiCommitMessage.dll"
