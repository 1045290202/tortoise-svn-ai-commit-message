// AI 生成提交信息 —— 流式进度弹窗（行为逻辑）
// 布局控件在 GenerationDialog.Designer.cs 的 InitializeComponent 里，设计器可见可调。
// 流程：生成中实时滚动思考/结果；完成后窗体停留，由用户点「填入日志框」确认，
//       或点「取消 / 关闭」放弃（保留提交框原输入）。
// 只依赖 System.Windows.Forms / System.Drawing（net48 自带）。
using System;
using System.Drawing;
using System.Media;
using System.Windows.Forms;

namespace TsvnAiCommitMessage
{
    internal partial class GenerationDialog : Form
    {
        private readonly DateTime _startedAt = DateTime.Now;

        private bool _finished;               // true 后不再视为取消
        private bool _thinkingSectionStarted;
        private bool _answerSectionStarted;

        /// <summary>用户点「填入日志框」后的提交信息；其余情况为 null。</summary>
        public string ResultMessage { get; private set; }

        /// <summary>失败原因（仅失败时有值，用于无 UI 日志排查）。</summary>
        public string ErrorMessage { get; private set; }

        /// <summary>生成过程中被用户取消（区别于失败）。</summary>
        public bool Cancelled { get; private set; }

        public GenerationDialog()
        {
            InitializeComponent();
            AppendColored("AI 正在分析变更内容…\n", Color.DimGray);
            uiTimer.Start();
        }

        // ── 工作线程调用入口（内部自行跨线程封送） ────────────────────

        public void AppendThinking(string text)
        {
            if (!_thinkingSectionStarted)
            {
                _thinkingSectionStarted = true;
                AppendColoredSafe("\n──── AI 思考 ────\n", Color.SteelBlue);
            }
            AppendColoredSafe(text, Color.Gray);
        }

        public void AppendAnswer(string text)
        {
            if (!_answerSectionStarted)
            {
                _answerSectionStarted = true;
                AppendColoredSafe("\n──── 生成结果 ────\n", Color.SteelBlue);
            }
            AppendColoredSafe(text, Color.Black);
        }

        /// <summary>生成成功：窗体不停留关闭，等用户确认后手动填入。</summary>
        public void Complete(string message)
        {
            RunOnUi(() =>
            {
                _finished = true;
                ResultMessage = message;
                uiTimer.Stop();
                statusLabel.ForeColor = Color.SeaGreen;
                statusLabel.Text = "生成完成，确认无误后点击「填入日志框」";
                insertButton.Enabled = true;
                AcceptButton = insertButton;
                cancelButton.Text = "取消（不填入）";
                SystemSounds.Asterisk.Play();
            });
        }

        /// <summary>生成失败：窗体停留展示错误，只能关闭。</summary>
        public void Fail(string error)
        {
            RunOnUi(() =>
            {
                _finished = true;
                ErrorMessage = error;
                uiTimer.Stop();
                statusLabel.ForeColor = Color.Firebrick;
                statusLabel.Text = string.IsNullOrEmpty(error) ? "生成失败" : "生成失败：" + error;
                AppendColoredSafe("\n[生成失败] " + statusLabel.Text + "\n", Color.Firebrick);
                insertButton.Enabled = false;
                cancelButton.Text = "关闭";
                SystemSounds.Hand.Play();
            });
        }

        // ── 事件处理（Designer 绑定） ──────────────────────────────────

        private void insertButton_Click(object sender, EventArgs e)
        {
            _finished = true; // 正常路径关闭，不算取消
            DialogResult = DialogResult.OK;
            Close();
        }

        private void cancelButton_Click(object sender, EventArgs e)
        {
            SetCancelled();
            DialogResult = DialogResult.Cancel;
            Close();
        }

        private void uiTimer_Tick(object sender, EventArgs e)
        {
            if (_finished) { uiTimer.Stop(); return; }
            var seconds = (int)(DateTime.Now - _startedAt).TotalSeconds;
            statusLabel.Text = string.Format("正在生成… 已用时 {0}s（完成后可先预览再填入）", seconds);
        }

        private void GenerationDialog_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (!_finished)
                SetCancelled(); // 生成中直接关窗 = 取消
        }

        // ── 内部 ────────────────────────────────────────────────────────

        private void SetCancelled()
        {
            _finished = true;
            Cancelled = true;
            uiTimer.Stop();
        }

        private void AppendColoredSafe(string text, Color color)
        {
            RunOnUi(() => AppendColored(text, color));
        }

        private void RunOnUi(Action action)
        {
            try
            {
                if (IsDisposed || Disposing) return;
                if (InvokeRequired) BeginInvoke(action);
                else action();
            }
            catch (ObjectDisposedException) { /* 窗体已关，忽略 */ }
            catch (InvalidOperationException) { /* 句柄未建/已销毁，忽略 */ }
        }

        private void AppendColored(string text, Color color)
        {
            if (string.IsNullOrEmpty(text)) return;
            logBox.SelectionStart = logBox.TextLength;
            logBox.SelectionLength = 0;
            logBox.SelectionColor = color;
            logBox.AppendText(text);
            logBox.SelectionStart = logBox.TextLength;
            logBox.ScrollToCaret();
        }
    }
}
