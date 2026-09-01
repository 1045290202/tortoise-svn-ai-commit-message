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
using TsvnAiCommitMessage.Common;
using TsvnAiCommitMessage.Tools;

namespace TsvnAiCommitMessage.Svn
{
    internal static class SvnDiffProvider
    {
        // diff 内容上限，超出截断，避免超长 diff 拖垮请求
        private const int MaxDiffChars = 120000;

        /// <summary>获取变更内容；svn 不可用或执行失败时返回空字符串（不阻塞生成）。
        /// 结果按 svn diff 的 Index: 分块过滤，只保留本次勾选提交的路径。</summary>
        public static string TryGetDiff(CommitContext context)
        {
            try
            {
                string svnExe = FindSvnExe();
                if (svnExe == null) return string.Empty;

                // 优先只对本次提交所选路径做 diff；路径为空则 diff 整个共同根目录
                bool wholeTree = false;
                var targets = new List<string>();
                if (context.PathList != null && context.PathList.Length > 0)
                    targets.AddRange(context.PathList);
                else if (!string.IsNullOrEmpty(context.CommonRoot))
                {
                    targets.Add(context.CommonRoot);
                    wholeTree = true;
                }
                if (targets.Count == 0) return string.Empty;

                var args = new StringBuilder("diff --git --internal-diff");
                foreach (var target in targets)
                    args.Append(' ').Append(QuoteArg(target));

                string output = RunSvnDiff(svnExe, args.ToString());
                if (string.IsNullOrEmpty(output)) return string.Empty;

                // 勾选项里若含目录，svn diff 会递归带出未勾选文件的变更；
                // 按 Index: 分块过滤，只保留勾选路径命中的块，确保"只看所选文件"。
                if (!wholeTree && !string.IsNullOrEmpty(context.CommonRoot))
                {
                    var relPaths = targets
                        .Select(t => PathUtil.MakeRelative(context.CommonRoot, t))
                        .Where(p => !string.IsNullOrEmpty(p))
                        .ToList();
                    output = FilterDiffToPaths(output, relPaths);
                }

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

        /// <summary>同步执行 svn diff（短超时，失败返回 null 不影响生成）。</summary>
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
                    BridgeProcessRunner.KillQuietly(process);
                    return null;
                }
                if (process.ExitCode != 0) return null;
                return stdoutTask.Result;
            }
        }

        /// <summary>
        /// 把 svn diff 输出按 "Index: " 行分块，仅保留路径命中的块。
        /// relPaths 为相对 CommonRoot 的勾选路径；目录条目按前缀匹配（保留其下文件）。
        /// </summary>
        private static string FilterDiffToPaths(string diff, List<string> relPaths)
        {
            if (relPaths == null || relPaths.Count == 0) return diff;

            var kept = new StringBuilder();
            var currentBlock = new StringBuilder();
            string currentPath = null;
            bool anyMatch = false;

            Action flush = () =>
            {
                if (currentBlock.Length == 0) return;
                if (currentPath != null && PathMatches(currentPath, relPaths))
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

        /// <summary>判断 diff 中的相对路径是否命中勾选路径（精确或目录前缀）。</summary>
        private static bool PathMatches(string diffPath, List<string> relPaths)
        {
            foreach (var rel in relPaths)
            {
                var relNorm = rel.Replace('/', '\\').TrimEnd('\\');
                if (string.IsNullOrEmpty(relNorm)) continue;

                if (string.Equals(diffPath, relNorm, StringComparison.OrdinalIgnoreCase))
                    return true;
                // relPath 是目录：保留其下所有文件
                if (diffPath.StartsWith(relNorm + "\\", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static string QuoteArg(string arg)
        {
            if (arg == null) return "\"\"";
            return "\"" + arg.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }
    }
}
