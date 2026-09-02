// AI 生成提交信息 —— 流式进度弹窗（仅 UI，生成流程见 Agent.WorkBuddyAgentClient）。
// 布局控件在下方 InitializeComponent（Rider/VS 设计器序列化目标就是本文件，可调）。
// 流程：生成中实时滚动思考/结果；完成后窗体停留，由用户点「填入日志框」确认，
//       或点「取消 / 关闭」放弃（保留提交框原输入）。
// 只依赖 System.Windows.Forms / System.Drawing（net48 自带）。
// 主题：配色取自 DialogTheme（跟随 TortoiseSVN 设置里的「深色主题」开关）；
//       不随主题变的配置（FlatStyle/边框/字体/布局）全部在 InitializeComponent，
//       ApplyTheme 只覆盖颜色，保证设计器可见完整静态样式；
//       按钮模仿 TSVN 本体深色按钮（两枚同款：深灰底 + 细描边，无 accent 主次色），
//       并用自绘 RoundedButton 画出 TSVN 风格的圆角描边（系统标准按钮是直角的）。
// 布局：mainSplit 上（文件列表）下（日志）可拖分隔条，窗口可缩放，比例跟随变化。
// 警告：Rider 设计器不要打开本窗体（旧缓冲保存会整文件回写，多次冲掉改动）。
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Media;
using System.Windows.Forms;
using TsvnAiCommitMessage.Common;

namespace TsvnAiCommitMessage.UI
{
    internal class GenerationDialog : Form
    {
        private System.ComponentModel.IContainer components = null;

        private System.Windows.Forms.SplitContainer mainSplit;
        private System.Windows.Forms.RichTextBox logBox;
        private System.Windows.Forms.Panel fileListPanel;
        private System.Windows.Forms.Label fileListLabel;
        private System.Windows.Forms.ListBox fileList;
        private System.Windows.Forms.Panel bottomPanel;
        private System.Windows.Forms.Label statusLabel;
        private System.Windows.Forms.Button insertButton;
        private System.Windows.Forms.Button cancelButton;
        private System.Windows.Forms.Timer uiTimer;

        private readonly DateTime _startedAt = DateTime.Now;
        private readonly DialogTheme _theme;

        private bool _finished;               // true 后不再视为取消
        private bool _thinkingSectionStarted;
        private bool _answerSectionStarted;
        private string _currentStep = "正在分析变更内容"; // 底部状态栏当前步骤文案

        public GenerationDialog()
        {
            _theme = DialogTheme.Resolve();
            InitializeComponent();
            ApplyTheme();
            AppendColored("AI 正在分析变更内容…\n", _theme.Colors.TextSecondary);
            uiTimer.Start();
        }

        /// <summary>用户点「填入日志框」后的提交信息；其余情况为 null。</summary>
        public string ResultMessage { get; private set; }

        /// <summary>失败原因（仅失败时有值，用于无 UI 日志排查）。</summary>
        public string ErrorMessage { get; private set; }

        /// <summary>生成过程中被用户取消（区别于失败）。</summary>
        public bool Cancelled { get; private set; }

        /// <summary>用户取消/关闭窗体时触发（UI 线程），供宿主立即打断后台生成。</summary>
        public event Action CancelRequested;

        /// <summary>清理所有正在使用的资源。</summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        // ── 主题 ────────────────────────────────────────────────────────

        /// <summary>按主题给控件配色（布局/样式骨架在 InitializeComponent；此处只动随主题变化的颜色）。</summary>
        private void ApplyTheme()
        {
            var c = _theme.Colors;
            bool dark = _theme.IsDark;

            Color formBack = dark ? Color.FromArgb(0x20, 0x20, 0x20) : SystemColors.Control;
            Color surface  = dark ? Color.FromArgb(0x1B, 0x1B, 0x1B) : Color.White;
            Color secondaryFore = dark ? c.TextSecondary : Color.DimGray;

            BackColor = formBack;
            mainSplit.BackColor = formBack; // 分隔条随窗体底色

            logBox.BackColor = surface;

            fileList.BackColor = surface;
            fileList.ForeColor = dark ? c.TextPrimary : SystemColors.WindowText;
            fileListLabel.ForeColor = secondaryFore;

            statusLabel.ForeColor = secondaryFore;

            // 按钮统一 TSVN 原生风：两枚同款（无 accent 主次色差），底色 + 细描边 + hover 微亮
            Color btnBack   = dark ? Color.FromArgb(0x41, 0x41, 0x41) : Color.White;
            Color btnBorder = dark ? Color.FromArgb(0x5A, 0x5A, 0x5A) : Color.FromArgb(0xAD, 0xAD, 0xAD);
            Color btnText   = dark ? c.TextPrimary : Color.FromArgb(0x33, 0x33, 0x33);
            Color btnHover  = dark ? Color.FromArgb(0x4D, 0x4D, 0x4D) : Color.FromArgb(0xE5, 0xE5, 0xE5);
            Color btnDown   = dark ? Color.FromArgb(0x58, 0x58, 0x58) : Color.FromArgb(0xD5, 0xD5, 0xD5);
            foreach (var btn in new[] { insertButton, cancelButton })
            {
                btn.FlatAppearance.BorderColor = btnBorder;
                btn.FlatAppearance.MouseOverBackColor = btnHover;
                btn.FlatAppearance.MouseDownBackColor = btnDown;
                btn.BackColor = btnBack;
                btn.ForeColor = btnText;
            }
        }

