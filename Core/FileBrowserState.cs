using System;
using System.Collections.Generic;

namespace 播放器.Core
{
    /// <summary>文件列表的排序依据。</summary>
    internal enum FileSortField
    {
        Name,
        Size,
        Modified,
        Type
    }

    /// <summary>
    /// 排序依据的落盘形式。
    /// <para>
    /// <b>刻意存字符串而不是枚举</b>：设置文件是纯文本，手改 / 换版本时可能出现不认识的值，
    /// 而 <c>JsonStringEnumConverter</c> 碰到未知枚举会让<b>整份设置</b>读不出来
    /// （项目里 `WindowMode` / `Deinterlace` 都是同一个理由）。认不出来就回"名称"。
    /// </para>
    /// </summary>
    internal static class FileSortFields
    {
        internal const string ByName = "Name";
        internal const string BySize = "Size";
        internal const string ByModified = "Modified";
        internal const string ByType = "Type";

        internal static string Normalize(string? value)
        {
            var text = (value ?? string.Empty).Trim();

            if (string.Equals(text, BySize, StringComparison.OrdinalIgnoreCase)) return BySize;
            if (string.Equals(text, ByModified, StringComparison.OrdinalIgnoreCase)) return ByModified;
            if (string.Equals(text, ByType, StringComparison.OrdinalIgnoreCase)) return ByType;

            return ByName;
        }

        internal static FileSortField Parse(string? value) => Normalize(value) switch
        {
            BySize => FileSortField.Size,
            ByModified => FileSortField.Modified,
            ByType => FileSortField.Type,
            _ => FileSortField.Name
        };

        internal static string NameOf(FileSortField field) => field switch
        {
            FileSortField.Size => BySize,
            FileSortField.Modified => ByModified,
            FileSortField.Type => ByType,
            _ => ByName
        };
    }

    /// <summary>
    /// 文件浏览器的导航状态：当前目录、返回 / 前进栈、收藏、最近浏览、排序与过滤开关。
    /// <para>
    /// 纯对象，不碰磁盘、不碰控件。界面只调这里的方法；主窗体只在退出 / 设置方案时
    /// <see cref="ApplyTo"/> 一次。历史记录里的 <c>null</c> 表示"此电脑"。
    /// </para>
    /// </summary>
    internal sealed class FileBrowserState
    {
        internal const int HistoryCapacity = 50;
        internal const int FavoriteCapacity = 20;
        internal const int RecentCapacity = 10;

        private readonly List<string?> _back = new List<string?>();
        private readonly List<string?> _forward = new List<string?>();

        /// <summary>当前目录；<c>null</c> = 此电脑。</summary>
        internal string? CurrentPath { get; private set; }

        /// <summary>最后一次<b>读取成功</b>的目录（退出时写进设置，坏路径不会被记住）。</summary>
        internal string? LastSuccessfulPath { get; private set; }

        internal bool CanGoBack => _back.Count > 0;

        internal bool CanGoForward => _forward.Count > 0;

        internal bool CanGoUp => CurrentPath != null;

        internal int BackCount => _back.Count;

        internal int ForwardCount => _forward.Count;

        internal List<string> Favorites { get; } = new List<string>();

        internal List<string> Recent { get; } = new List<string>();

        internal FileSortField SortField { get; set; } = FileSortField.Name;

        internal bool SortDescending { get; set; }

        internal bool ShowAllFiles { get; set; }

        /// <summary>用平铺缩略图显示音视频封面（1.27.0）。</summary>
        internal bool ShowThumbnails { get; set; }

        /// <summary>进入一个目录（会记一条后退历史，并清空前进栈）。</summary>
        internal void Navigate(string? path)
        {
            if (Same(CurrentPath, path)) return;

            Push(_back, CurrentPath);
            _forward.Clear();
            CurrentPath = path;
        }

        /// <summary>后退一步；没有历史时返回 <c>false</c>（目标可能是此电脑，所以用 bool 而不是 null 判断）。</summary>
        internal bool TryGoBack(out string? path)
        {
            if (_back.Count == 0)
            {
                path = null;
                return false;
            }

            Push(_forward, CurrentPath);

            var index = _back.Count - 1;
            path = _back[index];
            _back.RemoveAt(index);
            CurrentPath = path;
            return true;
        }

        /// <summary>前进一步；没有前进历史时返回 <c>false</c>。</summary>
        internal bool TryGoForward(out string? path)
        {
            if (_forward.Count == 0)
            {
                path = null;
                return false;
            }

            Push(_back, CurrentPath);

            var index = _forward.Count - 1;
            path = _forward[index];
            _forward.RemoveAt(index);
            CurrentPath = path;
            return true;
        }

        /// <summary>清空历史并直接把当前位置设成 <paramref name="path"/>（启动恢复用）。</summary>
        internal void Reset(string? path)
        {
            _back.Clear();
            _forward.Clear();
            CurrentPath = path;
            LastSuccessfulPath = path;
        }

