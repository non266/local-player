using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using 播放器.Core;

namespace 播放器
{
    /// <summary>
    /// 主窗口的播放记忆部分：记住每个文件播到哪儿、自动续播、以及"最近播放"菜单。
    /// </summary>
    public partial class MainForm
    {
        /// <summary>每隔多少个界面计时周期（200ms）把历史落盘一次。</summary>
        private const int HistoryFlushTicks = 100;

        private readonly PlaybackHistory _history = PlaybackHistory.Load();
        private PerFileAdjustments? _adjustments;

        /// <summary>
        /// 只读入口：这份窗体的播放历史（给冒烟测试摆"写入在飞时又改了"的时序用；
        /// 主工程里有 <c>InternalsVisibleTo("SmokeTest")</c>）。
        /// </summary>
        internal PlaybackHistory History => _history;

        /// <summary>
        /// 按文件记住的调整（音画 / 字幕延迟、歌词偏移）。
        /// <para>和播放进度共用 <see cref="_history"/>，但落盘时机不同：
        /// 调整是用户调出来的，改一次就立刻落盘。</para>
        /// </summary>
        private PerFileAdjustments Adjustments => _adjustments ??= new PerFileAdjustments(_history);

        private TimeSpan? _pendingResume;
        private int _historyFlushTicks;

        /// <summary>把当前播放位置记进历史（只动内存）。由界面计时器每 200ms 调一次。</summary>
        private void RecordCurrentPosition()
        {
            if (!_settings.ResumePlayback) return;
            if (!_engine.HasMedia) return;

            var path = _engine.CurrentPath;
            if (string.IsNullOrEmpty(path)) return;

            _history.Record(
                path,
                TimeSpan.FromMilliseconds(_engine.Time),
                TimeSpan.FromMilliseconds(_engine.Length));

            // 定期落盘，程序被强制结束时最多丢 20 秒的进度。
            // 注意用后台写：序列化 2000 条记录是几百 KB，放在 UI 线程上会让画面周期性地顿一下。
            if (++_historyFlushTicks < HistoryFlushTicks) return;

            _historyFlushTicks = 0;
            _history.SaveInBackground();
        }

        /// <summary>排定续播位置：等媒体真正播起来之后再定位。</summary>
        private void ScheduleResume(string filePath)
        {
            _pendingResume = null;

            if (!_settings.ResumePlayback) return;
            if (_history.TryGetResumePosition(filePath, out var position))
                _pendingResume = position;
        }

        /// <summary>
        /// 待续播位置要等媒体真正开始播放、且总时长已知之后才能 seek，
        /// 否则 libvlc 还没准备好，定位会被丢掉。
        /// </summary>
        private void ApplyPendingResume()
        {
            if (!_pendingResume.HasValue) return;
            if (!_engine.HasMedia || !_engine.IsPlaying) return;
            if (_engine.Length <= 0) return;

            var target = _pendingResume.Value;
            _pendingResume = null;

            _engine.SeekTo((long)target.TotalMilliseconds);

            var name = _playlist.Current?.DisplayName ?? Path.GetFileName(_engine.CurrentPath ?? string.Empty);
            SetStatus($"已从 {TimeFormatter.Format(target)} 继续播放：{name}");
        }

        /// <summary>
        /// 切歌 / 暂停时把当前进度落盘。
        /// <para>写文件交给线程池（<see cref="PlaybackHistory.SaveInBackground"/>），
        /// 免得切歌时因为序列化整份历史而卡一下。</para>
        /// </summary>
        private void CommitHistory()
        {
            if (!_settings.ResumePlayback) return;

            RecordCurrentPosition();
            _history.SaveInBackground();
        }

        /// <summary>
        /// 退出前把历史<b>同步</b>写下去。不能只排队——进程马上就要结束了。
        /// </summary>
        private void FlushHistory()
        {
            if (!_settings.ResumePlayback) return;

            RecordCurrentPosition();
            _history.Save();
        }

        /// <summary>
        /// 把"设置 / 播放历史读坏了"这件事告诉用户一次。
        /// <para>
        /// 这两份文件以前损坏时都是静默退回默认值，用户的窗口尺寸、主题、播放列表和
        /// 所有续播位置会一起消失且没有任何提示——现在文件会先备份，并在状态栏说明。
        /// </para>
        /// </summary>
        private void ReportLoadWarnings()
        {
            var warnings = new System.Collections.Generic.List<string>();

            if (!string.IsNullOrEmpty(_settings.LoadWarning)) warnings.Add(_settings.LoadWarning!);
            if (!string.IsNullOrEmpty(_history.LoadWarning)) warnings.Add(_history.LoadWarning!);
            if (!string.IsNullOrEmpty(PlaylistLibraryWarning)) warnings.Add(PlaylistLibraryWarning!);

            // 媒体键只注册上一部分时也要说一句：哪个键按了没反应，用户看不出来是被人占了
            if (!string.IsNullOrEmpty(_mediaKeyWarning)) warnings.Add(_mediaKeyWarning!);

            if (warnings.Count == 0) return;

            SetStatus(string.Join(" ", warnings));
        }

        // -----------------------------------------------------------------
        // 最近播放
        // -----------------------------------------------------------------

        private void RebuildRecentMenu()
        {
            ClearMenuItems(menuFileRecent.DropDownItems);

            var recent = _history.Recent
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToList();

            if (recent.Count == 0)
            {
                menuFileRecent.DropDownItems.Add(
                    new ToolStripMenuItem("（暂无记录）") { Enabled = false });
                menuFileRecent.Enabled = false;
                return;
            }

            menuFileRecent.Enabled = true;

            for (var i = 0; i < recent.Count; i++)
            {
                var path = recent[i];

                // 菜单里的 & 是加速键前缀，文件名里带有的话必须转义。
                var name = Path.GetFileName(path).Replace("&", "&&");
                var text = i < 9 ? $"&{i + 1} {name}" : name;

                if (_history.TryGetResumePosition(path, out var position))
                    text += $"　·　{TimeFormatter.Format(position)}";

                var item = new ToolStripMenuItem(text) { ToolTipText = path };
                item.Click += (sender, e) => OpenRecentFile(path);

                menuFileRecent.DropDownItems.Add(item);
            }

            menuFileRecent.DropDownItems.Add(new ToolStripSeparator());

            var clearItem = new ToolStripMenuItem("清除最近播放(&C)");
            clearItem.Click += (sender, e) =>
            {
                _history.Clear();
                _history.Save();
                RebuildRecentMenu();
                SetStatus("已清除播放历史");
            };

            menuFileRecent.DropDownItems.Add(clearItem);
        }

        private void OpenRecentFile(string path)
        {
            if (!File.Exists(path))
            {
                SetStatus("文件已不存在：" + path);
                return;
            }

            // 已经在列表里就直接播，否则追加进去。
            var existing = _playlist.IndexOfPath(path);
            if (existing >= 0)
            {
                PlayIndex(existing);
                return;
            }

            var added = _playlist.AddRangeDistinct(new[] { path });
            if (added.Count == 0) return;

            _scanner.Enqueue(new[] { path });
            PlayIndex(added[0]);
        }

        /// <summary>清掉所有文件的播放进度，但保留"最近播放"列表。</summary>
        private void ClearAllResumePositions()
        {
            _history.ClearPositions();
            _history.Save();

            _pendingResume = null;
            SetStatus("已清除所有文件的续播进度");
        }
    }
}
