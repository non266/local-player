using System;
using System.Drawing;
using System.Windows.Forms;
using 播放器.Core;
using 播放器.Ui;

namespace 播放器
{
    /// <summary>
    /// 系统集成：托盘与任务栏按钮、全局媒体键与桌面歌词热键、窗口消息处理。
    /// <para>这些都要在句柄建好之后才能做，所以入口在 <see cref="OnHandleCreated"/>。</para>
    /// </summary>
    public partial class MainForm : Form
    {
        // =====================================================================
        // 系统集成：主题跟随、全局媒体键、任务栏缩略图按钮
        // =====================================================================
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);

            ApplyNativeTheme(_palette);
            InitializeSystemIntegration();

            // 单实例靠一个窗口属性找到这个窗口，句柄就绪后登记。
            _singleInstance?.AttachWindow(Handle);

            // 句柄就绪之前，libvlc 的回调线程上攒下的事件现在可以安全地派发了
            // （构造期间就 Open 了启动参数里的媒体，那时 InvokeRequired 还不可靠）。
            _engine.FlushPendingUiActions();

            // 同一个道理：时长扫描在构造函数里就已经开跑了，句柄还没建好时收到的结果
            // 以前是直接丢掉的（那一项就永远显示 --:--）。
            FlushPendingDurations();
        }

        private void InitializeSystemIntegration()
        {
            if (_taskbarButtons == null)
            {
                try
                {
                    _taskbarButtons = new TaskbarThumbnailButtons(Handle);
                }
                catch (Exception)
                {
                    // 系统不支持就算了，功能降级。
                    _taskbarButtons = null;
                }
            }

            UpdateTaskbarIcons();
            ApplyGlobalMediaKeys(_settings.GlobalMediaKeys);

            // 桌面歌词的解锁热键是**常开**的，不跟着"全局媒体键"那个开关走：
            // 那个开关是给"媒体键被别的播放器占了"用的，关掉它不该顺带把解锁出口也关掉。
            ApplyDesktopLyricsHotKey(true);
        }

        /// <summary>按当前主题重建任务栏按钮的图标（GetHicon 得到的是独立副本，位图可以立刻释放）。</summary>
        private void UpdateTaskbarIcons()
        {
            if (_taskbarButtons == null) return;

            try
            {
                using (var icons = new MediaIconSet(_palette.IconInk, _palette.Accent, TaskbarIconSize))
                {
                    _taskbarButtons.SetIcons(icons.Previous, icons.Play, icons.Pause, icons.Next);
                }
            }
            catch (Exception ex)
            {
// 忽略。
                AppLog.Swallowed("忽略。", ex);
            }
        }

        private void ApplyGlobalMediaKeys(bool enabled)
        {
            if (!IsHandleCreated) return;

            if (enabled)
            {
                if (_registeredMediaKeys.Count > 0) return;

                _registeredMediaKeys = MediaKeys.Register(Handle);

                // 缺一个也要说清是哪个：以前只报"成功几个"，4 个里成了 3 个时界面什么都不说，
                // 那一个键按下去没反应，用户只会以为程序坏了。
                _mediaKeyWarning = MediaKeys.DescribeMissing(_registeredMediaKeys);

                if (_mediaKeyWarning != null)
                {
                    AppLog.Info(_mediaKeyWarning);
                    SetStatus(_mediaKeyWarning);
                }
            }
            else
            {
                if (_registeredMediaKeys.Count == 0) return;

                MediaKeys.Unregister(Handle);
                _registeredMediaKeys = Array.Empty<int>();
                _mediaKeyWarning = null;
            }
        }

        /// <summary>
        /// 注册 / 注销桌面歌词的解锁热键。失败只提示一句，不影响功能——
        /// 歌词仍然可以从菜单或托盘解锁，这只是"全屏看片时的快捷出口"。
        /// </summary>
        private void ApplyDesktopLyricsHotKey(bool enabled)
        {
            if (!IsHandleCreated) return;

            if (enabled)
            {
                if (_desktopLyricsHotKeyRegistered) return;

                _desktopLyricsHotKeyRegistered = AppHotKeys.Register(Handle) > 0;

                if (!_desktopLyricsHotKeyRegistered)
                    SetStatus($"桌面歌词解锁热键（{AppHotKeys.ToggleDesktopLyricsLockText}）注册失败：可能已被其它程序占用");
            }
            else
            {
                if (!_desktopLyricsHotKeyRegistered) return;

                AppHotKeys.Unregister(Handle);
                _desktopLyricsHotKeyRegistered = false;
            }
        }

        private void HandleMediaKey(MediaKeyCommand command)
        {
            switch (command)
            {
                case MediaKeyCommand.PlayPause:
                    TogglePlayPause();
                    break;
                case MediaKeyCommand.Stop:
                    StopPlayback();
                    break;
                case MediaKeyCommand.Next:
                    PlayNext(true);
                    break;
                case MediaKeyCommand.Previous:
                    PlayPrevious();
                    break;
            }
        }

        private void OnGlobalMediaKeysChanged(object? sender, EventArgs e)
        {
            if (_suspendUiEvents) return;

            _settings.GlobalMediaKeys = menuGlobalMediaKeys.Checked;
            ApplyGlobalMediaKeys(menuGlobalMediaKeys.Checked);

            if (menuGlobalMediaKeys.Checked && _registeredMediaKeys.Count == 0)
                return;   // 上面已经提示过失败原因

            SetStatus(menuGlobalMediaKeys.Checked ? "已启用全局媒体键" : "已停用全局媒体键");
        }

        private void OnResumePlaybackChanged(object? sender, EventArgs e)
        {
            if (_suspendUiEvents) return;

            _settings.ResumePlayback = menuResumePlayback.Checked;

            if (!_settings.ResumePlayback)
                _pendingResume = null;

            SetStatus(menuResumePlayback.Checked ? "已开启播放进度记忆" : "已关闭播放进度记忆");
        }

        /// <summary>
        /// 给冒烟测试用：把一条窗口消息喂给窗口（等价于系统发来一条）。
        /// <para><see cref="WndProc"/> 是 <c>protected override</c>，测试要的是"发消息"这个动作本身。</para>
        /// </summary>
        internal void ProcessWindowMessage(ref Message message) => WndProc(ref message);

        protected override void WndProc(ref Message m)
        {
            // 第二个实例传来的文件走 WM_COPYDATA，必须先于基类处理。
            if (_singleInstance != null && _singleInstance.TryHandleMessage(ref m)) return;

            base.WndProc(ref m);

            const int WmSettingChange = 0x001A;
            const int WmThemeChanged = 0x031A;
            const int WmHotKey = 0x0312;
            const int WmAppCommand = 0x0319;

            // ⚠ 这里必须先按消息类型分流，再读取 wParam / lParam。
            // 64 位下这两个参数经常是句柄甚至指针，值会超出 Int32 范围，
            // 对每条消息都调用 ToInt32() 会抛 OverflowException（真实踩过）。
            // 取值一律用 ToInt64()，只在确实需要时才掩码成 int。
            switch (m.Msg)
            {
                case TaskbarThumbnailButtons.WmCommand:
                {
                    // 任务栏缩略图按钮点击
                    switch (TaskbarThumbnailButtons.ResolveClick(m.WParam))
                    {
                        case TaskbarThumbnailButtons.ButtonPrevious:
                            PlayPrevious();
                            break;
                        case TaskbarThumbnailButtons.ButtonPlayPause:
                            TogglePlayPause();
                            break;
                        case TaskbarThumbnailButtons.ButtonNext:
                            PlayNext(true);
                            break;
                    }

                    break;
                }

                case WmHotKey:
                {
                    // 桌面歌词的锁定 / 解锁（主界面可能缩在托盘里，这是唯一的快捷出口）
                    var appHotKey = AppHotKeys.Resolve(m.WParam);
                    if (appHotKey == AppHotKeys.ToggleDesktopLyricsLock)
                    {
                        ToggleDesktopLyricsLock();
                        break;
                    }

                    // 全局媒体键（程序不在前台也能收到）
                    var command = MediaKeys.ResolveHotKey(m.WParam);
                    if (command.HasValue)
                        HandleMediaKey(command.Value);

                    break;
                }

                case WmAppCommand:
                {
                    // 程序在前台时系统走的是 WM_APPCOMMAND
                    var command = MediaKeys.ResolveAppCommand(m.LParam);
                    if (command.HasValue)
                    {
                        HandleMediaKey(command.Value);
                        m.Result = (IntPtr)1;   // 告诉系统已经处理，别再传给别的窗口
                    }

                    break;
                }

                case WmSettingChange:
                case WmThemeChanged:
                    // 系统浅色/深色切换：只有"跟随系统"才需要响应，
                    // 且解析出来的主题确实变了才重做，避免无谓地重建图标。
                    if (_themeReady && _settings.Theme == ThemeId.Auto &&
                        Themes.Resolve(ThemeId.Auto).Id != _palette.Id)
                        ApplyTheme(ThemeId.Auto);

                    break;
            }
        }
    }
}
