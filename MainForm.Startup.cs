using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using 播放器.Core;
using 播放器.Ui;

namespace 播放器
{
    /// <summary>启动与设置：接线、把设置铺到界面上、窗口位置恢复、命令行文件与会话恢复。</summary>
    public partial class MainForm : Form
    {
        // =====================================================================
        // 初始化
        // =====================================================================
        private void ConfigureToolStrips()
        {
            // 图标不在这里设置：它依赖主题（深色主题要用浅色线条），由 ApplyTheme -> ApplyIcons 负责。

            // 播放列表工具条用文字按钮，含义比图标更直观。
            foreach (var button in new[] { tspAdd, tspRemove, tspClear, tspUp, tspDown })
                button.DisplayStyle = ToolStripItemDisplayStyle.Text;

            tspAdd.Text = "添加";
            tspRemove.Text = "移除";
            tspClear.Text = "清空";
            tspUp.Text = "上移";
            tspDown.Text = "下移";

            // 画面比例菜单项用 Tag 承载实际传给 VLC 的字符串。
            menuAspectDefault.Tag = string.Empty;
            menuAspect169.Tag = "16:9";
            menuAspect43.Tag = "4:3";
            menuAspect185.Tag = "1.85:1";
            menuAspect235.Tag = "2.35:1";
            menuAspectOriginal.Tag = "1:1";
        }

