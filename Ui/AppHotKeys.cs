using System;
using 播放器.Core;
using System.Runtime.InteropServices;

namespace 播放器.Ui
{
    /// <summary>
    /// 播放器自己的全局热键。
    /// <para>
    /// 和 <see cref="MediaKeys"/> 分开：那一组是"任何程序里都能控制播放"（⏯⏭⏮），
    /// 这一组解决的是桌面歌词的一个死角——<b>锁定之后主界面如果正缩在托盘里，
    /// 就没有任何办法解锁了</b>（锁定的窗口在鼠标眼里不存在，右键菜单点不到；
    /// 托盘里能点，但用户得先想到去那里；Ctrl+D 只在主界面有焦点时有效）。
    /// </para>
    /// <para>
    /// 注册失败（被别的程序占了）不算错误：状态栏会提示，歌词仍然可以从菜单 / 托盘解锁。
    /// </para>
    /// </summary>
    internal static class AppHotKeys
    {
        /// <summary>系统热键消息（WM_HOTKEY）。</summary>
        public const int WmHotKey = 0x0312;

        /// <summary>
        /// 热键 id 的起始值。和 <see cref="MediaKeys"/> 的 <c>0x9E00</c> 分开，
        /// 免得两边的 id 撞在一起、按一下媒体键却去解锁歌词。
        /// </summary>
        private const int HotKeyBase = 0xA100;

        private const uint ModAlt = 0x0001;
        private const uint ModControl = 0x0002;

        /// <summary>按住不放时不重复触发（否则解锁会被连续开关）。</summary>
        private const uint ModNoRepeat = 0x4000;

        private const uint VkD = 0x44;

        /// <summary>桌面歌词锁定 / 解锁：Ctrl + Alt + D。</summary>
        public const int ToggleDesktopLyricsLock = HotKeyBase + 1;

        /// <summary>这个热键在界面上怎么称呼（菜单、状态栏、气泡提示共用一份文字）。</summary>
        public const string ToggleDesktopLyricsLockText = "Ctrl+Alt+D";

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        /// <summary>注册应用级热键，返回注册成功的个数。</summary>
        public static int Register(IntPtr handle)
        {
            var registered = 0;

            try
            {
                if (RegisterHotKey(handle, ToggleDesktopLyricsLock, ModControl | ModAlt | ModNoRepeat, VkD))
                    registered++;
            }
            catch (Exception ex)
            {
// 系统不支持 / 参数非法：当作没注册上
                AppLog.Swallowed("系统不支持 / 参数非法：当作没注册上", ex);
            }

            return registered;
        }

        public static void Unregister(IntPtr handle)
        {
            try
            {
                UnregisterHotKey(handle, ToggleDesktopLyricsLock);
            }
            catch (Exception ex)
            {
// 进程正在退出，注销失败无所谓
                AppLog.Swallowed("进程正在退出，注销失败无所谓", ex);
            }
        }

        /// <summary>
        /// 把 <c>WM_HOTKEY</c> 的 wParam 解成热键编号；不是我们的热键时返回 <c>0</c>。
        /// <para>64 位下 <c>wParam</c> 一律用 <c>ToInt64</c> 读，不要 <c>ToInt32</c>（会溢出）。</para>
        /// </summary>
        public static int Resolve(IntPtr wParam)
        {
            var id = unchecked((int)wParam.ToInt64());
            return id == ToggleDesktopLyricsLock ? id : 0;
        }
    }
}
