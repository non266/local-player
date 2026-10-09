using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using 播放器.Core;
using Windows.Media;
using Windows.Storage;
using Windows.Storage.Streams;

namespace 播放器.Ui
{
    /// <summary>系统媒体控件上那几个按钮（只映射我们用得到的）。</summary>
    internal enum SmtcButton
    {
        Play,
        Pause,
        Stop,
        Next,
        Previous
    }

    /// <summary>
    /// 系统媒体控件（SMTC）：Win10/11 音量弹窗、锁屏界面上那一块"正在播放"，
    /// 以及上面那几个按钮。
    /// <para>
    /// <b>这一版走 WinRT 投影</b>（目标框架 <c>net8.0-windows10.0.19041.0</c>）：
    /// 曲名 / 歌手 / 封面 / 进度 / 按钮事件全都是普通属性与事件，
    /// 不必再手写 vtable、也不必自己揉 HSTRING。
    /// 早先那一版"手写 COM"的写法（连同踩到的
    /// <c>UnmanagedType.IInspectable</c> / <c>UnmanagedType.HString</c> 两个运行时限制）
    /// 记在 [README 的实现要点] 与 CHANGELOG 里，留作"为什么当初绕了远路"的档案。
    /// </para>
    /// <para>
    /// 只有一处还得手写：<c>GetForWindow</c> 属于**头文件里的互操作接口**
    /// （<c>SystemMediaTransportControlsInterop.h</c>），投影里没有它，所以照旧自己声明一次，
    /// 拿到裸指针之后用 <c>MarshalInspectable</c> 换成投影对象。
    /// </para>
    /// <para>
    /// ⚠ <b>验到哪一层</b>：冒烟能验"接口拿到了、<c>IsEnabled</c> 与曲名 / 歌手 / 状态 / 时长
    /// 写进去读得回来、按钮开关都开了、按钮回调接上了"；
    /// <b>系统界面上到底画没画出来、按钮按下去系统那边有没有反应，只能人眼看</b>。
    /// </para>
    /// </summary>
    internal sealed class SmtcSession : IDisposable
    {
        private const int RoInitSingleThreaded = 1;
        private const int ChangedMode = unchecked((int)0x80010106);

        [DllImport("combase.dll", ExactSpelling = true)]
        private static extern int RoInitialize(int initType);

        [DllImport("combase.dll", ExactSpelling = true, PreserveSig = true)]
        private static extern int RoGetActivationFactory(
            IntPtr activatableClassId,
            ref Guid iid,
            [MarshalAs(UnmanagedType.Interface)] out object factory);

        [DllImport("combase.dll", ExactSpelling = true)]
        private static extern int WindowsCreateString(
            [MarshalAs(UnmanagedType.LPWStr)] string source, int length, out IntPtr hstring);

        [DllImport("combase.dll", ExactSpelling = true)]
        private static extern int WindowsDeleteString(IntPtr hstring);

        private static readonly Guid InteropIid = new Guid("ddb0472d-c911-4a1f-86d9-dc3d71a95f5a");
        private static readonly Guid ControlsIid = new Guid("99fa3ff4-1742-42a6-902e-087d41f965ec");