        private void WireEvents()
        {
            // ---- 文件菜单 ----
            menuFileOpen.Click += (s, e) => OpenFilesDialog();
            menuFileOpenFolder.Click += (s, e) => OpenFolderDialog();
            menuFileOpenUrl.Click += (s, e) => OpenUrlDialog();
            menuFileExit.Click += (s, e) => Close();

            // ---- 播放菜单 ----
            menuPlayPause.Click += (s, e) => TogglePlayPause();
            menuStop.Click += (s, e) => StopPlayback();
            menuPrev.Click += (s, e) => PlayPrevious();
            menuNext.Click += (s, e) => PlayNext(true);
            menuSeekBack.Click += (s, e) => SeekRelative(-SeekStepMilliseconds);
            menuSeekForward.Click += (s, e) => SeekRelative(SeekStepMilliseconds);
            menuVolumeUp.Click += (s, e) => AdjustVolume(5);
            menuVolumeDown.Click += (s, e) => AdjustVolume(-5);
            menuMute.Click += (s, e) => ToggleMute();
            menuRateHalf.Click += (s, e) => SetRate(0.5f);
            menuRate075.Click += (s, e) => SetRate(0.75f);
            menuRateNormal.Click += (s, e) => SetRate(1.0f);
            menuRate125.Click += (s, e) => SetRate(1.25f);
            menuRate150.Click += (s, e) => SetRate(1.5f);
            menuRate200.Click += (s, e) => SetRate(2.0f);
            menuRepeatNone.Click += (s, e) => SetRepeatMode(RepeatMode.None);
            menuRepeatOne.Click += (s, e) => SetRepeatMode(RepeatMode.One);
            menuRepeatAll.Click += (s, e) => SetRepeatMode(RepeatMode.All);
            menuShuffle.CheckedChanged += OnShuffleChanged;

            // ---- 播放记忆与系统集成 ----
            menuResumePlayback.CheckedChanged += OnResumePlaybackChanged;
            menuGlobalMediaKeys.CheckedChanged += OnGlobalMediaKeysChanged;
            menuClearResume.Click += (s, e) => ClearAllResumePositions();
            menuFileRecent.DropDownOpening += (s, e) => RebuildRecentMenu();

            // ---- 字幕菜单 ----
            menuSubtitleOpen.Click += (s, e) => OpenSubtitleDialog();
            menuSubtitleDisable.Click += (s, e) => DisableSubtitle();

            // ---- 视图菜单 ----
            menuViewPlaylist.CheckedChanged += OnShowPlaylistChanged;
            menuViewFullscreen.Click += (s, e) => ToggleFullscreen();
            menuThemeAuto.Click += (s, e) => ApplyTheme(ThemeId.Auto);
            menuThemeLight.Click += (s, e) => ApplyTheme(ThemeId.Light);
            menuThemeDark.Click += (s, e) => ApplyTheme(ThemeId.Dark);
            menuThemeOled.Click += (s, e) => ApplyTheme(ThemeId.Oled);
            menuThemeVlc.Click += (s, e) => ApplyTheme(ThemeId.Vlc);
            menuAspectDefault.Click += (s, e) => SetAspectRatio(menuAspectDefault);
            menuAspect169.Click += (s, e) => SetAspectRatio(menuAspect169);
            menuAspect43.Click += (s, e) => SetAspectRatio(menuAspect43);
            menuAspect185.Click += (s, e) => SetAspectRatio(menuAspect185);
            menuAspect235.Click += (s, e) => SetAspectRatio(menuAspect235);
            menuAspectOriginal.Click += (s, e) => SetAspectRatio(menuAspectOriginal);
            menuViewTopMost.CheckedChanged += OnTopMostChanged;

            // ---- 帮助菜单 ----
            menuHelpShortcuts.Click += (s, e) => ShowShortcuts();
            menuAbout.Click += (s, e) => ShowAbout();

            // ---- 工具栏 ----
            tsbOpen.Click += (s, e) => OpenFilesDialog();
            tsbOpenFolder.Click += (s, e) => OpenFolderDialog();
            tsbPlay.Click += (s, e) => PlayButtonClicked();
            tsbPause.Click += (s, e) => _engine.Pause();
            tsbStop.Click += (s, e) => StopPlayback();
            tsbPrev.Click += (s, e) => PlayPrevious();
            tsbNext.Click += (s, e) => PlayNext(true);
            tsbRewind.Click += (s, e) => SeekRelative(-SeekStepMilliseconds);
            tsbForward.Click += (s, e) => SeekRelative(SeekStepMilliseconds);
            tsbSnapshot.Click += (s, e) => TakeSnapshot();
            tsbTogglePlaylist.Click += (s, e) => menuViewPlaylist.Checked = !menuViewPlaylist.Checked;

            // ---- 进度与音量 ----
            trackSeek.MouseDown += (s, e) => _userSeeking = true;
            trackSeek.MouseUp += OnSeekMouseUp;
            trackSeek.Scroll += OnSeekScroll;
            trackVolume.Scroll += OnVolumeScroll;

            // ---- 播放列表 ----
            listViewPlaylist.DoubleClick += OnPlaylistDoubleClick;
            listViewPlaylist.KeyDown += OnPlaylistKeyDown;
            listViewPlaylist.MouseUp += OnPlaylistMouseUp;
            listViewPlaylist.ColumnClick += OnPlaylistColumnClick;

            // 列宽随面板宽度自适应（侧栏显示时这块面板会窄很多）
            listViewPlaylist.Resize += (s, e) => AdjustPlaylistColumns();

            // 拖动列表项换位
            listViewPlaylist.ItemDrag += OnPlaylistItemDrag;
            listViewPlaylist.DragOver += OnPlaylistDragOver;
            listViewPlaylist.DragDrop += OnPlaylistReorderDrop;

            // 搜索
            txtPlaylistSearch.TextChanged += (s, e) => ApplyPlaylistFilter(txtPlaylistSearch.Text);
            txtPlaylistSearch.KeyDown += (s, e) =>
            {
                if (e.KeyCode != Keys.Escape) return;
                ClearPlaylistFilter();
                e.Handled = true;
                e.SuppressKeyPress = true;
            };

            // 排序
            cmsSortName.Click += (s, e) => ToggleSort(PlaylistSortField.Name);
            cmsSortPath.Click += (s, e) => ToggleSort(PlaylistSortField.Path);
            cmsSortDuration.Click += (s, e) => ToggleSort(PlaylistSortField.Duration);
            cmsSortOriginal.Click += (s, e) => ApplySort(PlaylistSortField.AddedOrder, false);

            tspAdd.Click += (s, e) => OpenFilesDialog();
            tspRemove.Click += (s, e) => RemoveSelectedPlaylistItems();
            tspClear.Click += (s, e) => ClearPlaylist();
            tspUp.Click += (s, e) => MoveSelected(-1);
            tspDown.Click += (s, e) => MoveSelected(1);

            cmsPlay.Click += (s, e) => PlaySelectedItem();
            cmsRemove.Click += (s, e) => RemoveSelectedPlaylistItems();
            cmsMoveUp.Click += (s, e) => MoveSelected(-1);
            cmsMoveDown.Click += (s, e) => MoveSelected(1);
            cmsReveal.Click += (s, e) => RevealSelectedInExplorer();
            cmsCopyPath.Click += (s, e) => CopySelectedPaths();
            cmsClear.Click += (s, e) => ClearPlaylist();

            // ---- 拖放 ----
            DragEnter += OnDragEnter;
            DragDrop += OnDragDrop;
            foreach (Control target in new Control[] { listViewPlaylist, videoView, lblPlaceholder })
            {
                target.AllowDrop = true;
                target.DragEnter += OnDragEnter;
                target.DragDrop += OnDragDrop;
            }

            // ---- 视频区交互 ----
            videoView.DoubleClick += (s, e) => ToggleFullscreen();
            lblPlaceholder.DoubleClick += (s, e) => OpenFilesDialog();
            videoView.MouseWheel += OnSurfaceMouseWheel;
            lblPlaceholder.MouseWheel += OnSurfaceMouseWheel;
            pnlTransport.MouseWheel += OnSurfaceMouseWheel;

            // ---- 引擎与模型 ----
            _engine.StateChanged += OnEngineStateChanged;
            _engine.PlaybackEnded += OnEnginePlaybackEnded;
            _engine.ErrorOccurred += OnEngineError;
            _engine.TracksChanged += OnEngineTracksChanged;
            _playlist.Changed += OnPlaylistModelChanged;

            // 吸附在主窗口边上的悬浮窗要跟着主窗口一起走。
            // Move 与 Resize 都接：缩放主窗口时贴在外侧的窗口也得跟着让位。
            Move += (s, e) => LayoutAttachedWindows();
            Resize += (s, e) => LayoutAttachedWindows();

            FormClosing += OnFormClosingInternal;
            FormClosed += OnFormClosedInternal;
        }

