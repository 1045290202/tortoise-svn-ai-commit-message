// AI 提交信息生成插件 —— TortoiseSVN IBugtraqProvider COM 插件入口（COM 层，仅编排）。
// 注册后，提交对话框日志输入框右上角会出现一个按钮（由 GetLinkText 决定文案），
// 点击后 TortoiseSVN 调用 GetCommitMessage，用返回值回填日志框。
//
// 当前状态：agent 已接入 WorkBuddy CLI（实现见 Agent.WorkBuddyAgentClient），
// 生成失败或返回空时仍会保留用户已输入的内容。
// 插件实现 IBugTraqProvider2（commonURL 等附加信息可用；注意 v2 的 pathList 含义与 v1
// 相同，都是打开对话框时的选中路径——真正的勾选项由 Svn.CommitDialogListReader 同进程读取）。
using System;
using System.Runtime.InteropServices;
using Interop.BugTraqProvider;
using TsvnAiCommitMessage.Agent;
using TsvnAiCommitMessage.Svn;

namespace TsvnAiCommitMessage
{
    [ComVisible(true)]
    [Guid("A6F0E69F-1C6E-4C32-8FE3-A46F87825DFC")]
    [ClassInterface(ClassInterfaceType.None)]
    public class AiCommitMessageProvider : IBugTraqProvider2
    {
        private readonly IAgentClient agent = new WorkBuddyAgentClient();

        // TortoiseSVN 设置对话框里"测试"插件时调用；返回 false 则无法保存。
        public bool ValidateParameters(IntPtr hParentWnd, string parameters)
        {
            return true;
        }

        // 提交对话框日志框右上角按钮的文案。保持简短。
        public string GetLinkText(IntPtr hParentWnd, string parameters)
        {
            return "AI生成提交信息";
        }

        // 点击按钮时调用。返回值会整体替换日志框内容：
        // 生成失败或为空时必须原样返回 originalMessage，否则会丢掉用户已输入的内容。
        public string GetCommitMessage(IntPtr hParentWnd, string parameters, string commonRoot, string[] pathList, string originalMessage)
        {
            // TortoiseSVN 传给插件的 pathList 是打开提交对话框时选中的路径（Explorer 选区，
            // 例如整个工作副本目录），不是对话框里勾选的文件（TortoiseSVN 源码 CommitDlg.cpp
            // 中 OnBnClickedBugtraqbutton 用 m_pathList 证实；勾选项只在提交时的 CheckCommit
            // 才提供）。插件与 TortoiseProc 同进程，点击时直接读列表控件拿勾选项；
            // 读取失败时退回原始 pathList。
            var effectivePaths = CommitDialogListReader.TryReadCheckedPaths(hParentWnd, commonRoot)
                ?? pathList
                ?? Array.Empty<string>();

            var context = new CommitContext
            {
                CommonRoot = commonRoot,
                PathList = effectivePaths,
                OriginalMessage = originalMessage,
            };

            string generated;
            try
            {
                generated = agent.GenerateCommitMessage(context);
            }
            catch (Exception)
            {
                // 插件跑在 TortoiseProc.exe 进程内，任何异常都不能带崩提交对话框。
                return originalMessage;
            }

            if (string.IsNullOrEmpty(generated))
                return originalMessage;

            return generated;
        }

        // v2 接口：TortoiseSVN 检测到插件实现了 IBugTraqProvider2 后会优先调用这里，
        // 并额外传入 commonURL（所选路径的共同仓库 URL）。pathList 含义与 v1 相同，
        // 生成逻辑统一走 GetCommitMessage。
        public string GetCommitMessage2(IntPtr hParentWnd, string parameters, string commonURL, string commonRoot,
            string[] pathList, string originalMessage, string bugID, out string bugIDOut,
            out string[] revPropNames, out string[] revPropValues)
        {
            bugIDOut = null;
            revPropNames = Array.Empty<string>();
            revPropValues = Array.Empty<string>();
            return GetCommitMessage(hParentWnd, parameters, commonRoot, pathList, originalMessage);
        }

        // 提交前校验钩子：不拦截，原样放行。
        public string CheckCommit(IntPtr hParentWnd, string parameters, string commonURL, string commonRoot,
            string[] pathList, string commitMessage)
        {
            return commitMessage;
        }

        // 提交完成回调：对日志内容不做修改，原样返回。
        public string OnCommitFinished(IntPtr hParentWnd, string commonRoot, string[] pathList,
            string logMessage, int revision)
        {
            return logMessage;
        }

        // 没有配置界面，TortoiseSVN 设置页不显示"选项"按钮。
        public bool HasOptions()
        {
            return false;
        }

        // HasOptions 为 false 时不会被调用，返回原参数兜底。
        public string ShowOptionsDialog(IntPtr hParentWnd, string parameters)
        {
            return parameters;
        }
    }
}
