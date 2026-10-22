using System;
using System.Collections.Generic;
using System.IO;

namespace 播放器.Core
{
    /// <summary>
    /// 播放列表里的重复项：同一个文件被加了两次以上时，指出"该被选中的那些下标"。
    /// <para>
    /// 列表本身**允许**重复（同一段听两遍是合法用法，见 <c>MainForm.Files</c> 的说明），
    /// 所以这里只找、不删：保留第一次出现的那一项，其余下标交给界面去选中。
    /// </para>
    /// </summary>
    public static class PlaylistDuplicates
    {
        /// <summary>
        /// 返回"重复项"的下标（升序）：同一个文件**第一次出现的那一项保留**，后面的都算重复。
        /// <para>
        /// 比对用 <see cref="Path.GetFullPath"/> 归一、大小写不敏感（Windows 路径）：
        /// 相对路径、带 <c>..</c> 的路径、大小写不同的写法都能认成同一个文件。
        /// 归一失败（非法字符之类）就退回原串比较——找重复不该让界面崩。
        /// 空路径直接跳过（它本来就播不了，不算"重复"）。
        /// </para>
        /// </summary>
        public static IReadOnlyList<int> FindDuplicates(IReadOnlyList<string> paths)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var duplicates = new List<int>();

            for (var i = 0; i < paths.Count; i++)
            {
                var path = paths[i];

                if (string.IsNullOrWhiteSpace(path)) continue;

                if (!seen.Add(Normalize(path))) duplicates.Add(i);
            }

            return duplicates;
        }

        private static string Normalize(string path)
        {
            try
            {
                return Path.GetFullPath(path);
            }
            catch (Exception)
            {
                // 非法路径 / 太长 / 不支持的格式：退回原串比较，别在这里抛
                return path;
            }
        }
    }
}
