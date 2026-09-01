// 弹窗主题：跟随 TortoiseSVN「深色主题」开关（HKCU\Software\TortoiseSVN\DarkTheme），
// 与提交对话框同源；该值不存在（旧版本/未初始化）时回退系统应用深色模式；
// 浅色下保持设计器默认配色（ThemeColors 只提供语义色，控件底色不动）。
using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace TsvnAiCommitMessage.UI
{
    /// <summary>语义化配色集（正文 / 次要文本 / 分隔标题 / 步骤 / 成功 / 失败）。</summary>
    internal sealed class ThemeColors
    {
        public readonly Color TextPrimary;    // 正文/答案
        public readonly Color TextSecondary;  // 标签、思考文本
        public readonly Color SectionHeader;  // 「──── AI 思考 ────」等分隔标题
        public readonly Color Step;           // ▶ 步骤行
        public readonly Color Success;
        public readonly Color Error;

        public ThemeColors(Color textPrimary, Color textSecondary, Color sectionHeader,
            Color step, Color success, Color error)
        {
            TextPrimary = textPrimary;
            TextSecondary = textSecondary;
            SectionHeader = sectionHeader;
            Step = step;
            Success = success;
            Error = error;
        }
    }

    internal sealed class DialogTheme
    {
        public readonly bool IsDark;
        public readonly ThemeColors Colors;

        private DialogTheme(bool isDark, ThemeColors colors)
        {
            IsDark = isDark;
            Colors = colors;
        }

        /// <summary>检测并构造当前主题配色。</summary>
        public static DialogTheme Resolve()
        {
            var light = new ThemeColors(Color.Black, Color.Gray, Color.SteelBlue,
                Color.RoyalBlue, Color.SeaGreen, Color.Firebrick);
            var dark = new ThemeColors(
                Color.FromArgb(0xE8, 0xE8, 0xE8),
                Color.FromArgb(0x9A, 0x9A, 0x9A),
                Color.FromArgb(0x5B, 0x9B, 0xD5),
                Color.FromArgb(0x6F, 0xA8, 0xDC),
                Color.FromArgb(0x5F, 0xBF, 0x8F),
                Color.FromArgb(0xF2, 0x77, 0x7A));
            return IsDarkThemePreferred() ? new DialogTheme(true, dark) : new DialogTheme(false, light);
        }

        /// <summary>
        /// 跟随 TortoiseSVN 主题：优先读设置里的「深色主题」开关，
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

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        /// <summary>深色模式下启用标题栏深色（DWMWA_USE_IMMERSIVE_DARK_MODE，老版本回退 19）。</summary>
        public void ApplyDarkTitleBar(IntPtr handle)
        {
            if (!IsDark) return;
            foreach (var attr in new[] { 20, 19 })
            {
                int on = 1;
                if (DwmSetWindowAttribute(handle, attr, ref on, 4) == 0) break;
            }
        }
    }
}
