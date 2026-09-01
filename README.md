# TsvnAiCommitMessage

TortoiseSVN 提交对话框插件：在提交信息输入框右上角增加一个「AI生成提交信息」按钮，
点击后由 agent 生成提交信息并回填到日志框。

基于 `IBugtraqProvider` COM 接口（.NET 实现，无需 Visual Studio，用系统自带的
.NET Framework 4.x 编译器构建）。

## 当前状态

- 按钮、COM 注册、回填链路：已完成
- agent 接入：**接口已预留（`IAgentClient`），实现为空占位**
  - 接入时实现 `IAgentClient.GenerateCommitMessage(CommitContext)`，
    替换 `src/AiCommitMessageProvider.cs` 里的 `NullAgentClient` 即可，COM 层不用动
  - `CommitContext` 目前含 `CommonRoot` / `PathList` / `OriginalMessage`；
    需要 diff 内容时在此进程内调 `svn diff` 后挂到上下文上
  - 生成失败或返回空字符串时，保留用户已输入的内容

## 用 Rider 打开

打开根目录的 `TsvnAiCommitMessage.sln` 即可，包含两个工程：

- `IPlugin` — COM 接口程序集（`src/IBugTraqProvider.cs`）
- `TsvnAiCommitMessage` — 插件本体（`src/AiCommitMessageProvider.cs`），引用 IPlugin

注意：Rider 里的构建产物输出到 `bin/`，仅用于代码浏览与编辑；**正式构建/注册仍走
`build.cmd`**（用系统 csc 输出到 `build/`，注册脚本按该路径反射读取）。两边不要混用。

## 构建与注册

```cmd
build.cmd          一键构建 x64 + 注册（当前用户，免管理员）
```

或手动：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build.ps1 `
    -InterfaceSrc src\IBugTraqProvider.cs `
    -PluginSrc src\AiCommitMessageProvider.cs `
    -OutDir build -PluginAssembly TsvnAiCommitMessage -Platform x64
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\register.ps1 -DllPath build\TsvnAiCommitMessage.dll
```

位数必须与 TortoiseSVN 宿主一致（本机为 64 位，用 `-Platform x64`）。
对外分发时需同时提供 x86 / x64 两个版本。

## 在工作副本上启用

插件注册后还需在工作副本上设置属性才会出现在提交对话框：

```
svn propset bugtraq:provideruuid64 "{A6F0E69F-1C6E-4C32-8FE3-A46F87825DFC}" <wc路径>
```

属性提交进仓库后对整个团队生效。也可在 TortoiseSVN 设置对话框的
「提交集成 → Bugtraq」里添加（类别为 Issue Tracker Plugin）。

## 验证

在临时仓库里验，避免污染真实工作副本：

```powershell
$dir = (Get-ItemProperty 'HKLM:\SOFTWARE\TortoiseSVN').Directory
$tmp = Join-Path ([IO.Path]::GetTempPath()) 'tsvn-ai-check'
& "$dir\bin\svnadmin.exe" create "$tmp\repo"
& "$dir\bin\svn.exe" checkout "file:///$($tmp -replace '\\','/')/repo" "$tmp\wc"
Set-Content "$tmp\wc\seed.txt" 'initial'
& "$dir\bin\svn.exe" add "$tmp\wc\seed.txt"; & "$dir\bin\svn.exe" commit "$tmp\wc" -m 'initial'
Add-Content "$tmp\wc\seed.txt" 'pending change'
& "$dir\bin\svn.exe" propset bugtraq:provideruuid64 "{A6F0E69F-1C6E-4C32-8FE3-A46F87825DFC}" "$tmp\wc"
Start-Process "$dir\bin\TortoiseProc.exe" -ArgumentList '/command:commit', "/path:$tmp\wc"
```

预期：提交对话框日志框右上角出现「AI生成提交信息」按钮；点击后（未接入 agent）
日志框内容不变。按钮不出现时用 `reg query HKCU\Software\Classes\CLSID\{A6F0E69F-1C6E-4C32-8FE3-A46F87825DFC}` 排查注册。

## 卸载

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\register.ps1 -DllPath build\TsvnAiCommitMessage.dll -Action Unregister
svn propdel bugtraq:provideruuid64 <wc路径>
```

## 关键约束（改代码前先读）

1. `IBugTraqProvider.cs` 必须保持独立程序集（IPlugin.dll），插件与调用方共用；
   在插件里内嵌一份相同 IID 的接口副本会导致 `InvalidCastException`。
2. `GetCommitMessage` 返回值整体替换日志框；不改动时必须原样返回 `originalMessage`。
3. `pathList` 只有路径没有 diff，需要内容时自行执行 `svn diff`。
4. 插件运行在 `TortoiseProc.exe` 进程内，异常不能抛出 COM 边界（代码里已兜底）。