        /// <summary>
        /// 那个只在头文件里出现的互操作接口。
        /// <para>
        /// 槽位说明见注释末尾：WinRT 接口的 vtable 是 IUnknown(3) + IInspectable(3) + 各自的方法，
        /// 而 .NET 运行时不支持 <c>UnmanagedType.IInspectable</c>（实测报
        /// "Marshalling as IInspectable is not supported in the .NET runtime."），
        /// 所以把 IInspectable 那三个方法当普通方法声明出来占住第 4~6 个槽。
        /// 这三个我们从不调用，参数用 IntPtr/int 占位。
        /// </para>
        /// </summary>
        [ComImport]
        [Guid("ddb0472d-c911-4a1f-86d9-dc3d71a95f5a")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ISystemMediaTransportControlsInterop
        {
            [PreserveSig] int GetIids(out int iidCount, out IntPtr iids);
            [PreserveSig] int GetRuntimeClassName(out IntPtr name);
            [PreserveSig] int GetTrustLevel(out int level);

            [PreserveSig] int GetForWindow(IntPtr appWindow, ref Guid riid, out IntPtr controls);
        }

        private SystemMediaTransportControls? _controls;
        private bool _disposed;

        /// <summary>音量弹窗 / 锁屏上那几个按钮被按下了（在 WinRT 的线程上触发）。</summary>
        internal event Action<SmtcButton>? ButtonPressed;

        /// <summary>拿不到接口时的理由（只在创建那一次问出来，日志里记一次，不打扰用户）。</summary>
        internal static string? UnavailableReason { get; private set; }

        /// <summary>我们喂进去的时长 / 位置（系统那侧没有读回来的接口，如实记在这边）。</summary>
        internal long DurationMilliseconds { get; private set; }

        internal long PositionMilliseconds { get; private set; }

        /// <summary>时间轴有没有喂过。</summary>
        internal bool TimelineUpdated { get; private set; }

        /// <summary>封面有没有喂过（内嵌图落成临时文件之后异步设上去的）。</summary>
        internal bool CoverSet { get; private set; }

        /// <summary>系统那边的 IsEnabled（读回来问系统那个对象）。</summary>
        internal bool IsEnabled => _controls?.IsEnabled ?? false;

        /// <summary>那五个按钮开关（读回来；弹窗上显示不显示这些按钮就看它）。</summary>
        internal bool IsPlayEnabled => _controls?.IsPlayEnabled ?? false;

        internal bool IsPauseEnabled => _controls?.IsPauseEnabled ?? false;

        internal bool IsStopEnabled => _controls?.IsStopEnabled ?? false;

        internal bool IsNextEnabled => _controls?.IsNextEnabled ?? false;

        internal bool IsPreviousEnabled => _controls?.IsPreviousEnabled ?? false;

        /// <summary>系统那边的播放状态（读回来）。</summary>
        internal MediaPlaybackStatus Status => _controls?.PlaybackStatus ?? MediaPlaybackStatus.Closed;

        /// <summary>系统那边的曲名（读回来）。</summary>
        internal string Title => ReadTitle();

        /// <summary>系统那边的艺术家（读回来）。</summary>
        internal string Artist => ReadArtist();

        private SmtcSession(SystemMediaTransportControls controls) => _controls = controls;

        /// <summary>
        /// 给窗口建一个系统媒体控件会话。拿不到（系统太老 / 被策略禁掉）时返回 <c>null</c>，
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

                if (!TryGetForWindow(windowHandle, out var controls, out var error))
                {
                    note = error;
                    UnavailableReason = note;
                    return null;
                }

                var session = new SmtcSession(controls);

                controls.DisplayUpdater.Type = MediaPlaybackType.Music;

                // 按钮：开哪几个，弹窗 / 锁屏上就显示哪几个
                controls.IsPlayEnabled = true;
                controls.IsPauseEnabled = true;
                controls.IsStopEnabled = true;
                controls.IsNextEnabled = true;
                controls.IsPreviousEnabled = true;
                controls.IsEnabled = true;

                if (!controls.IsEnabled)
                {
                    note = "系统媒体控件没接受 IsEnabled";
                    UnavailableReason = note;
                    session.Dispose();
                    return null;
                }

                controls.ButtonPressed += (sender, args) => session.Raise(args.Button);

                UnavailableReason = null;
                AppLog.Info("系统媒体控件已接上（音量弹窗 / 锁屏里的「正在播放」，含那四个按钮）。");
                return session;
            }
            catch (Exception ex)
            {
                note = "接系统媒体控件时出错：" + ex.Message;
                UnavailableReason = note;
                return null;
            }
        }