        /// <summary>深色模式下启用标题栏深色（DWM）。</summary>
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            _theme.ApplyDarkTitleBar(Handle);
        }

        // ── 对外接口 ────────────────────────────────────────────────────

        /// <summary>
        /// 展示本次待提交的文件列表（提交对话框中勾选的变更项）。
        /// 在 ShowDialog 之前于 UI 线程调用；pathList 为空时不显示该区域。
        /// </summary>
        public void SetCommitFiles(string commonRoot, string[] pathList)
        {
            if (pathList == null || pathList.Length == 0)
            {
                fileListPanel.Visible = false;
                mainSplit.Panel1Collapsed = true; // 无文件时折叠整个上区（含分隔条）
                return;
            }
            mainSplit.Panel1Collapsed = false;

            fileList.BeginUpdate();
            try
            {
                foreach (var path in pathList)
                    fileList.Items.Add(MakeDisplayPath(commonRoot, path));
            }
            finally
            {
                fileList.EndUpdate();
            }
            fileListLabel.Text = "待提交文件（" + pathList.Length + "）";
        }

        // ── 工作线程调用入口（内部自行跨线程封送） ────────────────────

        public void AppendThinking(string text)
        {
            if (!_thinkingSectionStarted)
            {
                _thinkingSectionStarted = true;
                AppendColoredSafe("\n──── AI 思考 ────\n", _theme.Colors.SectionHeader);
            }
            AppendColoredSafe(text, _theme.Colors.TextSecondary);
        }

        public void AppendAnswer(string text)
        {
            if (!_answerSectionStarted)
            {
                _answerSectionStarted = true;
                AppendColoredSafe("\n──── 生成结果 ────\n", _theme.Colors.SectionHeader);
            }
            AppendColoredSafe(text, _theme.Colors.TextPrimary);
        }