        /// <summary>
        /// 把设置摆到界面上。
        /// <para>
        /// <paramref name="resetTrackPanel"/> 为 false 时<b>不动</b>侧栏里当前曲目的内容：
        /// 启动时（还没有曲目）要清成空状态，而运行中载入一份设置方案时不能这么干——
        /// 那会把正在听的那首歌的歌词 / 封面抹掉。
        /// </para>
        /// </summary>
        private void ApplySettings(bool resetTrackPanel = true)
        {
            _suspendUiEvents = true;
            try
            {
                trackVolume.Value = Math.Clamp(_settings.Volume, trackVolume.Minimum, trackVolume.Maximum);
                lblVolumeValue.Text = trackVolume.Value + "%";
                _engine.Volume = trackVolume.Value;
                _engine.Muted = _settings.Muted;
                _engine.Rate = _settings.Rate <= 0 ? 1.0f : _settings.Rate;

                menuShuffle.Checked = _settings.Shuffle;
                menuViewTopMost.Checked = _settings.AlwaysOnTop;
                TopMost = _settings.AlwaysOnTop;

                menuViewPlaylist.Checked = _settings.ShowPlaylist;
                splitMain.Panel2Collapsed = !_settings.ShowPlaylist;

                // 侧栏（歌词 / 封面 / 媒体信息 / 视频）；分离状态在窗口显示之后才恢复
                sidebarPanel.ActiveTab = _settings.SidebarPage;
                ApplySidebarVisibility(_settings.ShowSidebar);

                // 歌词字体要在侧栏显示之前装上，免得先按默认字体排一遍版再重排
                ApplyLyricsFont();

                if (_menuMinimizeToTray != null) _menuMinimizeToTray.Checked = _settings.MinimizeToTray;

                ApplyEqualizer();
                RestoreAudioDevice();

                // 还没有媒体，逐帧与延迟按钮先禁用
                sidebarPanel.VideoTools.SetPlaybackAvailable(false);

                menuResumePlayback.Checked = _settings.ResumePlayback;
                menuGlobalMediaKeys.Checked = _settings.GlobalMediaKeys;

                // 「当前歌单」要在恢复播放列表之前就位：标题里的前缀、覆盖保存入口都靠它
                ApplyPlaylistLibrarySettings();

                // 日志落盘开关来自设置（Program 启动时先按默认开着，这里按用户的选择纠正）
                AppLog.SetFileOutput(_settings.DiagnosticLog);

                if (_menuLogEnabled != null) _menuLogEnabled.Checked = _settings.DiagnosticLog;

                ApplyRepeatModeToMenu(_settings.RepeatMode);
                ApplyRateToMenu(_engine.Rate);
                ApplyAspectMenu(_settings.AspectRatio);
                if (!string.IsNullOrWhiteSpace(_settings.AspectRatio))
                    _engine.AspectRatio = _settings.AspectRatio;
                // 窗口位置与分隔条依赖最终布局，统一放到 OnLoad 处理。
            }
            finally
            {
                _suspendUiEvents = false;
            }

            UpdateVolumeLabel();
            RebuildTrackMenus();
            ApplySortMenu();
            RebuildRecentMenu();

            // 没开始播放之前，侧栏展示空状态而不是残留上一次的内容。
            if (resetTrackPanel) LoadTrackMetadata(null);

            // 恢复上次的排序（要在播放列表载入之前设好）。
            _sortField = _settings.SortField;
            _sortDescending = _settings.SortDescending;
            ApplySortMenu();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            // 此时控件尺寸（含 DPI 缩放）已经确定，再做依赖布局的恢复。
            RestoreWindowPlacement();
            ClampToWorkingArea();
            ApplyPlaylistVisibility(_settings.ShowPlaylist);

            // 侧栏宽度同样要等布局定下来再摆（ApplyPlaylistVisibility 里已经算过一次，
            // 这里再走一遍是因为侧栏的显示状态可能刚被改变）
            ApplySidebarVisibility(_settings.ShowSidebar);

            // 列宽最后算一次：此时播放列表面板的真实宽度才确定
            AdjustPlaylistColumns();

            // 恢复"分离出去"的窗口。放在 OnLoad 而不是 OnShown，
            // 是为了在窗口可见之前就把控件搬走，避免闪一下停靠布局。
            RestoreFloatingWindows();

            // 桌面歌词是独立的置顶窗口，和主窗口的布局无关，放这里恢复即可。
            RestoreDesktopLyrics();

            // 界面上来了再核对"上次那些文件还在不在"：网络盘离线时它会卡在 SMB 超时上，
            // 但那不该挡着窗口出来（见 RestoreSession 的说明）。
            CheckRestoredFilesInBackground(_restoredSessionFiles);
        }

