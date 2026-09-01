// WorkBuddy CLI 桥接 agent 实现 —— IAgentClient 的桥接版入口（流程编排）。
//
// 链路：插件(TortoiseProc 进程内) → 立即弹出流式进度窗体 → 后台线程获取 svn diff
//       → 拉起 node 跑 agent-bridge/codebuddy-bridge.js（执行细节见 BridgeProcessRunner）
//       → 桥接脚本调 WorkBuddy CLI（codebuddy -p --output-format stream-json）→ 逐行解析事件
//       → 弹窗实时显示思考/回答 → 用户点「填入日志框」确认后回填。
//
// 失败策略：任何环节出错（含用户取消）都返回 null，插件保留用户已输入的内容。
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using TsvnAiCommitMessage.Svn;
using TsvnAiCommitMessage.Tools;
using TsvnAiCommitMessage.UI;

namespace TsvnAiCommitMessage.Agent
{
    internal class WorkBuddyAgentClient : IAgentClient
    {
        // 桥接进程超时（CLI 侧自身超时 180s 会先发 error，这里只作进程级兜底）
        private const int BridgeTimeoutMs = 200000;

        public string GenerateCommitMessage(CommitContext context)
        {
            try
            {
                var state = new BridgeState();
                GenerationDialog dialog = TryCreateDialog();

                if (dialog != null)
                {
                    // 顶部展示本次勾选的待提交文件列表（主线程调用，先于 ShowDialog）
                    dialog.SetCommitFiles(context.CommonRoot, context.PathList);

                    // 用户取消/关窗时立即打断：杀掉桥接进程，不等下一行输出
                    dialog.CancelRequested += () =>
                    {
                        state.Cancelled = true;
                        var p = state.Process;
                        if (p != null) BridgeProcessRunner.KillQuietly(p);
                    };
                }

                // 后台线程跑全部耗时准备（node/svn diff 定位、svn diff、序列化）+ 桥接；
                // 主线程立刻 ShowDialog，避免 TortoiseProc UI 因 svn diff 卡死。
                // 生成完成后窗体停留，由用户点「填入日志框」确认才返回结果。
                var worker = Task.Run(() =>
                {
                    try
                    {
                        dialog?.SetStep("正在定位 node.exe 与桥接脚本…");
                        string nodeExe = PathLocator.FindNodeExe();
                        string bridgeJs = PathLocator.FindBridgeScript();
                        if (nodeExe == null || bridgeJs == null)
                        {
                            state.Error = nodeExe == null
                                ? "未找到可用的 node.exe"
                                : "未找到桥接脚本 codebuddy-bridge.js";
                            dialog?.Fail(state.Error);
                            return;
                        }

                        dialog?.SetStep("正在获取 svn diff…");
                        string diff = SvnDiffProvider.TryGetDiff(context);
                        dialog?.SetStep(diff.Length > 0
                            ? $"diff 获取完成（{diff.Length} 字符），正在打包请求…"
                            : "未获取到 diff，仅按路径生成，正在打包请求…");

                        var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                        string requestJson = serializer.Serialize(new Dictionary<string, object>
                        {
                            { "commonRoot", context.CommonRoot ?? "" },
                            { "pathList", context.PathList ?? Array.Empty<string>() },
                            { "diff", diff },
                            { "originalMessage", context.OriginalMessage ?? "" },
                        });

                        dialog?.SetStep("正在启动 AI 桥接进程…");
                        BridgeProcessRunner.Run(nodeExe, bridgeJs, requestJson, state, dialog);
                    }
                    catch (Exception e)
                    {
                        state.Error = state.Error ?? ("生成准备失败: " + e.Message);
                        try { dialog?.Fail(state.Error); } catch (Exception) { /* ignore */ }
                    }
                });

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

                // 有 UI 时窗体已关（用户操作结束），只给工作线程 15s 收尾；
                // 无 UI 时需完整等待：diff（≤30s）+ 桥接超时，多留余量。
                worker.Wait(shown ? 15000 : 30000 + BridgeTimeoutMs + 30000);

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
    }
}
