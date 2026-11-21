using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using LibVLCSharp.Shared;
using 播放器.Core;

namespace 播放器.Ui
{
    /// <summary>
    /// 缩略图的「视频首帧」来源。
    /// <para>
    /// 生产实现是 <see cref="VideoFrameGrabber"/>（libvlc 的 vmem 回调）；测试可以换成假的，
    /// 这样"封面 / 帧 / 类型图标"的优先级与预算能在不碰解码器的情况下逐像素断言。
    /// </para>
    /// </summary>
    internal interface IVideoFrameSource
    {
        /// <summary>取一帧画面；取不到返回 <c>null</c>（调用方按"没有缩略图"处理）。</summary>
        Bitmap? Grab(string path, int edge);
    }

    /// <summary>
    /// 视频首帧：<b>一个复用的 <see cref="MediaPlayer"/></b> + <c>vmem</c> 回调，不建窗口、不出声。
    /// <para>
    /// 为什么不用 <c>VideoView</c> + <c>TakeSnapshot</c>：那要真有一个窗口与消息循环，界面线程会被拖住；
    /// 而这里只想要 56×56 的一张图。代价是解码回来的画面要自己从内存里拷（CPU 转换、不能硬解），
    /// 对一张缩略图够用。
    /// </para>
    /// <para>
    /// 为什么复用同一个播放器、而<b>不</b> <c>Stop()</c>：1.5.0 的 M8 验过 <c>Stop()</c> 每掉一整套
    /// libvlc 原生线程（一次 +35 线程 / +150 句柄）。这里换片源走"设新 Media 再 Play"，停就走
    /// <c>SetPause(true)</c>。
    /// </para>
    /// <para>
    /// 三个"只能这么做"的细节（都是实测逼出来的）：
    /// ① <c>SetVideoCallbacks</c> 不给我们传 opaque，所以用静态的"当前实例"（同一时刻只允许一个抓帧器）；
    /// ② <c>pitches</c> / <c>lines</c> <b>必须写</b>：LibVLCSharp 用 <c>ref uint</c> 只暴露这两张表的首项
    /// （RV32 单平面够用），不写的话 VLC 不会把画面写进我们的缓冲——实测整帧几乎全黑；
    /// ③ 缓冲区<b>只增不减</b>：libvlc 可能拿着旧指针在写，播放中按尺寸重分配并 Free 旧缓冲，
    /// 实测会让画面全黑，甚至以 <c>STATUS_HEAP_CORRUPTION</c>（<c>0xC0000374</c>）退出。
    /// 所以每个"宽 × 高 × 行距"各留一块，Dispose 时统一释放。
    /// </para>
    /// <para>
    /// 只收"看起来是画面"的帧：全黑帧（VLC 起播时的空白帧、片头黑场）不算，继续等到有亮度的帧；
    /// 一段时间内只等到黑的就返回 <c>null</c>，让调用方退回类型图标——一张全黑的缩略图不如没有。
    /// </para>
    /// </summary>
    internal sealed class VideoFrameGrabber : IVideoFrameSource, IDisposable
    {
        /// <summary>单文件的总预算：第一次从 2 秒处取，失败再退回第 0 帧。</summary>
        internal const int TimeoutMs = 6000;

        /// <summary>第一次尝试的起播位置（秒）。短片取不到就退回 0。</summary>
        internal const int FirstTryStartSeconds = 2;

        /// <summary>第一次尝试占用的预算（剩下的留给"从第 0 帧再来一次"）。</summary>
        internal const int FirstTryTimeoutMs = 4000;

        /// <summary>等一帧"有内容"的画面的上限：到了就用这一帧，不再等更亮的。</summary>
        internal const int GoodFrameWaitMs = 2500;

        /// <summary>亮度 / 方差阈值：全黑帧（VLC 起播时的空白帧）不算画面。</summary>
        internal const int MinimumBrightness = 24;
        internal const int MinimumVariance = 8;

        private static readonly object ActiveGate = new object();
        private static VideoFrameGrabber? _active;

        // 委托要留引用：libvlc 回调里用的就是这几个。
        private static readonly MediaPlayer.LibVLCVideoFormatCb FormatCallback = OnFormat;
        private static readonly MediaPlayer.LibVLCVideoCleanupCb CleanupCallback = OnCleanup;
        private static readonly MediaPlayer.LibVLCVideoLockCb LockCallback = OnLock;
        private static readonly MediaPlayer.LibVLCVideoUnlockCb UnlockCallback = OnUnlock;
        private static readonly MediaPlayer.LibVLCVideoDisplayCb DisplayCallback = OnDisplay;

        private readonly object _frameGate = new object();
        private readonly ManualResetEventSlim _frameReady = new ManualResetEventSlim(false);
        private readonly List<(int Width, int Height, int Pitch)> _formatCalls = new List<(int, int, int)>();

