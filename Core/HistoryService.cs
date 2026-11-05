using System;

namespace 播放器.Core
{
    /// <summary>
    /// 播放历史与"按文件记住的调整"的<b>唯一主人</b>：什么时候记、什么时候落盘、按什么节奏落盘。
    /// <para>
    /// 以前这些散在 <c>MainForm.History.cs</c> 里：<c>_history</c> / <c>_adjustments</c> /
    /// <c>_pendingResume</c> / <c>_historyFlushTicks</c> 四个字段，加上"每 100 拍后台落一次盘"
    /// 的节奏。收进来之后，界面只调 <see cref="Tick"/> / <see cref="Commit"/> / <see cref="Flush"/>，
    /// 不再自己数拍子、也不自己决定什么时候写。
    /// </para>
    /// <para>线程约定与搬家之前一样：全部在界面线程上调；真正写文件由 <see cref="PlaybackHistory"/> 内部排队。</para>
    /// </summary>
    public sealed class HistoryService
    {
        /// <summary>每隔多少个界面计时周期（200ms）把历史落盘一次。</summary>
        private const int FlushEveryTicks = 100;

        private readonly PlaybackHistory _history;

        private PerFileAdjustments? _adjustments;

        private int _flushTicks;

        public HistoryService(PlaybackHistory? history = null)
        {
            _history = history ?? PlaybackHistory.Load();
        }

        /// <summary>底层记录（测试与「最近播放」菜单读它）。</summary>
        public PlaybackHistory History => _history;

        /// <summary>按文件记住的调整（音画 / 字幕延迟、歌词偏移、画面旋转、轨道）。</summary>
        public PerFileAdjustments Adjustments => _adjustments ??= new PerFileAdjustments(_history);

        /// <summary>读取历史失败时的说明（正常时为 <c>null</c>）。</summary>
        public string? LoadWarning => _history.LoadWarning;

        /// <summary>这个文件该从哪儿接着播（没记过 / 开关关掉时由调用方决定要不要用）。</summary>
        public TimeSpan? ResumePositionLookup(string filePath) =>
            _history.TryGetResumePosition(filePath, out var position) ? position : (TimeSpan?)null;

        /// <summary>
        /// 界面计时器每一拍：记位置（或只记"播过"），并每 100 拍后台落一次盘。
        /// <para>
        /// 「记住播放进度」<b>只控制"记不记位置"</b>：关掉之后仍然记"这个文件播过"，
        /// 否则「文件 → 最近播放」永远是空的（1.9.0 修，见 docs/1.9.0计划.md 的 A4）。
        /// </para>
        /// </summary>
        public void Tick(string? path, long positionMilliseconds, long lengthMilliseconds, bool rememberPosition)
        {
            if (string.IsNullOrEmpty(path)) return;

            Record(path, positionMilliseconds, lengthMilliseconds, rememberPosition);

            // 定期落盘，程序被强制结束时最多丢 20 秒的进度。
            // 注意用后台写：序列化 2000 条记录是几百 KB，放在 UI 线程上会让画面周期性地顿一下。
            if (++_flushTicks < FlushEveryTicks) return;

            _flushTicks = 0;
            _history.SaveInBackground();
        }

        /// <summary>切歌 / 暂停时：记一次并排队写盘（不阻塞界面）。</summary>
        public void Commit(string? path, long positionMilliseconds, long lengthMilliseconds, bool rememberPosition)
        {
            if (string.IsNullOrEmpty(path)) return;

            Record(path, positionMilliseconds, lengthMilliseconds, rememberPosition);
            _history.SaveInBackground();
        }

        /// <summary>退出前：记一次并<b>同步</b>写盘（进程马上就要结束了，等不了后台）。</summary>
        public void Flush(string? path, long positionMilliseconds, long lengthMilliseconds, bool rememberPosition)
        {
            if (!string.IsNullOrEmpty(path))
                Record(path, positionMilliseconds, lengthMilliseconds, rememberPosition);

            _history.Save();
        }

        private void Record(string path, long positionMilliseconds, long lengthMilliseconds, bool rememberPosition)
        {
            if (rememberPosition)
            {
                _history.Record(
                    path,
                    TimeSpan.FromMilliseconds(positionMilliseconds),
                    TimeSpan.FromMilliseconds(lengthMilliseconds));
            }
            else
            {
                _history.MarkPlayed(path);
            }
        }
    }
}