        /// <summary>
        /// 把初始窗口限制在当前屏幕工作区内，避免在高 DPI 或小屏笔记本上超出屏幕。
        /// </summary>
        private void ClampToWorkingArea()
        {
            if (WindowState != FormWindowState.Normal) return;

            var area = Screen.FromControl(this).WorkingArea;
            var bounds = Bounds;

            var width = Math.Min(bounds.Width, area.Width);
            var height = Math.Min(bounds.Height, area.Height);
            if (width == bounds.Width && height == bounds.Height) return;

            Bounds = new Rectangle(
                area.X + Math.Max(0, (area.Width - width) / 2),
                area.Y + Math.Max(0, (area.Height - height) / 2),
                width,
                height);
        }

        private void RestoreWindowPlacement()
        {
            // 没有保存过位置，或者保存的尺寸小到不合理：沿用设计尺寸并居中。
            if (_settings.WindowWidth <= 0 || _settings.WindowHeight <= 0)
            {
                StartPosition = FormStartPosition.CenterScreen;
                return;
            }

            var width = Math.Max(_settings.WindowWidth, MinimumSize.Width);
            var height = Math.Max(_settings.WindowHeight, MinimumSize.Height);
            var bounds = new Rectangle(_settings.WindowX, _settings.WindowY, width, height);

            if (!IsVisibleOnAnyScreen(bounds))
            {
                StartPosition = FormStartPosition.CenterScreen;
            }
            else
            {
                StartPosition = FormStartPosition.Manual;
                Bounds = bounds;
            }

            if (_settings.WindowMaximized)
                WindowState = FormWindowState.Maximized;
        }

        private static bool IsVisibleOnAnyScreen(Rectangle bounds)
        {
            foreach (var screen in Screen.AllScreens)
            {
                if (screen.WorkingArea.IntersectsWith(bounds)) return true;
            }
            return false;
        }

