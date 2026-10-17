using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using LibVLCSharp.Shared;
using LibVLCSharp.Shared.Structures;

namespace 播放器.Core
{
    /// <summary>
    /// 对 LibVLC 的托管封装：负责媒体的打开、播放控制、字幕与音轨切换。
    /// <para>
    /// 所有对外事件都通过 <see cref="ISynchronizeInvoke"/> 派发回 UI 线程，
    /// 因此界面代码无需再关心 VLC 回调线程。
    /// </para>
    /// </summary>
    public sealed partial class PlayerEngine : IDisposable
    {
        private readonly ISynchronizeInvoke _ui;

        /// <summary>
        /// 当前用的 libvlc 内核。
        /// <para>
        /// <b>不是 readonly</b>：画面旋转（<c>--video-filter=transform --transform-type=…</c>）
        /// 只能在内核这一级设，所以"换一种旋转"就是"换一个内核"
        /// （理由与实测见 <see cref="CoreArguments"/>）。
        /// </para>
        /// </summary>
        private LibVLC _libVlc;

        private MediaPlayer _mediaPlayer;

        /// <summary>
        /// 一个旋转值一套：**内核 + 挂在它上面的那个播放器**（惰性创建，最多 6 套）。
        /// <para>
        /// 为什么播放器也要按内核缓存（1.5.0 改的，M8）：以前每换一次内核就新建一个播放器、
        /// 随手把旧的释放掉。实测那样**每换一次泄漏约 170 个句柄 / 35 个 OS 线程**
        /// （正好是一个 libvlc 播放器的解码线程数），而且过多久都不还回来——
        /// 换到 240 次左右进程直接原生崩（`0xC0000005`，崩在 `old.Stop()` 里，就是 M8）。
        /// 现在"一个旋转值一套、切回去就复用"，换内核不再新建播放器，泄漏的源头就没了。
        /// </para>
        /// </summary>
        private sealed class CoreSlot
        {
            public CoreSlot(LibVLC core, MediaPlayer player)
            {
                Core = core;
                Player = player;
            }

            public LibVLC Core { get; }

            public MediaPlayer Player { get; }
        }

        /// <summary>按旋转值缓存的那几套（内核 + 播放器）：一个旋转值一套，惰性创建，最多 6 套。</summary>
        private readonly Dictionary<ScreenRotation, CoreSlot> _cores = new Dictionary<ScreenRotation, CoreSlot>();

        /// <summary>当前这个内核上带着哪种画面旋转。</summary>
        private ScreenRotation _coreRotation;

        /// <summary>正在换内核：这期间旧播放器发出来的状态事件要丢掉（那不是在放的东西）。</summary>
        private bool _switchingCore;

        // 换内核之后要重新铺上去的几样（libvlc 的播放器状态是每个播放器各一份）
        private int _desiredVolume = 100;
        private bool _desiredMuted;
        private float _desiredRate = 1.0f;
        private EqualizerState? _equalizerState;

        /// <summary>构造这个引擎的线程，也就是 UI 线程。</summary>
        private readonly int _uiThreadId = Environment.CurrentManagedThreadId;

        /// <summary>UI 线程还没准备好（窗体尚未创建句柄）时，先把待派发的事件攒在这里。</summary>
        private readonly Queue<Action> _pendingUiActions = new Queue<Action>();
        private readonly object _pendingGate = new object();

        private Media? _currentMedia;
        private string? _currentPath;
        private volatile PlayerState _state = PlayerState.Idle;
        private bool _endReached;
        private bool _disposed;

        /// <summary>逐帧的基准位置（毫秒）；−1 表示还没进入逐帧状态。</summary>
        private long _frameStepBaseMilliseconds = -1;

        /// <summary>相对基准位置已经走了几帧。</summary>
        private int _frameStepIndex;

        public PlayerEngine(ISynchronizeInvoke ui)
        {
            _ui = ui ?? throw new ArgumentNullException(nameof(ui));

            var slot = CreateSlot(ScreenRotation.None);
            _libVlc = slot.Core;
            _coreRotation = ScreenRotation.None;
            _mediaPlayer = slot.Player;
        }

        /// <summary>
        /// 底层 LibVLC 实例，供时长扫描等辅助模块复用。
        /// <para>⚠ 换画面旋转时它会被换掉（见 <see cref="CoreRebuilt"/>），不要长期持有。</para>
        /// </summary>
        public LibVLC LibVlc => _libVlc;

        /// <summary>底层 MediaPlayer，仅供 VideoView 绑定使用（换旋转时会换新的，见 <see cref="CoreRebuilt"/>）。</summary>
        public MediaPlayer Player => _mediaPlayer;