        /// <summary>
        /// 供冒烟测试驱动"按钮按下去之后我们做了什么"。
        /// <para>
        /// ⚠ 它<b>验不到</b>"系统真的把事件送过来了"（那要人去点一下音量弹窗上的按钮），
        /// 验的是<b>我们自己这一侧</b>：事件 → 映射 → 主窗体那五个动作。
        /// </para>
        /// </summary>
        internal void SimulateButton(SmtcButton button) => Raise(button switch
        {
            SmtcButton.Play => SystemMediaTransportControlsButton.Play,
            SmtcButton.Pause => SystemMediaTransportControlsButton.Pause,
            SmtcButton.Stop => SystemMediaTransportControlsButton.Stop,
            SmtcButton.Next => SystemMediaTransportControlsButton.Next,
            _ => SystemMediaTransportControlsButton.Previous
        });

        private void Raise(SystemMediaTransportControlsButton button)
        {
            if (_disposed) return;

            var mapped = button switch
            {
                SystemMediaTransportControlsButton.Play => SmtcButton.Play,
                SystemMediaTransportControlsButton.Pause => SmtcButton.Pause,
                SystemMediaTransportControlsButton.Stop => SmtcButton.Stop,
                SystemMediaTransportControlsButton.Next => SmtcButton.Next,
                SystemMediaTransportControlsButton.Previous => SmtcButton.Previous,
                _ => (SmtcButton?)null
            };

            if (mapped == null) return;

            try
            {
                ButtonPressed?.Invoke(mapped.Value);
            }
            catch (Exception ex)
            {
                AppLog.Swallowed("处理系统媒体控件的按钮失败。", ex);
            }
        }

        /// <summary>把这一首的曲名 / 歌手推给系统（换歌时调用）。空值会推空串，不会残留上一首。</summary>
        internal void SetTrack(string? title, string? artist, bool isVideo)
        {
            if (_disposed || _controls == null) return;

            try
            {
                var updater = _controls.DisplayUpdater;
                updater.Type = isVideo ? MediaPlaybackType.Video : MediaPlaybackType.Music;

                if (isVideo)
                {
                    updater.VideoProperties.Title = title ?? string.Empty;
                    updater.VideoProperties.Subtitle = artist ?? string.Empty;
                }
                else
                {
                    updater.MusicProperties.Title = title ?? string.Empty;
                    updater.MusicProperties.Artist = artist ?? string.Empty;
                    updater.MusicProperties.AlbumArtist = artist ?? string.Empty;
                }

                updater.Update();
            }
            catch (Exception ex)
            {
                AppLog.Swallowed("把曲名推给系统媒体控件失败。", ex);
            }
        }

        /// <summary>把播放状态推给系统。</summary>
        internal void SetStatus(MediaPlaybackStatus status)
        {
            if (_disposed || _controls == null) return;

            try
            {
                _controls.PlaybackStatus = status;
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

            if (_disposed || _controls == null) return;

            try
            {
                var end = TimeSpan.FromMilliseconds(DurationMilliseconds);

                _controls.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
                {
                    StartTime = TimeSpan.Zero,
                    EndTime = end,
                    MinSeekTime = TimeSpan.Zero,
                    MaxSeekTime = end,
                    Position = TimeSpan.FromMilliseconds(PositionMilliseconds)
                });

                TimelineUpdated = true;
            }
            catch (Exception ex)
            {
                AppLog.Swallowed("把时间轴推给系统媒体控件失败。", ex);
            }
        }

