// AI 生成提交信息 —— 流式进度弹窗
// 布局控件在下方 InitializeComponent（Rider/VS 设计器序列化目标就是本文件，可调）。
// 流程：生成中实时滚动思考/结果；完成后窗体停留，由用户点「填入日志框」确认，
//       或点「取消 / 关闭」放弃（保留提交框原输入）。
// 只依赖 System.Windows.Forms / System.Drawing（net48 自带）。
using System;
using System.Drawing;
using System.Media;
using System.Windows.Forms;

namespace TsvnAiCommitMessage
{
    internal class GenerationDialog : Form
    {
        private System.ComponentModel.IContainer components = null;

        private System.Windows.Forms.RichTextBox logBox;
        private System.Windows.Forms.Panel bottomPanel;
        private System.Windows.Forms.Label statusLabel;
        private System.Windows.Forms.Button insertButton;
        private System.Windows.Forms.Button cancelButton;
        private System.Windows.Forms.Timer uiTimer;

        private readonly DateTime _startedAt = DateTime.Now;

        private bool _finished;               // true 后不再视为取消
        private bool _thinkingSectionStarted;
        private bool _answerSectionStarted;
        private string _currentStep = "正在分析变更内容"; // 底部状态栏当前步骤文案

        /// <summary>用户点「填入日志框」后的提交信息；其余情况为 null。</summary>
        public string ResultMessage { get; private set; }

        /// <summary>失败原因（仅失败时有值，用于无 UI 日志排查）。</summary>
        public string ErrorMessage { get; private set; }

        /// <summary>生成过程中被用户取消（区别于失败）。</summary>
        public bool Cancelled { get; private set; }

        /// <summary>用户取消/关闭窗体时触发（UI 线程），供宿主立即打断后台生成。</summary>
        public event Action CancelRequested;

        public GenerationDialog()
        {
            InitializeComponent();
            AppendColored("AI 正在分析变更内容…\n", Color.DimGray);
            uiTimer.Start();
        }

        /// <summary>清理所有正在使用的资源。</summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
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

        /// <summary>工作线程报告一个大步骤的开始（日志区打一行 + 底部状态栏同步）。</summary>
        public void SetStep(string text)
        {
            RunOnUi(() =>
            {
                if (string.IsNullOrEmpty(text)) return;
                _currentStep = text.TrimEnd('…', '。');
                AppendColored("▶ " + text + "\n", Color.RoyalBlue);
            });
        }

        /// <summary>生成成功：窗体停留，等用户点「填入日志框」确认。</summary>
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

        // ── 事件处理（设计器绑定） ─────────────────────────────────────

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
            statusLabel.Text = string.Format("{0}… 已用时 {1}s", _currentStep, seconds);
        }

