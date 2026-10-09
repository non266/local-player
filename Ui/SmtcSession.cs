using System;
using System.Runtime.InteropServices;
using 播放器.Core;

namespace 播放器.Ui
{
    /// <summary>系统媒体控件里的播放状态（<c>Windows.Media.MediaPlaybackStatus</c> 的取值）。</summary>
    internal enum SmtcPlaybackStatus
    {
        Closed = 0,
        Changing = 1,
        Stopped = 2,
        Playing = 3,
        Paused = 4
    }

    /// <summary>系统媒体控件里的媒体类型（<c>Windows.Media.MediaPlaybackType</c> 的取值）。</summary>
    internal enum SmtcPlaybackType
    {
        Unknown = 0,
        Music = 1,
        Video = 2,
        Image = 3
    }

    /// <summary>
    /// 系统媒体控件（SMTC）：Win10/11 音量弹窗、锁屏界面上那一块"正在播放"。
    /// <para>
    /// <b>为什么手写 COM 而不改目标框架</b>：用现成的 WinRT 投影要把 TFM 改成
    /// <c>net8.0-windows10.0.19041.0</c>，那样老 Win10 就被这一版丢掉了；
    /// 手写这几个接口（照 <see cref="TaskbarThumbnailButtons"/> 的做法）代价是自己声明 vtable，
    /// 好处是 <b>TFM 不动</b>。接口的方法顺序与 IID 不是猜的，是从本机 Windows SDK 的
    /// <c>Windows.winmd</c>（10.0.18362）与 <c>SystemMediaTransportControlsInterop.h</c> 里读出来的，
    /// 顺序错一个槽就会调到别的方法上，所以下面按原顺序一个不漏地声明。
    /// </para>
    /// <para>
    /// ⚠ <b>这一版只做"显示"，不做那几个按钮</b>：按钮要靠 <c>ButtonPressed</c> 事件，
    /// 而它的参数是一个 <b>WinRT 委托</b>（<c>IInspectable</c> 派生、<c>Invoke</c> 在第 6 个槽）。
    /// 手写 COM 没法安全地提供一个这样的对象（托管委托包出去的 CCW 是 <c>IDispatch</c> 布局，
    /// 槽位对不上），硬做的话系统一按按钮就是栈错乱——比"没有按钮"糟得多。
    /// 所以 <c>IsPlayEnabled</c> 那几个开关<b>一律不开</b>：宁可不显示按钮，也不显示一排按不动的按钮。
    /// （媒体键仍然好用，那条路是 <c>RegisterHotKey</c>，和这里无关。）
    /// </para>
    /// <para>
    /// ⚠ <b>验到哪一层</b>：冒烟能验"接口拿到了、<c>IsEnabled</c> 与曲名 / 歌手 / 状态写进去读得回来"
    /// ——这些 getter 是<b>真的去问系统那个对象</b>的；但系统界面上到底画没画出来，只能人眼看一次。
    /// </para>
    /// </summary>
    internal sealed class SmtcSession : IDisposable
    {
        // ---- WinRT 激活（combase.dll） ----------------------------------------

        private const int RoInitSingleThreaded = 1;

        /// <summary>已经初始化过（别的模式）——不算错误。</summary>
        private const int ChangedMode = unchecked((int)0x80010106);

        [DllImport("combase.dll", ExactSpelling = true)]
        private static extern int RoInitialize(int initType);

        [DllImport("combase.dll", ExactSpelling = true, PreserveSig = true)]
        private static extern int RoGetActivationFactory(
            IntPtr activatableClassId,
            ref Guid iid,
            [MarshalAs(UnmanagedType.Interface)] out object factory);

        [DllImport("combase.dll", ExactSpelling = true, PreserveSig = true)]
        private static extern int RoActivateInstance(
            IntPtr activatableClassId,
            [MarshalAs(UnmanagedType.Interface)] out object instance);

        [DllImport("combase.dll", ExactSpelling = true)]
        private static extern int WindowsCreateString(
            [MarshalAs(UnmanagedType.LPWStr)] string source, int length, out IntPtr hstring);

        [DllImport("combase.dll", ExactSpelling = true)]
        private static extern int WindowsDeleteString(IntPtr hstring);

