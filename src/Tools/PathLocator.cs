// 外部依赖定位：node.exe、桥接脚本。
// 探测顺序都带兜底，任何单个候选异常只跳过该项，不让定位过程抛出。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace TsvnAiCommitMessage.Tools
{
    internal static class PathLocator
    {
        /// <summary>按优先级探测可用的 node.exe；找不到返回 null。</summary>
        public static string FindNodeExe()
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

        /// <summary>判断 fileName 是否在 PATH 的某个目录下存在。</summary>
        public static bool WhereExists(string fileName)
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
        public static string FindBridgeScript()
        {
            try
            {
                var dllDir = Path.GetDirectoryName(typeof(PathLocator).Assembly.Location);
                if (dllDir == null) return null;
                var script = Path.Combine(dllDir, "agent-bridge", "codebuddy-bridge.js");
                return File.Exists(script) ? script : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
