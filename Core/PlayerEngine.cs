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
    public sealed class PlayerEngine : IDisposable
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

        /// <summary>按旋转值缓存的内核：一个旋转值一个，惰性创建，最多 6 个。</summary>
        private readonly Dictionary<ScreenRotation, LibVLC> _cores = new Dictionary<ScreenRotation, LibVLC>();

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

            _libVlc = CreateCore(ScreenRotation.None);
            _coreRotation = ScreenRotation.None;
            _mediaPlayer = CreatePlayer(_libVlc);
        }

        /// <summary>
        /// 一个内核要带的参数。
        /// <para>
        /// 🔴 <b>画面旋转只能在这里设</b>，这一点是实测出来的（B-2 的前置验证，四组探针）：
        /// <c>transform</c> 是<b>视频输出级</b>的滤镜，它读的是<b>内核</b>的配置——
        /// 媒体级的 <c>:video-filter=transform</c> / <c>:transform-type=…</c> 不论怎么写都不生效
        /// （挂着链子也好、只写类型也好，画面一动不动）；实例级的 <c>--video-filter</c> /
        /// <c>--transform-type</c> 立刻生效，而且<b>媒体级也覆盖不了内核里的类型</b>。
        /// 所以"换了旋转值"就必须"换内核"，见 <see cref="EnsureCore"/>。
        /// </para>
        /// </summary>
        internal static string[] CoreArguments(ScreenRotation rotation)
        {
            var args = new List<string>
            {
                "--no-video-title-show",   // 不在画面上叠加文件名
                "--no-snapshot-preview",   // 截图时不闪一下预览
                "--no-stats",
                "--quiet"
            };

            var type = ScreenRotations.TransformType(rotation);

            if (type != null)
            {
                args.Add("--video-filter=" + ScreenRotations.VideoFilter);
                args.Add("--transform-type=" + type);
            }

            return args.ToArray();
        }

        /// <summary>建一个内核并记进缓存（同一个旋转值只建一次）。</summary>
        private LibVLC CreateCore(ScreenRotation rotation)
        {
            var core = new LibVLC(CoreArguments(rotation));
            _cores[rotation] = core;
            return core;
        }

        /// <summary>在新内核上建一个播放器，作为交换前的准备（状态由 <see cref="EnsureCore"/> 铺回去）。</summary>
        private MediaPlayer CreatePlayer(LibVLC core)
        {
            var player = new MediaPlayer(core);

            player.Opening += (s, e) => { if (!_switchingCore) OnOpening(); };
            player.Playing += (s, e) => { if (!_switchingCore) OnPlaying(); };
            player.Paused += (s, e) => { if (!_switchingCore) SetState(PlayerState.Paused); };
            player.Stopped += (s, e) => { if (!_switchingCore) OnStopped(); };
            player.EndReached += (s, e) => { if (!_switchingCore) OnEndReached(); };
            player.EncounteredError += (s, e) => { if (!_switchingCore) OnEncounteredError(); };
            player.ESAdded += (s, e) => { if (!_switchingCore) RaiseTracksChanged(); };
            player.ESDeleted += (s, e) => { if (!_switchingCore) RaiseTracksChanged(); };

            return player;
        }

        private void OnOpening()
        {
            _endReached = false;
            SetState(PlayerState.Opening);
        }

        private void OnPlaying()
        {
            _endReached = false;
            SetState(PlayerState.Playing);
            RaiseTracksChanged();
        }

        /// <summary>
        /// 把内核换成带 <paramref name="rotation"/> 的那个（同一个就什么都不做）。
        /// <para>
        /// 顺序要紧：<b>先建新播放器 → 通知界面把窗口改挂过去 → 再放掉旧播放器</b>。
        /// 反过来的话窗口上会留着一个已经释放的播放器。
        /// </para>
        /// <para>
        /// 旧<b>内核</b>不释放，留在 <see cref="_cores"/> 里（同一个旋转值下次还要用：
        /// 关掉程序时统一释放）。
        /// </para>
        /// </summary>
        private void EnsureCore(ScreenRotation rotation)
        {
            if (rotation == _coreRotation) return;

            if (!_cores.TryGetValue(rotation, out var core)) core = CreateCore(rotation);

            var old = _mediaPlayer;

            _switchingCore = true;

            try
            {
                DisposeCurrentMedia();

                try { old.Stop(); }
                catch (Exception ex) { AppLog.Swallowed("换内核之前停旧播放器失败。", ex); }

                _libVlc = core;
                _coreRotation = rotation;
                _mediaPlayer = CreatePlayer(core);

                ApplyPlayerState(_mediaPlayer);
            }
            finally
            {
                _switchingCore = false;
            }

            // 界面必须在这里把 VideoView 改挂到新的 Player 上（旧的那个紧接着就被释放）
            CoreRebuilt?.Invoke(this, EventArgs.Empty);

            try { old.Dispose(); }
            catch (Exception ex) { AppLog.Swallowed("释放旧播放器失败。", ex); }
        }

        /// <summary>把"用户那一套"铺到一个（可能是全新的）播放器上。</summary>
        private void ApplyPlayerState(MediaPlayer player)
        {
            player.Volume = Math.Clamp(_desiredVolume, 0, 100);
            player.Mute = _desiredMuted;
            player.SetRate(Math.Clamp(_desiredRate, 0.25f, 4.0f));

            if (_equalizerState != null) ApplyEqualizerTo(player, _equalizerState);
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

        // ---- 打开与播放 -------------------------------------------------------

        /// <summary>
        /// 打开一个本地文件或网络流并立即开始播放。失败时抛出异常，由调用方提示用户。
        /// </summary>
        public void Open(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("媒体路径不能为空。", nameof(path));

            if (!MediaFormats.IsOpenable(path))
                throw new FileNotFoundException("找不到媒体文件。", path);

            // 画面旋转是内核级选项：要换的那种旋转和当前内核不一致时，在这里先换内核
            // （界面会在 CoreRebuilt 里把窗口改挂到新播放器上）。
            EnsureCore(Rotation);

            // 先释放上一个媒体，避免 libvlc 侧引用堆积。
            DisposeCurrentMedia();

            var media = CreateMedia(path);
            _currentMedia = media;
            _currentPath = path;
            _endReached = false;
            ResetFrameStepping();

            if (!_mediaPlayer.Play(media))
            {
                var detail = _libVlc.LastLibVLCError;
                throw new InvalidOperationException(
                    string.IsNullOrEmpty(detail) ? "无法开始播放该媒体。" : detail);
            }
        }

        /// <summary>继续播放；若已播完或已停止则从头重新播放。</summary>
        public void Play()
        {
            if (_currentMedia == null) return;

            if (_state == PlayerState.Ended || _state == PlayerState.Stopped)
            {
                _endReached = false;
                ResetFrameStepping();
                _mediaPlayer.Play(_currentMedia);
                return;
            }

            if (!_mediaPlayer.IsPlaying)
            {
                ResetFrameStepping();
                _mediaPlayer.Play();
            }
        }

        /// <summary>从头重新播放当前媒体（用于单曲循环）。</summary>
        public void Replay()
        {
            if (_currentMedia == null) return;
            _endReached = false;
            _mediaPlayer.Play(_currentMedia);
        }

        /// <summary>暂停。</summary>
        public void Pause()
        {
            if (_mediaPlayer.CanPause && _mediaPlayer.IsPlaying)
                _mediaPlayer.SetPause(true);
        }

        /// <summary>在播放/暂停之间切换。</summary>
        public void TogglePlayPause()
        {
            if (_currentMedia == null) return;

            if (_mediaPlayer.IsPlaying)
                Pause();
            else
                Play();
        }

        /// <summary>停止播放并释放当前媒体。</summary>
        public void Stop()
        {
            _endReached = false;
            if (_currentMedia == null) return;

            _mediaPlayer.Stop();
            DisposeCurrentMedia();
            _currentPath = null;
            SetState(PlayerState.Stopped);
        }

        /// <summary>跳转到指定毫秒位置。</summary>
        public void SeekTo(long milliseconds)
        {
            if (_currentMedia == null) return;

            var length = Length;
            var target = Math.Clamp(milliseconds, 0, length > 0 ? length : long.MaxValue);

            // 跳转之后原来的逐帧序号就没意义了
            ResetFrameStepping();

            _mediaPlayer.SeekTo(TimeSpan.FromMilliseconds(target));
        }

        /// <summary>按相对毫秒数跳转。</summary>
        public void SeekBy(long deltaMilliseconds) => SeekTo(Time + deltaMilliseconds);

        /// <summary>按百分比跳转（0~1）。</summary>
        public void SeekToFraction(double fraction)
        {
            var length = Length;
            if (length <= 0) return;
            SeekTo((long)(length * Math.Clamp(fraction, 0.0, 1.0)));
        }

        // ---- 字幕与音轨 -------------------------------------------------------

        /// <summary>加载外挂字幕文件并立即启用。</summary>
        public bool AddSubtitleFile(string path)
        {
            if (!File.Exists(path)) return false;
            if (_currentMedia == null) return false;

            var uri = new Uri(path).AbsoluteUri;
            return _mediaPlayer.AddSlave(MediaSlaveType.Subtitle, uri, true);
        }

        /// <summary>关闭字幕显示（-1 表示禁用）。</summary>
        public void DisableSubtitle() => _mediaPlayer.SetSpu(-1);

        /// <summary>获取可用字幕轨道。</summary>
        public IReadOnlyList<TrackDescription> GetSubtitleTracks() =>
            _mediaPlayer.SpuDescription ?? Array.Empty<TrackDescription>();

        /// <summary>获取可用音轨。</summary>
        public IReadOnlyList<TrackDescription> GetAudioTracks() =>
            _mediaPlayer.AudioTrackDescription ?? Array.Empty<TrackDescription>();

        /// <summary>切换字幕轨道。</summary>
        public void SetSubtitleTrack(int id) => _mediaPlayer.SetSpu(id);

        /// <summary>切换音轨。</summary>
        public void SetAudioTrack(int id) => _mediaPlayer.SetAudioTrack(id);

        /// <summary>当前字幕轨道 id。</summary>
        public int CurrentSubtitleTrack => _mediaPlayer.Spu;

        /// <summary>当前音轨 id。</summary>
        public int CurrentAudioTrack => _mediaPlayer.AudioTrack;

        // ---- 均衡器 -----------------------------------------------------------

        /// <summary>
        /// 应用均衡器设置；未启用（或传 <c>null</c>）时关闭均衡器。
        /// <para>
        /// libvlc 会把均衡器保留在播放器上，之后打开的媒体会自动套用，
        /// 所以只需要在设置变化时调用一次，不必每首歌都重设。
        /// </para>
        /// </summary>
        /// <returns>是否成功。</returns>
        public bool ApplyEqualizer(EqualizerState? state)
        {
            _equalizerState = state;
            return ApplyEqualizerTo(_mediaPlayer, state);
        }

        /// <summary>
        /// 把均衡器设置铺到指定的播放器上（换内核之后要重新铺一遍——
        /// 均衡器是挂在播放器上的，不是内核上的）。
        /// </summary>
        private static bool ApplyEqualizerTo(MediaPlayer player, EqualizerState? state)
        {
            try
            {
                if (state == null || !state.Enabled)
                {
                    player.UnsetEqualizer();
                    return true;
                }

                var amps = state.NormalizedAmps();

                // 无参构造 = 全零的均衡器，再逐段写入用户保存的曲线
                using var equalizer = new Equalizer();
                var bands = Math.Min((int)equalizer.BandCount, amps.Length);

                equalizer.SetPreamp(state.Preamp);
                for (var band = 0; band < bands; band++) equalizer.SetAmp(amps[band], (uint)band);

                player.SetEqualizer(equalizer);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ---- 音频输出设备 -----------------------------------------------------

        /// <summary>
        /// 列出可选的音频输出设备，第一项固定是"系统默认设备"。
        /// <para>
        /// 正在播放时优先用播放器自己的设备表（最准）；没在播放就退回向
        /// libvlc 询问输出模块的设备表——否则暂停状态下列表会是空的。
        /// </para>
        /// </summary>
        public IReadOnlyList<AudioDeviceOption> GetAudioOutputDevices()
        {
            var options = new List<AudioDeviceOption> { new(null, "系统默认设备") };
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var current = _mediaPlayer.AudioOutputDeviceEnum;

                if (current is { Length: > 0 })
                {
                    foreach (var device in current)
                    {
                        if (string.IsNullOrEmpty(device.DeviceIdentifier)) continue;
                        if (!seen.Add(device.DeviceIdentifier)) continue;

                        options.Add(new AudioDeviceOption(device.DeviceIdentifier, device.Description));
                    }

                    return options;
                }

                var module = ChooseAudioOutputModule();
                if (module == null) return options;

                foreach (var device in _libVlc.AudioOutputDevices(module))
                {
                    if (string.IsNullOrEmpty(device.DeviceIdentifier)) continue;
                    if (!seen.Add(device.DeviceIdentifier)) continue;

                    options.Add(new AudioDeviceOption(device.DeviceIdentifier, device.Description, module));
                }
            }
            catch (Exception ex)
            {
// 枚举失败就只给"系统默认设备"
                AppLog.Swallowed("枚举失败就只给'系统默认设备'", ex);
            }

            return options;
        }

        /// <summary>切换音频输出设备；下一首（或重新播放）生效。</summary>
        public bool SetAudioOutputDevice(AudioDeviceOption? option)
        {
            if (option == null) return false;

            // libvlc 推荐模块传 NULL；不同 LibVLCSharp 版本对 NULL 的容忍度不一样，
            // 所以依次尝试几种组合，哪种不抛异常就用哪种。
            var attempts = new List<(string? Module, string? Device)>
            {
                (option.Module, option.DeviceId)
            };

            if (option.Module != null) attempts.Add((null, option.DeviceId));
            attempts.Add(("mmdevice", option.DeviceId));

            foreach (var (module, device) in attempts)
            {
                try
                {
                    _mediaPlayer.SetOutputDevice(module!, device!);
                    return true;
                }
                catch (Exception ex)
                {
// 换下一种组合
                    AppLog.Swallowed("换下一种组合", ex);
                }
            }

            return false;
        }

        private string? ChooseAudioOutputModule()
        {
            var outputs = _libVlc.AudioOutputs;
            if (outputs == null || outputs.Length == 0) return null;

            // Windows 上默认是 mmdevice；找不到就用第一个可用的
            foreach (var output in outputs)
                if (string.Equals(output.Name, "mmdevice", StringComparison.OrdinalIgnoreCase)) return output.Name;

            return outputs[0].Name;
        }

        // ---- 章节 -------------------------------------------------------------

        /// <summary>当前媒体的章节数；没有媒体或不支持章节时为 0。</summary>
        public int ChapterCount
        {
            get
            {
                try
                {
                    // libvlc 在没有媒体时返回 -1 表示出错；对调用方来说"没有章节"就是 0。
                    return Math.Max(0, _mediaPlayer.ChapterCount);
                }
                catch (Exception)
                {
                    return 0;
                }
            }
        }

        /// <summary>当前章节序号；无章节时为 <c>-1</c>。</summary>
        public int CurrentChapter
        {
            get
            {
                try { return _mediaPlayer.Chapter; }
                catch (Exception) { return -1; }
            }
        }

        /// <summary>当前标题下的全部章节。</summary>
        public IReadOnlyList<ChapterDescription> GetChapters()
        {
            try { return _mediaPlayer.FullChapterDescriptions(-1) ?? Array.Empty<ChapterDescription>(); }
            catch (Exception) { return Array.Empty<ChapterDescription>(); }
        }

        /// <summary>跳到指定章节。</summary>
        public void SetChapter(int index)
        {
            try { _mediaPlayer.Chapter = index; }
            catch (Exception) { }
        }

        /// <summary>跳到下一章。</summary>
        public void NextChapter()
        {
            try { _mediaPlayer.NextChapter(); }
            catch (Exception) { }
        }

        /// <summary>跳到上一章。</summary>
        public void PreviousChapter()
        {
            try { _mediaPlayer.PreviousChapter(); }
            catch (Exception) { }
        }

        // ---- 媒体信息 ---------------------------------------------------------

        /// <summary>当前媒体的轨道清单；尚未开始播放时为空。</summary>
        public IReadOnlyList<MediaTrack> GetTracks()
        {
            var media = _currentMedia;
            if (media == null) return Array.Empty<MediaTrack>();

            try { return media.Tracks ?? Array.Empty<MediaTrack>(); }
            catch (Exception) { return Array.Empty<MediaTrack>(); }
        }

        /// <summary>轨道的可读编解码器名称，例如 <c>H264 - MPEG-4 AVC (part 10)</c>。</summary>
        public string? DescribeCodec(MediaTrack track)
        {
            var media = _currentMedia;
            if (media == null) return null;

            try { return media.CodecDescription(track.TrackType, (uint)track.Id); }
            catch (Exception) { return null; }
        }

        // ---- 去隔行 -----------------------------------------------------------

        /// <summary>
        /// 设置去隔行模式；<c>null</c> 或空串表示关闭。
        /// <para>可用值：<c>auto</c>、<c>blend</c>、<c>bob</c>、<c>linear</c>、<c>x</c>、
        /// <c>yadif</c>、<c>yadif2x</c>、<c>phosphor</c>、<c>ivtc</c>。</para>
        /// </summary>
        public bool SetDeinterlace(string? mode)
        {
            try
            {
                _mediaPlayer.SetDeinterlace(string.IsNullOrWhiteSpace(mode) ? null : mode);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ---- 音画与字幕同步 ---------------------------------------------------

        /// <summary>音频延迟（毫秒），正值表示声音推后。</summary>
        public long AudioDelayMilliseconds
        {
            get
            {
                try { return _mediaPlayer.AudioDelay / 1000; }
                catch (Exception) { return 0; }
            }
        }

        /// <summary>字幕延迟（毫秒），正值表示字幕推后。</summary>
        public long SubtitleDelayMilliseconds
        {
            get
            {
                try { return _mediaPlayer.SpuDelay / 1000; }
                catch (Exception) { return 0; }
            }
        }

        /// <summary>设置音频延迟（毫秒）。libvlc 的接口收的是微秒。</summary>
        public bool SetAudioDelayMilliseconds(long milliseconds)
        {
            try { return _mediaPlayer.SetAudioDelay(Math.Clamp(milliseconds, -60000, 60000) * 1000); }
            catch (Exception) { return false; }
        }

        /// <summary>设置字幕延迟（毫秒）。libvlc 的接口收的是微秒。</summary>
        public bool SetSubtitleDelayMilliseconds(long milliseconds)
        {
            try { return _mediaPlayer.SetSpuDelay(Math.Clamp(milliseconds, -60000, 60000) * 1000); }
            catch (Exception) { return false; }
        }

        // ---- 逐帧 -------------------------------------------------------------

        /// <summary>
        /// 当前视频的帧间隔（毫秒）；未知时为 0。
        /// <para>后退一帧要靠它来估算——libvlc 只提供"下一帧"，没有"上一帧"。</para>
        /// </summary>
        public double FrameDurationMilliseconds
        {
            get
            {
                var track = FindVideoTrack();
                if (track == null) return 0;

                var video = track.Value.Data.Video;
                if (video.FrameRateNum == 0 || video.FrameRateDen == 0) return 0;

                var fps = video.FrameRateNum / (double)video.FrameRateDen;
                return fps > 0.01 ? 1000.0 / fps : 0;
            }
        }

        /// <summary>当前视频的帧率；未知时为 0。</summary>
        public double FrameRate
        {
            get
            {
                var duration = FrameDurationMilliseconds;
                return duration > 0 ? 1000.0 / duration : 0;
            }
        }

        /// <summary>
        /// 走一帧。<paramref name="forward"/> 为 <c>false</c> 时退一帧。
        /// <para>三个 libvlc 限制决定了这里的写法：</para>
        /// <list type="bullet">
        /// <item><c>NextFrame()</c> 在当前环境下<b>不生效</b>——调用成功，但快照与屏幕像素都毫无变化
        /// （用三个视频、快照对比与真实按键两种方式验证过）。</item>
        /// <item>libvlc 本来就没有"上一帧"（VLC 本体也只有下一帧）。</item>
        /// <item>暂停时 <c>MediaPlayer.Time</c> 不推进——它是播放时钟，不是帧号。</item>
        /// </list>
        /// <para>
        /// 所以两个方向统一用"按帧长 seek"来实现：暂停状态下 libvlc 会解码到目标时间并显示那一帧，
        /// 落点稳定。帧序号由本类自己维护，跳转或重新播放时清零。
        /// </para>
        /// </summary>
        public bool StepFrame(bool forward)
        {
            if (_currentMedia == null) return false;

            try
            {
                // 逐帧只在暂停状态下有意义。调用方要等暂停真正生效再调，
                // 否则 NextFrame 会被静默丢掉。
                if (_mediaPlayer.IsPlaying && _mediaPlayer.CanPause)
                    _mediaPlayer.SetPause(true);

                var frameDuration = FrameDurationMilliseconds;
                if (frameDuration <= 0.5) frameDuration = 40;      // 拿不到帧率就按 25fps 估

                // 暂停时 MediaPlayer.Time 不推进（它是播放时钟），所以自己记住相对暂停点走了几帧。
                if (_frameStepBaseMilliseconds < 0)
                {
                    _frameStepBaseMilliseconds = Time;
                    _frameStepIndex = 0;
                }

                var next = _frameStepIndex + (forward ? 1 : -1);
                if (next < 0) return false;                        // 已经是这一段的第一帧

                // 前进方向也要设上限。以前只挡了后退，按住"下一帧"越过片尾之后
                // _frameStepIndex 会一直累加，再想用"上一帧"回到片内就要按很多次。
                var length = Length;
                var candidate = _frameStepBaseMilliseconds + (long)Math.Round(next * frameDuration);
                if (length > 0 && candidate > length) return false;

                // ⚠ 这里刻意<b>不用</b> libvlc 的 NextFrame()：实测它在当前环境下完全不生效——
                // 调用返回成功，但无论快照还是屏幕像素都一模一样（用三个视频、两种方式验证过）。
                // 而"定位到某个精确时间"是可靠的：暂停状态下 libvlc 会解码到目标时间并显示那一帧，
                // 所以两个方向都用 seek 来实现，既统一又真的能走帧。
                _frameStepIndex = next;

                var target = _frameStepBaseMilliseconds + (long)Math.Round(_frameStepIndex * frameDuration);

                // 直接用底层 SeekTo：走公开的 SeekTo 会把帧序号重置掉
                _mediaPlayer.SeekTo(TimeSpan.FromMilliseconds(Math.Max(0, target)));
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>清掉逐帧状态（播放、跳转、换片之后都不该继续沿用）。</summary>
        private void ResetFrameStepping()
        {
            _frameStepBaseMilliseconds = -1;
            _frameStepIndex = 0;
        }

        private MediaTrack? FindVideoTrack()
        {
            try
            {
                foreach (var track in GetTracks())
                    if (track.TrackType == TrackType.Video) return track;
            }
            catch (Exception ex)
            {
// 读不到就当没有视频轨
                AppLog.Swallowed("读不到就当没有视频轨", ex);
            }

            return null;
        }

        // ---- 截图 -------------------------------------------------------------

        /// <summary>把当前画面保存为图片，返回是否成功。暂停状态下也可以截。</summary>
        public bool TakeSnapshot(string filePath)
        {
            // 不要求正在播放：暂停时截图是常见需求（VLC 也支持），只要媒体已经打开。
            if (_currentMedia == null) return false;

            try
            {
                var folder = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
                return _mediaPlayer.TakeSnapshot(0, filePath, 0, 0);
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ---- 内部实现 ---------------------------------------------------------

        private Media CreateMedia(string path)
        {
            // 串流走 Uri 构造，本地文件走路径构造；选项串由 MediaOptions 拼（纯函数，便于断言）。
            Uri? uri = null;
            var isStream = MediaFormats.IsStreamUri(path) && Uri.TryCreate(path, UriKind.Absolute, out uri);

            var media = isStream && uri != null
                ? new Media(_libVlc, uri)
                : new Media(_libVlc, path, FromType.FromPath);

            foreach (var option in MediaOptions.For(isStream, NormalizeVolume))
                media.AddOption(option);

            return media;
        }

        private void DisposeCurrentMedia()
        {
            var media = _currentMedia;
            _currentMedia = null;
            if (media == null) return;

            try
            {
                media.Dispose();
            }
            catch (Exception ex)
            {
// 释放失败不应影响后续播放。
                AppLog.Swallowed("释放失败不应影响后续播放。", ex);
            }
        }

        private void OnStopped()
        {
            // 自然播放结束会同时触发 EndReached 与 Stopped，此处避免把状态改回 Stopped。
            if (_endReached) return;
            SetState(PlayerState.Stopped);
        }

        private void OnEndReached()
        {
            _endReached = true;
            SetState(PlayerState.Ended);
            Post(() => PlaybackEnded?.Invoke(this, EventArgs.Empty));
        }

        private void OnEncounteredError()
        {
            var detail = _libVlc.LastLibVLCError;
            var message = string.IsNullOrEmpty(detail) ? "无法播放该媒体，可能是格式不受支持或文件已损坏。" : detail;

            SetState(PlayerState.Error);
            Post(() => ErrorOccurred?.Invoke(this, message));
        }

        private void RaiseTracksChanged() => Post(() => TracksChanged?.Invoke(this, EventArgs.Empty));

        internal void SetState(PlayerState state)
        {
            if (_state == state) return;
            _state = state;
            Post(() => StateChanged?.Invoke(this, state));
        }

        /// <summary>
        /// 把回调切换到 UI 线程执行。
        /// <para>
        /// ⚠ 判断"是否已经在 UI 线程上"用的是构造时记下的线程 id，<b>刻意不用
        /// <see cref="ISynchronizeInvoke.InvokeRequired"/></b>：控件在<b>还没创建句柄</b>时
        /// <c>InvokeRequired</c> 会返回 <c>false</c>，于是 libvlc 回调线程上的事件会被
        /// <b>就地执行</b>——那就是在跨线程操作 ToolStrip / ListView 等控件。
        /// </para>
        /// <para>
        /// 这不是理论问题：主窗口的构造函数里就会打开命令行传入的媒体，
        /// 而那一刻句柄可能还没建好，此时 libvlc 的 <c>Opening</c> / <c>Playing</c> 回调
        /// 会直接在原生线程上跑进 <c>UpdateTransportState</c> 之类的地方，
        /// 表现为"偶发"的跨线程异常（冒烟测试实测 30 次里撞到 1 次，
        /// 栈顶是 <c>ToolStripStatusLabel.OnTextChanged</c>）。
        /// </para>
        /// <para>UI 线程还没准备好时先排队，主窗口建好句柄后会调
        /// <see cref="FlushPendingUiActions"/> 补发，事件不会丢。</para>
        /// </summary>
        private void Post(Action action)
        {
            if (_disposed) return;

            if (Environment.CurrentManagedThreadId == _uiThreadId)
            {
                action();
                return;
            }

            try
            {
                _ui.BeginInvoke(action, null);
            }
            catch (ObjectDisposedException)
            {
                // 窗体已经销毁，丢掉即可。
            }
            catch (InvalidOperationException)
            {
                // 句柄还没建好（或正在销毁）：先攒起来，等 UI 线程来取。
                lock (_pendingGate)
                {
                    if (_disposed) return;
                    _pendingUiActions.Enqueue(action);
                }
            }
        }

        /// <summary>
        /// 补发构造期间攒下的事件（窗体建好句柄之后调用，必须在 UI 线程上）。
        /// </summary>
        public void FlushPendingUiActions()
        {
            Action[] pending;

            lock (_pendingGate)
            {
                if (_pendingUiActions.Count == 0) return;

                pending = _pendingUiActions.ToArray();
                _pendingUiActions.Clear();
            }

            // 用普通 for 而不是 foreach：这些动作有可能再次触发 Post，
            // 虽然不会回到这个数组，但显式索引更好读。
            for (var i = 0; i < pending.Length; i++)
            {
                if (_disposed) return;
                pending[i]();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                _mediaPlayer.Stop();
            }
            catch (Exception) { }

            DisposeCurrentMedia();

            try
            {
                _mediaPlayer.Dispose();
            }
            catch (Exception) { }

            // 缓存着的那几个内核（每个旋转值一个）也一起放掉
            foreach (var core in _cores.Values)
            {
                try
                {
                    core.Dispose();
                }
                catch (Exception) { }
            }

            _cores.Clear();
        }
    }
}