        private void GenerationDialog_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (!_finished)
                SetCancelled(); // 生成中直接关窗 = 取消
        }

        // ── 内部 ────────────────────────────────────────────────────────

        private void SetCancelled()
        {
            if (Cancelled) return;
            _finished = true;
            Cancelled = true;
            uiTimer.Stop();
            var handler = CancelRequested;
            if (handler != null) handler();
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

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            this.components = new System.ComponentModel.Container();
            this.logBox = new System.Windows.Forms.RichTextBox();
            this.bottomPanel = new System.Windows.Forms.Panel();
            this.statusLabel = new System.Windows.Forms.Label();
            this.insertButton = new System.Windows.Forms.Button();
            this.cancelButton = new System.Windows.Forms.Button();
            this.uiTimer = new System.Windows.Forms.Timer(this.components);
            this.bottomPanel.SuspendLayout();
            this.SuspendLayout();
            //
            // logBox
            //
            this.logBox.BackColor = System.Drawing.Color.White;
            this.logBox.DetectUrls = false;
            this.logBox.Dock = System.Windows.Forms.DockStyle.Fill;
            this.logBox.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F);
            this.logBox.Location = new System.Drawing.Point(10, 8);
            this.logBox.Margin = new System.Windows.Forms.Padding(3, 2, 3, 2);
            this.logBox.Name = "logBox";
            this.logBox.ReadOnly = true;
            this.logBox.Size = new System.Drawing.Size(564, 319);
            this.logBox.TabIndex = 0;
            this.logBox.Text = "";
            //
            // bottomPanel
            //
            this.bottomPanel.Controls.Add(this.statusLabel);
            this.bottomPanel.Controls.Add(this.insertButton);
            this.bottomPanel.Controls.Add(this.cancelButton);
            this.bottomPanel.Dock = System.Windows.Forms.DockStyle.Bottom;
            this.bottomPanel.Location = new System.Drawing.Point(10, 327);
            this.bottomPanel.Margin = new System.Windows.Forms.Padding(3, 2, 3, 2);
            this.bottomPanel.Name = "bottomPanel";
            this.bottomPanel.Padding = new System.Windows.Forms.Padding(0, 6, 0, 6);
            this.bottomPanel.Size = new System.Drawing.Size(564, 44);
            this.bottomPanel.TabIndex = 1;
            //
            // statusLabel
            //
            this.statusLabel.AutoSize = true;
            this.statusLabel.Dock = System.Windows.Forms.DockStyle.Fill;
            this.statusLabel.ForeColor = System.Drawing.Color.DimGray;
            this.statusLabel.Location = new System.Drawing.Point(0, 6);
            this.statusLabel.Name = "statusLabel";
            this.statusLabel.Size = new System.Drawing.Size(65, 12);
            this.statusLabel.TabIndex = 0;
            this.statusLabel.Text = "正在生成…";
            this.statusLabel.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            //
            // insertButton
            //
            this.insertButton.Dock = System.Windows.Forms.DockStyle.Right;
            this.insertButton.Enabled = false;
            this.insertButton.Location = new System.Drawing.Point(370, 6);
            this.insertButton.Margin = new System.Windows.Forms.Padding(3, 2, 3, 2);
            this.insertButton.Name = "insertButton";
            this.insertButton.Size = new System.Drawing.Size(110, 32);
            this.insertButton.TabIndex = 1;
            this.insertButton.Text = "填入日志框";
            this.insertButton.UseVisualStyleBackColor = true;
            this.insertButton.Click += new System.EventHandler(this.insertButton_Click);
            //
            // cancelButton
            //
            this.cancelButton.DialogResult = System.Windows.Forms.DialogResult.Cancel;
            this.cancelButton.Dock = System.Windows.Forms.DockStyle.Right;
            this.cancelButton.Location = new System.Drawing.Point(488, 6);
            this.cancelButton.Margin = new System.Windows.Forms.Padding(3, 2, 3, 2);
            this.cancelButton.Name = "cancelButton";
            this.cancelButton.Size = new System.Drawing.Size(76, 32);
            this.cancelButton.TabIndex = 2;
            this.cancelButton.Text = "取消";
            this.cancelButton.UseVisualStyleBackColor = true;
            this.cancelButton.Click += new System.EventHandler(this.cancelButton_Click);
            //
            // uiTimer
            //
            this.uiTimer.Interval = 1000;
            this.uiTimer.Tick += new System.EventHandler(this.uiTimer_Tick);
            //
            // GenerationDialog
            //
            this.AcceptButton = this.insertButton;
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 12F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.CancelButton = this.cancelButton;
            this.ClientSize = new System.Drawing.Size(584, 381);
            this.Controls.Add(this.logBox);
            this.Controls.Add(this.bottomPanel);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
            this.Margin = new System.Windows.Forms.Padding(3, 2, 3, 2);
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Name = "GenerationDialog";
            this.Padding = new System.Windows.Forms.Padding(10, 8, 10, 8);
            this.ShowInTaskbar = false;
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Text = "AI生成提交信息";
            this.FormClosing += new System.Windows.Forms.FormClosingEventHandler(this.GenerationDialog_FormClosing);
            this.bottomPanel.ResumeLayout(false);
            this.bottomPanel.PerformLayout();
            this.ResumeLayout(false);
        }
    }
}
