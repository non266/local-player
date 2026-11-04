using System;
using 播放器.Core;
using System.Drawing;
using System.Runtime.InteropServices;

namespace 播放器.Ui
{
    /// <summary>
    /// 任务栏缩略图上的播放控制按钮（鼠标悬停任务栏图标时出现的那个预览面板）。
    /// <para>
    /// 通过 <c>ITaskbarList3</c> 实现。这是一组 COM 接口，方法顺序必须和系统头文件
    /// 完全一致，否则 vtable 会错位——所以下面把 ITaskbarList / ITaskbarList2 / ITaskbarList3
    /// 的全部方法按原顺序声明齐了，一个都不能漏。
    /// </para>
    /// <para>
    /// ⚠ <b>1.11.0 修的那个坑</b>：这套按钮从 1.0 基线起就有，却从来没在任务栏上出现过。
    /// 原因不是 COM 声明（那些是对的），而是<b>注册的时机</b>：以前在 <c>OnHandleCreated</c> 里就调
    /// <c>ThumbBarAddButtons</c>，那一刻窗口还没显示、任务栏上还没有它的按钮——Explorer 收下调用
    /// 照样返回 <c>S_OK</c>，代码就把 <c>_added</c> 置成真，之后再也不会重试。
    /// 现在<b>必须等窗口可见</b>才加（<see cref="EnsureAdded"/> 的第一道守卫），
    /// 并且句柄换了要重新挂（<see cref="Attach"/>）。
    /// </para>
    /// <para>系统不支持或调用失败时静默降级，只是不影响播放；失败原因会记日志。</para>
    /// </summary>
    internal sealed class TaskbarThumbnailButtons : IDisposable
    {
        /// <summary>系统的"缩略图按钮被点了"通知码（WM_COMMAND 的高位字）。</summary>
        private const int ThbnClicked = 0x1800;

        /// <summary>WM_COMMAND。缩略图按钮的点击就是通过它通知的。</summary>
        public const int WmCommand = 0x0111;

        private const uint ThbIcon = 0x00000002;
        private const uint ThbTooltip = 0x00000004;
        private const uint ThbFlags = 0x00000008;

        /// <summary>THBF_ENABLED 是 0：不置位就是可用。</summary>
        private const uint ThbfEnabled = 0x00000000;

        /// <summary>THBF_DISABLED：置灰，点不动。</summary>
        private const uint ThbfDisabled = 0x00000001;

        public const int ButtonPrevious = 1;
        public const int ButtonPlayPause = 2;
        public const int ButtonNext = 3;

        /// <summary>1.11.0 补的第四个按钮（桌面卡片与工具栏上早就有了）。</summary>
        public const int ButtonStop = 4;

        /// <summary>固定四个按钮：上一个 / 播放暂停 / 下一个 / 停止。</summary>
        public const int ButtonTotal = 4;

        private IntPtr _windowHandle;

        private ITaskbarList3? _taskbar;
        private bool _added;
        private bool _disposed;

        // 推给任务栏的状态（决定图标与置灰）
        private bool _hasMedia;
        private bool _isPlaying;
        private bool _canPrevious;
        private bool _canNext;

        private IntPtr _previousIcon;
        private IntPtr _playIcon;
        private IntPtr _pauseIcon;
        private IntPtr _nextIcon;
        private IntPtr _stopIcon;

        // 诊断 / 测试用
        private int _addAttempts;
        private int _addsWhileHidden;
        private int _hiddenSkips;
        private uint[] _lastFlags = Array.Empty<uint>();

        public TaskbarThumbnailButtons(IntPtr windowHandle)
        {
            _windowHandle = windowHandle;
        }

        /// <summary>按钮是否已经成功挂上（失败时调用方不必再更新图标）。</summary>
        public bool IsActive => _added;

        /// <summary>测试用：一共真的调过几次 <c>ThumbBarAddButtons</c>。</summary>
        internal int AddAttempts => _addAttempts;

        /// <summary>
        /// 测试用：<b>在窗口不可见时</b>调 <c>ThumbBarAddButtons</c> 的次数。
        /// <para>
        /// 正常永远是 0——这就是"按钮从来没出现"那个坑的不变式。变异（把可见性守卫去掉）会让它变成 1。
        /// </para>
        /// </summary>
        internal int AddsWhileHidden => _addsWhileHidden;

        /// <summary>测试用：因为窗口还没显示而跳过的注册次数（正常 ≥ 1，说明守卫在工作）。</summary>
        internal int HiddenSkips => _hiddenSkips;

        /// <summary>测试用：按钮个数（固定 4）。</summary>
        internal int ButtonCount => ButtonTotal;

        /// <summary>测试用：最近一次推给任务栏的 dwFlags（置灰位在这里看）。</summary>
        internal uint[] LastFlags => _lastFlags;

        /// <summary>挂到一个（新）窗口句柄上；句柄变了就把"已挂上"复位，等下一次可见时重挂。</summary>
        public void Attach(IntPtr windowHandle)
        {
            if (_disposed) return;
            if (_windowHandle == windowHandle) return;

            _windowHandle = windowHandle;
            _added = false;
        }

