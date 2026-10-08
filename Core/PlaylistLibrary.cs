using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace 播放器.Core
{
    /// <summary>歌单库里的一份歌单（磁盘上的一个 m3u8 文件）。</summary>
    public sealed class SavedPlaylist
    {
        public SavedPlaylist(string name, string filePath, DateTime modifiedUtc, int trackCount, long size)
        {
            Name = name;
            FilePath = filePath;
            ModifiedUtc = modifiedUtc;
            TrackCount = trackCount;
            Size = size;
        }

        /// <summary>歌单名（不含扩展名）。</summary>
        public string Name { get; }

        /// <summary>磁盘上的完整路径。</summary>
        public string FilePath { get; }

        public DateTime ModifiedUtc { get; }

        /// <summary>条目数；<b>-1</b> 表示这个文件读不出来。</summary>
        public int TrackCount { get; }

        public long Size { get; }

        /// <summary>最后修改时间（本地时间）。</summary>
        public DateTime ModifiedLocal => ModifiedUtc.ToLocalTime();
    }

    /// <summary>载入歌单的结果。</summary>
    public sealed class PlaylistLoadResult
    {
        /// <summary>现在还能打开的条目（保持了歌单里的顺序）。</summary>
        public List<string> Tracks { get; } = new List<string>();

        /// <summary>歌单里一共有多少条目（含已经不在了的）。</summary>
        public int Total { get; internal set; }

        /// <summary>其中文件已经不在了的条目数。</summary>
        public int Missing { get; internal set; }

        /// <summary>整体失败时的说明（文件不存在、读不出来）；正常时为 <c>null</c>。</summary>
        public string? Error { get; internal set; }

        public bool Ok => Error == null;
    }

    /// <summary>
    /// 歌单库：把播放列表按名字存成磁盘上的 m3u8，随时切换。
    /// <para>
    /// 存的是<b>标准 m3u8 文件</b>而不是自定义格式，理由有三个：
    /// 用户在资源管理器里能直接看到、能拷给别人、能用别的播放器打开；
    /// 存坏了也只是丢一个歌单，不会连累设置文件；出问题时用记事本就能查。
    /// </para>
    /// <para>
    /// 目录固定为 <c>数据目录\播放列表</c>（数据目录见 <see cref="AppSettings.SettingsDirectory"/>）。
    /// 只扫顶层，不进子目录——用户往里面放子文件夹是"归档"，不该被当成歌单列表的一部分。
    /// </para>
    /// <para>
    /// 本类只负责磁盘上的那份真相，不持有"当前用的是哪个歌单"这种界面状态
    /// （那个在 <c>AppSettings.CurrentPlaylistName</c> 里）。
    /// </para>
    /// </summary>
    public static class PlaylistLibrary
    {
        /// <summary>歌单目录名。</summary>
        public const string FolderName = "播放列表";

        /// <summary>
        /// 歌单顺序文件名（就在歌单目录里）。
        /// <para>
        /// <b>一行一个歌单名</b>，写的是用户在歌单窗口里拖出来的顺序。
        /// 用纯文本而不是 json：它和歌单躺在同一个目录里，拿记事本就能看能改，
        /// <b>删掉它就回到按名字的自然顺序</b>——和这个目录"一个歌单一个见得了人的文件"的风格一致。
        /// 文件名里不可能有换行，所以一行一个名字不会有歧义。
        /// </para>
        /// <para>
        /// 它<b>不是歌单</b>：<see cref="List"/> 只扫 <c>.m3u</c> / <c>.m3u8</c>，
        /// 这个 <c>.txt</c> 不会被当成一份歌单列出来。
        /// </para>
        /// </summary>
        public const string OrderFileName = "歌单顺序.txt";

        /// <summary>歌单名长度上限（规则在 <see cref="SavedName"/> 里，和设置方案共用一套）。</summary>
        public const int MaximumNameLength = SavedName.MaximumLength;

        /// <summary>认作歌单的扩展名。</summary>
        private static readonly string[] Extensions = { ".m3u8", ".m3u" };

        /// <summary>目录路径 → (文件长度, 修改时间, 条目数)。只用来避免菜单每次下拉都重读一遍歌单文件。</summary>
        private static readonly Dictionary<string, (long Length, DateTime ModifiedUtc, int Count)> CountCache =
            new Dictionary<string, (long, DateTime, int)>(StringComparer.OrdinalIgnoreCase);

        /// <summary>歌单目录（不保证存在）。</summary>
        public static string Directory => Path.Combine(AppSettings.SettingsDirectory, FolderName);

        /// <summary>歌单顺序文件的完整路径。</summary>
        public static string OrderFilePath => Path.Combine(Directory, OrderFileName);

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

        // ---- 查询 -------------------------------------------------------------

        /// <summary>
        /// 列出歌单库里的全部歌单。
        /// <para>
        /// 顺序：<b>用户在歌单窗口里拖出来的顺序优先</b>（见 <see cref="OrderFileName"/>），
        /// 没被拖过的（新导入 / 新建的，以及顺序文件里已经不存在的老名字）跟在后面按<b>自然顺序</b>排
        /// （"歌单2" 在 "歌单10" 前面）。没有顺序文件时就是纯自然顺序，和以前一样。
        /// </para>
        /// </summary>
        public static IReadOnlyList<SavedPlaylist> List()
        {
            var found = new List<SavedPlaylist>();
            var directory = Directory;

            if (!System.IO.Directory.Exists(directory)) return found;

            IEnumerable<string> files;
            try
            {
                files = System.IO.Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly);
            }
            catch (Exception)
            {
                return found;
            }

            // 同一个名字可能同时有 .m3u 和 .m3u8 两个文件（用户自己拷进来的），
            // 只认最新的那一个，免得菜单里出现两行一模一样的名字。
            var byName = new Dictionary<string, SavedPlaylist>(StringComparer.CurrentCultureIgnoreCase);

            foreach (var file in files)
            {
                var extension = Path.GetExtension(file);

                if (!Extensions.Any(e => string.Equals(e, extension, StringComparison.OrdinalIgnoreCase)))
                    continue;

                FileInfo info;
                try
                {
                    info = new FileInfo(file);
                    if (!info.Exists) continue;
                }
                catch (Exception)
                {
                    continue;
                }

                var name = Path.GetFileNameWithoutExtension(file);
                if (string.IsNullOrWhiteSpace(name)) continue;

                var saved = new SavedPlaylist(name, info.FullName, info.LastWriteTimeUtc, CountTracks(info), info.Length);

                if (byName.TryGetValue(name, out var existing) && existing.ModifiedUtc >= saved.ModifiedUtc)
                    continue;

                byName[name] = saved;
            }

            found.AddRange(byName.Values);
            found.Sort((a, b) => CompareNatural(a.Name, b.Name));

            return ApplyOrder(found);
        }

        // ---- 顺序（用户在歌单窗口里拖出来的那个顺序） ---------------------------

        /// <summary>
        /// 把顺序文件里的名字排到前面，其余的跟在后面（保持传进来的自然顺序）。
        /// <para>
        /// 顺序文件里<b>已经不存在的名字直接忽略</b>：删掉歌单、改名字都不会让它变成
        /// "必须定期清理的垃圾"，只会让那份歌单回到后面那一堆里。
        /// </para>
        /// </summary>
        private static List<SavedPlaylist> ApplyOrder(List<SavedPlaylist> playlists)
        {
            var order = ReadOrder();
            if (order.Count == 0) return playlists;

            var rest = new List<SavedPlaylist>(playlists);
            var ordered = new List<SavedPlaylist>(playlists.Count);

            foreach (var name in order)
            {
                var index = rest.FindIndex(p => string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase));
                if (index < 0) continue;

                ordered.Add(rest[index]);
                rest.RemoveAt(index);
            }

            ordered.AddRange(rest);
            return ordered;
        }

        /// <summary>读顺序文件（一行一个名字，空行忽略）；没有 / 读不出来时返回空表。</summary>
        private static List<string> ReadOrder()
        {
            var names = new List<string>();

            try
            {
                var path = OrderFilePath;
                if (!File.Exists(path)) return names;

                foreach (var line in File.ReadAllLines(path))
                {
                    var name = line.Trim();
                    if (name.Length > 0) names.Add(name);
                }
            }
            catch (Exception)
            {
                // 读不出来就当"还没排过"：顺序是锦上添花，不该因为它挡住歌单列表。
                return new List<string>();
            }

            return names;
        }

        /// <summary>
        /// 把 <paramref name="names"/> 记成歌单顺序（写进顺序文件）。
        /// <para>只写<b>现在真的存在</b>的名字，重复的只留一次——顺序文件里不该留下不存在的名字。</para>
        /// </summary>
        public static bool SetOrder(IReadOnlyList<string> names, out string error)
        {
            error = string.Empty;
            if (names == null) throw new ArgumentNullException(nameof(names));

            var existing = new HashSet<string>(
                List().Select(playlist => playlist.Name), StringComparer.CurrentCultureIgnoreCase);

            var lines = new List<string>();
            var seen = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);

            foreach (var name in names)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (!existing.Contains(name)) continue;
                if (!seen.Add(name)) continue;

                lines.Add(name);
            }

            return WriteOrder(lines, out error);
        }

        /// <summary>
        /// 把某一份歌单挪到第 <paramref name="targetIndex"/> 位（拖放落下去时做的事），并把新顺序记进顺序文件。
        /// <para><paramref name="targetIndex"/> 是<b>挪完之后的最终下标</b>（0 = 排在第一个）。</para>
        /// </summary>
        public static bool Move(string name, int targetIndex, out string error)
        {
            error = string.Empty;

            var names = List().Select(playlist => playlist.Name).ToList();
            var from = names.FindIndex(
                candidate => string.Equals(candidate, name, StringComparison.CurrentCultureIgnoreCase));

            if (from < 0)
            {
                error = $"歌单库里没有「{name}」。";
                return false;
            }

            if (names.Count < 2) return true;      // 只有一份，没什么可挪的

            targetIndex = Math.Clamp(targetIndex, 0, names.Count - 1);
            if (targetIndex == from) return true;

            var moved = names[from];
            names.RemoveAt(from);
            names.Insert(targetIndex, moved);

            return SetOrder(names, out error);
        }

        /// <summary>写顺序文件（原子写）；失败返回 false 并给出理由。</summary>
        private static bool WriteOrder(List<string> names, out string error)
        {
            error = string.Empty;

            try
            {
                EnsureDirectory();

                // 末尾补一个换行：用记事本打开时最后一行不会和"文件末尾"粘在一起。
                var text = names.Count == 0
                    ? string.Empty
                    : string.Join(Environment.NewLine, names) + Environment.NewLine;

                SafeFile.WriteAllText(OrderFilePath, text);
                return true;
            }
            catch (Exception ex)
            {
                error = "记顺序失败：" + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 顺序文件里把旧名字换成新名字。
        /// <para>
        /// 改名之后那份歌单<b>不该掉到"没排过"的那一堆里</b>——用户拖出来的位置还是个位置。
        /// 顺序文件坏了 / 写不进去也不该让改名失败，所以这里只记一条日志、不改返回值。
        /// </para>
        /// </summary>
        private static void ReplaceOrderName(string oldName, string newName)
        {
            var order = ReadOrder();
            if (order.Count == 0) return;

            var changed = false;

            for (var i = 0; i < order.Count; i++)
            {
                if (!string.Equals(order[i], oldName, StringComparison.CurrentCultureIgnoreCase)) continue;

                order[i] = newName;
                changed = true;
            }

            if (changed && !WriteOrder(order, out var error)) AppLog.Info("歌单顺序文件没更新成：" + error);
        }

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

        // ---- 条目数缓存 -------------------------------------------------------

        /// <summary>清掉条目数缓存（歌单被别的东西改了、或者测试要重读时用）。</summary>
        public static void Invalidate() => CountCache.Clear();

        /// <summary>
        /// 数一个歌单文件里有多少条目；读不出来返回 -1。
        /// <para>文件没变（长度和修改时间都一样）就直接用缓存：菜单每次下拉都要列一遍歌单。</para>
        /// </summary>
        private static int CountTracks(FileInfo info)
        {
            var path = info.FullName;

            if (CountCache.TryGetValue(path, out var cached) &&
                cached.Length == info.Length &&
                cached.ModifiedUtc == info.LastWriteTimeUtc)
            {
                return cached.Count;
            }

            int count;

            try
            {
                count = PlaylistFile.ReadPaths(path).Count;
            }
            catch (Exception)
            {
                count = -1;
            }

            CountCache[path] = (info.Length, info.LastWriteTimeUtc, count);
            return count;
        }

        // ---- 排序 -------------------------------------------------------------

        /// <summary>
        /// 自然顺序比较：名字里的数字按<b>数值</b>比，"歌单2" 排在 "歌单10" 前面
        /// （纯字符串比较会得到"歌单10"在前，和人眼的直觉相反）。其余部分按当前区域、不区分大小写。
        /// <para>实现在 <see cref="SavedName.CompareNatural"/>（设置方案列出来也按这个顺序）。</para>
        /// </summary>
        public static int CompareNatural(string? left, string? right) => SavedName.CompareNatural(left, right);

        /// <summary>把歌单名描述成"名字（N 项）"，N 未知时只说名字。</summary>
        public static string Describe(SavedPlaylist playlist)
        {
            if (playlist == null) throw new ArgumentNullException(nameof(playlist));

            var text = new StringBuilder(playlist.Name);
            text.Append(playlist.TrackCount < 0 ? "（读不出来）" : $"（{playlist.TrackCount} 项）");

            return text.ToString();
        }
    }
}
