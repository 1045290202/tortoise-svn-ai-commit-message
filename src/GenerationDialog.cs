// AI 生成提交信息 —— 流式进度弹窗
// 布局控件在下方 InitializeComponent（Rider/VS 设计器序列化目标就是本文件，可调）。
// 流程：生成中实时滚动思考/结果；完成后窗体停留，由用户点「填入日志框」确认，
//       或点「取消 / 关闭」放弃（保留提交框原输入）。
// 只依赖 System.Windows.Forms / System.Drawing（net48 自带）。
// 主题：跟随 TortoiseSVN 设置里的「深色主题」开关（HKCU\Software\TortoiseSVN\DarkTheme），
//       该值不存在时回退系统应用深色模式；浅色下保持设计器默认配色。
using System;
using System.Drawing;
using System.Media;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TsvnAiCommitMessage
{
    internal class GenerationDialog : Form
    {
        private System.ComponentModel.IContainer components = null;

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

        private bool _finished;               // true 后不再视为取消
        private bool _thinkingSectionStarted;
        private bool _answerSectionStarted;
        private string _currentStep = "正在分析变更内容"; // 底部状态栏当前步骤文案

        // ── 主题（跟随 TortoiseSVN 深色主题开关） ──────────────────────
        private bool _isDark;
        private Color _colTextPrimary;    // 正文/答案
        private Color _colTextSecondary;  // 标签、思考文本
        private Color _colSectionHeader;  // 「──── AI 思考 ────」等分隔标题
        private Color _colStep;           // ▶ 步骤行
        private Color _colSuccess;
        private Color _colError;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        /// <summary>
        /// 跟随 TortoiseSVN 主题：优先读设置里的「深色主题」开关（HKCU\Software\TortoiseSVN\DarkTheme），
        /// 与提交对话框同源；该值不存在（旧版本/未初始化）时回退系统应用深色模式。
        /// </summary>
        private static bool IsDarkThemePreferred()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\TortoiseSVN"))
                {
                    var v = key == null ? null : key.GetValue("DarkTheme");
                    if (v is int) return (int)v != 0;
                }
            }
            catch (Exception) { }
            return IsSystemDarkMode();
        }

        /// <summary>读注册表判断系统应用是否处于深色模式（Win10 1809+）；读不到按浅色处理。</summary>
        private static bool IsSystemDarkMode()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    var v = key == null ? null : key.GetValue("AppsUseLightTheme");
                    if (v is int) return (int)v == 0;
                }
            }
            catch (Exception) { }
            return false;
        }

        /// <summary>按主题给全部控件配色（浅色 = 设计器默认值，仅深色时覆盖）。</summary>
        private void ApplyTheme()
        {
            _isDark = IsDarkThemePreferred();
            if (!_isDark)
            {
                _colTextPrimary = Color.Black;
                _colTextSecondary = Color.Gray;
                _colSectionHeader = Color.SteelBlue;
                _colStep = Color.RoyalBlue;
                _colSuccess = Color.SeaGreen;
                _colError = Color.Firebrick;
                return;
            }

            _colTextPrimary = Color.FromArgb(0xE8, 0xE8, 0xE8);
            _colTextSecondary = Color.FromArgb(0x9A, 0x9A, 0x9A);
            _colSectionHeader = Color.FromArgb(0x5B, 0x9B, 0xD5);
            _colStep = Color.FromArgb(0x6F, 0xA8, 0xDC);
            _colSuccess = Color.FromArgb(0x5F, 0xBF, 0x8F);
            _colError = Color.FromArgb(0xF2, 0x77, 0x7A);

            BackColor = Color.FromArgb(0x20, 0x20, 0x20);

            logBox.BackColor = Color.FromArgb(0x1B, 0x1B, 0x1B);
            logBox.BorderStyle = BorderStyle.FixedSingle;

            fileList.BackColor = Color.FromArgb(0x1B, 0x1B, 0x1B);
            fileList.ForeColor = _colTextPrimary;
            fileListLabel.ForeColor = _colTextSecondary;

            statusLabel.ForeColor = _colTextSecondary;

            foreach (var btn in new[] { insertButton, cancelButton })
            {
                btn.FlatStyle = FlatStyle.Flat;
                btn.FlatAppearance.BorderColor = Color.FromArgb(0x55, 0x55, 0x55);
                btn.BackColor = Color.FromArgb(0x2D, 0x2D, 0x2D);
                btn.ForeColor = _colTextPrimary;
            }
        }

        /// <summary>深色模式下启用标题栏深色（DWMWA_USE_IMMERSIVE_DARK_MODE，老版本回退 19）。</summary>
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (!_isDark) return;
            foreach (var attr in new[] { 20, 19 })
            {
                int on = 1;
                if (DwmSetWindowAttribute(Handle, attr, ref on, 4) == 0) break;
            }
        }

        /// <summary>用户点「填入日志框」后的提交信息；其余情况为 null。</summary>
        public string ResultMessage { get; private set; }

        /// <summary>失败原因（仅失败时有值，用于无 UI 日志排查）。</summary>
        public string ErrorMessage { get; private set; }

        /// <summary>生成过程中被用户取消（区别于失败）。</summary>
        public bool Cancelled { get; private set; }

        /// <summary>用户取消/关闭窗体时触发（UI 线程），供宿主立即打断后台生成。</summary>
        public event Action CancelRequested;

        /// <summary>
        /// 展示本次待提交的文件列表（提交对话框中勾选的变更项）。
        /// 在 ShowDialog 之前于 UI 线程调用；pathList 为空时不显示该区域。
        /// </summary>
        public void SetCommitFiles(string commonRoot, string[] pathList)
        {
            if (pathList == null || pathList.Length == 0)
            {
                fileListPanel.Visible = false;
                return;
            }

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

        /// <summary>把绝对路径转成相对 commonRoot 的展示路径；转不动就原样显示。</summary>
        private static string MakeDisplayPath(string commonRoot, string path)
        {
            try
            {
                if (string.IsNullOrEmpty(commonRoot) || string.IsNullOrEmpty(path)) return path;
                var rootUri = new Uri(commonRoot.TrimEnd('\\') + "\\");
                var pathUri = new Uri(path);
                if (!rootUri.IsBaseOf(pathUri)) return path;
                return Uri.UnescapeDataString(rootUri.MakeRelativeUri(pathUri).ToString()).Replace('/', '\\');
            }
            catch (Exception)
            {
                return path;
            }
        }

        public GenerationDialog()
        {
            InitializeComponent();
            ApplyTheme();
            AppendColored("AI 正在分析变更内容…\n", _colTextSecondary);
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
                AppendColoredSafe("\n──── AI 思考 ────\n", _colSectionHeader);
            }
            AppendColoredSafe(text, _colTextSecondary);
        }

        public void AppendAnswer(string text)
        {
            if (!_answerSectionStarted)
            {
                _answerSectionStarted = true;
                AppendColoredSafe("\n──── 生成结果 ────\n", _colSectionHeader);
            }
            AppendColoredSafe(text, _colTextPrimary);
        }

        /// <summary>工作线程报告一个大步骤的开始（日志区打一行 + 底部状态栏同步）。</summary>
        public void SetStep(string text)
        {
            RunOnUi(() =>
            {
                if (string.IsNullOrEmpty(text)) return;
                _currentStep = text.TrimEnd('…', '。');
                AppendColored("▶ " + text + "\n", _colStep);
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
                statusLabel.ForeColor = _colSuccess;
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
                statusLabel.ForeColor = _colError;
                statusLabel.Text = string.IsNullOrEmpty(error) ? "生成失败" : "生成失败：" + error;
                AppendColoredSafe("\n[生成失败] " + statusLabel.Text + "\n", _colError);
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
            this.fileListPanel = new System.Windows.Forms.Panel();
            this.fileList = new System.Windows.Forms.ListBox();
            this.fileListLabel = new System.Windows.Forms.Label();
            this.bottomPanel = new System.Windows.Forms.Panel();
            this.statusLabel = new System.Windows.Forms.Label();
            this.insertButton = new System.Windows.Forms.Button();
            this.cancelButton = new System.Windows.Forms.Button();
            this.uiTimer = new System.Windows.Forms.Timer(this.components);
            this.fileListPanel.SuspendLayout();
            this.bottomPanel.SuspendLayout();
            this.SuspendLayout();
            //
            // logBox
            //
            this.logBox.BackColor = System.Drawing.Color.White;
            this.logBox.BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle;
            this.logBox.DetectUrls = false;
            this.logBox.Dock = System.Windows.Forms.DockStyle.Fill;
            this.logBox.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F);
            this.logBox.Location = new System.Drawing.Point(10, 140);
            this.logBox.Margin = new System.Windows.Forms.Padding(3, 2, 3, 2);
            this.logBox.Name = "logBox";
            this.logBox.ReadOnly = true;
            this.logBox.Size = new System.Drawing.Size(564, 268);
            this.logBox.TabIndex = 0;
            this.logBox.Text = "";
            //
            // fileListPanel
            //
            this.fileListPanel.Controls.Add(this.fileList);
            this.fileListPanel.Controls.Add(this.fileListLabel);
            this.fileListPanel.Dock = System.Windows.Forms.DockStyle.Top;
            this.fileListPanel.Location = new System.Drawing.Point(10, 8);
            this.fileListPanel.Name = "fileListPanel";
            this.fileListPanel.Padding = new System.Windows.Forms.Padding(0, 0, 0, 4);
            this.fileListPanel.Size = new System.Drawing.Size(564, 132);
            this.fileListPanel.TabIndex = 2;
            //
            // fileList
            //
            this.fileList.BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle;
            this.fileList.Dock = System.Windows.Forms.DockStyle.Fill;
            this.fileList.Font = new System.Drawing.Font("Consolas", 9F);
            this.fileList.FormattingEnabled = true;
            this.fileList.IntegralHeight = false;
            this.fileList.ItemHeight = 14;
            this.fileList.Location = new System.Drawing.Point(0, 12);
            this.fileList.Name = "fileList";
            this.fileList.Size = new System.Drawing.Size(564, 116);
            this.fileList.TabIndex = 1;
            //
            // fileListLabel
            //
            this.fileListLabel.AutoSize = true;
            this.fileListLabel.Dock = System.Windows.Forms.DockStyle.Top;
            this.fileListLabel.ForeColor = System.Drawing.Color.DimGray;
            this.fileListLabel.Location = new System.Drawing.Point(0, 0);
            this.fileListLabel.Name = "fileListLabel";
            this.fileListLabel.Size = new System.Drawing.Size(65, 12);
            this.fileListLabel.TabIndex = 0;
            this.fileListLabel.Text = "待提交文件";
            //
            // bottomPanel
            //
            this.bottomPanel.Controls.Add(this.statusLabel);
            this.bottomPanel.Controls.Add(this.insertButton);
            this.bottomPanel.Controls.Add(this.cancelButton);
            this.bottomPanel.Dock = System.Windows.Forms.DockStyle.Bottom;
            this.bottomPanel.Location = new System.Drawing.Point(10, 408);
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
            this.insertButton.Location = new System.Drawing.Point(412, 6);
            this.insertButton.Margin = new System.Windows.Forms.Padding(3, 2, 3, 2);
            this.insertButton.Name = "insertButton";
            this.insertButton.Size = new System.Drawing.Size(76, 32);
            this.insertButton.TabIndex = 1;
            this.insertButton.Text = "填入";
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
            this.ClientSize = new System.Drawing.Size(584, 460);
            this.Controls.Add(this.logBox);
            this.Controls.Add(this.bottomPanel);
            this.Controls.Add(this.fileListPanel);
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
            this.fileListPanel.ResumeLayout(false);
            this.fileListPanel.PerformLayout();
            this.bottomPanel.ResumeLayout(false);
            this.bottomPanel.PerformLayout();
            this.ResumeLayout(false);
        }
    }
}
