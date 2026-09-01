// 通用小工具：路径处理。
using System;

namespace TsvnAiCommitMessage.Common
{
    internal static class PathUtil
    {
        /// <summary>把绝对路径转为相对 root 的路径；无法转换时原样返回。</summary>
        public static string MakeRelative(string root, string path)
        {
            try
            {
                if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(path)) return path;
                var rootUri = new Uri(root.TrimEnd('\\') + "\\");
                var pathUri = new Uri(path);
                if (!rootUri.IsBaseOf(pathUri)) return path;
                return Uri.UnescapeDataString(rootUri.MakeRelativeUri(pathUri).ToString()).Replace('/', '\\');
            }
            catch (Exception)
            {
                return path;
            }
        }
    }
}
