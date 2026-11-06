using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using 播放器.Core;
using 播放器.Ui;

namespace 播放器
{
    /// <summary>
    /// 外壳：状态栏一行字、窗口标题、快捷键说明与关于、诊断菜单，
    /// 以及退出时的收尾（关窗前落盘、保存会话）。
    /// </summary>
    public partial class MainForm : Form
    {
        /// <summary>
        /// 清空一个下拉菜单，并<b>真正释放</b>里面的项。
        /// <para>
        /// <c>DropDownItems.Clear()</c> 只是把项从集合里摘下来，并不会释放它们
        /// （实测移除之后 <c>IsDisposed</c> 仍是 false，只是 Owner 变成 null）。
        /// 字幕轨道 / 音轨菜单每次播放状态变化都会重建，"最近播放"每次下拉都会重建，
        /// 长时间使用就会攒下一堆 ToolStripItem 以及它们在展开时创建的下拉窗口句柄。
        /// </para>
        /// </summary>
        private static void ClearMenuItems(ToolStripItemCollection items)
        {
            // 先复制一份：Dispose 会改动集合，直接 foreach 会抛"集合已修改"。
            var snapshot = new ToolStripItem[items.Count];
            items.CopyTo(snapshot, 0);

            foreach (var item in snapshot)
                item.Dispose();

            items.Clear();
        }

        // =====================================================================
        // 状态栏与提示
        // =====================================================================
        private void SetStatus(string text)
        {
            lblStatus.Text = text;
        }

        /// <summary>
        /// 冒烟用：按当前状态重刷一次传输条与卡片（占位提示的显示也跟着它走）。
        /// <para>和 <c>RefreshRecentMenu</c> 同一类入口：有些分支（例如"停止之后没有媒体"）
        /// 只有状态变化才会跑到，测试里不好等。</para>
        /// </summary>
        internal void RefreshTransportState() => UpdateTransportState();

        private void UpdateTransportState()
        {
            // 命令表面（菜单 / 工具栏 / 托盘 / 任务栏 / 卡片）的可用性与勾选态由命令层一次推完。
            RefreshCommandSurfaces();

            var hasMedia = _engine.HasMedia;

            // 视频控件始终可见（保证 HWND 存在），空闲时用占位标签盖住它。
            // 音频窗口 / 迷你模式里那块位置是卡片，不能再用"拖文件到这里"的大字盖上去。
            lblPlaceholder.Visible = !hasMedia && !IsAudioWindow && !IsMiniWindow;

            // 没有媒体就没有进度可看：全屏进度条跟着收起来。
            if (!hasMedia) _surfaces.Fullscreen.HideBar();

            if (!hasMedia)
            {
                _suppressSeekEvent = true;
                trackSeek.Value = 0;
                _suppressSeekEvent = false;
                lblCurrentTime.Text = "00:00:00";
                lblTotalTime.Text = "00:00:00";
                lblMediaInfo.Text = "无媒体";
            }
            else
            {
                var position = _playlist.CurrentIndex >= 0
                    ? $"{_playlist.CurrentIndex + 1}/{_playlist.Count}"
                    : "-/" + _playlist.Count;
                var extension = Path.GetExtension(_engine.CurrentPath ?? string.Empty).TrimStart('.').ToUpperInvariant();
                lblMediaInfo.Text = string.IsNullOrEmpty(extension) ? position : position + " · " + extension;
            }

            lblRate.Text = _engine.Rate.ToString("0.##") + "x";

            // A-B 循环那几项的可用状态跟着"有没有媒体"走（不可用时菜单项点不动，
            // 但菜单文字里的"当前时间"要等下拉时才刷新）。
            RefreshAbLoopMenu();

            // 「下一首播放」那两项跟着"列表空不空 / 队列空不空"走，同理。
            RefreshQueueMenu();
        }

        private void UpdateWindowTitle()
        {
            const string appName = "全能本地播放器";

            var path = _engine.CurrentPath;
            if (!string.IsNullOrWhiteSpace(path))
            {
                var name = MediaFormats.IsDiscLocation(path)
                    ? OpticalDisc.DescribeLocation(path)
                    : MediaFormats.IsStreamUri(path) ? path : Path.GetFileName(path);
                Text = name + " - " + appName;
                return;
            }

            var current = _playlist.Current;
            Text = current != null ? current.DisplayName + " - " + appName : appName;
        }

