// svn diff 获取与按勾选路径过滤。
// svn.exe 定位：TortoiseSVN 安装目录（注册表）→ PATH。
// 失败策略：svn 不可用或执行失败时返回空字符串（不阻塞生成，仅按路径列表生成）。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Win32;
using TsvnAiCommitMessage.Agent;
using TsvnAiCommitMessage.Tools;

namespace TsvnAiCommitMessage.Svn
{
    internal static class SvnDiffProvider
    {
        // diff 内容上限，超出截断，避免超长 diff 拖垮请求
        private const int MaxDiffChars = 120000;
        // status 输出上限，状态行较短，留足余量即可
        private const int MaxStatusChars = 20000;

        /// <summary>最近一次 diff 获取失败的原因；成功或无变更时为 null。供 UI 透出排障。</summary>
        public static string LastError { get; private set; }

        /// <summary>
        /// 构建 svn 子命令的目标路径列表：优先用勾选的 PathList；
        /// PathList 为空则回退整个 CommonRoot（wholeTree 置真，diff 无需再按路径过滤）。
        /// </summary>
        private static List<string> BuildTargets(CommitContext context, out bool wholeTree)
        {
            wholeTree = false;
            var targets = new List<string>();
            if (context.PathList != null && context.PathList.Length > 0)
                targets.AddRange(context.PathList);
            else if (!string.IsNullOrEmpty(context.CommonRoot))
            {
                targets.Add(context.CommonRoot);
                wholeTree = true;
            }
            return targets;
        }

        /// <summary>
        /// 获取本次变更的 svn status 输出（含 A/M/D/R/? 等状态码），供 agent 判断变更类型。
        /// svn 不可用或执行失败时返回空字符串（不阻塞生成）。
        /// </summary>
        public static string TryGetStatus(CommitContext context)
        {
            try
            {
                string svnExe = FindSvnExe();
                if (svnExe == null) return string.Empty;

                bool wholeTree;
                var targets = BuildTargets(context, out wholeTree);
                if (targets.Count == 0) return string.Empty;

                var args = new StringBuilder("status");
                foreach (var target in targets)
                    args.Append(' ').Append(QuoteArg(target));

                string output = RunSvn(svnExe, args.ToString(), out _);
                if (output == null) return string.Empty;

                output = output.TrimEnd();
                if (output.Length > MaxStatusChars)
                    output = output.Substring(0, MaxStatusChars) + "\n...（status 过长已截断）";
                return output;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        /// <summary>获取变更内容；svn 不可用或执行失败时返回空字符串（不阻塞生成）。
        /// 结果按 svn diff 的 Index: 分块过滤，只保留本次勾选提交的路径。</summary>
        public static string TryGetDiff(CommitContext context)
        {
            LastError = null;
            try
            {
                string svnExe = FindSvnExe();
                if (svnExe == null)
                {
                    LastError = "未找到 svn.exe（TortoiseSVN 注册表/PATH 均未命中）";
                    return string.Empty;
                }

                // 优先只对本次提交所选路径做 diff；路径为空则 diff 整个共同根目录
                bool wholeTree;
                var targets = BuildTargets(context, out wholeTree);
                if (targets.Count == 0) return string.Empty;

                var args = new StringBuilder("diff --git --internal-diff");
                foreach (var target in targets)
                    args.Append(' ').Append(QuoteArg(target));

                string output = RunSvn(svnExe, args.ToString(), out string svnError);
                if (output == null)
                {
                    LastError = string.IsNullOrEmpty(svnError)
                        ? "svn diff 执行失败（超时或异常退出）"
                        : "svn diff 失败: " + svnError.Trim();
                    return string.Empty;
                }
                if (output.Length == 0) return string.Empty; // svn 正常但无本地改动

                // 勾选项里若含目录，svn diff 会递归带出未勾选文件的变更；
                // 按 Index: 分块过滤，只保留勾选路径命中的块，确保"只看所选文件"。
                if (!wholeTree && !string.IsNullOrEmpty(context.CommonRoot))
                    output = FilterDiffToPaths(output, targets, context.CommonRoot);

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
                    using (var key = Registry.LocalMachine.OpenSubKey(keyPath))
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
            return PathLocator.WhereExists("svn.exe") ? "svn.exe" : null;
        }

        /// <summary>同步执行 svn 子命令（diff/status 共用；短超时，失败返回 null 并带出 stderr，不影响生成）。</summary>
        private static string RunSvn(string svnExe, string arguments, out string errorMessage)
        {
            errorMessage = null;
            var startInfo = new ProcessStartInfo
            {
                FileName = svnExe,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // svn.exe 在 Windows 上按系统 ANSI 代码页输出（中文系统=GBK），
                // 按 UTF-8 解会把中文文件名解成乱码，导致 Index 路径匹配不上。
                // Encoding.Default = 系统 ANSI 代码页，系统开 UTF-8 时也自适应。
                StandardOutputEncoding = Encoding.Default,
                StandardErrorEncoding = Encoding.Default,
            };

            using (var process = Process.Start(startInfo))
            {
                if (process == null) return null;
                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();

                if (!process.WaitForExit(30000))
                {
                    BridgeProcessRunner.KillQuietly(process);
                    return null;
                }
                if (process.ExitCode != 0)
                {
                    errorMessage = stderrTask.Result;
                    return null;
                }
                return stdoutTask.Result;
            }
        }

        /// <summary>
        /// 把 svn diff 输出按 "Index: " 行分块，仅保留路径命中的块。
        /// targets 为勾选的绝对路径；Index 行可能是绝对路径（传绝对 target 时 svn 就这么输出，
        /// 如 "Index: E:/proj/a.cs"）也可能是相对路径，统一归一成绝对路径再比对。
        /// target 是目录时按前缀匹配（保留其下文件）。
        /// </summary>
        private static string FilterDiffToPaths(string diff, List<string> targets, string commonRoot)
        {
            if (targets == null || targets.Count == 0) return diff;

            var targetSet = targets
                .Select(NormalizeAbsPath)
                .Where(p => !string.IsNullOrEmpty(p))
                .ToList();
            if (targetSet.Count == 0) return diff;

            var kept = new StringBuilder();
            var currentBlock = new StringBuilder();
            string currentPath = null;
            bool anyMatch = false;

            Action flush = () =>
            {
                if (currentBlock.Length == 0) return;
                if (currentPath != null && PathMatches(currentPath, targetSet, commonRoot))
                {
                    anyMatch = true;
                    kept.Append(currentBlock);
                }
                currentBlock.Clear();
            };

            using (var reader = new StringReader(diff))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.StartsWith("Index: ", StringComparison.Ordinal))
                    {
                        flush();
                        currentPath = NormalizeDiffPath(line.Substring("Index: ".Length).Trim());
                    }
                    currentBlock.AppendLine(line);
                }
            }
            flush();

            // 全部被滤掉时返回空串（比误用全量 diff 更符合"只看所选"）
            return anyMatch ? kept.ToString() : string.Empty;
        }

