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
    /// 系统不支持或调用失败时静默降级，不影响播放。
    /// </para>
    /// </summary>
    internal sealed class TaskbarThumbnailButtons : IDisposable
    {
        private const int ThbnClicked = 0x1800;

        /// <summary>WM_COMMAND。缩略图按钮的点击就是通过它通知的。</summary>
        public const int WmCommand = 0x0111;

        private const uint ThbIcon = 0x00000002;
        private const uint ThbTooltip = 0x00000004;
        private const uint ThbFlags = 0x00000008;
        private const uint ThbfEnabled = 0x00000000;

        public const int ButtonPrevious = 1;
        public const int ButtonPlayPause = 2;
        public const int ButtonNext = 3;

        private readonly IntPtr _windowHandle;
        private ITaskbarList3? _taskbar;
        private bool _added;
        private bool _disposed;
        private bool _isPlaying;

        private IntPtr _previousIcon;
        private IntPtr _playIcon;
        private IntPtr _pauseIcon;
        private IntPtr _nextIcon;

        public TaskbarThumbnailButtons(IntPtr windowHandle)
        {
            _windowHandle = windowHandle;
        }

        /// <summary>按钮是否已经成功挂上（失败时调用方不必再更新图标）。</summary>
        public bool IsActive => _added;

        /// <summary>登记按钮。窗口句柄就绪后调用；失败可稍后重试。</summary>
        public bool EnsureAdded()
        {
            if (_disposed || _added || _windowHandle == IntPtr.Zero) return _added;

            try
            {
                if (_taskbar == null)
                {
                    var clsid = new Guid("56FDF344-FD6D-11d0-958A-006097C9A090");   // CLSID_TaskbarList
                    var instance = Activator.CreateInstance(Type.GetTypeFromCLSID(clsid)!);
                    if (instance == null) return false;

                    _taskbar = (ITaskbarList3)instance;
                    _taskbar.HrInit();
                }

                if (_previousIcon == IntPtr.Zero || _playIcon == IntPtr.Zero ||
                    _nextIcon == IntPtr.Zero || _pauseIcon == IntPtr.Zero)
                    return false;

                var buttons = BuildButtons();
                _added = _taskbar.ThumbBarAddButtons(_windowHandle, (uint)buttons.Length, buttons) == 0;
            }
            catch (Exception)
            {
                _added = false;
            }

            return _added;
        }

        /// <summary>换一套图标（主题切换时用），并刷新按钮状态。</summary>
        public void SetIcons(Bitmap previous, Bitmap play, Bitmap pause, Bitmap next)
        {
            if (_disposed) return;

            DestroyIcons();

            try
            {
                _previousIcon = previous.GetHicon();
                _playIcon = play.GetHicon();
                _pauseIcon = pause.GetHicon();
                _nextIcon = next.GetHicon();
            }
            catch (Exception)
            {
                DestroyIcons();
                return;
            }

            Update();
        }

        /// <summary>同步播放/暂停按钮的图标。</summary>
        public void SetPlaying(bool playing)
        {
            if (_disposed || _isPlaying == playing) return;

            _isPlaying = playing;
            Update();
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
// 忽略。
                AppLog.Swallowed("忽略。", ex);
            }
        }

        private THUMBBUTTON[] BuildButtons() => new[]
        {
            CreateButton(ButtonPrevious, _previousIcon, "上一个"),
            CreateButton(ButtonPlayPause, _isPlaying ? _pauseIcon : _playIcon, _isPlaying ? "暂停" : "播放"),
            CreateButton(ButtonNext, _nextIcon, "下一个")
        };

        private static THUMBBUTTON CreateButton(int id, IntPtr icon, string tooltip) => new THUMBBUTTON
        {
            dwMask = ThbIcon | ThbTooltip | ThbFlags,
            iId = (uint)id,
            iBitmap = 0,
            hIcon = icon,
            szTip = tooltip,
            dwFlags = ThbfEnabled
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

            _previousIcon = IntPtr.Zero;
            _playIcon = IntPtr.Zero;
            _pauseIcon = IntPtr.Zero;
            _nextIcon = IntPtr.Zero;
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
// 忽略。
                AppLog.Swallowed("忽略。", ex);
            }
        }

        [DllImport("user32.dll", EntryPoint = "DestroyIcon", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIconNative(IntPtr hIcon);

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
                    System.Runtime.InteropServices.Marshal.FinalReleaseComObject(_taskbar);
                }
                catch (Exception ex)
                {
// 已经释放过就忽略。
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