        /// <summary>
        /// F1 帮助正文。
        /// <para>
        /// 命令那部分<b>从命令目录现生成</b>：以前这里是一大段手写字符串，与
        /// <c>ProcessCmdKey</c> 的 switch、设计器里的 <c>ShortcutKeyDisplayString</c> 三份各写一遍，
        /// 改一个键要记得改三处。现在目录是唯一来源，这里只剩"不是命令"的鼠标手势与几个高级键。
        /// </para>
        /// </summary>
        internal string BuildShortcutsText() => PlayerCommandCatalog.FormatHelp() + ExtraShortcutsText;

        private const string ExtraShortcutsText =
            "播放控制（鼠标）\n" +
            "    鼠标滚轮          调节音量\n\n" +
            "画面\n" +
            "    Esc              退出全屏\n" +
            "    双击画面          进入 / 退出全屏\n" +
            "    右键画面          字幕轨道 / 音轨 / 播放控制（全屏里也能用）\n\n" +
            "列表与帮助\n" +
            "    Delete           从列表移除选中项\n" +
            "    F1               显示本帮助\n\n" +
            "歌词与视频（高级）\n" +
            "    Ctrl+] / Ctrl+[  歌词提前 / 延后 0.5 秒\n" +
            "    E / Shift+E      逐帧前进 / 后退\n" +
            "    G / H            音频延迟 -50 / +50 毫秒\n" +
            "    J / K            字幕延迟 -50 / +50 毫秒";

        private void ShowShortcuts()
        {
            MessageBox.Show(this, BuildShortcutsText(), "快捷键说明", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void ShowAbout()
        {
            MessageBox.Show(this, BuildAboutText(), "关于", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /// <summary>
        /// 「关于」里那一整段文字（单独抽出来，冒烟测试才不用弹框就能核对内容）。
        /// <para>
        /// 运行环境<b>从进程里现读</b>，不再手写 ".NET 8"——1.12.0 的关于框还写着 8，
        /// 而构建基线早就是 .NET 10 了；手写的版本号迟早再对不上。
        /// </para>
        /// </summary>
        internal string BuildAboutText()
        {
            string libVlcVersion;
            try
            {
                libVlcVersion = _engine.LibVlc.Version;
            }
            catch (Exception)
            {
                libVlcVersion = "未知";
            }

            // 版本号只有 csproj 里那一个来源；取不到就照实说"未知版本"，
            // 不要在这里再写一个数字——那种兜底值迟早和 csproj 对不上（还很难发现）。
            var version = typeof(MainForm).Assembly.GetName().Version?.ToString() ?? "未知版本";

            return
                $"全能本地播放器  {version}\n" +
                "项目主页：https://github.com/non266/local-player\n\n" +
                $"运行环境：.NET {Environment.Version.Major} / Windows Forms\n" +
                $"播放内核：LibVLC {libVlcVersion}\n\n" +
                "支持格式由 VLC 内核决定，涵盖绝大多数常见音视频\n" +
                "容器与编码，并支持外挂字幕、多音轨切换与网络串流。\n\n" +
                "设置文件：\n" + AppSettings.SettingsFilePath + "\n\n" +
                SettingsProfileLine() +
                "诊断日志：\n" + AppLog.FilePath;
        }

        /// <summary>
        /// 「关于」里那一行"当前方案"（没有在用方案时返回空串）。
        /// <para>设置路径旁边写上用的是哪一份方案，排查"我的设置怎么变了"时一眼就能看出。</para>
        /// </summary>
        private string SettingsProfileLine()
        {
            var name = CurrentSettingsProfileName;
            if (name.Length == 0) return string.Empty;

            var path = SettingsLibrary.ResolvePath(name) ?? SettingsLibrary.Directory;

            return $"当前设置方案：{name}（改动自动写回）\n{path}\n\n";
        }

        /// <summary>只读入口：设计器里的「帮助」菜单（给冒烟测试用）。</summary>
        internal ToolStripMenuItem MenuHelp => menuHelp;

        /// <summary>
        /// 「帮助」里那三项与日志有关的东西。
        /// <para>
        /// 出错时用户能做的只有"把日志发给我"，所以查看 / 打开 / 关掉三条路都得在界面上有。
        /// </para>
        /// </summary>
        private void ConfigureLogMenu()
        {
            _menuViewLog = new ToolStripMenuItem("查看日志…");
            _menuViewLog.Click += (s, e) => LogViewDialog.Show(this, _palette);

            _menuOpenLogFile = new ToolStripMenuItem("打开日志文件");
            _menuOpenLogFile.Click += (s, e) => OpenLogFile();

            _menuLogEnabled = new ToolStripMenuItem("记录诊断日志")
            {
                CheckOnClick = true,
                Checked = true
            };
            _menuLogEnabled.Click += (s, e) =>
            {
                _settings.DiagnosticLog = _menuLogEnabled.Checked;
                AppLog.SetFileOutput(_settings.DiagnosticLog);

                SetStatus(_settings.DiagnosticLog
                    ? "已开始记录诊断日志：" + AppLog.DefaultFilePath
                    : "已停止写入诊断日志（内存里仍保留最近 " + AppLog.BufferLimit + " 条，可在「帮助 → 查看日志」里看）");

                AppLog.Info("诊断日志落盘开关：" + (_settings.DiagnosticLog ? "开" : "关"));
            };

            var insertAt = menuHelp.DropDownItems.IndexOf(menuHelpShortcuts);

            if (insertAt < 0)
            {
                menuHelp.DropDownItems.Insert(0, _menuLogEnabled);
                menuHelp.DropDownItems.Insert(0, _menuOpenLogFile);
                menuHelp.DropDownItems.Insert(0, _menuViewLog);
            }
            else
            {
                // 快捷键说明 / 关于 之后，另起一组
                menuHelp.DropDownItems.Insert(insertAt, _menuViewLog);
                menuHelp.DropDownItems.Insert(insertAt + 1, _menuOpenLogFile);
                menuHelp.DropDownItems.Insert(insertAt + 2, _menuLogEnabled);
                menuHelp.DropDownItems.Insert(insertAt + 3, new ToolStripSeparator());
            }
        }

        private void OpenLogFile()
        {
            var path = AppLog.FilePath ?? AppLog.DefaultFilePath;

            try
            {
                var folder = Path.GetDirectoryName(path) ?? AppLog.DefaultFilePath;

                if (File.Exists(path))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
                    {
                        UseShellExecute = true
                    });
                }
                else
                {
                    if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);

                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"")
                    {
                        UseShellExecute = true
                    });
                }

                SetStatus("日志：" + path);
            }
            catch (Exception ex)
            {
                AppLog.Warn("打开日志文件失败", ex);
                ShowError("打开日志文件失败", ex);
            }
        }