        /// <summary>换一套图标（主题切换时用），并刷新按钮状态。</summary>
        public void SetIcons(Bitmap previous, Bitmap play, Bitmap pause, Bitmap next, Bitmap stop)
        {
            if (_disposed) return;

            DestroyIcons();

            try
            {
                _previousIcon = previous.GetHicon();
                _playIcon = play.GetHicon();
                _pauseIcon = pause.GetHicon();
                _nextIcon = next.GetHicon();
                _stopIcon = stop.GetHicon();
            }
            catch (Exception ex)
            {
                AppLog.Swallowed("任务栏按钮图标生成失败，这一轮先不挂按钮", ex);
                DestroyIcons();
                return;
            }

            Update();
        }

        /// <summary>
        /// 同步播放状态与可用性：没媒体时播放 / 停止不可点，列表为空时上一个 / 下一个不可点。
        /// </summary>
        public void SetTransportState(bool hasMedia, bool isPlaying, bool canPrevious, bool canNext)
        {
            if (_disposed) return;

            if (_hasMedia == hasMedia && _isPlaying == isPlaying &&
                _canPrevious == canPrevious && _canNext == canNext)
                return;

            _hasMedia = hasMedia;
            _isPlaying = isPlaying;
            _canPrevious = canPrevious;
            _canNext = canNext;

            Update();
        }

        /// <summary>
        /// 登记按钮。窗口句柄就绪<b>且窗口可见</b>之后调用；失败可稍后重试。
        /// <para>
        /// ⚠ 可见性守卫不能删：窗口还没显示时任务栏上还没有它的按钮，这时调
        /// <c>ThumbBarAddButtons</c> 会返回 <c>S_OK</c> 却什么也不做——然后 <c>_added</c> 一置真，
        /// 整场都不会再试（1.0～1.10 的实际表现就是"这套按钮从来没出现过"）。
        /// </para>
        /// </summary>
        public bool EnsureAdded()
        {
            if (_disposed || _added) return _added;
            if (_windowHandle == IntPtr.Zero) return false;

            if (!IsWindowVisible(_windowHandle))
            {
                _hiddenSkips++;
                return false;
            }

            if (_previousIcon == IntPtr.Zero || _playIcon == IntPtr.Zero ||
                _nextIcon == IntPtr.Zero || _pauseIcon == IntPtr.Zero || _stopIcon == IntPtr.Zero)
                return false;

            try
            {
                _addAttempts++;

                // 到这一步窗口一定是可见的；这里再问一次，只是给冒烟一条"绝不能在这里是假的"
                // 不变式（变异去掉上面的守卫时，这条会立刻变成 1）。
                if (!IsWindowVisible(_windowHandle)) _addsWhileHidden++;

                if (_taskbar == null)
                {
                    var clsid = new Guid("56FDF344-FD6D-11d0-958A-006097C9A090");   // CLSID_TaskbarList
                    var instance = Activator.CreateInstance(Type.GetTypeFromCLSID(clsid)!);
                    if (instance == null) return false;

                    _taskbar = (ITaskbarList3)instance;
                    _taskbar.HrInit();
                }

                var buttons = BuildButtons();
                var hr = _taskbar.ThumbBarAddButtons(_windowHandle, (uint)buttons.Length, buttons);

                _added = hr == 0;

                AppLog.Info($"任务栏缩略图按钮：挂载 hr=0x{hr:X8}（0 为成功），按钮 {buttons.Length} 个，"
                            + $"窗口可见={IsWindowVisible(_windowHandle)}，这是第 {_addAttempts} 次尝试");

                if (!_added)
                    AppLog.Info("任务栏缩略图按钮挂不上：系统可能不支持，或在被别的外壳接管；功能降级，播放不受影响");
            }
            catch (Exception ex)
            {
                _added = false;
                AppLog.Swallowed("任务栏缩略图按钮挂载失败（功能降级，不影响播放）", ex);
            }

            return _added;
        }

        private void Update()
        {
            if (!EnsureAdded() || _taskbar == null) return;

            try
            {
                var buttons = BuildButtons();
                _taskbar.ThumbBarUpdateButtons(_windowHandle, (uint)buttons.Length, buttons);
            }
            catch (Exception ex)
            {
                AppLog.Swallowed("任务栏缩略图按钮状态没推上去（不影响播放）", ex);
            }
        }

        private THUMBBUTTON[] BuildButtons()
        {
            var buttons = new[]
            {
                CreateButton(ButtonPrevious, _previousIcon, "上一个", _canPrevious),
                CreateButton(ButtonPlayPause, _isPlaying ? _pauseIcon : _playIcon,
                    _isPlaying ? "暂停" : "播放", _hasMedia),
                CreateButton(ButtonNext, _nextIcon, "下一个", _canNext),
                CreateButton(ButtonStop, _stopIcon, "停止", _hasMedia)
            };

            _lastFlags = new uint[buttons.Length];

            for (var i = 0; i < buttons.Length; i++)
                _lastFlags[i] = buttons[i].dwFlags;

            return buttons;
        }

