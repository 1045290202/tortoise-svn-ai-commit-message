// WorkBuddy CLI 桥接 agent 实现 —— 接入 IAgentClient
//
// 链路：插件(TortoiseProc 进程内) → 拉起 node 跑 agent-bridge/codebuddy-bridge.js
//       → 桥接脚本调 WorkBuddy CLI（codebuddy -p）→ 解析结果回填。
//
// 关键坑（已在桥接脚本内规避，这里只做说明）：
//   CLI 内部服务默认监听 127.0.0.1:10003，与 WorkBuddy 桌面端冲突时进程会静默挂死，
//   桥接脚本通过 SERVER__PORT 随机空闲端口规避。
//
// 失败策略：任何环节出错都返回 null，插件保留用户已输入的内容（COM 层同样有兜底）。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Web.Script.Serialization; // 引用 System.Web.Extensions（.NET Framework 自带）
using Interop.BugTraqProvider;

namespace TsvnAiCommitMessage
{
    internal class WorkBuddyAgentClient : IAgentClient
    {
        // 桥接进程超时（含 CLI 生成时间；CLI 侧自身超时 180s，这里留出余量）
        private const int BridgeTimeoutMs = 200000;
        // svn diff 内容上限，超出截断，避免超长 diff 拖垮请求
        private const int MaxDiffChars = 120000;

        public string GenerateCommitMessage(CommitContext context)
        {
            try
            {
                string nodeExe = FindNodeExe();
                string bridgeJs = FindBridgeScript();
                if (nodeExe == null || bridgeJs == null)
                    return null;

                var request = new Dictionary<string, object>
                {
                    { "commonRoot", context.CommonRoot ?? "" },
                    { "pathList", context.PathList ?? new string[0] },
                    { "diff", TryGetSvnDiff(context) },
                    { "originalMessage", context.OriginalMessage ?? "" },
                };

                var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                string stdout = RunProcess(nodeExe, "\"" + bridgeJs + "\"",
                    serializer.Serialize(request), BridgeTimeoutMs);

                return ParseBridgeResponse(serializer, stdout);
            }
            catch (Exception)
            {
                // 插件跑在 TortoiseProc.exe 进程内，任何异常都不能带崩提交对话框。
                return null;
            }
        }

        // ── 桥接响应解析 ────────────────────────────────────────────────

        private static string ParseBridgeResponse(JavaScriptSerializer serializer, string stdout)
        {
            if (string.IsNullOrEmpty(stdout))
                return null;

            var response = serializer.Deserialize<Dictionary<string, object>>(stdout.Trim());
            if (response == null)
                return null;

            object ok;
            response.TryGetValue("ok", out ok);
            if (!Equals(ok, true))
                return null; // 失败原因在 "error" 字段，调试时可看 TortoiseSVN 日志

            object message;
            response.TryGetValue("message", out message);
            var text = message as string;
            return string.IsNullOrWhiteSpace(text) ? null : text;
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

                string output = RunProcess(svnExe, args.ToString(), null, 30000);
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

        // ── 进程执行 ────────────────────────────────────────────────────

        /// <summary>同步执行外部进程：stdin 喂入（可空），stdout 按 UTF-8 读取，超时强杀。</summary>
        private static string RunProcess(string fileName, string arguments, string stdIn, int timeoutMs)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = stdIn != null,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = SafeWorkingDirectory(),
            };

            using (var process = Process.Start(startInfo))
            {
                if (process == null) return null;

                // 先并发启动 stdout / stderr 读取，防止任一管道写满时子进程阻塞死锁
                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();
                string stderr = null; // 当前仅作防死锁读取；失败详情在桥接响应的 error 字段

                if (stdIn != null)
                {
                    // 异步写入（请求体可能上百 KB，同步写会在管道满时阻塞），写完即关
                    try
                    {
                        var stdinTask = process.StandardInput.WriteAsync(stdIn);
                        stdinTask.Wait(30000);
                    }
                    catch (Exception) { /* 子进程提前退出时管道破裂，忽略 */ }
                    finally
                    {
                        try { process.StandardInput.Close(); } catch (Exception) { /* ignore */ }
                    }
                }

                string stdout = stdoutTask.Result;
                stderr = stderrTask.Result;

                process.WaitForExit(timeoutMs);
                if (!process.HasExited)
                {
                    try { process.Kill(); } catch (Exception) { /* 已退出则忽略 */ }
                    return null;
                }
                if (process.ExitCode != 0) return null;

                return stdout;
            }
        }

        private static string SafeWorkingDirectory()
        {
            try
            {
                return Directory.GetCurrentDirectory();
            }
            catch (Exception)
            {
                return Environment.GetFolderPath(Environment.SpecialFolder.System);
            }
        }

        private static string QuoteArg(string arg)
        {
            if (arg == null) return "\"\"";
            return "\"" + arg.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }
    }
}