        [DllImport("combase.dll", ExactSpelling = true)]
        private static extern IntPtr WindowsGetStringRawBuffer(IntPtr hstring, out uint length);

        private static readonly Guid InteropIid = new Guid("ddb0472d-c911-4a1f-86d9-dc3d71a95f5a");
        private static readonly Guid ControlsIid = new Guid("99fa3ff4-1742-42a6-902e-087d41f965ec");
        private static readonly Guid TimelineIid = new Guid("5125316a-c3a2-475b-8507-93534dc88f15");

        private const string ControlsClass = "Windows.Media.SystemMediaTransportControls";
        private const string TimelineClass = "Windows.Media.SystemMediaTransportControlsTimelineProperties";

        // ---- COM 接口（顺序 = ABI 顺序，一个都不能少、不能换） ----------------
        //
        // ⚠ 每个接口都要**自己再声明一遍 IInspectable 的那三个方法**（GetIids / GetRuntimeClassName /
        // GetTrustLevel），InterfaceType 写 InterfaceIsIUnknown：
        // .NET 运行时不支持 `UnmanagedType.IInspectable` / `ComInterfaceType.InterfaceIsIInspectable`
        // （实测报 "Marshalling as IInspectable is not supported in the .NET runtime."），
        // 而 WinRT 接口的 vtable 是 IUnknown(3) + IInspectable(3) + 各自的方法。
        // 把它们当普通方法声明出来，槽位正好占住第 4~6 个，后面的方法就落在第 7 个及以后 ✓。
        // 这三个我们**从不调用**，参数一律 IntPtr/int 占位（免得再踩 HSTRING 的坑）。

