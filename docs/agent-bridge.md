# Agent 桥接层技术细节

`agent-bridge/codebuddy-bridge.js` 的实现说明：调用链、协议、环境隔离与已知坑。
面向修改桥接层或更换 agent 实现的开发者，使用说明见根目录 README。

## 调用链

```
GetCommitMessage（COM）
  → 弹出流式进度窗体（实时显示 AI 思考/生成内容，可取消）
  → WorkBuddyAgentClient（进程内先跑 svn diff / svn status）
  → 后台拉起 node 执行 agent-bridge/codebuddy-bridge.js
  → 桥接脚本以 -p --output-format stream-json 调 CLI
  → 逐行解析事件回填窗体 → 完成后自动关闭并填入日志框
```

生成失败、超时或用户取消时，保留用户已输入的内容。

## 桥接协议

stdin 收单个 JSON 请求：

| 字段 | 说明 |
|---|---|
| `commonRoot` | 工作副本共同根目录 |
| `pathList` | 变更文件列表（最多取前 50 个） |
| `status` | svn status 输出（含 A/M/D/R/? 状态码，可为空） |
| `diff` | svn diff 内容（可为空） |
| `originalMessage` | 用户已输入的日志（可为空） |
| `timeoutMs` | 可选，默认 180000 |
| `model` | 可选，不传用 CLI 默认 |
| `cliPath` | 可选，覆盖 CLI 路径 |
| `promptPath` | 可选，覆盖提示词文件/所在目录 |

stdout 回**行式 JSON 事件**：

- `{"type":"delta","kind":"thinking","text":...}` —— AI 思考增量
- `{"type":"delta","kind":"text","text":...}` —— 生成结果增量
- `{"type":"done","message":...}` / `{"type":"error","error":...}` —— 终态

done/error 之后进程退出；exit code 0=成功 1=失败。

提示词模板外置在脚本同目录 `commit-message-prompt.md`，解析优先级：
请求 `promptPath` > 环境变量 `WORKBUDDY_PROMPT_PATH` > 同目录默认文件；
读不到时回退内置兜底模板。

## 长 prompt 走 stdin

Windows `CreateProcess` 命令行上限约 32K 字符，整段 prompt 放 `-p` 参数会报
`ENAMETOOLONG`。桥接在 `-p` 里只留一句短指令，spawn 后把完整 prompt 写入
子进程 stdin（官方支持的管道输入，CLI 会将 stdin 内容与 `-p` 指令合并）。
实现：`stdio: ['pipe','pipe','pipe']` + `child.stdin.end(prompt, 'utf8')`；
stdin 挂空 error handler，CLI 提前退出导致的 EPIPE 由 exit 分支统一兜底。

## diff 预算降级

diff 全量传入会撑大 token 消耗并稀释模型注意力（主流提交信息生成器均设预算），
三级策略：

1. diff ≤ 50000 字符 → 全量传入；
2. 超预算 → 按「Index: 文件」分块贪心打包进预算，未收录文件数写入提示词，
   靠文件清单 / svn status 兜底；第一个文件块即使单独超预算也强制保留；
3. diff > 250000 字符 → 整体放弃 diff，仅传文件清单 + svn status + 降级说明。

文件清单与 svn status 始终全量保留，保证降级时整体意图不丢。

## 环境隔离

桥接拉起的 CLI 只受桥接传入的提示词影响，屏蔽用户/项目全部配置：

| 手段 | 作用 |
|---|---|
| `--tools ""` | 禁用全部内置工具：无法读写文件，无法调用 Skill/MCP |
| `--setting-sources ""` | settings.json 的 user/project/local 来源全不加载 |
| `--strict-mcp-config` | 未传 `--mcp-config` 时不加载任何 MCP 服务器 |
| cwd 指向 `.isolated-cwd` 空目录 | 项目级 `CODEBUDDY.md` 与 `.codebuddy/`（rules/skills/commands/记忆）按 cwd 向上扫描发现，空目录即全失效 |
| `CODEBUDDY_DISABLE_AUTO_MEMORY=1` + `CODEBUDDY_MEMORY_ENABLED=false` | 短路 CLI 记忆注入 |

## 已知坑

- **SERVER__PORT 冲突**：CLI 内部服务默认监听 `127.0.0.1:10003`，与 WorkBuddy
  桌面端冲突时进程静默挂死（EADDRINUSE 未处理）。桥接每次调用前随机挑空闲端口，
  通过 `SERVER__PORT` 环境变量传给 CLI 规避。
- **CLI 输出解析**：`--output-format stream-json` 下取
  `stream_event.event.delta` 的 `thinking_delta` / `text_delta`，
  末尾 `type === "result"` 条目的 `result` 字段为最终文本；非 JSON 行直接跳过。
- 拿到 `result` 后立即 `SIGKILL` 子进程——CLI 事件循环可能不自行退出。

## 换 agent 实现方式

实现 `IAgentClient.GenerateCommitMessage(CommitContext)`，替换
`AiCommitMessageProvider` 里的 `WorkBuddyAgentClient` 即可，COM 层不用动。

## 关键约束（改代码前先读）

1. `IBugTraqProvider.cs` 必须保持独立程序集（IPlugin.dll），插件与调用方共用；
   在插件里内嵌一份相同 IID 的接口副本会导致 `InvalidCastException`。
2. `GetCommitMessage` 返回值整体替换日志框；不改动时必须原样返回 `originalMessage`。
3. `pathList` 只有路径没有 diff，需要内容时自行执行 `svn diff`。
4. 插件运行在 `TortoiseProc.exe` 进程内，异常不能抛出 COM 边界（代码里已兜底）。