        private LibVLC? _libVlc;
        private MediaPlayer? _player;
        private Media? _media;

        /// <summary>交给 vmem 的像素平面（非托管、32 字节对齐）。</summary>
        private IntPtr _plane = IntPtr.Zero;

        /// <summary>所有分配过的非托管缓冲区：只增不减，直到 Dispose（见 PrepareBuffer 的注释）。</summary>
        private readonly List<IntPtr> _buffers = new List<IntPtr>();

        /// <summary>每种尺寸一份缓冲：libvlc 会在一次播放里换几次格式，不能共用同一块内存。</summary>
        private readonly Dictionary<(int Width, int Height, int Pitch), IntPtr> _planeByGeometry =
            new Dictionary<(int, int, int), IntPtr>();

        private int _width;
        private int _height;
        private int _pitch;

        /// <summary>Lock 那一刻的缓冲与尺寸：Display 用它，不能用"现在"的字段（格式可能已经换过）。</summary>
        private readonly Dictionary<IntPtr, (int Width, int Height, int Pitch)> _planes =
            new Dictionary<IntPtr, (int Width, int Height, int Pitch)>();

        private byte[]? _frame;
        private bool _captured;
        private int _capturedWidth;
        private int _capturedHeight;
        private int _capturedPitch;

        /// <summary>每次换片源 +1：迟到的上一部片子的画面不能当成这一部的结果。</summary>
        private int _serial;

        private int _capturedSerial;

        private int _failures;
        private bool _disabled;
        private bool _disposed;

        internal VideoFrameGrabber()
        {
            lock (ActiveGate)
            {
                if (_active != null && !ReferenceEquals(_active, this))
                    throw new InvalidOperationException("同一时刻只能有一个抓帧器");

                _active = this;
            }
        }

        /// <summary>连续失败几次就该"本会话别再试了"（交给视图判断）。</summary>
        internal int ConsecutiveFailures
        {
            get { lock (_frameGate) return _failures; }
        }

        /// <summary>连续失败 3 次之后自己停用：不再为每一个文件白等 6 秒。</summary>
        internal bool Disabled => _disabled;

        /// <summary>给探针看的：最近一次拿到帧的尺寸。</summary>
        internal (int Width, int Height) LastFrameSize => (_width, _height);

        /// <summary>给探针看的：把最近一帧原样拷出来（像素布局 / 通道顺序排查用）。</summary>
        internal (byte[] Pixels, int Width, int Height, int Pitch)? TryTakeLastFrame()
        {
            lock (_frameGate)
            {
                if (!_captured || _frame == null || _capturedPitch <= 0 || _capturedHeight <= 0) return null;

                var copy = new byte[_capturedPitch * _capturedHeight];
                Buffer.BlockCopy(_frame, 0, copy, 0, copy.Length);

                return (copy, _capturedWidth, _capturedHeight, _capturedPitch);
            }
        }

        /// <summary>给探针看的：格式回调每次报上来的尺寸与行距（只留最近 24 条）。</summary>
        internal IReadOnlyList<(int Width, int Height, int Pitch)> FormatCalls
        {
            get
            {
                lock (_frameGate) return _formatCalls.ToArray();
            }
        }

        public Bitmap? Grab(string path, int edge)
        {
            if (_disposed || _disabled || string.IsNullOrWhiteSpace(path)) return null;

            if (!EnsurePlayer()) return null;

            var bitmap = TryGrab(path, FirstTryStartSeconds, FirstTryTimeoutMs, edge);

            // 2 秒处取不到（短片 / 起播失败 / 片头就结束）：退回第 0 帧再来一次。
            bitmap ??= TryGrab(path, 0, TimeoutMs - FirstTryTimeoutMs, edge);

            lock (_frameGate)
            {
                if (bitmap == null)
                {
                    _failures++;

                    if (_failures >= 3 && !_disabled)
                    {
                        _disabled = true;
                        AppLog.Warn("抓帧：连续 3 个视频取不到首帧，本会话停用视频画面缩略图");
                    }
                }
                else
                {
                    _failures = 0;
                }
            }

            return bitmap;
        }