        /// <summary>
        /// 把封面推给系统：内嵌图先落到数据目录里一个临时文件，再让系统自己去读
        /// （<c>RandomAccessStreamReference.CreateFromFile</c> 只认 <c>StorageFile</c>，而它只能异步拿）。
        /// </summary>
        internal void SetCover(byte[]? data, string? mimeType)
        {
            if (_disposed || _controls == null) return;

            if (data is not { Length: > 0 })
            {
                CoverSet = false;
                return;
            }

            string path;

            try
            {
                var extension = mimeType switch
                {
                    "image/png" => ".png",
                    "image/gif" => ".gif",
                    "image/bmp" => ".bmp",
                    _ => ".jpg"
                };

                path = Path.Combine(AppSettings.SettingsDirectory, "smtc-cover" + extension);
                File.WriteAllBytes(path, data);
            }
            catch (Exception ex)
            {
                AppLog.Swallowed("把封面落成临时文件失败。", ex);
                return;
            }

            _ = SetCoverAsync(path);
        }

        private async Task SetCoverAsync(string path)
        {
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(path);
                if (_disposed || _controls == null) return;

                _controls.DisplayUpdater.Thumbnail = RandomAccessStreamReference.CreateFromFile(file);
                CoverSet = true;
            }
            catch (Exception ex)
            {
                AppLog.Swallowed("把封面推给系统媒体控件失败。", ex);
            }
        }

        /// <summary>清掉系统那边显示的曲目（没有在播的时候）。</summary>
        internal void Clear()
        {
            if (_disposed || _controls == null) return;

            try
            {
                _controls.DisplayUpdater.ClearAll();
                _controls.PlaybackStatus = MediaPlaybackStatus.Closed;
                _controls.DisplayUpdater.Thumbnail = null;

                DurationMilliseconds = 0;
                PositionMilliseconds = 0;
                TimelineUpdated = false;
                CoverSet = false;
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
                if (_controls != null)
                {
                    _controls.IsEnabled = false;
                    _controls.ButtonPressed -= (sender, args) => { };
                }
            }
            catch (Exception)
            {
                // 退出路径上失败无所谓
            }

            _controls = null;
        }

        private static bool TryGetForWindow(IntPtr windowHandle, out SystemMediaTransportControls controls, out string error)
        {
            controls = null!;
            error = string.Empty;

            const string className = "Windows.Media.SystemMediaTransportControls";

            if (WindowsCreateString(className, className.Length, out var hstring) != 0)
            {
                error = "建不了类名的 HSTRING";
                return false;
            }

            try
            {
                var iid = InteropIid;

                if (RoGetActivationFactory(hstring, ref iid, out var factory) < 0 || factory == null)
                {
                    error = $"拿不到 {className} 的互操作工厂（要 Win10 1809+）";
                    return false;
                }

                var interop = (ISystemMediaTransportControlsInterop)factory;
                var controlsIid = ControlsIid;

                if (interop.GetForWindow(windowHandle, ref controlsIid, out var raw) < 0 || raw == IntPtr.Zero)
                {
                    error = "GetForWindow 没给出系统媒体控件（这个窗口可能不被支持）";
                    return false;
                }

                try
                {
                    controls = WinRT.MarshalInspectable<SystemMediaTransportControls>.FromAbi(raw);
                }
                finally
                {
                    Marshal.Release(raw);
                }

                return controls != null;
            }
            catch (Exception ex)
            {
                error = "拿系统媒体控件出错：" + ex.Message;
                return false;
            }
            finally
            {
                WindowsDeleteString(hstring);
            }
        }

        private string ReadTitle()
        {
            if (_disposed || _controls == null) return string.Empty;

            try
            {
                var updater = _controls.DisplayUpdater;

                return updater.Type == MediaPlaybackType.Video
                    ? updater.VideoProperties.Title ?? string.Empty
                    : updater.MusicProperties.Title ?? string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private string ReadArtist()
        {
            if (_disposed || _controls == null) return string.Empty;

            try
            {
                var updater = _controls.DisplayUpdater;

                return updater.Type == MediaPlaybackType.Video
                    ? updater.VideoProperties.Subtitle ?? string.Empty
                    : updater.MusicProperties.Artist ?? string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }
    }
}