        /// <summary>工作线程报告一个大步骤的开始（日志区打一行 + 底部状态栏同步）。</summary>
        public void SetStep(string text)
        {
            RunOnUi(() =>
            {
                if (string.IsNullOrEmpty(text)) return;
                _currentStep = text.TrimEnd('…', '。');
                AppendColored("▶ " + text + "\n", _theme.Colors.Step);
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
                statusLabel.ForeColor = _theme.Colors.Success;
                statusLabel.Text = "生成完成，确认无误后点击「填入」";
                insertButton.Enabled = true;
                AcceptButton = insertButton;
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
                statusLabel.ForeColor = _theme.Colors.Error;
                statusLabel.Text = string.IsNullOrEmpty(error) ? "生成失败" : "生成失败：" + error;
                AppendColoredSafe("\n[生成失败] " + statusLabel.Text + "\n", _theme.Colors.Error);
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

        /// <summary>把绝对路径转成相对 commonRoot 的展示路径；转不动就原样显示。</summary>
        private static string MakeDisplayPath(string commonRoot, string path)
        {
            return PathUtil.MakeRelative(commonRoot, path);
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
            this.mainSplit = new System.Windows.Forms.SplitContainer();
            this.logBox = new System.Windows.Forms.RichTextBox();
            this.fileListPanel = new System.Windows.Forms.Panel();
            this.fileList = new System.Windows.Forms.ListBox();
            this.fileListLabel = new System.Windows.Forms.Label();
            this.bottomPanel = new System.Windows.Forms.Panel();
            this.statusLabel = new System.Windows.Forms.Label();
            this.insertButton = new RoundedButton();
            this.cancelButton = new RoundedButton();
            this.uiTimer = new System.Windows.Forms.Timer(this.components);
            this.mainSplit.Panel1.SuspendLayout();
            this.mainSplit.Panel2.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.mainSplit)).BeginInit();
            this.fileListPanel.SuspendLayout();
            this.bottomPanel.SuspendLayout();
            this.SuspendLayout();
            //
            // mainSplit（上：文件列表，下：日志；比例跟随窗口缩放）
            //
            this.mainSplit.Dock = System.Windows.Forms.DockStyle.Fill;
            this.mainSplit.FixedPanel = System.Windows.Forms.FixedPanel.None;
            this.mainSplit.Location = new System.Drawing.Point(12, 10);
            this.mainSplit.Name = "mainSplit";
            this.mainSplit.Orientation = System.Windows.Forms.Orientation.Horizontal;
            this.mainSplit.Panel1.Controls.Add(this.fileListPanel);
            this.mainSplit.Panel1MinSize = 48;
            this.mainSplit.Panel2.Controls.Add(this.logBox);
            this.mainSplit.Panel2MinSize = 120;
            this.mainSplit.Size = new System.Drawing.Size(560, 392);
            this.mainSplit.SplitterDistance = 140;
            this.mainSplit.SplitterWidth = 6;
            this.mainSplit.TabIndex = 3;
            //
            // logBox
            //
            this.logBox.BackColor = System.Drawing.Color.White;
            this.logBox.BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle;
            this.logBox.DetectUrls = false;
            this.logBox.Dock = System.Windows.Forms.DockStyle.Fill;
            this.logBox.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F);
            this.logBox.Location = new System.Drawing.Point(0, 0);
            this.logBox.Name = "logBox";
            this.logBox.ReadOnly = true;
            this.logBox.Size = new System.Drawing.Size(560, 246);
            this.logBox.TabIndex = 0;
            this.logBox.Text = "";
            //
            // fileListPanel
            //
            this.fileListPanel.Controls.Add(this.fileList);
            this.fileListPanel.Controls.Add(this.fileListLabel);
            this.fileListPanel.Dock = System.Windows.Forms.DockStyle.Fill;
            this.fileListPanel.Location = new System.Drawing.Point(0, 0);
            this.fileListPanel.Name = "fileListPanel";
            this.fileListPanel.Padding = new System.Windows.Forms.Padding(0, 0, 0, 4);
            this.fileListPanel.Size = new System.Drawing.Size(560, 140);
            this.fileListPanel.TabIndex = 2;
            //
            // fileList
            //
            this.fileList.BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle;
            this.fileList.Dock = System.Windows.Forms.DockStyle.Fill;
            this.fileList.Font = new System.Drawing.Font("Consolas", 9F);
            this.fileList.FormattingEnabled = true;
            this.fileList.IntegralHeight = false;
            this.fileList.ItemHeight = 15;
            this.fileList.Location = new System.Drawing.Point(0, 18);
            this.fileList.Name = "fileList";
            this.fileList.Size = new System.Drawing.Size(560, 118);
            this.fileList.TabIndex = 1;
            //
            // fileListLabel
            //
            this.fileListLabel.AutoSize = true;
            this.fileListLabel.Dock = System.Windows.Forms.DockStyle.Top;
            this.fileListLabel.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F);
            this.fileListLabel.ForeColor = System.Drawing.Color.DimGray;
            this.fileListLabel.Location = new System.Drawing.Point(0, 0);
            this.fileListLabel.Name = "fileListLabel";
            this.fileListLabel.Size = new System.Drawing.Size(69, 17);
            this.fileListLabel.TabIndex = 0;
            this.fileListLabel.Text = "待提交文件";
            //
            // bottomPanel（statusLabel 最后 Add = z-order 最底，避免盖住锚定的按钮）
            //
            this.bottomPanel.Controls.Add(this.cancelButton);
            this.bottomPanel.Controls.Add(this.insertButton);
            this.bottomPanel.Controls.Add(this.statusLabel);
            this.bottomPanel.Dock = System.Windows.Forms.DockStyle.Bottom;
            this.bottomPanel.Location = new System.Drawing.Point(12, 412);
            this.bottomPanel.Name = "bottomPanel";
            this.bottomPanel.Padding = new System.Windows.Forms.Padding(0, 8, 0, 8);
            this.bottomPanel.Size = new System.Drawing.Size(560, 48);
            this.bottomPanel.TabIndex = 1;
            //
            // statusLabel
            //
            this.statusLabel.AutoSize = false;
            this.statusLabel.Dock = System.Windows.Forms.DockStyle.Fill;
            this.statusLabel.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F);
            this.statusLabel.ForeColor = System.Drawing.Color.DimGray;
            this.statusLabel.Location = new System.Drawing.Point(0, 8);
            this.statusLabel.Name = "statusLabel";
            this.statusLabel.Size = new System.Drawing.Size(368, 32);
            this.statusLabel.TabIndex = 0;
            this.statusLabel.Text = "正在生成…";
            this.statusLabel.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            //
            // insertButton（锚定右上、固定尺寸）
            //
            this.insertButton.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right)));
            this.insertButton.Enabled = false;
            this.insertButton.FlatAppearance.BorderSize = 0;
            this.insertButton.FlatStyle = System.Windows.Forms.FlatStyle.Standard;
            this.insertButton.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F);
            this.insertButton.Location = new System.Drawing.Point(356, 10);
            this.insertButton.Name = "insertButton";
            this.insertButton.Size = new System.Drawing.Size(92, 28);
            this.insertButton.TabIndex = 1;
            this.insertButton.Text = "填入";
            this.insertButton.UseVisualStyleBackColor = false;
            this.insertButton.UseWaitCursor = true;
            this.insertButton.Click += new System.EventHandler(this.insertButton_Click);
            //
            // cancelButton（锚定右上、固定尺寸）
            //
            this.cancelButton.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right)));
            this.cancelButton.DialogResult = System.Windows.Forms.DialogResult.Cancel;
            this.cancelButton.FlatAppearance.BorderSize = 0;
            this.cancelButton.FlatStyle = System.Windows.Forms.FlatStyle.Standard;
            this.cancelButton.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F);
            this.cancelButton.Location = new System.Drawing.Point(456, 10);
            this.cancelButton.Name = "cancelButton";
            this.cancelButton.Size = new System.Drawing.Size(92, 28);
            this.cancelButton.TabIndex = 2;
            this.cancelButton.Text = "取消";
            this.cancelButton.UseVisualStyleBackColor = false;
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
            this.ClientSize = new System.Drawing.Size(584, 460);
            this.Controls.Add(this.mainSplit);
            this.Controls.Add(this.bottomPanel);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.Sizable;
            this.Margin = new System.Windows.Forms.Padding(3, 2, 3, 2);
            this.MaximizeBox = true;
            this.MinimizeBox = false;
            this.MinimumSize = new System.Drawing.Size(600, 500);
            this.Name = "GenerationDialog";
            this.Padding = new System.Windows.Forms.Padding(12, 10, 12, 10);
            this.ShowInTaskbar = false;
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Text = "AI生成提交信息";
            this.FormClosing += new System.Windows.Forms.FormClosingEventHandler(this.GenerationDialog_FormClosing);
            this.mainSplit.Panel1.ResumeLayout(false);
            this.mainSplit.Panel2.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.mainSplit)).EndInit();
            this.fileListPanel.ResumeLayout(false);
            this.fileListPanel.PerformLayout();
            this.bottomPanel.ResumeLayout(false);
            this.ResumeLayout(false);
        }

        // ── 圆角按钮（模仿 TSVN 深色模式的自绘 RoundRect 描边按钮） ─────

        /// <summary>
        /// 自绘圆角按钮：底色/描边/hover 色仍取自 BackColor/ForeColor/FlatAppearance，
        /// 与 ApplyTheme 主题配色联动；文字用 TextRenderer 居中，禁用时置灰。
        /// </summary>
        private sealed class RoundedButton : Button
        {
            private const int CornerRadius = 4; // 圆角半径（px），近似 TSVN 本体观感
            private bool _hover;
            private bool _pressed;

            public RoundedButton()
            {
                // 全自绘：不用系统按钮渲染，避免直角边框
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                         ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            }

            protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
            protected override void OnMouseDown(MouseEventArgs e) { _pressed = true; Invalidate(); base.OnMouseDown(e); }
            protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
            protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Parent != null ? Parent.BackColor : SystemColors.Control);

                Color back = _pressed ? FlatAppearance.MouseDownBackColor
                           : _hover && Enabled ? FlatAppearance.MouseOverBackColor
                           : BackColor;
                Color text = Enabled ? ForeColor : Color.FromArgb(0x87, 0x87, 0x87);

                using (var path = RoundedPath(ClientRectangle, CornerRadius))
                {
                    using (var brush = new SolidBrush(back)) g.FillPath(brush, path);
                    using (var pen = new Pen(FlatAppearance.BorderColor)) g.DrawPath(pen, path);
                }

                TextRenderer.DrawText(g, Text, Font, ClientRectangle, text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            }

            /// <summary>构建圆角矩形路径（右/下留 1px 给描边，避免被裁掉）。</summary>
            private static GraphicsPath RoundedPath(Rectangle r, int radius)
            {
                var rect = new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1);
                int d = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
                var path = new GraphicsPath();
                if (d <= 0)
                {
                    path.AddRectangle(new Rectangle(0, 0, 1, 1));
                    return path;
                }
                path.AddArc(rect.X, rect.Y, d, d, 180, 90);
                path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
                path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
                path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                return path;
            }
        }
    }
}
