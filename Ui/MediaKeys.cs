using System;
using 播放器.Core;
using System.Runtime.InteropServices;

namespace 播放器.Ui
{
    /// <summary>媒体键对应的操作。</summary>
    internal enum MediaKeyCommand
    {
        PlayPause,
        Stop,
        Next,
        Previous
    }

    /// <summary>
    /// 全局媒体键。
    /// <para>
    /// 程序不在前台时也要能用键盘上的 ⏯⏭⏮ 控制，所以走 <c>RegisterHotKey</c> 注册系统级热键，
    /// 而不是只监听 <c>WM_APPCOMMAND</c>（那个只有窗口处于前台才会收到）。
    /// 前台时两条路径都接，互不冲突。
    /// </para>
    /// <para>
    /// 注意：媒体键是独占资源，如果别的播放器（Spotify 等）已经占用了，这里的注册会失败——
    /// 属于正常情况，功能降级为"仅前台可用"，不影响其它功能。
    /// </para>
    /// </summary>
    internal static class MediaKeys
    {
        private const int HotKeyBase = 0x9E00;

        private const int VkMediaNextTrack = 0xB0;
        private const int VkMediaPrevTrack = 0xB1;
        private const int VkMediaStop = 0xB2;
        private const int VkMediaPlayPause = 0xB3;

        // WM_APPCOMMAND 的 lParam 高位
        private const int AppCommandMediaNextTrack = 11;
        private const int AppCommandMediaPrevTrack = 12;
        private const int AppCommandMediaStop = 13;
        private const int AppCommandMediaPlayPause = 14;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        /// <summary>注册四个媒体键。返回成功注册的数量。</summary>
        public static int Register(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return 0;

            var registered = 0;
            if (RegisterHotKey(handle, HotKeyBase + 1, 0, VkMediaPlayPause)) registered++;
            if (RegisterHotKey(handle, HotKeyBase + 2, 0, VkMediaStop)) registered++;
            if (RegisterHotKey(handle, HotKeyBase + 3, 0, VkMediaNextTrack)) registered++;
            if (RegisterHotKey(handle, HotKeyBase + 4, 0, VkMediaPrevTrack)) registered++;

            return registered;
        }

        public static void Unregister(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return;

            for (var i = 1; i <= 4; i++)
            {
                try
                {
                    UnregisterHotKey(handle, HotKeyBase + i);
                }
                catch (Exception ex)
                {
// 忽略。
                    AppLog.Swallowed("忽略。", ex);
                }
            }
        }

        /// <summary>
        /// 把 WM_HOTKEY 的 wParam 解析成操作。
        /// <para>
        /// 参数用 <see cref="IntPtr"/> 而不是 int：64 位下消息参数常是句柄或指针，
        /// 直接 <c>ToInt32()</c> 会抛 <see cref="OverflowException"/>。
        /// </para>
        /// </summary>
        public static MediaKeyCommand? ResolveHotKey(IntPtr wParam)
        {
            var id = (int)(wParam.ToInt64() & 0xFFFF);

            return (id - HotKeyBase) switch
            {
                1 => MediaKeyCommand.PlayPause,
                2 => MediaKeyCommand.Stop,
                3 => MediaKeyCommand.Next,
                4 => MediaKeyCommand.Previous,
                _ => null
            };
        }

        /// <summary>把 WM_APPCOMMAND 的 lParam 解析成操作（程序在前台时走这条）。</summary>
        public static MediaKeyCommand? ResolveAppCommand(IntPtr lParam)
        {
            var value = lParam.ToInt64();
            var command = (int)((value >> 16) & 0x0FFF);

            return command switch
            {
                AppCommandMediaPlayPause => MediaKeyCommand.PlayPause,
                AppCommandMediaStop => MediaKeyCommand.Stop,
                AppCommandMediaNextTrack => MediaKeyCommand.Next,
                AppCommandMediaPrevTrack => MediaKeyCommand.Previous,
                _ => null
            };
        }
    }
}