        /// <summary>
        /// 恢复上次退出时的播放列表。
        /// <para>
        /// <b>这里一次磁盘都不查</b>：以前对每个路径调 <c>MediaFormats.IsOpenable</c>（同步
        /// <c>File.Exists</c>），而上次若是从已经离线的网络共享 / NAS 播的，每个路径都要等一次
        /// SMB 超时（几十秒一个），表现就是"双击之后半天不出窗口"，而且那些条目还会被静默丢掉。
        /// 现在先把列表<b>原样</b>恢复出来（窗口立刻出来），再由
        /// <see cref="CheckRestoredFilesInBackground"/> 在后台核对，把打不开的标出来。
        /// </para>
        /// </summary>
        private void RestoreSession()
        {
            var files = _settings.LastPlaylist
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToList();

            if (files.Count == 0) return;

            _restoredSessionFiles = files;

            _playlist.AddRange(files);

            // 恢复上次的排序（此时时长还没扫描出来，按时长排会在扫描过程中逐步稳定下来）。
            if (_settings.SortField != PlaylistSortField.AddedOrder)
                _playlist.Sort(_settings.SortField, _settings.SortDescending);

            _scanner.Enqueue(files);

            if (_settings.LastPlaylistIndex >= 0 && _settings.LastPlaylistIndex < _playlist.Count)
                _playlist.SetCurrent(_settings.LastPlaylistIndex);
        }

        /// <summary>
        /// 后台核对"上次的播放列表"里那些文件现在还在不在。
        /// <para>
        /// 只做两件事：把打不开的条目<b>标出来</b>（和点了播放才发现时的标记完全一样），
        /// 并在状态栏说一句总数。<b>不会移除条目</b>——网络盘只是暂时离线时，
        /// 用户下次把盘挂上还能直接播；悄悄删掉才是真的丢东西。
        /// </para>
        /// <para>
        /// 网络串流不查（它本来就没有"文件在不在"这回事）。
        /// </para>
        /// </summary>
        private void CheckRestoredFilesInBackground(IReadOnlyList<string> files)
        {
            var candidates = files.Where(path => !MediaFormats.IsStreamUri(path)).ToList();

            if (candidates.Count == 0) return;

            Task.Run(() =>
            {
                var missing = new List<string>();

                foreach (var path in candidates)
                {
                    try
                    {
                        if (!File.Exists(path)) missing.Add(path);
                    }
                    catch (Exception ex)
                    {
                        // 路径畸形之类：按"打不开"处理，但把原因记进日志
                        missing.Add(path);
                        AppLog.Swallowed("核对这条路径时出错，按打不开处理：" + path, ex);
                    }
                }

                if (missing.Count == 0) return;

                // 回到 UI 线程改列表与状态栏（这一步可能在窗口已经关掉之后才跑完）
                try
                {
                    if (IsDisposed || !IsHandleCreated) return;

                    BeginInvoke(new Action(() => MarkMissingRestoredFiles(missing)));
                }
                catch (Exception ex)
                {
                    AppLog.Swallowed("窗口已经关了，这次核对结果就不标了。", ex);
                }
            });
        }

        /// <summary>把"现在打不开"的那几条在列表里标出来，并说一句总数。</summary>
        private void MarkMissingRestoredFiles(IReadOnlyList<string> missing)
        {
            if (IsDisposed) return;

            var set = new HashSet<string>(missing, StringComparer.OrdinalIgnoreCase);
            var marked = 0;

            foreach (var item in _playlist.Items)
            {
                if (!set.Contains(item.FilePath)) continue;
                if (item.HasError && item.ErrorMessage == MissingFileMessage) continue;

                item.HasError = true;
                item.ErrorMessage = MissingFileMessage;
                marked++;
            }

            if (marked == 0) return;

            RefreshPlaylistView();

            SetStatus($"上次的列表里有 {marked} 项现在打不开（网络盘离线或文件被移走），已经在列表里标出来了");
            AppLog.Info($"启动核对：上次的播放列表里 {marked} 项打不开，已标记（共核对 {set.Count} 条路径）");
        }

        private void HandleStartupFiles()
        {
            var files = _startupArgs
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Select(a => a.Trim('"'))
                .Where(a => !a.StartsWith("-", StringComparison.Ordinal))
                .ToList();

            if (files.Count == 0) return;

            AddPathsWithFolders(files, AddPlayback.Always);
        }
    }
}