        private static THUMBBUTTON CreateButton(int id, IntPtr icon, string tooltip, bool enabled) => new THUMBBUTTON
        {
            dwMask = ThbIcon | ThbTooltip | ThbFlags,
            iId = (uint)id,
            iBitmap = 0,
            hIcon = icon,
            szTip = tooltip,
            dwFlags = enabled ? ThbfEnabled : ThbfDisabled
        };

        /// <summary>
        /// 从 WM_COMMAND 的 wParam 里取出被点击的缩略图按钮 id；不是按钮点击则返回 -1。
        /// <para>
        /// 参数类型必须是 <see cref="IntPtr"/>：64 位下 wParam 常是大句柄或指针，
        /// 直接 <c>ToInt32()</c> 会抛 <see cref="OverflowException"/>。
        /// 调用方也要先确认消息类型是 WM_COMMAND 再进来。
        /// </para>
        /// </summary>
        public static int ResolveClick(IntPtr wParam)
        {
            var value = wParam.ToInt64();

            if (((value >> 16) & 0xFFFF) != ThbnClicked) return -1;

            return (int)(value & 0xFFFF);
        }

        private void DestroyIcons()
        {
            DestroyIcon(_previousIcon);
            DestroyIcon(_playIcon);
            DestroyIcon(_pauseIcon);
            DestroyIcon(_nextIcon);
            DestroyIcon(_stopIcon);

            _previousIcon = IntPtr.Zero;
            _playIcon = IntPtr.Zero;
            _pauseIcon = IntPtr.Zero;
            _nextIcon = IntPtr.Zero;
            _stopIcon = IntPtr.Zero;
        }

        private static void DestroyIcon(IntPtr icon)
        {
            if (icon == IntPtr.Zero) return;

            try
            {
                DestroyIconNative(icon);
            }
            catch (Exception ex)
            {
                AppLog.Swallowed("忽略。", ex);
            }
        }

        [DllImport("user32.dll", EntryPoint = "DestroyIcon", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIconNative(IntPtr hIcon);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            DestroyIcons();

            // 光把字段置空不会调用 COM 的 Release：那要等 GC 终结 RCW，
            // 而任务栏对象是个被 shell 长期持有的服务器，等于活到进程结束。
            if (_taskbar != null)
            {
                try
                {
                    Marshal.FinalReleaseComObject(_taskbar);
                }
                catch (Exception ex)
                {
                    AppLog.Swallowed("已经释放过就忽略。", ex);
                }
            }

            _taskbar = null;
            _added = false;
        }

        // -----------------------------------------------------------------
        // COM 互操作
        // -----------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential, Pack = 8, CharSet = CharSet.Unicode)]
        private struct THUMBBUTTON
        {
            public uint dwMask;
            public uint iId;
            public uint iBitmap;
            public IntPtr hIcon;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szTip;

            public uint dwFlags;
        }

        [ComImport]
        [Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ITaskbarList3
        {
            // ---- ITaskbarList ----
            [PreserveSig] int HrInit();
            [PreserveSig] int AddTab(IntPtr hwnd);
            [PreserveSig] int DeleteTab(IntPtr hwnd);
            [PreserveSig] int ActivateTab(IntPtr hwnd);
            [PreserveSig] int SetActiveAlt(IntPtr hwnd);

            // ---- ITaskbarList2 ----
            [PreserveSig] int MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);

            // ---- ITaskbarList3 ----
            [PreserveSig] int SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
            [PreserveSig] int SetProgressState(IntPtr hwnd, int flags);
            [PreserveSig] int RegisterTab(IntPtr hwndTab, IntPtr hwndMdi);
            [PreserveSig] int UnregisterTab(IntPtr hwndTab);
            [PreserveSig] int SetTabOrder(IntPtr hwndTab, IntPtr hwndInsertBefore);
            [PreserveSig] int SetTabActive(IntPtr hwndTab, IntPtr hwndMdi, uint reserved);

            [PreserveSig] int ThumbBarAddButtons(
                IntPtr hwnd, uint count, [MarshalAs(UnmanagedType.LPArray)] THUMBBUTTON[] buttons);

            [PreserveSig] int ThumbBarUpdateButtons(
                IntPtr hwnd, uint count, [MarshalAs(UnmanagedType.LPArray)] THUMBBUTTON[] buttons);

            [PreserveSig] int ThumbBarSetImageList(IntPtr hwnd, IntPtr imageList);

            [PreserveSig] int SetOverlayIcon(IntPtr hwnd, IntPtr icon, [MarshalAs(UnmanagedType.LPWStr)] string description);
            [PreserveSig] int SetThumbnailTooltip(IntPtr hwnd, [MarshalAs(UnmanagedType.LPWStr)] string tooltip);
            [PreserveSig] int SetThumbnailClip(IntPtr hwnd, IntPtr clip);
        }
    }
}
