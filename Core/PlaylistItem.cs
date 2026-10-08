using System;
using System.IO;

namespace 播放器.Core
{
    /// <summary>
    /// 播放列表中的一项。时长在后台扫描到之前为未知。
    /// </summary>
    public sealed class PlaylistItem
    {
        public PlaylistItem(string filePath)
        {
            FilePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        }

        /// <summary>本地绝对路径或网络流地址。</summary>
        public string FilePath { get; }

        /// <summary>加入列表时的序号。排序之后仍然保留，用于"恢复原始顺序"。</summary>
        public int AddedOrder { get; set; }

        /// <summary>是否为网络流。</summary>
        public bool IsStream => MediaFormats.IsStreamUri(FilePath);

        /// <summary>带扩展名的文件名。</summary>
        public string FileName
        {
            get
            {
                if (IsStream) return FilePath;
                try
                {
                    return Path.GetFileName(FilePath);
                }
                catch (ArgumentException)
                {
                    return FilePath;
                }
            }
        }

        /// <summary>不带扩展名的文件名，作为列表中的显示标题。</summary>
        public string DisplayName
        {
            get
            {
                var name = FileName;
                var ext = Path.GetExtension(name);
                return string.IsNullOrEmpty(ext) ? name : name.Substring(0, name.Length - ext.Length);
            }
        }

        /// <summary>媒体时长；未知时为 <c>null</c>。</summary>
        public TimeSpan? Duration { get; set; }

        /// <summary>列表里显示的时长文本。</summary>
        public string DurationText =>
            Duration.HasValue ? TimeFormatter.Format(Duration.Value) : "--:--";

        /// <summary>该项是否已被标记为无法播放（文件缺失或解码失败）。</summary>
        public bool HasError { get; set; }

        /// <summary>无法播放的原因。</summary>
        public string? ErrorMessage { get; set; }

        /// <summary>
        /// 是否排在「下一首播放」队列里（列表标题前显示 <c>▶</c>）。
        /// <para>
        /// <b>只在内存里用，不写盘</b>：那块标记跟着 <see cref="PlaybackQueue"/> 走，
        /// 而队列本身不落盘（见它的说明），所以下次打开程序时不该留下 <c>▶</c>。
        /// </para>
        /// </summary>
        public bool Queued { get; set; }

        public override string ToString() => DisplayName;
    }
}
