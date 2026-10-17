using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

// 增删改：新建 / 覆盖保存 / 载入 / 改名 / 删除 / 导入 / 在资源管理器里选中。

namespace 播放器.Core
{
    public static partial class PlaylistLibrary
    {
        /// <summary>歌单是否存在（名字不区分大小写）。</summary>
        public static bool Exists(string? name) => ResolvePath(name) != null;

        /// <summary>名字对应的现有文件；名字非法或文件不存在时返回 <c>null</c>。</summary>
        public static string? ResolvePath(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;

            var wanted = name.Trim();

            // 大小写不敏感的文件系统上，"rock" 和 "Rock" 是同一个文件。
            // 统一按目录里实际的名字走，避免出现"同一个歌单两个大小写不同的文件"。
            foreach (var saved in List())
            {
                if (string.Equals(saved.Name, wanted, StringComparison.CurrentCultureIgnoreCase))
                    return saved.FilePath;
            }

            return null;
        }

        /// <summary>这个名字该写到哪个文件：已有同名文件就沿用它的大小写，否则按 .m3u8 新建。</summary>
        private static string PathFor(string name) =>
            ResolvePath(name) ?? Path.Combine(EnsureDirectory(), name + ".m3u8");

        // ---- 增删改 -----------------------------------------------------------

        /// <summary>
        /// 新建一个空歌单。
        /// <para>
        /// 名字被占了直接抛错、<b>不覆盖</b>：覆盖别人的歌单是在删他的东西，
        /// 那件事只能由用户在"要覆盖吗"那里明确点过才算数。
        /// </para>
        /// </summary>
        public static SavedPlaylist Create(string name)
        {
            if (!TryNormalizeName(name, out var clean, out var error))
                throw new ArgumentException(error, nameof(name));

            if (ResolvePath(clean) != null)
                throw new InvalidOperationException($"已经有一个叫「{clean}」的歌单了。");

            return Save(clean, Array.Empty<PlaylistItem>());
        }

        /// <summary>
        /// 把一组条目存成歌单；同名时<b>覆盖</b>。返回存下来的那份。
        /// </summary>
        public static SavedPlaylist Save(string name, IReadOnlyList<PlaylistItem> items)
        {
            if (!TryNormalizeName(name, out var clean, out var error))
                throw new ArgumentException(error, nameof(name));

            if (items == null) throw new ArgumentNullException(nameof(items));

            var path = PathFor(clean);

            // PlaylistFile.Save 走的是"临时文件 + 改名覆盖"（SafeFile），
            // 所以覆盖已有歌单时不会出现半截文件。
            PlaylistFile.Save(path, items);

            CountCache.Remove(path);
            Invalidate();

            var info = new FileInfo(path);

            return new SavedPlaylist(
                Path.GetFileNameWithoutExtension(path),
                info.FullName,
                info.Exists ? info.LastWriteTimeUtc : DateTime.UtcNow,
                items.Count,
                info.Exists ? info.Length : 0);
        }

        /// <summary>
        /// 读取歌单。<b>文件已经不在的条目会被单独计数</b>，而不是像 <see cref="PlaylistFile.Load"/> 那样悄悄丢掉。
        /// </summary>
        public static PlaylistLoadResult Load(string name)
        {
            var result = new PlaylistLoadResult();

            var path = ResolvePath(name);
            if (path == null)
            {
                result.Error = $"找不到歌单「{name}」。";
                return result;
            }

            List<string> all;
            try
            {
                all = PlaylistFile.ReadPaths(path);
            }
            catch (Exception ex)
            {
                result.Error = "歌单读取失败：" + ex.Message;
                return result;
            }

            result.Total = all.Count;

            foreach (var entry in all)
            {
                if (MediaFormats.IsOpenable(entry)) result.Tracks.Add(entry);
            }

            result.Missing = result.Total - result.Tracks.Count;

            if (result.Total == 0)
                result.Error = $"歌单「{Path.GetFileNameWithoutExtension(path)}」是空的。";

            return result;
        }