        [ComImport]
        [Guid("ddb0472d-c911-4a1f-86d9-dc3d71a95f5a")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ISystemMediaTransportControlsInterop
        {
            [PreserveSig] int GetIids(out int iidCount, out IntPtr iids);
            [PreserveSig] int GetRuntimeClassName(out IntPtr name);
            [PreserveSig] int GetTrustLevel(out int level);

            [PreserveSig]
            int GetForWindow(
                IntPtr appWindow,
                ref Guid riid,
                [MarshalAs(UnmanagedType.Interface)] out ISystemMediaTransportControls controls);
        }

        [ComImport]
        [Guid("99fa3ff4-1742-42a6-902e-087d41f965ec")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ISystemMediaTransportControls
        {
            [PreserveSig] int GetIids(out int iidCount, out IntPtr iids);
            [PreserveSig] int GetRuntimeClassName(out IntPtr name);
            [PreserveSig] int GetTrustLevel(out int level);

            [PreserveSig] int get_PlaybackStatus(out SmtcPlaybackStatus value);
            [PreserveSig] int put_PlaybackStatus(SmtcPlaybackStatus value);
            [PreserveSig] int get_DisplayUpdater(
                [MarshalAs(UnmanagedType.Interface)] out ISystemMediaTransportControlsDisplayUpdater value);
            [PreserveSig] int get_SoundLevel(out int value);
            [PreserveSig] int get_IsEnabled([MarshalAs(UnmanagedType.Bool)] out bool value);
            [PreserveSig] int put_IsEnabled([MarshalAs(UnmanagedType.Bool)] bool value);
            [PreserveSig] int get_IsPlayEnabled([MarshalAs(UnmanagedType.Bool)] out bool value);
            [PreserveSig] int put_IsPlayEnabled([MarshalAs(UnmanagedType.Bool)] bool value);
            [PreserveSig] int get_IsStopEnabled([MarshalAs(UnmanagedType.Bool)] out bool value);
            [PreserveSig] int put_IsStopEnabled([MarshalAs(UnmanagedType.Bool)] bool value);
            [PreserveSig] int get_IsPauseEnabled([MarshalAs(UnmanagedType.Bool)] out bool value);
            [PreserveSig] int put_IsPauseEnabled([MarshalAs(UnmanagedType.Bool)] bool value);
            [PreserveSig] int get_IsRecordEnabled([MarshalAs(UnmanagedType.Bool)] out bool value);
            [PreserveSig] int put_IsRecordEnabled([MarshalAs(UnmanagedType.Bool)] bool value);
            [PreserveSig] int get_IsFastForwardEnabled([MarshalAs(UnmanagedType.Bool)] out bool value);
            [PreserveSig] int put_IsFastForwardEnabled([MarshalAs(UnmanagedType.Bool)] bool value);
            [PreserveSig] int get_IsRewindEnabled([MarshalAs(UnmanagedType.Bool)] out bool value);
            [PreserveSig] int put_IsRewindEnabled([MarshalAs(UnmanagedType.Bool)] bool value);
            [PreserveSig] int get_IsPreviousEnabled([MarshalAs(UnmanagedType.Bool)] out bool value);
            [PreserveSig] int put_IsPreviousEnabled([MarshalAs(UnmanagedType.Bool)] bool value);
            [PreserveSig] int get_IsNextEnabled([MarshalAs(UnmanagedType.Bool)] out bool value);
            [PreserveSig] int put_IsNextEnabled([MarshalAs(UnmanagedType.Bool)] bool value);
            [PreserveSig] int get_IsChannelUpEnabled([MarshalAs(UnmanagedType.Bool)] out bool value);
            [PreserveSig] int put_IsChannelUpEnabled([MarshalAs(UnmanagedType.Bool)] bool value);
            [PreserveSig] int get_IsChannelDownEnabled([MarshalAs(UnmanagedType.Bool)] out bool value);
            [PreserveSig] int put_IsChannelDownEnabled([MarshalAs(UnmanagedType.Bool)] bool value);

            // 事件（add_ / remove_）在 ABI 里排在这里。这一版不订阅，
            // 但槽位必须留着——少了它们后面就没有别的成员了，留着是为了让上面的顺序不会
            // "看起来像是对的"：这两个方法一在，谁改错了顺序都会被编译期/审查挡一下。
            [PreserveSig] int add_ButtonPressed(IntPtr handler, out long token);
            [PreserveSig] int remove_ButtonPressed(long token);
            [PreserveSig] int add_PropertyChanged(IntPtr handler, out long token);
            [PreserveSig] int remove_PropertyChanged(long token);
        }

        [ComImport]
        [Guid("ea98d2f6-7f3c-4af2-a586-72889808efb1")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ISystemMediaTransportControls2
        {
            [PreserveSig] int GetIids(out int iidCount, out IntPtr iids);
            [PreserveSig] int GetRuntimeClassName(out IntPtr name);
            [PreserveSig] int GetTrustLevel(out int level);

            [PreserveSig] int get_AutoRepeatMode(out int value);
            [PreserveSig] int put_AutoRepeatMode(int value);
            [PreserveSig] int get_ShuffleEnabled([MarshalAs(UnmanagedType.Bool)] out bool value);
            [PreserveSig] int put_ShuffleEnabled([MarshalAs(UnmanagedType.Bool)] bool value);
            [PreserveSig] int get_PlaybackRate(out double value);
            [PreserveSig] int put_PlaybackRate(double value);
            [PreserveSig] int UpdateTimelineProperties([MarshalAs(UnmanagedType.Interface)] object timeline);
        }

        [ComImport]
        [Guid("8abbc53e-fa55-4ecf-ad8e-c984e5dd1550")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ISystemMediaTransportControlsDisplayUpdater
        {
            [PreserveSig] int GetIids(out int iidCount, out IntPtr iids);
            [PreserveSig] int GetRuntimeClassName(out IntPtr name);
            [PreserveSig] int GetTrustLevel(out int level);

            [PreserveSig] int get_Type(out SmtcPlaybackType value);
            [PreserveSig] int put_Type(SmtcPlaybackType value);
            [PreserveSig] int get_AppMediaId(out IntPtr value);
            [PreserveSig] int put_AppMediaId(IntPtr value);
            [PreserveSig] int get_Thumbnail([MarshalAs(UnmanagedType.Interface)] out object value);
            [PreserveSig] int put_Thumbnail([MarshalAs(UnmanagedType.Interface)] object value);
            [PreserveSig] int get_MusicProperties([MarshalAs(UnmanagedType.Interface)] out IMusicDisplayProperties value);
            [PreserveSig] int get_VideoProperties([MarshalAs(UnmanagedType.Interface)] out object value);
            [PreserveSig] int get_ImageProperties([MarshalAs(UnmanagedType.Interface)] out object value);
            [PreserveSig] int CopyFromFileAsync(SmtcPlaybackType type, IntPtr file, out IntPtr operation);
            [PreserveSig] int ClearAll();
            [PreserveSig] int Update();
        }

        [ComImport]
        [Guid("6bbf0c59-d0a0-4d26-92a0-f978e1d18e7b")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMusicDisplayProperties
        {
            [PreserveSig] int GetIids(out int iidCount, out IntPtr iids);
            [PreserveSig] int GetRuntimeClassName(out IntPtr name);
            [PreserveSig] int GetTrustLevel(out int level);

            // ⚠ 字符串是 HSTRING，而 .NET 的 `UnmanagedType.HString` 在运行时里同样**不支持**
            // （实测 "Cannot marshal 'parameter #1'"），所以这里一律用 IntPtr + 自己调
            // WindowsCreateString / WindowsGetStringRawBuffer（见 SetMusicString / ReadMusic）。
            [PreserveSig] int get_Title(out IntPtr value);
            [PreserveSig] int put_Title(IntPtr value);
            [PreserveSig] int get_AlbumArtist(out IntPtr value);
            [PreserveSig] int put_AlbumArtist(IntPtr value);
            [PreserveSig] int get_Artist(out IntPtr value);
            [PreserveSig] int put_Artist(IntPtr value);
        }

        [ComImport]
        [Guid("5125316a-c3a2-475b-8507-93534dc88f15")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ISystemMediaTransportControlsTimelineProperties
        {
            [PreserveSig] int GetIids(out int iidCount, out IntPtr iids);
            [PreserveSig] int GetRuntimeClassName(out IntPtr name);
            [PreserveSig] int GetTrustLevel(out int level);

            [PreserveSig] int get_StartTime(out long value);
            [PreserveSig] int put_StartTime(long value);
            [PreserveSig] int get_EndTime(out long value);
            [PreserveSig] int put_EndTime(long value);
            [PreserveSig] int get_MinSeekTime(out long value);
            [PreserveSig] int put_MinSeekTime(long value);
            [PreserveSig] int get_MaxSeekTime(out long value);
            [PreserveSig] int put_MaxSeekTime(long value);
            [PreserveSig] int get_Position(out long value);
            [PreserveSig] int put_Position(long value);
        }

        // ---- 实例 -------------------------------------------------------------

        private ISystemMediaTransportControls? _controls;
        private ISystemMediaTransportControls2? _more;
        private ISystemMediaTransportControlsDisplayUpdater? _updater;
        private IMusicDisplayProperties? _music;
        private ISystemMediaTransportControlsTimelineProperties? _timeline;
        private bool _disposed;

        /// <summary>拿不到接口时的理由（只在创建那一次问出来，日志里记一次，不打扰用户）。</summary>
        internal static string? UnavailableReason { get; private set; }

        /// <summary>我们喂进去的时长 / 位置（系统那侧没有读回来的接口，如实记在这边）。</summary>
        internal long DurationMilliseconds { get; private set; }

        internal long PositionMilliseconds { get; private set; }

        /// <summary>时间轴有没有喂过（<c>UpdateTimelineProperties</c> 没有返回值，只能记"喂过"）。</summary>
        internal bool TimelineUpdated { get; private set; }

        private SmtcSession()
        {
        }

        /// <summary>系统那边的 <c>IsEnabled</c>（读回来问系统那个对象，不是记我们自己的）。</summary>
        internal bool IsEnabled
        {
            get
            {
                if (_disposed || _controls == null) return false;

                try
                {
                    return _controls.get_IsEnabled(out var value) >= 0 && value;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        /// <summary>系统那边的播放状态（读回来）。</summary>
        internal SmtcPlaybackStatus Status
        {
            get
            {
                if (_disposed || _controls == null) return SmtcPlaybackStatus.Closed;

                try
                {
                    return _controls.get_PlaybackStatus(out var value) >= 0 ? value : SmtcPlaybackStatus.Closed;
                }
                catch (Exception)
                {
                    return SmtcPlaybackStatus.Closed;
                }
            }
        }

        /// <summary>系统那边的曲名（读回来）。</summary>
        internal string Title => ReadMusic(title: true);

        /// <summary>系统那边的艺术家（读回来）。</summary>
        internal string Artist => ReadMusic(title: false);

        private string ReadMusic(bool title)
        {
            if (_disposed || _music == null) return string.Empty;

            IntPtr hstring;

            try
            {
                var hr = title ? _music.get_Title(out hstring) : _music.get_Artist(out hstring);

                if (hr < 0)
                {
                    AppLog.Info($"读系统媒体控件的{(title ? "曲名" : "艺术家")}失败：0x{hr:X8}");
                    return string.Empty;
                }

                if (hstring == IntPtr.Zero) return string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }

            try
            {
                var raw = WindowsGetStringRawBuffer(hstring, out var length);

                return raw == IntPtr.Zero || length == 0
                    ? string.Empty
                    : Marshal.PtrToStringUni(raw, (int)length) ?? string.Empty;
            }
            finally
            {
                WindowsDeleteString(hstring);
            }
        }

        private delegate int StringSetter(IntPtr value);

        /// <summary>把一串 .NET 字符串作为 HSTRING 交给系统（自己建、自己删）。</summary>
        private static void SetMusicString(StringSetter setter, string? value)
        {
            var text = value ?? string.Empty;

            if (WindowsCreateString(text, text.Length, out var hstring) < 0) return;

            try
            {
                var result = setter(hstring);

                if (result < 0) AppLog.Info($"给系统媒体控件写字符串失败：0x{result:X8}");
            }
            catch (Exception ex)
            {
                AppLog.Swallowed("把曲名推给系统媒体控件失败。", ex);
            }
            finally
            {
                WindowsDeleteString(hstring);
            }
        }

        /// <summary>
        /// 给窗口建一个系统媒体控件会话。拿不到（系统太老 / 被策略禁掉 / 这个窗口不行）时返回 <c>null</c>，
        /// 并把理由写进 <paramref name="note"/> 与 <see cref="UnavailableReason"/>。
        /// </summary>
        internal static SmtcSession? TryCreate(IntPtr windowHandle, out string note)
        {
            note = string.Empty;

            if (windowHandle == IntPtr.Zero)
            {
                note = "窗口句柄还没建好";
                UnavailableReason = note;
                return null;
            }

            try
            {
                var init = RoInitialize(RoInitSingleThreaded);
                if (init < 0 && init != ChangedMode)
                {
                    note = $"RoInitialize 失败（0x{init:X8}）";
                    UnavailableReason = note;
                    return null;
                }

                var factory = ActivationFactory(ControlsClass, InteropIid, out var factoryError);
                if (factory == null)
                {
                    note = factoryError;
                    UnavailableReason = note;
                    return null;
                }

                var interop = (ISystemMediaTransportControlsInterop)factory;
                var iid = ControlsIid;

                if (interop.GetForWindow(windowHandle, ref iid, out var controls) != 0 || controls == null)
                {
                    note = "GetForWindow 没给出系统媒体控件（这个窗口可能不被支持）";
                    UnavailableReason = note;
                    return null;
                }

                var session = new SmtcSession { _controls = controls };

                controls.get_DisplayUpdater(out var updater);
                session._updater = updater;

                if (updater != null)
                {
                    // 音乐（曲名 / 歌手），并把它挂上
                    var typeResult = updater.put_Type(SmtcPlaybackType.Music);
                    var musicResult = updater.get_MusicProperties(out var music);
                    session._music = music;

                    if (typeResult < 0 || musicResult < 0 || music == null)
                    {
                        AppLog.Info($"系统媒体控件：显示那块没接上"
                                    + $"（put_Type=0x{typeResult:X8}、get_MusicProperties=0x{musicResult:X8}）");
                    }
                }

                // 时间轴要另一个接口（ISystemMediaTransportControls2）+ 一个系统那边的时间轴对象
                session._more = controls as ISystemMediaTransportControls2;

                if (ActivateInstance(TimelineClass, out var instance) && instance != null)
                    session._timeline = instance as ISystemMediaTransportControlsTimelineProperties;

                controls.put_IsEnabled(true);

                if (!session.IsEnabled)
                {
                    note = "系统媒体控件没接受 IsEnabled";
                    UnavailableReason = note;
                    session.Dispose();
                    return null;
                }

                UnavailableReason = null;
                AppLog.Info("系统媒体控件已接上（音量弹窗 / 锁屏里的「正在播放」）。");
                return session;
            }
            catch (Exception ex)
            {
                note = "接系统媒体控件时出错：" + ex.Message;
                UnavailableReason = note;
                return null;
            }
        }

        /// <summary>把这一首的曲名 / 歌手推给系统（换歌时调用）。空值会推空串，不会残留上一首。</summary>
        internal void SetTrack(string? title, string? artist)
        {
            if (_disposed || _music == null) return;

            SetMusicString(_music.put_Title, title);
            SetMusicString(_music.put_Artist, artist);
            SetMusicString(_music.put_AlbumArtist, artist);

            try
            {
                _updater?.Update();
            }
            catch (Exception ex)
            {
                AppLog.Swallowed("让系统媒体控件刷新显示失败。", ex);
            }
        }

        /// <summary>把播放状态推给系统。</summary>
        internal void SetStatus(SmtcPlaybackStatus status)
        {
            if (_disposed || _controls == null) return;

            try
            {
                _controls.put_PlaybackStatus(status);
            }
            catch (Exception ex)
            {
                AppLog.Swallowed("把播放状态推给系统媒体控件失败。", ex);
            }
        }

        /// <summary>把时长与当前位置推给系统（音量弹窗里那条进度）。</summary>
        internal void SetTimeline(long durationMilliseconds, long positionMilliseconds)
        {
            DurationMilliseconds = Math.Max(0, durationMilliseconds);
            PositionMilliseconds = Math.Max(0, positionMilliseconds);

            if (_disposed || _more == null || _timeline == null) return;

            try
            {
                // WinRT 的 TimeSpan 就是 100 纳秒的整数（= .NET 的 TimeSpan.Ticks）
                _timeline.put_StartTime(0);
                _timeline.put_EndTime(TimeSpan.FromMilliseconds(DurationMilliseconds).Ticks);
                _timeline.put_MinSeekTime(0);
                _timeline.put_MaxSeekTime(TimeSpan.FromMilliseconds(DurationMilliseconds).Ticks);
                _timeline.put_Position(TimeSpan.FromMilliseconds(PositionMilliseconds).Ticks);

                if (_more.UpdateTimelineProperties(_timeline) != 0) return;

                TimelineUpdated = true;
            }
            catch (Exception ex)
            {
                AppLog.Swallowed("把时间轴推给系统媒体控件失败。", ex);
            }
        }

        /// <summary>清掉系统那边显示的曲目（没有在播的时候）。</summary>
        internal void Clear()
        {
            if (_disposed) return;

            try
            {
                _updater?.ClearAll();
                _controls?.put_PlaybackStatus(SmtcPlaybackStatus.Closed);
                DurationMilliseconds = 0;
                PositionMilliseconds = 0;
                TimelineUpdated = false;
            }
            catch (Exception ex)
            {
                AppLog.Swallowed("清系统媒体控件失败。", ex);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // 关掉 IsEnabled：不走这一步的话，音量弹窗里会一直挂着"上一首"。
            try
            {
                _controls?.put_IsEnabled(false);
            }
            catch (Exception)
            {
                // 退出路径上失败无所谓
            }

            _timeline = null;
            _music = null;
            _updater = null;
            _more = null;
            _controls = null;
        }

        // ---- 激活小工具 -------------------------------------------------------

        private static object? ActivationFactory(string className, Guid iid, out string error)
        {
            error = string.Empty;

            if (WindowsCreateString(className, className.Length, out var hstring) != 0)
            {
                error = "建不了类名的 HSTRING";
                return null;
            }

            try
            {
                var hr = RoGetActivationFactory(hstring, ref iid, out var factory);

                if (hr < 0 || factory == null)
                {
                    error = $"拿不到 {className} 的激活工厂（0x{hr:X8}）";
                    return null;
                }

                return factory;
            }
            finally
            {
                WindowsDeleteString(hstring);
            }
        }

        private static bool ActivateInstance(string className, out object? instance)
        {
            instance = null;

            if (WindowsCreateString(className, className.Length, out var hstring) != 0) return false;

            try
            {
                return RoActivateInstance(hstring, out instance) >= 0 && instance != null;
            }
            finally
            {
                WindowsDeleteString(hstring);
            }
        }
    }
}
