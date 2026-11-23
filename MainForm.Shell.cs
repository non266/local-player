using System;
using System.Collections.Generic;
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
            "    右键画面          字幕轨道 / 音轨 / 光盘菜单 / 播放控制（全屏里也能用）\n\n" +
            "光盘（DVD）\n" +
            "    方向键 / 回车      菜单上移动 / 确定（菜单在屏上时才接管这几个键）\n" +
            "    播放 → 光盘        标题 / 回到菜单 / 直接播放主标题\n\n" +
            "列表与帮助\n" +
            "    Delete           从列表移除选中项\n" +
            "    F1               显示本帮助";

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

            // 1.20.0：转储 / 在线缓存 / 日志都在这个目录里，崩溃提示里指的也是它
            _menuOpenDataFolder = new ToolStripMenuItem("打开数据目录");
            _menuOpenDataFolder.Click += (s, e) => OpenDataFolder();

            _menuLogEnabled = new ToolStripMenuItem("记录诊断日志")
            {
                CheckOnClick = true,
                Checked = true
            };
            _menuLogEnabled.Click += (s, e) =>
            {
                _settings.DiagnosticLog = _menuLogEnabled.Checked;
                AppLog.SetFileOutput(_settings.DiagnosticLog);

                // 1.20.0：转储的开关跟着它走（关掉就把 HKCU 下那个键也撤掉）
                CrashDump.Sync(_settings.DiagnosticLog, CrashDump.ProductExeName);

                SetStatus(_settings.DiagnosticLog
                    ? "已开始记录诊断日志：" + AppLog.DefaultFilePath
                    : "已停止写入诊断日志（内存里仍保留最近 " + AppLog.BufferLimit + " 条，可在「帮助 → 查看日志」里看）");

                AppLog.Info("诊断日志落盘开关：" + (_settings.DiagnosticLog ? "开" : "关"));
            };

            var insertAt = menuHelp.DropDownItems.IndexOf(menuHelpShortcuts);

            if (insertAt < 0)
            {
                menuHelp.DropDownItems.Insert(0, _menuLogEnabled);
                menuHelp.DropDownItems.Insert(0, _menuOpenDataFolder);
                menuHelp.DropDownItems.Insert(0, _menuOpenLogFile);
                menuHelp.DropDownItems.Insert(0, _menuViewLog);
            }
            else
            {
                // 快捷键说明 / 关于 之后，另起一组
                menuHelp.DropDownItems.Insert(insertAt, _menuViewLog);
                menuHelp.DropDownItems.Insert(insertAt + 1, _menuOpenLogFile);
                menuHelp.DropDownItems.Insert(insertAt + 2, _menuOpenDataFolder);
                menuHelp.DropDownItems.Insert(insertAt + 3, _menuLogEnabled);
                menuHelp.DropDownItems.Insert(insertAt + 4, new ToolStripSeparator());
            }
        }

        /// <summary>打开数据目录（设置 / 日志 / 在线缓存 / 设置方案都在这里）。</summary>
        private void OpenDataFolder()
        {
            var folder = AppSettings.SettingsDirectory;

            try
            {
                if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);

                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"")
                {
                    UseShellExecute = true
                });

                SetStatus("数据目录：" + folder);
            }
            catch (Exception ex)
            {
                AppLog.Warn("打开数据目录失败", ex);
                ShowError("打开数据目录失败", ex);
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

            // 1.29.0：每做一段记一行。挂死时看门狗把这份"走到哪一步"写进 smoke-journal.txt，
            // 下一轮就不用再猜是卡在放引擎、还是卡在放系统媒体控件。
            ShutdownTrace.Begin();

            ShutdownTrace.Step("刷新历史");
            FlushHistory();

            // 1.20.0：留一个"这次是正常退出"的记号。下次启动拿它和崩溃转储比时间，
            // 比它新的转储才算"上次没正常退出"（见 CrashDump）。
            CrashDump.MarkCleanExit();

            _disposed = true;

            ShutdownTrace.Step("停计时器");
            _uiTimer.Stop();
            _uiTimer.Dispose();
            _skipTimer.Stop();
            _skipTimer.Dispose();
            DisposeVideoMenu();

            // 音频卡片与全屏进度条都是模块自己的窗口 / 计时器，一起收。
            ShutdownTrace.Step("放窗口模块");
            _surfaces?.Dispose();

            ShutdownTrace.Step("放扫描器");
            _scanner.DurationFound -= OnDurationFound;
            _scanner.Dispose();

            // 标签读取队列：只关掉（不再起新的、待读丢掉），**不等待**在飞的那条——
            // 离线共享上一次读能卡几十秒，关窗不该陪着等。线程是后台线程，进程照常退。
            ShutdownTrace.Step("关标签队列");
            _metadata.Dispose();
            _metadataPendingTimer.Stop();
            _metadataPendingTimer.Dispose();

            ShutdownTrace.Step("撤媒体键");
            if (_registeredMediaKeys.Count > 0)
            {
                MediaKeys.Unregister(Handle);
                _registeredMediaKeys = Array.Empty<int>();
            }

            ShutdownTrace.Step("撤桌面歌词热键");
            if (_desktopLyricsHotKeyRegistered)
            {
                AppHotKeys.Unregister(Handle);
                _desktopLyricsHotKeyRegistered = false;
            }

            ShutdownTrace.Step("放任务栏按钮");
            _taskbarButtons?.Dispose();
            _taskbarButtons = null;

            // 系统媒体控件：关掉 IsEnabled 再放接口（音量弹窗里立刻不再挂着这一首）
            ShutdownTrace.Step("放系统媒体控件");
            _smtc?.Dispose();
            _smtc = null;

            // 先把画面输出摘下来，再放引擎：libvlc 的 vout 线程要往 videoView 的 HWND 上画，
            // 而窗体随后就会把这个 HWND 销毁。实测（1.18.0 验收）在"刚切到视频窗口就关窗"的
            // 时序下会卡在 MediaPlayer.Stop 里——Windows 记成 Application Hang、进程被关掉
            // （退出码 0xCFFFFFFF，事件日志里是 Application Hang 而不是访问违例）。
            // 摘下来之后 libvlc 不再持有我们的窗口句柄，引擎可以照常停、照常放。
            ShutdownTrace.Step("摘画面输出");
            videoView.MediaPlayer = null;
            VideoOutputDetachedBeforeTeardown = videoView.MediaPlayer == null;

            // 1.29.0：释放本身还是同一个顺序，但挪到后台并给界面 2 秒上界。
            // 常态下与以前完全一样（同步放完再往下走）；只有真的卡住时才放弃等待——
            // 用户按下关闭之后，界面不该陪着原生线程一起被 Windows 判成"无响应"。
            ShutdownTrace.Step("放引擎（有界 2 秒）");

            var engine = _engine;
            var slowMilliseconds = BoundedTeardown.SlowTeardownMilliseconds;
            var completed = BoundedTeardown.Run(
                () =>
                {
                    // 只给冒烟用：验证"上界到了就放弃等待"
                    if (slowMilliseconds > 0) System.Threading.Thread.Sleep(slowMilliseconds);
                    engine.Dispose();
                },
                ShutdownEngineLimit,
                elapsed =>
                {
                    LastShutdownTimedOut = true;
                    ShutdownTrace.Step($"收尾超时（{elapsed.TotalMilliseconds:0} ms 仍未放完）");
                    AppLog.Warn($"收尾超时：引擎 {elapsed.TotalMilliseconds:0} ms 还没放完，"
                                + "不再等它（后台线程继续放，进程退出时由系统回收）");
                });

            if (completed) ShutdownTrace.Step("引擎已放");

            ShutdownTrace.Step("放主题资源");
            DisposeThemeResources();

            // 列表加粗行用的字体是本类自己 new 的，设计器的 Dispose 只管 components，
            // 不管它。
            ShutdownTrace.Step("放字体");
            _playingItemFont.Dispose();

            ShutdownTrace.Step("放视频资源");
            DisposeVideoResources();

            ShutdownTrace.Step("放托盘");
            DisposeTray();

            ShutdownTrace.Step("放单实例");
            DisposeSingleInstance();

            ShutdownTrace.Step("放桌面歌词窗口");
            DisposeDesktopLyricsWindow();

            // 歌词页的右键菜单是手动挂到控件上的，Control.Dispose 不会管它
            ShutdownTrace.Step("放歌词菜单");
            _lyricsContextMenu?.Dispose();
            _lyricsContextMenu = null;

            ShutdownTrace.Step("收尾完成");
        }

        /// <summary>引擎收尾的等待上界（1.29.0）。</summary>
        private static readonly TimeSpan ShutdownEngineLimit = TimeSpan.FromSeconds(2);

        /// <summary>测试用：最近一次收尾是不是"超时后放弃等待"。</summary>
        internal bool LastShutdownTimedOut { get; private set; }

        /// <summary>
        /// 测试用：放引擎之前，画面输出到底摘掉了没有（1.18.0 那条坑的硬证据）。
        /// <para>
        /// 光看追踪里有没有「摘画面输出」这一行守不住——把那一行挪到放引擎后面，顺序照样"对"。
        /// 所以这里记的是<b>事实</b>：赋值之后 <c>videoView.MediaPlayer</c> 是不是真的空了。
        /// </para>
        /// </summary>
        internal bool VideoOutputDetachedBeforeTeardown { get; private set; }

        /// <summary>测试用：最近一次收尾走过的步骤（顺序即执行顺序）。</summary>
        internal IReadOnlyList<string> CloseTrace => ShutdownTrace.LastSteps;

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
            SyncFileBrowserSettings();
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
