using System;
using System.Collections.Generic;
using System.Linq;
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

    /// <summary>一个全局媒体键：注册用的 id、对应的虚拟键、以及出问题时给用户看的名字。</summary>
    internal sealed class MediaKeyDefinition
    {
        public MediaKeyDefinition(int id, int virtualKey, string name)
        {
            Id = id;
            VirtualKey = virtualKey;
            Name = name;
        }

        /// <summary>RegisterHotKey 的 id（也是 WM_HOTKEY 的 wParam 低位）。</summary>
        public int Id { get; }

        /// <summary>虚拟键码（VK_MEDIA_*）。</summary>
        public int VirtualKey { get; }

        /// <summary>界面上叫它什么（"播放 / 暂停"）。</summary>
        public string Name { get; }
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

        /// <summary>要注册的四个键；顺序就是注册顺序，id 与 <see cref="ResolveHotKey"/> 一一对应。</summary>
        public static readonly IReadOnlyList<MediaKeyDefinition> Requested = new[]
        {
            new MediaKeyDefinition(HotKeyBase + 1, VkMediaPlayPause, "播放 / 暂停"),
            new MediaKeyDefinition(HotKeyBase + 2, VkMediaStop, "停止"),
            new MediaKeyDefinition(HotKeyBase + 3, VkMediaNextTrack, "下一首"),
            new MediaKeyDefinition(HotKeyBase + 4, VkMediaPrevTrack, "上一首")
        };

        /// <summary>
        /// 注册四个媒体键，返回<b>真正注册成功的那几个 id</b>。
        /// <para>
        /// 以前这里只返回"成功几个"，调用方把非零当成了全部成功：4 个里只成了 1 个时界面什么都不说，
        /// 另外 3 个键按下去没反应，用户只会以为程序坏了。媒体键是<b>独占资源</b>
        /// （别的播放器占了就注册不上），所以这件事必须能说清是哪几个。
        /// </para>
        /// </summary>
        public static IReadOnlyList<int> Register(IntPtr handle)
        {
            var registered = new List<int>();

            if (handle == IntPtr.Zero) return registered;

            foreach (var key in Requested)
            {
                if (RegisterHotKey(handle, key.Id, 0, (uint)key.VirtualKey)) registered.Add(key.Id);
            }

            return registered;
        }

        /// <summary>没注册上的那几个（返回定义，便于取名字）。</summary>
        public static IReadOnlyList<MediaKeyDefinition> Missing(IReadOnlyCollection<int>? registered)
        {
            var done = new HashSet<int>(registered ?? (IReadOnlyCollection<int>)Array.Empty<int>());

            return Requested.Where(key => !done.Contains(key.Id)).ToList();
        }

        /// <summary>
        /// 把"哪几个没注册上"说成一句人话；一个都没缺时返回 <c>null</c>。
        /// <para>名字要写出来（而不是只说个数）：用户看到"下一首没注册上"才知道去关掉谁。</para>
        /// </summary>
        public static string? DescribeMissing(IReadOnlyCollection<int>? registered)
        {
            var missing = Missing(registered);

            if (missing.Count == 0) return null;

            var names = string.Join("、", missing.Select(key => key.Name));
            var head = missing.Count == Requested.Count
                ? "全局媒体键一个都没注册上"
                : $"全局媒体键有 {missing.Count} 个没注册上";

            return $"{head}：{names}（可能已被其它播放器占用；这几个键只在前台有效）";
        }

        public static void Unregister(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return;

            foreach (var key in Requested)
            {
                try
                {
                    UnregisterHotKey(handle, key.Id);
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
