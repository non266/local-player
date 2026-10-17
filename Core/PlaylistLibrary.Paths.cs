using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

// 目录与名字：歌单目录、名字规则（SavedName 那套的入口）。

namespace 播放器.Core
{
    public static partial class PlaylistLibrary
    {
        /// <summary>确保歌单目录存在，返回它的路径。</summary>
        public static string EnsureDirectory()
        {
            var directory = Directory;
            if (!System.IO.Directory.Exists(directory))
                System.IO.Directory.CreateDirectory(directory);

            return directory;
        }

        // ---- 名字 -------------------------------------------------------------

        /// <summary>
        /// 把用户输入的名字收拾成可用的文件名。
        /// <para>规则在 <see cref="SavedName.TryNormalize"/> 里（和设置方案共用），这里只是接上主语。</para>
        /// </summary>
        public static bool TryNormalizeName(string? raw, out string name, out string error) =>
            SavedName.TryNormalize(raw, "歌单名", out name, out error);

        /// <summary>歌单名是否可用（合法且不重名）；不合法时给出理由。</summary>
        public static bool CanUseName(string? raw, out string name, out string error)
        {
            if (!TryNormalizeName(raw, out name, out error)) return false;

            if (ResolvePath(name) != null)
            {
                error = $"已经有一个叫「{name}」的歌单了。";
                return false;
            }

            return true;
        }
    }
}