        /// <summary>当前引擎状态。</summary>
        public PlayerState State => _state;

        /// <summary>当前是否已打开媒体（无论是否在播放）。</summary>
        public bool HasMedia => _currentMedia != null;

        /// <summary>当前媒体的路径；未打开时为 <c>null</c>。</summary>
        public string? CurrentPath => _currentPath;

        // ---- 事件 -------------------------------------------------------------

        /// <summary>状态变化。</summary>
        public event EventHandler<PlayerState>? StateChanged;

        /// <summary>当前媒体自然播放结束。</summary>
        public event EventHandler? PlaybackEnded;

        /// <summary>播放出错，参数为面向用户的错误描述。</summary>
        public event EventHandler<string>? ErrorOccurred;

        /// <summary>音轨/字幕轨道列表发生变化。</summary>
        public event EventHandler? TracksChanged;

        /// <summary>
        /// <b>内核被换掉了</b>（换画面旋转时）：<see cref="Player"/> 与 <see cref="LibVlc"/> 都是新的了。
        /// <para>
        /// ⚠ 处理器里<b>必须</b>把 <c>VideoView.MediaPlayer</c> 改挂到新的 <see cref="Player"/> 上，
        /// 并把持有旧 <see cref="LibVlc"/> 的东西（时长扫描）重建——旧播放器在这个事件之后
        /// 立刻就被释放了。
        /// </para>
        /// </summary>
        public event EventHandler? CoreRebuilt;

        // ---- 属性 -------------------------------------------------------------

        /// <summary>媒体总时长（毫秒），未知时为 0。</summary>
        public long Length
        {
            get
            {
                var length = _mediaPlayer.Length;
                return length > 0 ? length : 0;
            }
        }

        /// <summary>当前播放位置（毫秒）。</summary>
        public long Time
        {
            get
            {
                var time = _mediaPlayer.Time;
                return time > 0 ? time : 0;
            }
        }

        /// <summary>播放进度 0~1。</summary>
        public float Position => _mediaPlayer.Position;

        /// <summary>是否正在播放。</summary>
        public bool IsPlaying => _mediaPlayer.IsPlaying;

        /// <summary>音量 0~100。</summary>
        public int Volume
        {
            get => Math.Clamp(_mediaPlayer.Volume, 0, 100);
            set
            {
                _desiredVolume = Math.Clamp(value, 0, 100);
                _mediaPlayer.Volume = _desiredVolume;
            }
        }

        /// <summary>是否静音。</summary>
        public bool Muted
        {
            get => _mediaPlayer.Mute;
            set
            {
                _desiredMuted = value;
                _mediaPlayer.Mute = value;
            }
        }

        /// <summary>播放速率（1.0 为正常速度）。</summary>
        public float Rate
        {
            get
            {
                var rate = _mediaPlayer.Rate;
                return rate <= 0 ? 1.0f : rate;
            }
            set
            {
                _desiredRate = Math.Clamp(value, 0.25f, 4.0f);
                _mediaPlayer.SetRate(_desiredRate);
            }
        }

        /// <summary>画面宽高比，例如 <c>"16:9"</c>；<c>null</c> 或空串表示默认。</summary>
        public string? AspectRatio
        {
            get
            {
                var value = _mediaPlayer.AspectRatio;
                return string.IsNullOrEmpty(value) ? null : value;
            }
            set => _mediaPlayer.AspectRatio = string.IsNullOrWhiteSpace(value) ? null : value;
        }

        /// <summary>
        /// 音量均衡（给媒体挂 libvlc 的 <c>normvol</c> 音频滤镜）。
        /// <para>
        /// ⚠ 这是<b>媒体级</b>选项：它写进媒体的选项串，<b>改它不会影响已经打开的媒体</b>，
        /// 要重新创建媒体（也就是重载当前这一首）才生效。界面那边切换时会重载并说明。
        /// </para>
        /// </summary>
        public bool NormalizeVolume { get; set; }

        /// <summary>
        /// 画面旋转 / 翻转。
        /// <para>
        /// ⚠ 它是<b>内核级</b>选项（libvlc 3 的 <c>transform</c> 只能这么设，实测见
        /// <see cref="CoreArguments"/>）：换它就要<b>换内核</b>，所以真正生效的时刻是
        /// <see cref="Open"/>（界面那边是"按文件记住"的，开播之前摆好）。
        /// </para>
        /// </summary>
        public ScreenRotation Rotation { get; set; }

        /// <summary>当前媒体里有没有视频轨（音频文件、纯音频串流都是 false）。</summary>
        public bool HasVideo => FindVideoTrack() != null;
    }
}
