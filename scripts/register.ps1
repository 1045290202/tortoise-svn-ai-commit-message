<#
Registers or unregisters a managed TortoiseSVN issue-tracker plugin.

Default scope is HKCU\Software\Classes, which Windows merges into HKEY_CLASSES_ROOT,
so no elevation is needed. Use -Machine for a per-machine install (admin prompt);
TortoiseSVN's settings dialog lists plugins found through the component category
regardless of scope.

The CLSID, class name and assembly identity are read from the DLL itself, so they
can never drift out of sync with the source.
#>
param(
    [Parameter(Mandatory)][string]$DllPath,
    [ValidateSet('Register', 'Unregister')][string]$Action = 'Register',
    [switch]$Machine
)

$ErrorActionPreference = 'Stop'
# {3494FA92-B139-4730-9591-01135D5E7831} = CATID_BugTraqProvider, the category TortoiseSVN enumerates.
# {62C8FE65-4EBB-45E7-B440-6E39B2CDBF29} = managed-type category that RegAsm also writes.
$CatBugTraq = '{3494FA92-B139-4730-9591-01135D5E7831}'
$CatManaged = '{62C8FE65-4EBB-45E7-B440-6E39B2CDBF29}'

$root = if ($Machine) { 'HKLM:\SOFTWARE\Classes' } else { 'HKCU:\Software\Classes' }

$asm = [System.Reflection.Assembly]::LoadFrom((Resolve-Path $DllPath).Path)
$provider = $null
$clsid = $null
foreach ($type in $asm.GetExportedTypes()) {
    if (-not $type.IsClass) { continue }
    $comVisible = $false
    $guidValue = $null
    foreach ($attr in [System.Reflection.CustomAttributeData]::GetCustomAttributes($type)) {
        switch ($attr.AttributeType.FullName) {
            'System.Runtime.InteropServices.ComVisibleAttribute' { $comVisible = [bool]$attr.ConstructorArguments[0].Value }
            'System.Runtime.InteropServices.GuidAttribute' { $guidValue = [string]$attr.ConstructorArguments[0].Value }
        }
    }
    if (-not $comVisible -or -not $guidValue) { continue }
    # Skip the all-zero placeholder from the template so it can never be registered by accident.
    if ($guidValue -match '^(0+-)+0+$') { continue }
    $provider = $type
    $clsid = '{' + $guidValue.ToUpperInvariant() + '}'
    break
}
if (-not $provider) { throw "no ComVisible class with a real Guid attribute found in $DllPath" }
$progId = $provider.FullName
$assemblyName = $asm.FullName
$runtime = 'v4.0.30319'
$codebase = 'file:///' + ($asm.Location -replace '\\', '/')

$clsKey = "$root\CLSID\$clsid"
$inprocKey = "$clsKey\InprocServer32"

if ($Action -eq 'Unregister') {
    foreach ($k in @("$root\$progId", $clsKey)) { if (Test-Path $k) { Remove-Item $k -Recurse -Force } }
    Write-Output "Unregistered $clsid ($progId)"
    Write-Output ''
    Write-Output '==> 注销完成。若提交对话框仍显示按钮，属 TortoiseSVN 缓存，重开对话框或重启资源管理器即可。'
    return
}

$reg = [Microsoft.Win32.Registry]::LocalMachine
if (-not $Machine) { $reg = [Microsoft.Win32.Registry]::CurrentUser }
# Convert a drive-style path (HKCU:\Software\Classes\...) to the name Registry API expects.
function New-K([string]$path) {
    $name = ($path -replace '^HKCU:\\', '') -replace '^HKLM:\\', ''
    return $reg.CreateSubKey($name)
}

foreach ($keyPath in @("$root\$progId", $clsKey, $inprocKey, "$inprocKey\$($asm.GetName().Version)",
                       "$clsKey\ProgId", "$clsKey\Implemented Categories\$CatManaged",
                       "$clsKey\Implemented Categories\$CatBugTraq")) {
    New-K $keyPath | Out-Null
}

$k = New-K "$root\$progId";      $k.SetValue('', $progId); $k.SetValue('CLSID', $clsid); $k.Close()
$k = New-K $clsKey;              $k.SetValue('', $progId); $k.Close()
$k = New-K $inprocKey
$k.SetValue('', 'mscoree.dll')
$k.SetValue('ThreadingModel', 'Both')
$k.SetValue('Class', $progId)
$k.SetValue('Assembly', $assemblyName)
$k.SetValue('RuntimeVersion', $runtime)
$k.SetValue('CodeBase', $codebase)
$k.Close()
$k = New-K "$inprocKey\$($asm.GetName().Version)"
$k.SetValue('Class', $progId)
$k.SetValue('Assembly', $assemblyName)
$k.SetValue('RuntimeVersion', $runtime)
$k.SetValue('CodeBase', $codebase)
$k.Close()
$k = New-K "$clsKey\ProgId"; $k.SetValue('', $progId); $k.Close()

Write-Output "Registered   : $clsid"
Write-Output "class        : $progId"
Write-Output "assembly     : $assemblyName"
Write-Output "codebase     : $codebase"
Write-Output "scope        : $root"
Write-Output "Enable per working copy with: svn propset bugtraq:provideruuid64 $clsid <wc-path>"
Write-Output ''
Write-Output '==> 注册完成。重开 TortoiseSVN 提交对话框即可看到按钮（需工作副本已设置 bugtraq:provideruuid64 属性）。'
