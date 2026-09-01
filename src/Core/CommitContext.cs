// 生成链路的公共数据契约：提交上下文 + agent 抽象。
// Plugin（COM 层）负责收集上下文；IAgentClient 实现负责消费并产出提交信息。
namespace TsvnAiCommitMessage
{
    /// <summary>
    /// 提交给 agent 的上下文。后续接入真实 agent 时按需扩展字段
    /// （diff 内容由 Svn.SvnDiffProvider 单独获取，不随此结构传递）。
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
    /// agent 接入点：
    /// 接入时实现一个 IAgentClient（HTTP 调本地/远端服务、CLI 拉起等均可），
    /// 替换 Provider 里的实现即可，COM 层不用动。
    /// 返回 null/空字符串表示生成失败，插件会保留用户原有输入。
    /// </summary>
    public interface IAgentClient
    {
        string GenerateCommitMessage(CommitContext context);
    }
}
