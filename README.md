# TsvnAiCommitMessage

TortoiseSVN 提交对话框插件：在提交信息输入框右上角增加一个「AI生成提交信息」按钮，
点击后由 agent 生成提交信息并回填到日志框。

基于 `IBugtraqProvider` COM 接口（.NET 实现，用 Rider / MSBuild 构建）。

## 当前状态

- 按钮、COM 注册、回填链路：已完成
- agent 接入：**已完成，走 WorkBuddy CLI（`codebuddy -p`）**
  - 调用链：`GetCommitMessage` → `WorkBuddyAgentClient`（进程内先跑 `svn diff`）
    → 拉起 node 执行 `agent-bridge/codebuddy-bridge.js`
    → 桥接脚本调 WorkBuddy CLI 生成提交信息 → 解析结果回填日志框
  - 生成失败或返回空字符串时，保留用户已输入的内容
  - diff 超过 120k 字符会截断；单次生成超时约 180s

### 桥接层要点（agent-bridge/codebuddy-bridge.js）

- 协议：stdin 收 JSON（`commonRoot` / `pathList` / `diff` / `originalMessage`），
  stdout 回 `{"ok":true,"message":"..."}` 或 `{"ok":false,"error":"..."}`。
- **关键坑**：CLI 内部服务默认监听 `127.0.0.1:10003`，与 WorkBuddy 桌面端冲突时
  进程会静默挂死（EADDRINUSE 未处理）。桥接脚本每次调用前随机挑一个空闲端口，
  通过 `SERVER__PORT` 环境变量传给 CLI 规避。
- CLI 解析：`-p --output-format json --no-session-persistence`，取消息数组中
  `type === "result"` 条目的 `result` 字段。

### 运行依赖

- node.exe：按序探测 `WORKBUDDY_NODE` 环境变量 → `~/.workbuddy/binaries/node/versions/`（取最新）
  → `%APPDATA%\nodejs\node.exe` → PATH。
- bridge.js：与插件 DLL 同目录发布（`agent-bridge\codebuddy-bridge.js`，csproj 已配置随构建拷贝）。
- CLI 路径：默认 `C:\Program Files\WorkBuddy\resources\app.asar.unpacked\cli\bin\codebuddy`，
  可用 `WORKBUDDY_CLI_PATH` 环境变量覆盖。
- svn.exe：注册表 `HKLM\SOFTWARE\TortoiseSVN\Directory` → PATH；不可用时无 diff 也能生成
  （仅靠路径列表）。

### 换 agent 实现方式

实现 `IAgentClient.GenerateCommitMessage(CommitContext)`，替换
`AiCommitMessageProvider` 里的 `WorkBuddyAgentClient` 即可，COM 层不用动。

## 用 Rider 打开

打开根目录的 `TsvnAiCommitMessage.sln` 即可，包含两个工程：

- `IPlugin` — COM 接口程序集（`src/IBugTraqProvider.cs`）
- `TsvnAiCommitMessage` — 插件本体（`src/AiCommitMessageProvider.cs`），引用 IPlugin

本机开发工作流：Rider 里 Build（Ctrl+Shift+B）编译，然后运行配置里选
**Register Plugin** 执行注册（输出在 Run 窗口），重开提交对话框即可看到新逻辑。
注销用 **Unregister Plugin** 配置。两个配置都在右上角下拉的 `plugin` 分组里。

## 构建与注册

日常开发：Rider Build（Ctrl+Shift+B）即可（见上节）。

根目录一键脚本支持配置参数（缺省 Debug）：

```cmd
register.cmd                注册（bin\Debug 产物）
register.cmd Release        注册（bin\Release 产物）
unregister.cmd              注销
unregister.cmd Release      注销
```

Rider 运行配置（`.run\`，团队共享）里有对应的四个：Register/Unregister Plugin
的 Debug、Release 版本。注意注册指向哪套产物，按钮加载的就是哪套 DLL，
切换构建配置后记得重跑对应注册。

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

双击 `unregister.cmd`，或手动：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\register.ps1 -DllPath bin\Debug\TsvnAiCommitMessage.dll -Action Unregister
svn propdel bugtraq:provideruuid64 <wc路径>
```

## 关键约束（改代码前先读）

1. `IBugTraqProvider.cs` 必须保持独立程序集（IPlugin.dll），插件与调用方共用；
   在插件里内嵌一份相同 IID 的接口副本会导致 `InvalidCastException`。
2. `GetCommitMessage` 返回值整体替换日志框；不改动时必须原样返回 `originalMessage`。
3. `pathList` 只有路径没有 diff，需要内容时自行执行 `svn diff`。
4. 插件运行在 `TortoiseProc.exe` 进程内，异常不能抛出 COM 边界（代码里已兜底）。