        /// <summary>一次目录读取成功：记住它，并推进"最近浏览"。</summary>
        internal void NoteSuccessfulLoad(string? path)
        {
            LastSuccessfulPath = path;

            if (path != null) PushRecent(path);
        }

        /// <summary>收藏一个目录；已经在收藏里返回 <c>false</c>。</summary>
        internal bool AddFavorite(string? path)
        {
            if (!FileBrowserPaths.TryNormalize(path, out var normalized, out _) || normalized == null)
                return false;

            if (IsFavorite(normalized)) return false;

            Favorites.Insert(0, normalized);
            TrimBack(Favorites, FavoriteCapacity);
            return true;
        }

        internal bool RemoveFavorite(string? path)
        {
            if (!FileBrowserPaths.TryNormalize(path, out var normalized, out _) || normalized == null)
                return false;

            for (var i = 0; i < Favorites.Count; i++)
            {
                if (!Same(Favorites[i], normalized)) continue;

                Favorites.RemoveAt(i);
                return true;
            }

            return false;
        }

        internal bool IsFavorite(string? path)
        {
            if (!FileBrowserPaths.TryNormalize(path, out var normalized, out _) || normalized == null)
                return false;

            foreach (var favorite in Favorites)
            {
                if (Same(favorite, normalized)) return true;
            }

            return false;
        }

        /// <summary>推进"最近浏览"：去重、最新在前、最多 <see cref="RecentCapacity"/> 条。</summary>
        internal void PushRecent(string? path)
        {
            if (!FileBrowserPaths.TryNormalize(path, out var normalized, out _) || normalized == null)
                return;

            for (var i = 0; i < Recent.Count; i++)
            {
                if (!Same(Recent[i], normalized)) continue;

                Recent.RemoveAt(i);
                break;
            }

            Recent.Insert(0, normalized);
            TrimBack(Recent, RecentCapacity);
        }

        internal void ClearRecent() => Recent.Clear();

        /// <summary>从设置还原；坏路径 / 坏排序值都安静地回默认。</summary>
        internal static FileBrowserState FromSettings(AppSettings settings)
        {
            var path = FileBrowserPaths.TryNormalize(settings.FileBrowserLastPath, out var normalized, out _)
                ? normalized
                : null;

            var state = new FileBrowserState
            {
                CurrentPath = path,
                LastSuccessfulPath = path,
                SortField = FileSortFields.Parse(settings.FileBrowserSort),
                SortDescending = settings.FileBrowserSortDescending,
                ShowAllFiles = settings.FileBrowserShowAllFiles,
                ShowThumbnails = settings.FileBrowserThumbnails
            };

            state.Favorites.AddRange(NormalizeList(settings.FileBrowserFavorites, FavoriteCapacity));
            state.Recent.AddRange(NormalizeList(settings.FileBrowserRecent, RecentCapacity));

            return state;
        }

        /// <summary>写回设置（只改内存字段，落盘由主窗体统一做）。</summary>
        internal void ApplyTo(AppSettings settings)
        {
            settings.FileBrowserLastPath = LastSuccessfulPath ?? string.Empty;
            settings.FileBrowserFavorites = new List<string>(Favorites);
            settings.FileBrowserRecent = new List<string>(Recent);
            settings.FileBrowserSort = FileSortFields.NameOf(SortField);
            settings.FileBrowserSortDescending = SortDescending;
            settings.FileBrowserShowAllFiles = ShowAllFiles;
            settings.FileBrowserThumbnails = ShowThumbnails;
        }

        /// <summary>把一份设置列表归一：只留能当目录的绝对路径、去重（不区分大小写）、截到上限。</summary>
        internal static List<string> NormalizeList(IEnumerable<string>? raw, int capacity)
        {
            var result = new List<string>();
            if (raw == null) return result;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in raw)
            {
                if (!FileBrowserPaths.TryNormalize(item, out var path, out _) || path == null) continue;
                if (!seen.Add(path)) continue;

                result.Add(path);

                if (result.Count >= capacity) break;
            }

            return result;
        }

        private static void Push(List<string?> stack, string? path)
        {
            stack.Add(path);
            TrimFront(stack, HistoryCapacity);
        }

        /// <summary>历史栈：新的追加在末尾，超限时丢最前面（最旧的）。</summary>
        private static void TrimFront<T>(List<T> list, int capacity)
        {
            while (list.Count > capacity)
                list.RemoveAt(0);
        }

        /// <summary>收藏 / 最近：新的插在最前面，超限时丢最后面（最旧的）。</summary>
        private static void TrimBack<T>(List<T> list, int capacity)
        {
            while (list.Count > capacity)
                list.RemoveAt(list.Count - 1);
        }

        private static bool Same(string? left, string? right) =>
            string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }
}
