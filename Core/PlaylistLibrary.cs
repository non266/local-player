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
    public static partial class PlaylistLibrary
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