        private Bitmap? TryGrab(string path, int startSeconds, int timeoutMs, int edge)
        {
            lock (_frameGate)
            {
                _captured = false;
                _frameReady.Reset();
            }

            if (!SetMedia(path, startSeconds)) return null;

            int serial;

            lock (_frameGate)
            {
                serial = _serial;
            }

            var played = false;

            try
            {
                played = _player!.Play();
            }
            catch (Exception ex)
            {
                AppLog.Swallowed("抓帧：起播失败 " + path, ex);
            }

            if (!played)
            {
                Pause();
                return null;
            }

            var wait = Math.Min(timeoutMs, GoodFrameWaitMs);

            try
            {
                _frameReady.Wait(wait);
            }
            catch (ObjectDisposedException)
            {
                // 视图正在销毁：直接放弃。
            }

            Pause();

            byte[] frame;
            int width, height, pitch;

            lock (_frameGate)
            {
                if (!_captured || _frame == null || _capturedSerial != serial) return null;

                width = _capturedWidth;
                height = _capturedHeight;
                pitch = _capturedPitch;

                frame = new byte[pitch * height];
                Buffer.BlockCopy(_frame, 0, frame, 0, frame.Length);
            }

            var bitmap = CoverThumbnails.ScaleBgra(frame, width, height, pitch, edge);

            if (bitmap == null)
                AppLog.Swallowed("抓帧：画面解不出来 " + path, new InvalidOperationException("ScaleBgra 返回 null"));

            return bitmap;
        }

        private bool SetMedia(string path, int startSeconds)
        {
            try
            {
                var media = new Media(_libVlc!, path, FromType.FromPath);

                media.AddOption(":no-audio");

                if (startSeconds > 0)
                    media.AddOption(":start-time=" + startSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));

                _player!.Media = media;

                lock (_frameGate)
                {
                    _serial++;
                }

                var previous = _media;
                _media = media;

                try
                {
                    previous?.Dispose();
                }
                catch (Exception ex)
                {
                    AppLog.Swallowed("抓帧：释放上一条 Media 失败", ex);
                }

                return true;
            }
            catch (Exception ex)
            {
                AppLog.Swallowed("抓帧：打开失败 " + path, ex);
                return false;
            }
        }

        private bool EnsurePlayer()
        {
            if (_player != null) return true;

            lock (_frameGate)
            {
                if (_disposed) return false;

                try
                {
                    _libVlc = new LibVLC("--no-audio", "--no-spu", "--quiet", "--no-video-title-show", "--no-osd");
                    _player = new MediaPlayer(_libVlc);

                    // 顺序不能反：先定格式回调，再定内存回调（libvlc 的文档要求两者一起用）。
                    _player.SetVideoFormatCallbacks(FormatCallback, CleanupCallback);
                    _player.SetVideoCallbacks(LockCallback, UnlockCallback, DisplayCallback);

                    return true;
                }
                catch (Exception ex)
                {
                    AppLog.Swallowed("抓帧：初始化 libvlc 失败", ex);
                    _player?.Dispose();
                    _player = null;
                    _libVlc?.Dispose();
                    _libVlc = null;
                    return false;
                }
            }
        }

        private void Pause()
        {
            try
            {
                _player?.SetPause(true);
            }
            catch (Exception ex)
            {
                AppLog.Swallowed("抓帧：暂停失败", ex);
            }
        }

        // ---- 回调（静态：opaque 不可用，只能看 _active）-------------------------