        private void ShowError(string title, Exception exception)
        {
            // 弹给用户的错误同时进日志：否则"当时到底报了什么"只有用户记得住
            AppLog.Error(title + "：" + exception.Message, exception);

            MessageBox.Show(this, exception.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        // =====================================================================
        // 生命周期
        // =====================================================================
        private void OnFormClosingInternal(object? sender, FormClosingEventArgs e)
        {
            // 设置了"关闭窗口时缩到托盘"时，点关闭只是躲起来，程序继续放歌。
            if (ShouldHideToTray(e))
            {
                e.Cancel = true;
                HideToTray();
                return;
            }

            if (IsFullscreen)
                ExitFullscreen();

            // 关窗之前把当前进度存下来，下次打开才能接着播。
            // 退出路径上历史必须同步落盘（后台写可能赶不上进程结束）。
            FlushHistory();
            SaveSession();
        }

        private void OnFormClosedInternal(object? sender, FormClosedEventArgs e) => ShutdownForm();

        /// <summary>
        /// 收尾：停表、落下历史、放掉引擎与各种资源。
        /// <para>
        /// <b>两条路都要走到这里</b>：正常关窗（<see cref="OnFormClosedInternal"/>）和
        /// <b>只 Dispose 没 Close</b>（<see cref="Dispose(bool)"/>，冒烟里的 <c>using var form</c>
        /// 就是这种用法）。以前只在 FormClosed 里收尾，于是"只 Dispose"会留下一个
        /// <b>还在跑的 200 ms 计时器</b>和一个没放的引擎：那个计时器每 20 秒就把
        /// <b>自己那份旧历史</b>（<c>_history</c> 是一个实例一份内存副本）写回磁盘，
        /// 把别人刚写进去的记录盖掉——第 18 步偶发红的一个真实来源。
        /// </para>
        /// </summary>
        private void ShutdownForm()
        {
            if (_shutdown) return;
            _shutdown = true;

            FlushHistory();

            _disposed = true;

            _uiTimer.Stop();
            _uiTimer.Dispose();
            _skipTimer.Stop();
            _skipTimer.Dispose();
            DisposeVideoMenu();

            // 音频卡片与全屏进度条都是模块自己的窗口 / 计时器，一起收。
            _surfaces?.Dispose();

            _scanner.DurationFound -= OnDurationFound;
            _scanner.Dispose();

            if (_registeredMediaKeys.Count > 0)
            {
                MediaKeys.Unregister(Handle);
                _registeredMediaKeys = Array.Empty<int>();
            }

            if (_desktopLyricsHotKeyRegistered)
            {
                AppHotKeys.Unregister(Handle);
                _desktopLyricsHotKeyRegistered = false;
            }

            _taskbarButtons?.Dispose();
            _taskbarButtons = null;

            // 系统媒体控件：关掉 IsEnabled 再放接口（音量弹窗里立刻不再挂着这一首）
            _smtc?.Dispose();
            _smtc = null;

            _engine.Dispose();
            DisposeThemeResources();

            // 列表加粗行用的字体是本类自己 new 的，设计器的 Dispose 只管 components，
            // 不管它。
            _playingItemFont.Dispose();

            DisposeVideoResources();
            DisposeTray();
            DisposeSingleInstance();
            DisposeDesktopLyricsWindow();

            // 歌词页的右键菜单是手动挂到控件上的，Control.Dispose 不会管它
            _lyricsContextMenu?.Dispose();
            _lyricsContextMenu = null;
        }

        private void SaveSession()
        {
            _settings.Volume = trackVolume.Value;
            _settings.Muted = _engine.Muted;
            _settings.Rate = _engine.Rate;
            _settings.AlwaysOnTop = TopMost;
            _settings.AspectRatio = _engine.AspectRatio ?? string.Empty;

            // ShowPlaylist / ShowSidebar 由各自的 Apply*Visibility 维护（要考虑"已分离"的情况），
            // 这里不要再用分隔条的折叠状态去反推，否则分离状态下会被改成 false。
            _settings.SidebarPage = sidebarPanel.ActiveTab;
            _settings.LastPlaylist = _playlist.GetPaths();
            _settings.LastPlaylistIndex = _playlist.CurrentIndex;
            _settings.CurrentPlaylistName = _playlists.CurrentName;

            if (!IsFullscreen)
            {
                // 下次启动要不要直接进迷你模式，就在这一次退出时定下来。
                _settings.MiniMode = IsMiniWindow;

                // 当前形态的几何由对应的模块写回（视频 / 音频 / 迷你各记各的）
                _surfaces.CaptureGeometry();

                try
                {
                    // 存的是"视频宽度"，侧栏宽度单独存：
                    // 这样收起侧栏、分离再归位之后，两者的宽度都还能还原。
                    // 音频窗口里两栏是折叠的，这几项不动（否则会把视频窗口的宽度写成 0）。
                    if (!IsAudioWindow && !splitVideo.Panel2Collapsed)
                    {
                        _settings.SplitterDistance = splitVideo.SplitterDistance;
                        _settings.SidebarWidth = SidebarDockedWidth;
                    }
                }
                catch (Exception ex)
                {
// 忽略。
                    AppLog.Swallowed("忽略。", ex);
                }
            }

            // 分离出去的窗口的位置大小也要记（用户可能一直让它们飘着）
            SaveFloatingWindowBounds();

            // 桌面歌词的位置同理：它可能一整场都没被关掉
            SaveDesktopLyricsBounds();

            // 保存失败（目录只读、磁盘满、文件被占用）以前会让异常从 FormClosing 逃进
            // 消息循环，每次关窗弹一个"程序遇到了未处理的错误"。这里兜住并如实告知。
            try
            {
                _settings.Save();
            }
            catch (Exception ex)
            {
                ShowError("保存设置失败", ex);
            }

            // 当前方案也要跟着落盘：设置是"改一下记在心里、退出时写一次"，方案文件同理
            // （切换方案 / 停用 / 删除之前另有一次写回，见 MainForm.SettingsProfiles.cs）。
            SaveCurrentSettingsProfileQuietly();
        }
    }
}
