// AI 生成提交信息 —— 流式进度弹窗
// 模态显示在提交对话框之上，实时滚动 AI 思考/回答内容，支持取消。
// 只依赖 System.Windows.Forms / System.Drawing（net48 自带）。
using System;
using System.Drawing;
using System.Media;
using System.Windows.Forms;

namespace TsvnAiCommitMessage
{
    internal class GenerationDialog : Form
    {
        private readonly RichTextBox _log;
        private readonly Label _status;
        private readonly Button _cancelButton;
        private readonly Timer _timer;
        private readonly DateTime _startedAt = DateTime.Now;

        private bool _finished;
        private bool _thinkingSectionStarted;
        private bool _answerSectionStarted;

        /// <summary>成功生成时为提交信息；失败/取消时为 null。</summary>
        public string ResultMessage { get; private set; }

        /// <summary>失败原因（仅失败时有值）。</summary>
        public string ErrorMessage { get; private set; }

        /// <summary>用户是否取消了生成。</summary>
        public bool Cancelled { get; private set; }

        public GenerationDialog()
        {
            Text = "AI生成提交信息";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = false;
            Font = new Font("Microsoft YaHei UI", 9F);
            ClientSize = new Size(560, 380);

            _log = new RichTextBox
            {
                ReadOnly = true,
                Dock = DockStyle.Fill,
                BackColor = Color.White,
                BorderStyle = BorderStyle.None,
                WordWrap = true,
                DetectUrls = false,
            };

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(12, 10, 12, 8) };
            _status = new Label
            {
                Text = "正在生成…",
                AutoSize = true,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.DimGray,
            };
            _cancelButton = new Button
            {
                Text = "取消",
                DialogResult = DialogResult.Cancel,
                Dock = DockStyle.Right,
                Width = 90,
            };
            _cancelButton.Click += (s, e) => SetCancelled();
            bottom.Controls.Add(_status);
            bottom.Controls.Add(_cancelButton);

            Controls.Add(_log);
            Controls.Add(bottom);

            CancelButton = _cancelButton;
            FormClosing += (s, e) =>
            {
                if (!_finished) SetCancelled();
            };

            // 每秒刷新用时
            _timer = new Timer { Interval = 1000 };
            _timer.Tick += (s, e) => UpdateStatus();
            _timer.Start();

            AppendColored("AI 正在分析变更内容…\n", Color.DimGray);
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

        public void Complete(string message)
        {
            _finished = true;
            ResultMessage = message;
            RunOnUi(() =>
            {
                _timer.Stop();
                SystemSounds.Asterisk.Play();
                Close();
            });
        }

        public void Fail(string error)
        {
            _finished = true;
            ErrorMessage = error;
            RunOnUi(() =>
            {
                _timer.Stop();
                SystemSounds.Hand.Play();
                Close();
            });
        }

        // ── 内部 ────────────────────────────────────────────────────────

        private void SetCancelled()
        {
            _finished = true;
            Cancelled = true;
            _timer.Stop();
        }

        private void UpdateStatus()
        {
            var seconds = (int)(DateTime.Now - _startedAt).TotalSeconds;
            _status.Text = string.Format("正在生成… 已用时 {0}s（完成后自动填入日志框）", seconds);
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
            _log.SelectionStart = _log.TextLength;
            _log.SelectionLength = 0;
            _log.SelectionColor = color;
            _log.AppendText(text);
            _log.SelectionStart = _log.TextLength;
            _log.ScrollToCaret();
        }
    }
}
