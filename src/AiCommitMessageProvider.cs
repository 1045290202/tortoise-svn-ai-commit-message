// AI 提交信息生成插件 —— TortoiseSVN IBugtraqProvider COM 插件
// 注册后，提交对话框日志输入框右上角会出现一个按钮（由 GetLinkText 决定文案），
// 点击后 TortoiseSVN 调用 GetCommitMessage，用返回值回填日志框。
//
// 当前状态：agent 接入接口已预留（IAgentClient），实现为空占位，
// 未接入前点击按钮原样保留用户已输入的内容。
using System;
using System.Runtime.InteropServices;
using Interop.BugTraqProvider;

namespace TsvnAiCommitMessage
{
    /// <summary>
    /// 提交给 agent 的上下文。后续接入真实 agent 时按需扩展字段
    /// （例如在此处补上 svn diff 内容——插件进程内可直接调用 svn.exe 获取）。
    /// </summary>
    public class CommitContext
    {
        /// <summary>本次提交所选变更项的共同根目录。</summary>
        public string CommonRoot { get; set; }

        /// <summary>本次提交的路径列表（只有路径，不含 diff 内容）。</summary>
        public string[] PathList { get; set; }

        /// <summary>用户在日志框中已输入的内容。</summary>
        public string OriginalMessage { get; set; }
    }

    /// <summary>
    /// agent 接入点。未想好接入方式前先留此接口：
    /// 接入时实现一个 IAgentClient（HTTP 调本地/远端服务、CLI 拉起等均可），
    /// 替换 Provider 里的占位实现即可，COM 层不用动。
    /// 返回 null/空字符串表示生成失败，插件会保留用户原有输入。
    /// </summary>
    public interface IAgentClient
    {
        string GenerateCommitMessage(CommitContext context);
    }

    /// <summary>占位实现：未接入 agent，永远返回 null。</summary>
    internal class NullAgentClient : IAgentClient
    {
        public string GenerateCommitMessage(CommitContext context)
        {
            // TODO: 在此接入真实 agent（接口先空着）。
            return null;
        }
    }

    [ComVisible(true)]
    [Guid("A6F0E69F-1C6E-4C32-8FE3-A46F87825DFC")]
    [ClassInterface(ClassInterfaceType.None)]
    public class AiCommitMessageProvider : IBugTraqProvider
    {
        private readonly IAgentClient agent = new NullAgentClient();

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
            var context = new CommitContext
            {
                CommonRoot = commonRoot,
                PathList = pathList ?? new string[0],
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
    }
}
