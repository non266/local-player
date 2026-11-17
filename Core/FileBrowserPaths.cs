using System;
using System.IO;

namespace 播放器.Core
{
    /// <summary>
    /// 文件浏览器的路径处理：地址归一、父目录、根判断、菜单短标签。
    /// <para>
    /// 这一层<b>一次磁盘都不碰</b>：地址栏里打进来的东西先在这里变成"要么是一个绝对路径，
    /// 要么是此电脑，要么是一句看得懂的错误"，真正的目录读取交给
    /// <see cref="FileBrowserService"/> 在线程池上做。
    /// </para>
    /// <para>
    /// 特殊照顾 <c>D:</c>：<see cref="Path.GetFullPath"/> 会把它解释成"D 盘当前目录"，
    /// 而用户想说的是盘根，所以先补成 <c>D:\</c>。网络流的 <c>scheme://</c> 地址直接拒绝——
    /// 那是「打开网络串流」的事，不是文件浏览器的事。
    /// </para>
    /// </summary>
    internal static class FileBrowserPaths
    {
        /// <summary>空路径的显示名（"此电脑"视图）。</summary>
        internal const string ComputerLabel = "此电脑";

        /// <summary>把用户输入 / 设置里的字符串归一成绝对路径；空串表示"此电脑"。</summary>
        internal static bool TryNormalize(string? raw, out string? path, out string error)
        {
            path = null;
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(raw)) return true;

            var text = raw.Trim().Trim('"').Trim();
            if (text.Length == 0) return true;

            if (IsComputerAlias(text)) return true;

            try
            {
                text = Environment.ExpandEnvironmentVariables(text);

                if (TryExtractFileUri(text, out var localPath))
                {
                    text = localPath;
                }
                else if (LooksLikeNetworkUri(text))
                {
                    error = "网络流地址不能在文件浏览器里打开";
                    return false;
                }

                // "D:" 不是盘根：GetFullPath 会把它当成"D 盘的当前目录"。
                if (text.Length == 2 && char.IsLetter(text[0]) && text[1] == ':')
                    text += "\\";

                if (!Path.IsPathFullyQualified(text))
                {
                    error = "请输入完整的文件夹路径（例如 D:\\Movies）";
                    return false;
                }

                if (text.StartsWith(@"\\", StringComparison.Ordinal) &&
                    !text.StartsWith(@"\\?\", StringComparison.Ordinal) &&
                    !text.StartsWith(@"\\.\", StringComparison.Ordinal))
                {
                    var parts = text.Substring(2).Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);

                    if (parts.Length < 2)
                    {
                        error = "网络路径要写成 \\\\服务器\\共享名";
                        return false;
                    }
                }

                var full = Path.GetFullPath(text);
                var root = Path.GetPathRoot(full) ?? string.Empty;

                if (full.Length > root.Length)
                    full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                if (full.Length == 0) return true;

                path = full;
                return true;
            }
            catch (ArgumentException)
            {
                error = "路径里有不能用的字符";
                return false;
            }
            catch (NotSupportedException)
            {
                error = "这种路径格式不支持";
                return false;
            }
            catch (PathTooLongException)
            {
                error = "路径太长了";
                return false;
            }
            catch (IOException)
            {
                error = "路径看不懂";
                return false;
            }
        }

        /// <summary>设置文件里的路径：归一失败就当成"此电脑"，不把坏值带进界面。</summary>
        internal static string NormalizeStored(string? raw) =>
            TryNormalize(raw, out var path, out _) ? path ?? string.Empty : string.Empty;

        /// <summary>是不是盘根 / UNC 共享根（再往上就是"此电脑"）。</summary>
        internal static bool IsRoot(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;

            try
            {
                var root = Path.GetPathRoot(path);
                if (string.IsNullOrEmpty(root)) return false;

                return string.Equals(
                    path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        /// <summary>父目录；盘根 / 共享根 / 此电脑返回 <c>null</c>（它们的上一级是此电脑）。</summary>
        internal static string? ParentOf(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || IsRoot(path)) return null;

            try
            {
                var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var parent = Path.GetDirectoryName(trimmed);

                if (string.IsNullOrEmpty(parent)) return null;

                // GetDirectoryName("D:\\") 之类偶尔会给出 "D:"，补回盘根。
                if (parent.Length == 2 && char.IsLetter(parent[0]) && parent[1] == ':')
                    parent += "\\";

                if (parent.StartsWith(@"\\", StringComparison.Ordinal))
                {
                    var parts = parent.Substring(2).Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2) return null;
                }

                return parent;
            }
            catch (ArgumentException)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
            }
        }

        /// <summary>
        /// 菜单里的短标签：保留路径最后两段，最多 <paramref name="maxLength"/> 个字符。
        /// 完整路径仍然放在 tooltip 里，菜单不会被一段长路径撑爆。
        /// </summary>
        internal static string Describe(string? path, int maxLength = 60)
        {
            if (string.IsNullOrWhiteSpace(path)) return ComputerLabel;

            var label = path;

            try
            {
                if (!IsRoot(path))
                {
                    var root = Path.GetPathRoot(path) ?? string.Empty;
                    var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    var rest = trimmed.Length > root.Length ? trimmed.Substring(root.Length) : string.Empty;
                    var parts = rest.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);

                    label = parts.Length switch
                    {
                        0 => trimmed,
                        1 => parts[0],
                        _ => parts[^2] + "\\" + parts[^1]
                    };
                }
            }
            catch (ArgumentException)
            {
                label = path;
            }

            if (maxLength > 0 && label.Length > maxLength)
                label = "…" + label.Substring(label.Length - maxLength + 1);

            return label;
        }

        private static bool IsComputerAlias(string text) =>
            string.Equals(text, ComputerLabel, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "计算机", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "我的电脑", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "This PC", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "Computer", StringComparison.OrdinalIgnoreCase);

        private static bool TryExtractFileUri(string text, out string localPath)
        {
            localPath = text;

            if (!text.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) return false;

            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || !uri.IsFile) return false;

            localPath = uri.LocalPath;
            return true;
        }

        /// <summary><c>http://</c> / <c>rtsp://</c> / <c>smb://</c> 这类地址不是本地目录。</summary>
        private static bool LooksLikeNetworkUri(string text) =>
            text.IndexOf("://", StringComparison.Ordinal) >= 0 &&
            !text.StartsWith("file://", StringComparison.OrdinalIgnoreCase);
    }
}
