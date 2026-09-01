// WorkBuddy CLI 桥接 agent 实现 —— 接入 IAgentClient
//
// 链路：插件(TortoiseProc 进程内) → 弹出流式进度窗体 → 后台拉起 node 跑
//       agent-bridge/codebuddy-bridge.js → 桥接脚本调 WorkBuddy CLI
//       （codebuddy -p --output-format stream-json）→ 逐行解析事件
//       → 弹窗实时显示思考/回答 → done 后回填日志框。
//
// 关键坑（已在桥接脚本内规避，这里只做说明）：
//   CLI 内部服务默认监听 127.0.0.1:10003，与 WorkBuddy 桌面端冲突时进程会静默挂死，
//   桥接脚本通过 SERVER__PORT 随机空闲端口规避。
//
// 失败策略：任何环节出错（含用户取消）都返回 null，插件保留用户已输入的内容。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization; // 引用 System.Web.Extensions（.NET Framework 自带）
using Interop.BugTraqProvider;

namespace TsvnAiCommitMessage
{
    internal class WorkBuddyAgentClient : IAgentClient
    {
        // 桥接进程超时（CLI 侧自身超时 180s 会先发 error，这里只作进程级兜底）
        private const int BridgeTimeoutMs = 200000;
        // svn diff 内容上限，超出截断，避免超长 diff 拖垮请求
        private const int MaxDiffChars = 120000;

        /// <summary>桥接调用过程中的共享状态（工作线程写，UI 线程读）。</summary>
        private class BridgeState
        {
            public volatile bool Cancelled;
            public bool Success;
            public string Result;
            public string Error;
            public Process Process;
        }

        public string GenerateCommitMessage(CommitContext context)
        {
            try
            {
                string nodeExe = FindNodeExe();
                string bridgeJs = FindBridgeScript();
                if (nodeExe == null || bridgeJs == null)
                    return null;

                var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                string requestJson = serializer.Serialize(new Dictionary<string, object>
                {
                    { "commonRoot", context.CommonRoot ?? "" },
                    { "pathList", context.PathList ?? new string[0] },
                    { "diff", TryGetSvnDiff(context) },
                    { "originalMessage", context.OriginalMessage ?? "" },
                });

                var state = new BridgeState();
                GenerationDialog dialog = TryCreateDialog();

                // 后台线程跑桥接；主线程 ShowDialog 实时展示流式内容。
                // 生成完成后窗体停留，由用户点「填入日志框」确认才返回结果。
                var worker = Task.Run(() => RunBridge(nodeExe, bridgeJs, requestJson, state, dialog));

                bool shown = false;
                if (dialog != null)
                {
                    try
                    {
                        dialog.ShowDialog();
                        shown = true;
                    }
                    catch (Exception)
                    {
                        // 非 STA 等场景弹窗失败，退化为无 UI 等待
                    }
                }

                worker.Wait(shown ? 15000 : BridgeTimeoutMs + 30000);

                if (shown)
                {
                    // 只有用户点「填入日志框」（DialogResult.OK）才回填；
                    // 取消 / 关闭 / 失败一律保留用户原输入。
                    return dialog.DialogResult == DialogResult.OK && !string.IsNullOrEmpty(dialog.ResultMessage)
                        ? dialog.ResultMessage
                        : null;
                }
                return state.Success ? state.Result : null;
            }
            catch (Exception)
            {
                // 插件跑在 TortoiseProc.exe 进程内，任何异常都不能带崩提交对话框。
                return null;
            }
        }

        private static GenerationDialog TryCreateDialog()
        {
            try
            {
                return new GenerationDialog();
            }
            catch (Exception)
            {
                return null; // 无窗体环境退化为纯后台等待
            }
        }

        // ── 桥接进程执行（工作线程） ────────────────────────────────────

        private static void RunBridge(string nodeExe, string bridgeJs, string requestJson,
            BridgeState state, GenerationDialog dialog)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = nodeExe,
                    Arguments = "\"" + bridgeJs + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };

                using (var process = Process.Start(startInfo))
                {
                    if (process == null)
                    {
                        state.Error = "无法启动桥接进程";
                        dialog?.Fail(state.Error);
                        return;
                    }
                    state.Process = process;

                    using (var stdin = process.StandardInput)
                    {
                        stdin.Write(requestJson);
                        stdin.Close();
                    }

                    var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                    string line;
                    while ((line = process.StandardOutput.ReadLine()) != null)
                    {
                        if (state.Cancelled) break;
                        if (line.Length == 0) continue;
                        HandleBridgeEvent(serializer, line, state, dialog);
                        if (state.Success || state.Error != null) break;
                    }

                    if (state.Cancelled)
                    {
                        KillQuietly(process);
                        return; // 用户已取消，dialog 已自行关闭
                    }

                    if (state.Success)
                    {
                        KillQuietly(process);
                        dialog?.Complete(state.Result);
                        return;
                    }

                    // 无结果退出：补一个错误信息
                    if (state.Error == null)
                    {
                        string stderr = process.StandardError.ReadToEnd();
                        state.Error = string.IsNullOrEmpty(stderr)
                            ? "桥接进程意外退出"
                            : "桥接进程退出：" + stderr.Trim();
                    }
                    KillQuietly(process);
                    dialog?.Fail(state.Error);
                }
            }
            catch (Exception e)
            {
                state.Error = state.Error ?? ("桥接调用异常: " + e.Message);
                try { dialog?.Fail(state.Error); } catch (Exception) { /* ignore */ }
            }
        }

        private static void HandleBridgeEvent(JavaScriptSerializer serializer, string line,
            BridgeState state, GenerationDialog dialog)
        {
            Dictionary<string, object> evt;
            try
            {
                evt = serializer.Deserialize<Dictionary<string, object>>(line);
            }
            catch (Exception)
            {
                return; // 跳过非 JSON 行
            }
            if (evt == null) return;

            object type;
            evt.TryGetValue("type", out type);
            var typeText = type as string;

            if (typeText == "delta")
            {
                object kind, text;
                evt.TryGetValue("kind", out kind);
                evt.TryGetValue("text", out text);
                var body = text as string;
                if (string.IsNullOrEmpty(body)) return;
                if (Equals(kind, "thinking")) dialog?.AppendThinking(body);
                else dialog?.AppendAnswer(body);
                return;
            }

            if (typeText == "done")
            {
                object message;
                evt.TryGetValue("message", out message);
                state.Result = message as string;
                state.Success = !string.IsNullOrWhiteSpace(state.Result);
                if (!state.Success) state.Error = "AI 返回空提交信息";
                return;
            }

            if (typeText == "error")
            {
                object error;
                evt.TryGetValue("error", out error);
                state.Error = error as string ?? "未知错误";
            }
        }

        private static void KillQuietly(Process process)
        {
            try
            {
                if (!process.HasExited) process.Kill();
            }
            catch (Exception) { /* 已退出则忽略 */ }
        }

        // ── 路径定位 ────────────────────────────────────────────────────

        /// <summary>按优先级探测可用的 node.exe；找不到返回 null。</summary>
        private static string FindNodeExe()
        {
            var candidates = new List<string>();

            // 1. 显式环境变量
            var env = Environment.GetEnvironmentVariable("WORKBUDDY_NODE");
            if (!string.IsNullOrEmpty(env))
                candidates.Add(env);

            // 2. WorkBuddy 托管 node（版本目录取最新）
            try
            {
                var versionsDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".workbuddy", "binaries", "node", "versions");
                if (Directory.Exists(versionsDir))
                {
                    string best = null;
                    foreach (var dir in Directory.GetDirectories(versionsDir))
                    {
                        var exe = Path.Combine(dir, "node.exe");
                        if (File.Exists(exe) && (best == null || string.CompareOrdinal(dir, best) > 0))
                            best = exe;
                    }
                    if (best != null)
                        candidates.Add(best);
                }
            }
            catch (Exception) { /* 探测失败继续下一个候选 */ }

            // 3. 全局 npm node
            candidates.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "nodejs", "node.exe"));

            // 4. PATH
            candidates.Add("node.exe");

            foreach (var candidate in candidates)
            {
                try
                {
                    if (candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                        ? File.Exists(candidate)
                        : WhereExists(candidate))
                        return candidate;
                }
                catch (Exception) { /* 忽略单个候选的探测异常 */ }
            }
            return null;
        }

        private static bool WhereExists(string fileName)
        {
            var path = Environment.GetEnvironmentVariable("PATH");
            if (path == null) return false;
            foreach (var dir in path.Split(';'))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                try
                {
                    if (File.Exists(Path.Combine(dir.Trim(), fileName)))
                        return true;
                }
                catch (Exception) { /* 跳过非法路径 */ }
            }
            return false;
        }

        /// <summary>bridge.js 与插件 DLL 同目录发布（agent-bridge\codebuddy-bridge.js）。</summary>
        private static string FindBridgeScript()
        {
            try
            {
                var dllDir = Path.GetDirectoryName(typeof(WorkBuddyAgentClient).Assembly.Location);
                if (dllDir == null) return null;
                var script = Path.Combine(dllDir, "agent-bridge", "codebuddy-bridge.js");
                return File.Exists(script) ? script : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ── svn diff ────────────────────────────────────────────────────

        /// <summary>获取变更内容；svn 不可用或执行失败时返回空字符串（不阻塞生成）。</summary>
        private static string TryGetSvnDiff(CommitContext context)
        {
            try
            {
                string svnExe = FindSvnExe();
                if (svnExe == null) return string.Empty;

                // 优先只对本次提交所选路径做 diff；路径为空则 diff 整个共同根目录
                var targets = new List<string>();
                if (context.PathList != null && context.PathList.Length > 0)
                    targets.AddRange(context.PathList);
                else if (!string.IsNullOrEmpty(context.CommonRoot))
                    targets.Add(context.CommonRoot);
                if (targets.Count == 0) return string.Empty;

                var args = new StringBuilder("diff --git --internal-diff");
                foreach (var target in targets)
                    args.Append(' ').Append(QuoteArg(target));

                string output = RunSvnDiff(svnExe, args.ToString());
                if (string.IsNullOrEmpty(output)) return string.Empty;

                if (output.Length > MaxDiffChars)
                    output = output.Substring(0, MaxDiffChars) + "\n...（diff 过长已截断）";
                return output;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        /// <summary>svn.exe 定位：TortoiseSVN 安装目录（注册表）→ PATH。</summary>
        private static string FindSvnExe()
        {
            try
            {
                foreach (var keyPath in new[] { "SOFTWARE\\TortoiseSVN", "SOFTWARE\\WOW6432Node\\TortoiseSVN" })
                {
                    using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(keyPath))
                    {
                        var dir = key != null ? key.GetValue("Directory") as string : null;
                        if (!string.IsNullOrEmpty(dir))
                        {
                            var exe = Path.Combine(dir, "bin", "svn.exe");
                            if (File.Exists(exe)) return exe;
                        }
                    }
                }
            }
            catch (Exception) { /* 注册表不可访问则走 PATH 兜底 */ }
            return WhereExists("svn.exe") ? "svn.exe" : null;
        }

        /// <summary>同步执行 svn diff（短超时，失败返回空串不影响生成）。</summary>
        private static string RunSvnDiff(string svnExe, string arguments)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = svnExe,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            using (var process = Process.Start(startInfo))
            {
                if (process == null) return null;
                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();

                if (!process.WaitForExit(30000))
                {
                    KillQuietly(process);
                    return null;
                }
                if (process.ExitCode != 0) return null;
                return stdoutTask.Result;
            }
        }

        private static string QuoteArg(string arg)
        {
            if (arg == null) return "\"\"";
            return "\"" + arg.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }
    }
}
