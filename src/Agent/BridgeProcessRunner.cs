// 桥接进程执行与流式事件解析。
// 输入：node 拉起 agent-bridge/codebuddy-bridge.js，stdin 一次性写请求 JSON；
// 输出：stdout 逐行 JSON 事件（delta/done/error），边读边更新 BridgeState、推送弹窗。
//
// 关键坑（已在桥接脚本内规避，这里只做说明）：
//   CLI 内部服务默认监听 127.0.0.1:10003，与 WorkBuddy 桌面端冲突时进程会静默挂死，
//   桥接脚本通过 SERVER__PORT 随机空闲端口规避。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Web.Script.Serialization;
using TsvnAiCommitMessage.UI;

namespace TsvnAiCommitMessage.Agent
{
    /// <summary>桥接调用过程中的共享状态（工作线程写，UI 线程读）。</summary>
    internal class BridgeState
    {
        public volatile bool Cancelled;
        public bool Success;
        public string Result;
        public string Error;
        public Process Process;
    }

    internal static class BridgeProcessRunner
    {
        /// <summary>拉起桥接进程、写入请求并消费事件流。完成后通过 dialog/state 反馈结果。</summary>
        public static void Run(string nodeExe, string bridgeJs, string requestJson,
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
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding = System.Text.Encoding.UTF8,
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

                    dialog?.SetStep("已启动，等待 AI 输出…");

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

        /// <summary>解析一行桥接事件并分发到弹窗/状态。</summary>
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

        /// <summary>尽力终止进程；已退出或句柄失效时静默忽略。</summary>
        public static void KillQuietly(Process process)
        {
            try
            {
                if (!process.HasExited) process.Kill();
            }
            catch (Exception) { /* 已退出则忽略 */ }
        }
    }
}
