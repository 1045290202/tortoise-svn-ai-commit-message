// 读取 TortoiseSVN 提交对话框中【勾选】的文件列表。
//
// 背景（TortoiseSVN 源码 CommitDlg.cpp 证实）：
//   插件按钮点击时传给 GetCommitMessage/GetCommitMessage2 的 pathList 是
//   m_pathList —— 即打开提交对话框时从资源管理器选中的路径（如整个工作副本目录），
//   而不是对话框里勾选的项；勾选项（m_selectedPathList）只在点确定时的
//   CheckCommit 里才提供给插件，官方 COM 接口在设计上就拿不到。
//
// 方案：插件与 TortoiseProc.exe 同进程，点击按钮时直接向对话框的文件列表控件
//       （MFC CListCtrl → SysListView32，勾选状态走 state image）发 Win32 消息读取。
// 兜底：任何环节读不到（找不到列表控件 / 无状态图 / 文本为空 / 勾选数为 0——
//       提交至少要勾一项，读到 0 说明机制失效）都返回 null，调用方退回原始 pathList。
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace TsvnAiCommitMessage
{
    internal static class CommitDialogListReader
    {
        private const uint LVM_FIRST = 0x1000;
        private const uint LVM_GETITEMCOUNT = LVM_FIRST + 4;    // 0x1004
        private const uint LVM_GETITEMSTATE = LVM_FIRST + 44;   // 0x102C
        private const uint LVM_GETITEMTEXTW = LVM_FIRST + 115;  // 0x1073
        private const uint LVIF_TEXT = 0x0001;
        private const uint LVIS_STATEIMAGE_MASK = 0xF000;

        // state image 约定（与 CListCtrl::GetCheck 一致）：1=未勾选，2=勾选，3=半选（目录部分勾选）
        private const int StateImageUnchecked = 1;

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr parent, EnumChildProc callback, IntPtr lParam);

        private delegate bool EnumChildProc(IntPtr child, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int maxCount);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct LVITEMW
        {
            public uint mask;
            public int iItem;
            public int iSubItem;
            public uint state;
            public uint stateMask;
            public IntPtr pszText;
            public int cchTextMax;
            public int iImage;
            public IntPtr lParam;
            public int iIndent;
            public int iGroupId;
            public int cColumns;
            public IntPtr puColumns;
            public IntPtr piColFmt;
            public int iGroup;
        }

        /// <summary>
        /// 读取提交对话框列表中勾选的文件（绝对路径）。
        /// 成功返回非 null 路径数组；读取失败返回 null，由调用方退回原始 pathList。
        /// 必须在提交对话框的 UI 线程上调用（点击插件按钮时即在该线程）。
        /// </summary>
        public static string[] TryReadCheckedPaths(IntPtr dialogHandle, string commonRoot)
        {
            try
            {
                if (dialogHandle == IntPtr.Zero) return null;

                var candidates = new List<IntPtr>();
                EnumChildWindows(dialogHandle, (child, lp) =>
                {
                    var name = new StringBuilder(64);
                    GetClassName(child, name, 64);
                    if (string.Equals(name.ToString(), "SysListView32", StringComparison.OrdinalIgnoreCase)
                        && IsWindowVisible(child))
                    {
                        candidates.Add(child);
                    }
                    return true;
                }, IntPtr.Zero);

                List<string> best = null;
                foreach (var list in candidates)
                {
                    var checkedItems = ReadCheckedItems(list);
                    if (checkedItems == null) continue;
                    if (best == null || checkedItems.Count > best.Count)
                        best = checkedItems;
                }

                // 勾选数为 0：提交对话框至少要勾一项才可能提交，读到 0 说明状态图机制不匹配，不可信
                if (best == null || best.Count == 0) return null;

                var resolved = new List<string>();
                foreach (var text in best)
                {
                    var path = ResolvePath(commonRoot, text);
                    if (!string.IsNullOrEmpty(path)) resolved.Add(path);
                }
                return resolved.Count > 0 ? resolved.ToArray() : null;
            }
            catch (Exception)
            {
                return null; // 插件跑在 TortoiseProc 进程内，任何异常都不能带崩提交对话框
            }
        }

        /// <summary>读取一个列表控件的勾选项文本；结构不像勾选文件列表时返回 null。</summary>
        private static List<string> ReadCheckedItems(IntPtr list)
        {
            int count = (int)SendMessage(list, LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero);
            if (count <= 0 || count > 100000) return null;

            var checkedItems = new List<string>();
            for (int i = 0; i < count; i++)
            {
                uint state = (uint)SendMessage(list, LVM_GETITEMSTATE, (IntPtr)i, (IntPtr)LVIS_STATEIMAGE_MASK);
                int stateImage = (int)((state & LVIS_STATEIMAGE_MASK) >> 12);
                if (stateImage < StateImageUnchecked) return null; // 无状态图：不是勾选列表

                string text = GetItemText(list, i);
                if (string.IsNullOrEmpty(text)) return null; // 文本读不到（可能是虚拟列表），不可信

                if (stateImage > StateImageUnchecked) // 勾选(2)与半选(3)都视为提交
                    checkedItems.Add(text);
            }
            return checkedItems;
        }

        /// <summary>取列表控件第 0 列文本；失败返回 null。</summary>
        private static string GetItemText(IntPtr list, int index)
        {
            IntPtr textBuffer = Marshal.AllocHGlobal(2048); // 最多 1024 个 UTF-16 字符
            IntPtr itemBuffer = IntPtr.Zero;
            try
            {
                var item = new LVITEMW
                {
                    mask = LVIF_TEXT,
                    iSubItem = 0,
                    pszText = textBuffer,
                    cchTextMax = 1024,
                };
                itemBuffer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(LVITEMW)));
                Marshal.StructureToPtr(item, itemBuffer, false);
                SendMessage(list, LVM_GETITEMTEXTW, (IntPtr)index, itemBuffer);
                return Marshal.PtrToStringUni(textBuffer);
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                if (itemBuffer != IntPtr.Zero) Marshal.FreeHGlobal(itemBuffer);
                Marshal.FreeHGlobal(textBuffer);
            }
        }

        /// <summary>
        /// 把列表里显示的（相对）路径还原成绝对路径。
        /// 相对基准不确定时从 commonRoot 逐级向上找第一个存在的路径；找不到则按 commonRoot 拼接兜底。
        /// </summary>
        private static string ResolvePath(string commonRoot, string displayText)
        {
            if (string.IsNullOrEmpty(displayText)) return null;
            var text = displayText.Replace('/', '\\').Trim();
            if (Path.IsPathRooted(text)) return text;
            if (string.IsNullOrEmpty(commonRoot)) return text;

            string baseDir;
            try { baseDir = Path.GetFullPath(commonRoot.TrimEnd('\\')); }
            catch (Exception) { return Path.Combine(commonRoot, text); }

            string current = baseDir;
            while (!string.IsNullOrEmpty(current))
            {
                var candidate = Path.Combine(current, text);
                if (File.Exists(candidate) || Directory.Exists(candidate))
                    return candidate;
                var parent = Path.GetDirectoryName(current);
                if (parent == null || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                    break;
                current = parent;
            }
            return Path.Combine(baseDir, text);
        }
    }
}