        /// <summary>把歌单改名。成功时 <paramref name="newName"/> 是实际用的名字。</summary>
        public static bool Rename(string oldName, string newName, out string newNameOut, out string error)
        {
            newNameOut = string.Empty;
            error = string.Empty;

            var path = ResolvePath(oldName);
            if (path == null)
            {
                error = $"找不到歌单「{oldName}」。";
                return false;
            }

            if (!TryNormalizeName(newName, out var clean, out error)) return false;

            var destination = Path.Combine(
                Path.GetDirectoryName(path) ?? EnsureDirectory(), clean + Path.GetExtension(path));

            // 名字一个字都没变：不折腾文件，直接算成功
            if (string.Equals(destination, path, StringComparison.Ordinal))
            {
                newNameOut = Path.GetFileNameWithoutExtension(path);
                return true;
            }

            // 目标已经被**另一个**文件占了才拒绝。只差大小写时，文件系统上其实是同一个文件，
            // 那种情况要放行（下面用 overwrite 走改名，Windows 支持只改大小写的重命名）。
            if (File.Exists(destination) && !string.Equals(destination, path, StringComparison.OrdinalIgnoreCase))
            {
                error = $"已经有一个叫「{clean}」的歌单了。";
                return false;
            }

            try
            {
                File.Move(path, destination, overwrite: true);
            }
            catch (Exception ex)
            {
                error = "改名失败：" + ex.Message;
                return false;
            }

            CountCache.Remove(path);
            Invalidate();

            // 改过名的这一份还留在它原来的位置上（顺序文件里跟着换名字）
            ReplaceOrderName(Path.GetFileNameWithoutExtension(path), Path.GetFileNameWithoutExtension(destination));

            newNameOut = Path.GetFileNameWithoutExtension(destination);
            return true;
        }

        /// <summary>删除歌单（只删歌单文件本身，媒体文件一个都不动）。</summary>
        public static bool Delete(string name, out string error)
        {
            error = string.Empty;

            var path = ResolvePath(name);
            if (path == null)
            {
                error = $"找不到歌单「{name}」。";
                return false;
            }

            try
            {
                File.Delete(path);
            }
            catch (Exception ex)
            {
                error = "删除失败：" + ex.Message;
                return false;
            }

            CountCache.Remove(path);
            Invalidate();
            return true;
        }

        /// <summary>
        /// 把一个外部的 m3u/m3u8 收进歌单库（原文件一个字节都不动）。
        /// <para>
        /// <b>不是原样拷贝，而是读出来重新写一遍</b>：外面的 m3u 常用相对路径
        /// （"../音乐/01.mp3"），那种路径是相对<b>它原来所在的目录</b>的，
        /// 文件一拷进歌单库就全部指错了地方（实测：拷完一首也找不到）。
        /// 读的时候按原目录解析成绝对路径，写回来就是一份到哪儿都能用的歌单。
        /// </para>
        /// <para>同名时的取舍交给调用方：<paramref name="overwrite"/> 为 false 就返回失败并说明。</para>
        /// </summary>
        public static bool Import(string sourcePath, bool overwrite, out string name, out string error)
        {
            name = string.Empty;
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                error = "文件不存在。";
                return false;
            }

            var extension = Path.GetExtension(sourcePath);

            if (!Extensions.Any(e => string.Equals(e, extension, StringComparison.OrdinalIgnoreCase)))
            {
                error = "只认 m3u / m3u8 文件。";
                return false;
            }

            var raw = Path.GetFileNameWithoutExtension(sourcePath);

            if (!TryNormalizeName(raw, out var clean, out error)) return false;

            List<string> entries;

            try
            {
                // 相对路径在这个调用里就按"源文件所在的目录"解析成绝对路径了
                entries = PlaylistFile.ReadPaths(sourcePath);
            }
            catch (Exception ex)
            {
                error = "读不出来：" + ex.Message;
                return false;
            }

            if (entries.Count == 0)
            {
                error = "这个文件里没有可用的条目。";
                return false;
            }

            var destination = ResolvePath(clean);

            if (destination != null)
            {
                if (!overwrite)
                {
                    error = $"已经有一个叫「{clean}」的歌单了。";
                    return false;
                }
            }
            else
            {
                destination = Path.Combine(EnsureDirectory(), clean + ".m3u8");
            }

            try
            {
                PlaylistFile.Save(destination, entries.Select(path => new PlaylistItem(path)).ToList());
            }
            catch (Exception ex)
            {
                error = "导入失败：" + ex.Message;
                return false;
            }

            CountCache.Remove(destination);
            Invalidate();

            name = Path.GetFileNameWithoutExtension(destination);
            return true;
        }

        /// <summary>在资源管理器里选中某个文件。不存在的路径交给调用方处理。</summary>
        public static bool TryReveal(string path)
        {
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
                    {
                        UseShellExecute = true
                    });

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