        /// <summary>规范化 diff 里的文件路径：统一分隔符、去掉 git 前缀 a/ b/。</summary>
        private static string NormalizeDiffPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            path = path.Replace('/', '\\');
            // svn --git 的 Index 行不带 a/ 前缀，但稳妥起见剥掉常见前缀
            if (path.StartsWith("a\\", StringComparison.Ordinal) || path.StartsWith("b\\", StringComparison.Ordinal))
                path = path.Substring(2);
            return path.TrimStart('\\');
        }

        /// <summary>路径归一为规范绝对路径（统一分隔符、展开 ..）；失败返回原样。</summary>
        private static string NormalizeAbsPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            path = path.Replace('/', '\\');
            try { return Path.GetFullPath(path).TrimEnd('\\'); }
            catch (Exception) { return path; }
        }

        /// <summary>判断 diff 中的路径是否命中勾选路径（统一转绝对路径后精确或目录前缀匹配）。</summary>
        private static bool PathMatches(string diffPath, List<string> absTargets, string commonRoot)
        {
            string diffAbs = NormalizeDiffPath(diffPath);
            if (!Path.IsPathRooted(diffAbs) && !string.IsNullOrEmpty(commonRoot))
                diffAbs = Path.Combine(commonRoot, diffAbs);
            diffAbs = NormalizeAbsPath(diffAbs);

            foreach (var target in absTargets)
            {
                if (string.Equals(diffAbs, target, StringComparison.OrdinalIgnoreCase))
                    return true;
                // target 是目录：保留其下所有文件
                if (diffAbs.StartsWith(target + "\\", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        // Windows 命令行引号规则（CommandLineToArgvW）：
        //   - 反斜杠不是转义符，绝不能翻倍（翻倍会让 svn 把参数解析坏，报 E195002）；
        //   - 只有紧邻引号的连续反斜杠才需要翻倍；引号本身前加一个反斜杠。
        private static string QuoteArg(string arg)
        {
            if (arg == null) return "\"\"";
            bool needQuote = arg.Length == 0 || arg.IndexOfAny(new[] { ' ', '\t', '"' }) >= 0;
            if (!needQuote) return arg;

            var sb = new StringBuilder(arg.Length + 8);
            sb.Append('"');
            int backslashes = 0;
            foreach (var c in arg)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }
                if (c == '"')
                    sb.Append('\\', backslashes * 2 + 1);
                else
                    sb.Append('\\', backslashes);
                backslashes = 0;
                sb.Append(c);
            }
            sb.Append('\\', backslashes * 2); // 结尾反斜杠先翻倍再接收尾引号
            sb.Append('"');
            return sb.ToString();
        }
    }
}