        private static uint OnFormat(
            ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
        {
            var self = _active;

            if (self == null || self._disposed) return 0;

            // 只要 RV32（BGRX），宽高保持解码器给的原生值：不缩放就不会失真。
            for (var i = 0; i < 4; i++)
                Marshal.WriteByte(chroma, i, (byte)"RV32"[i]);

            var pitch = (int)(((width * 4) + 31) / 32 * 32);

            // 必须写（见类注释 ②）：不写 VLC 不会把画面写进我们的缓冲。
            pitches = (uint)pitch;
            lines = height;

            self.RecordFormat((int)width, (int)height, pitch);

            return self.PrepareBuffer((int)width, (int)height, pitch) ? 1u : 0u;
        }

        private static void OnCleanup(ref IntPtr opaque)
        {
            // 缓冲区归抓帧器自己管，这里没有要释放的。
        }

        private static IntPtr OnLock(IntPtr opaque, IntPtr planes)
        {
            var self = _active;

            if (self == null) return IntPtr.Zero;

            lock (self._frameGate)
            {
                var plane = self._plane;

                if (plane == IntPtr.Zero) return IntPtr.Zero;

                // planes 是 libvlc 分配好的 void* 数组，单平面只写第 0 项。
                Marshal.WriteIntPtr(planes, 0, plane);

                // 返回的 picture 会原样交给 Unlock / Display：用平面地址当标识（_planes 里查得到它的尺寸）。
                return plane;
            }
        }

        private static void OnUnlock(IntPtr opaque, IntPtr picture, IntPtr planes)
        {
            // 解码已写完；真正拷画面在 OnDisplay（显示那一刻才算"这一帧"）。
        }

        private static void OnDisplay(IntPtr opaque, IntPtr picture)
        {
            var self = _active;

            if (self == null || self._disposed) return;

            lock (self._frameGate)
            {
                if (self._captured || self._frame == null) return;

                if (!self._planes.TryGetValue(picture, out var geometry)) return;

                int width = geometry.Width;
                int height = geometry.Height;
                int pitch = geometry.Pitch;
                var bytes = pitch * height;

                if (width <= 0 || height <= 0 || bytes <= 0 || self._frame.Length < bytes) return;

                // 起播时的空白帧 / 片头黑场：没有亮度也没有层次，跳过，继续等。
                var stats = SampleStats(picture, width, height, pitch);

                if (stats.Brightness < MinimumBrightness && stats.Variance < MinimumVariance) return;

                Marshal.Copy(picture, self._frame, 0, bytes);

                self._captured = true;
                self._capturedSerial = self._serial;
                self._capturedWidth = width;
                self._capturedHeight = height;
                self._capturedPitch = pitch;
            }

            try
            {
                self._frameReady.Set();
            }
            catch (ObjectDisposedException)
            {
                // 视图已经销毁：这一帧没人要了。
            }
        }

        /// <summary>抽样算亮度与"有没有层次"（方差），用来把空白帧挡在外面。</summary>
        private static (int Brightness, int Variance) SampleStats(IntPtr plane, int width, int height, int pitch)
        {
            var pixels = width * height;
            var stride = Math.Max(1, pixels / 1200);

            long sum = 0;
            long sumSquares = 0;
            var count = 0;

            for (var index = 0; index < pixels; index += stride)
            {
                var x = index % width;
                var y = index / width;
                var value = Marshal.ReadInt32(IntPtr.Add(plane, y * pitch + x * 4));

                var luma = (((value >> 16) & 0xFF) * 30 + ((value >> 8) & 0xFF) * 59 + (value & 0xFF) * 11) / 100;

                sum += luma;
                sumSquares += (long)luma * luma;
                count++;
            }

            if (count == 0) return (0, 0);

            var mean = (int)(sum / count);
            var variance = (int)Math.Max(0, sumSquares / count - (long)mean * mean);

            return (mean, variance);
        }

        private void RecordFormat(int width, int height, int pitch)
        {
            lock (_frameGate)
            {
                if (_formatCalls.Count >= 24) _formatCalls.RemoveAt(0);

                _formatCalls.Add((width, height, pitch));
            }
        }

        private bool PrepareBuffer(int width, int height, int pitch)
        {
            lock (_frameGate)
            {
                if (width <= 0 || height <= 0 || pitch <= 0) return false;

                var key = (width, height, pitch);

                if (!_planeByGeometry.TryGetValue(key, out var plane))
                {
                    // ⚠ 每个尺寸一份缓冲、只增不减：libvlc 可能拿着旧指针在写，
                    // 播放中 Free 掉旧缓冲实测表现为画面全黑，甚至 STATUS_HEAP_CORRUPTION。
                    var bytesForGeometry = pitch * height;
                    var raw = Marshal.AllocHGlobal(bytesForGeometry + 32);
                    var aligned = (raw.ToInt64() + 31) / 32 * 32;

                    _buffers.Add(raw);

                    plane = new IntPtr(aligned);
                    _planeByGeometry[key] = plane;
                    _planes[plane] = key;
                }

                _plane = plane;
                _width = width;
                _height = height;
                _pitch = pitch;

                var bytes = pitch * height;
                var frame = _frame;

                if (frame == null || frame.Length < bytes) _frame = new byte[bytes];

                return true;
            }
        }

        public void Dispose()
        {
            lock (ActiveGate)
            {
                if (ReferenceEquals(_active, this)) _active = null;
            }

            lock (_frameGate)
            {
                if (_disposed) return;

                _disposed = true;
            }

            try
            {
                _player?.SetPause(true);
            }
            catch (Exception ex)
            {
                AppLog.Swallowed("抓帧：释放前暂停失败", ex);
            }

            try
            {
                if (_player != null)
                {
                    _player.Media = null;
                    _player.Dispose();
                    _player = null;
                }
            }
            catch (Exception ex)
            {
                AppLog.Swallowed("抓帧：释放播放器失败", ex);
            }

            try
            {
                _libVlc?.Dispose();
            }
            catch (Exception ex)
            {
                AppLog.Swallowed("抓帧：释放内核失败", ex);
            }

            _libVlc = null;

            try
            {
                _media?.Dispose();
            }
            catch (Exception ex)
            {
                AppLog.Swallowed("抓帧：释放 Media 失败", ex);
            }

            _media = null;

            lock (_frameGate)
            {
                foreach (var raw in _buffers)
                {
                    try
                    {
                        Marshal.FreeHGlobal(raw);
                    }
                    catch (Exception ex)
                    {
                        AppLog.Swallowed("抓帧：释放缓冲区失败", ex);
                    }
                }

                _buffers.Clear();
                _planes.Clear();
                _planeByGeometry.Clear();

                _plane = IntPtr.Zero;
                _frame = null;
                _captured = false;
            }

            _frameReady.Dispose();
        }
    }
}
