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
        /// <summary>
        /// 播放历史与"按文件记住的调整"都归它：什么时候记、什么时候落盘、每 100 拍后台写一次。
        /// <para>界面只在这里调 Tick / Commit / Flush，不自己数拍子（见 <see cref="HistoryService"/>）。</para>
        /// </summary>
        private readonly HistoryService _historyService = new HistoryService();

        /// <summary>
        /// 只读入口：这份窗体的播放历史（给冒烟测试摆"写入在飞时又改了"的时序用；
        /// 主工程里有 <c>InternalsVisibleTo("SmokeTest")</c>）。
        /// </summary>
        internal PlaybackHistory History => _historyService.History;

        /// <summary>
        /// 按文件记住的调整（音画 / 字幕延迟、歌词偏移、画面旋转、轨道）。
        /// <para>和播放进度共用同一份记录，但落盘时机不同：调整是用户调出来的，改一次就立刻落盘。</para>
        /// </summary>
        private PerFileAdjustments Adjustments => _historyService.Adjustments;

        /// <summary>把当前播放位置记进历史（只动内存，每 100 拍后台落一次盘）。由界面计时器每 200ms 调一次。</summary>
        private void RecordCurrentPosition() =>
            _historyService.Tick(
                _engine.HasMedia ? _engine.CurrentPath : null,
                _engine.Time,
                _engine.Length,
                _settings.ResumePlayback);

        /// <summary>
        /// 给播放业务查"这个文件该从哪儿接着播"。
        /// <para>续播的<b>时机</b>（要等媒体播起来 + 总时长已知）在
        /// <see cref="PlaybackCoordinator"/> 里，这里只回答"记的是多少"。</para>
        /// </summary>
        private TimeSpan? ResumePositionLookup(string filePath) => _historyService.ResumePositionLookup(filePath);

        /// <summary>
        /// 切歌 / 暂停时把当前进度落盘。
        /// <para><b>不以「记住播放进度」为前提</b>：那个开关只决定记不记位置，
        /// 最近播放与按文件记下的调整都要落盘（1.9.0 修，以前关掉开关这里整个早退）。</para>
        /// </summary>
        private void CommitHistory() =>
            _historyService.Commit(
                _engine.HasMedia ? _engine.CurrentPath : null,
                _engine.Time,
                _engine.Length,
                _settings.ResumePlayback);

        /// <summary>退出前把历史<b>同步</b>写下去。不能只排队——进程马上就要结束了。</summary>
        private void FlushHistory() =>
            _historyService.Flush(
                _engine.HasMedia ? _engine.CurrentPath : null,
                _engine.Time,
                _engine.Length,
                _settings.ResumePlayback);

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
            if (!string.IsNullOrEmpty(History.LoadWarning)) warnings.Add(History.LoadWarning!);
            if (!string.IsNullOrEmpty(PlaylistLibraryWarning)) warnings.Add(PlaylistLibraryWarning!);

            // 媒体键只注册上一部分时也要说一句：哪个键按了没反应，用户看不出来是被人占了
            if (!string.IsNullOrEmpty(_mediaKeyWarning)) warnings.Add(_mediaKeyWarning!);

            if (warnings.Count == 0) return;

            SetStatus(string.Join(" ", warnings));
        }

        // -----------------------------------------------------------------
        // 最近播放
        // -----------------------------------------------------------------

        /// <summary>
        /// 给冒烟测试用：按当前历史重排「最近播放」菜单（平时由下拉触发）。
        /// <para>菜单里那串「· 位置」是否显示，取决于「记住播放进度」——这条只有靠它才验得到。</para>
        /// </summary>
        internal void RefreshRecentMenu() => RebuildRecentMenu();

        private void RebuildRecentMenu()
        {
            ClearMenuItems(menuFileRecent.DropDownItems);

            var recent = History.Recent
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

                // 「· 位置」只在"记住播放进度"开着时显示：关着的时候打开它也不会续播，
                // 显示出来就是骗人（1.9.0 修，见 docs/1.9.0计划.md 的 A4）。
                if (_settings.ResumePlayback && History.TryGetResumePosition(path, out var position))
                    text += $"　·　{TimeFormatter.Format(position)}";

                var item = new ToolStripMenuItem(text) { ToolTipText = path };
                item.Click += (sender, e) => OpenRecentFile(path);

                menuFileRecent.DropDownItems.Add(item);
            }

            menuFileRecent.DropDownItems.Add(new ToolStripSeparator());

            var clearItem = new ToolStripMenuItem("清除最近播放(&C)");
            clearItem.Click += (sender, e) =>
            {
                History.Clear();
                History.Save();
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
            History.ClearPositions();
            History.Save();

            _playback.ClearPendingResume();
            SetStatus("已清除所有文件的续播进度");
        }
    }
}
