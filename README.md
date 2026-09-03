# TsvnAiCommitMessage

TortoiseSVN 提交对话框插件：在提交信息输入框右上角增加一个「AI生成提交信息」按钮，
点击后由 AI 生成提交信息并回填到日志框。生成过程在独立进度窗体中实时流式展示
（AI 思考与生成内容），可随时取消。

基于 `IBugtraqProvider` COM 接口（.NET 实现），AI 能力走本机已安装的
WorkBuddy CLI（`codebuddy -p`）。技术实现细节见 [docs/agent-bridge.md](docs/agent-bridge.md)。

## 使用前提

- TortoiseSVN（64 位，与插件位数一致）
- WorkBuddy 桌面端已安装并登录（CLI 复用其认证，无需单独登录）
- node.exe（按序探测 `WORKBUDDY_NODE` 环境变量 → `~/.workbuddy/binaries/node/versions/`
  → `%APPDATA%\nodejs\node.exe` → PATH）
- svn.exe（注册表 `HKLM\SOFTWARE\TortoiseSVN\Directory` → PATH；不可用时无 diff
  也能生成，仅靠文件清单）

## 安装

1. 构建：Rider 打开根目录 `TsvnAiCommitMessage.sln`，Build（Ctrl+Shift+B），
   或运行 `register.cmd [Debug|Release]` 注册；注销用 `unregister.cmd`。
   位数必须与 TortoiseSVN 宿主一致（本机 64 位用 `-Platform x64`）。
2. 在工作副本上启用——注册后还需设置属性才会出现在提交对话框：

   ```
   svn propset bugtraq:provideruuid64 "{A6F0E69F-1C6E-4C32-8FE3-A46F87825DFC}" <wc路径>
   ```

   属性提交进仓库后对整个团队生效。也可在 TortoiseSVN 设置对话框的
   「提交集成 → Bugtraq」里添加（类别为 Issue Tracker Plugin）。
3. 重开提交对话框，日志框右上角出现「AI生成提交信息」按钮即安装成功。

按钮不出现时，可在临时仓库里验证安装（避免污染真实工作副本）：

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

仍不出现时用 `reg query HKCU\Software\Classes\CLSID\{A6F0E69F-1C6E-4C32-8FE3-A46F87825DFC}` 排查注册。

## 使用

1. 提交对话框中勾选文件，点「AI生成提交信息」；
2. 等待生成（约 10~60 秒，超时 180s），进度窗体可实时查看与取消；
3. 结果自动填入日志框，可继续手工修改后再提交。

生成风格由外置提示词模板控制（`agent-bridge/commit-message-prompt.md`），
修改该文件即可调整输出格式，无需重编插件。

## 限制

- **AI 不改文件**：调用 AI 时禁用了其全部工具与配置加载（包括用户/项目的
  记忆、Skill、Rules、MCP），它只能基于传入的变更内容生成文本，不会读写任何文件。
- **diff 预算**：diff ≤ 50k 字符全量参与生成；更大的 diff 按文件分块降级，
  超过 250k 字符只传文件清单与 svn 状态，此时生成质量会下降（摘要偏宽泛）。
- **超时**：单次生成超过 180 秒自动取消，保留已输入内容。
- **失败兜底**：CLI 未登录、超时或生成失败时原样保留用户已输入的日志，不阻断提交。
- **位数**：插件位数必须与 TortoiseProc.exe 一致；对外分发需同时提供 x86/x64。

## 卸载

双击 `unregister.cmd`，或手动：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\register.ps1 -DllPath bin\Debug\TsvnAiCommitMessage.dll -Action Unregister
svn propdel bugtraq:provideruuid64 <wc路径>
```
