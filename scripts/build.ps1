<#
Compiles a TortoiseSVN issue-tracker plugin with the .NET Framework C# compiler,
so no Visual Studio or .NET SDK install is required.

The interface and the plugin must land in two assemblies, and both must be loaded
from the same IPlugin.dll: a managed CCW answers QueryInterface only when the
interface type identity matches exactly, so a second copy of the interface inside
the plugin assembly fails with InvalidCastException.
#>
param(
    [Parameter(Mandatory)][string]$InterfaceSrc,
    [Parameter(Mandatory)][string[]]$PluginSrc,
    [Parameter(Mandatory)][string]$OutDir,
    [string]$InterfaceAssembly = 'IPlugin',
    [string]$PluginAssembly = 'TsvnPlugin',
    [ValidateSet('x64', 'x86', 'AnyCPU')][string]$Platform = 'x64'
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$frameworkRoot = if ($Platform -eq 'x86') { "$env:WINDIR\Microsoft.NET\Framework" } else { "$env:WINDIR\Microsoft.NET\Framework64" }
$v4 = Get-ChildItem $frameworkRoot -Directory -Filter 'v4.*' | Sort-Object Name -Descending | Select-Object -First 1
if (-not $v4) { throw "no .NET Framework 4.x compiler under $frameworkRoot" }
$csc = Join-Path $v4.FullName 'csc.exe'

$ifaceDll = Join-Path $OutDir "$InterfaceAssembly.dll"
$pluginDll = Join-Path $OutDir "$PluginAssembly.dll"

& $csc -nologo -target:library -platform:$Platform -codepage:65001 "-out:$ifaceDll" $InterfaceSrc
if ($LASTEXITCODE) { throw "interface compile failed" }

& $csc -nologo -target:library -platform:$Platform -codepage:65001 "-r:$ifaceDll" "-out:$pluginDll" $PluginSrc
if ($LASTEXITCODE) { throw "plugin compile failed" }

$version = [System.Reflection.AssemblyName]::GetAssemblyName($pluginDll).Version
Write-Output "interface : $ifaceDll"
Write-Output "plugin    : $pluginDll"
Write-Output "version   : $version"
Write-Output "next        : register.ps1 -DllPath `"$pluginDll`""
