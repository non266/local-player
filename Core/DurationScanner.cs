using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using LibVLCSharp.Shared;

namespace 播放器.Core
{
    /// <summary>后台扫描到时长时的事件参数。</summary>
    public sealed class DurationFoundEventArgs : EventArgs
    {
        public DurationFoundEventArgs(string filePath, TimeSpan duration)
        {
            FilePath = filePath;
            Duration = duration;
        }

        public string FilePath { get; }

        public TimeSpan Duration { get; }
    }

    /// <summary>
    /// 单线程后台任务：用 LibVLC 解析媒体元数据，补全播放列表里未知的时长。
    /// <para>解析只读文件头，不影响正在进行的播放；失败的文件会被移除记录，以便以后再试。</para>
    /// </summary>
    public sealed class DurationScanner : IDisposable
    {
        /// <summary>交给 libvlc 的预解析超时（毫秒）。</summary>
        private const int ParseTimeoutMilliseconds = 5000;

        /// <summary>本地等待解析完成的上限；超过则认为该文件解析卡住，跳过。</summary>
        private const int ParseWaitMilliseconds = 15000;

        /// <summary>等待解析的分片长度：每片结束时都检查一次取消，退出时才能及时放手。</summary>
        private const int ParseWaitSliceMilliseconds = 250;

        private readonly LibVLC _libVlc;
        private readonly Channel<string> _channel;
        private readonly ConcurrentDictionary<string, byte> _seen;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly Task _worker;
        private bool _disposed;

        /// <summary>扫描到有效时长时触发（在后台线程上，调用方需自行切回 UI 线程）。</summary>
        public event EventHandler<DurationFoundEventArgs>? DurationFound;

        public DurationScanner(LibVLC libVlc)
        {
            _libVlc = libVlc ?? throw new ArgumentNullException(nameof(libVlc));
            _channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });
            _seen = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
            _worker = Task.Run(WorkerLoopAsync);
        }

        /// <summary>排入待扫描的文件。重复排入会被忽略。</summary>
        public void Enqueue(IEnumerable<string> filePaths)
        {
            if (_disposed) return;

            foreach (var path in filePaths)
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                if (!MediaFormats.IsMediaFile(path)) continue;
                if (_seen.TryAdd(path, 0))
                    _channel.Writer.TryWrite(path);
            }
        }

        /// <summary>
        /// 丢弃尚未开始处理的排队项，并忘掉"哪些文件排过队"的记录（清空播放列表时调用）。
        /// <para>
        /// <b><see cref="_seen"/> 必须一起清。</b>它只记"排过队"，而解析成功后不会移除
        /// （那是有意的，避免重复解析）。如果只清队列不清它，把播放列表清空、再重新添加
        /// 同一个目录时，所有文件都还在 <see cref="_seen"/> 里，一条都不会重新排队，
        /// 新加入的条目就永远停在 "--:--"，非得重启程序才恢复。
        /// </para>
        /// </summary>
        public void ClearPending()
        {
            while (_channel.Reader.TryRead(out _)) { }
            _seen.Clear();
        }

        private async Task WorkerLoopAsync()
        {
            try
            {
                while (await _channel.Reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
                {
                    while (_channel.Reader.TryRead(out var path))
                    {
                        if (_cts.IsCancellationRequested) return;

                        var duration = Probe(path);
                        if (duration > TimeSpan.Zero)
                        {
                            DurationFound?.Invoke(this, new DurationFoundEventArgs(path, duration));
                        }
                        else
                        {
                            // 解析失败或不是有效媒体：允许后续重试。
                            _seen.TryRemove(path, out _);
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { /* 后台任务不应把异常抛到未观察的 Task 上 */ }
        }

        /// <summary>
        /// 同步解析一个文件的时长。
        /// <para>
        /// <b>这里必须同步等待，不能写成 await。</b>
        /// <see cref="Media.Parse"/> 返回的 Task 是在 libvlc 自己的解析回调线程上完成的，
        /// 用 await 时后续代码可能<b>就地运行在该回调线程上</b>，那么紧随其后的
        /// <c>Media.Dispose()</c> 就会在 libvlc 仍处于回调栈内时释放该 media，
        /// 回调返回后访问已释放内存 —— 表现为无法捕获的原生访问违例（0xC0000005，进程直接消失）。
        /// 改成阻塞等待后，释放动作发生在扫描器自己的工作线程上，与回调线程错开。
        /// </para>
        /// </summary>
        private TimeSpan Probe(string path)
        {
            Media? media = null;
            var started = Environment.TickCount64;
            try
            {
                // 退出过程中不要再碰 LibVLC：主窗口随后就会释放它。
                if (_cts.IsCancellationRequested) return TimeSpan.Zero;

                media = new Media(_libVlc, path, FromType.FromPath);

                var parseTask = media.Parse(MediaParseOptions.ParseLocal, ParseTimeoutMilliseconds);

                // 分片等待而不是一次等 15 秒：每片结束时检查取消，
                // 这样 Dispose 一旦取消，最迟 250ms 就能退出这一项，
                // 不会在退出流程里继续握着 LibVLC（那是原生的访问违例）。
                var waited = 0;
                while (!parseTask.Wait(ParseWaitSliceMilliseconds))
                {
                    waited += ParseWaitSliceMilliseconds;

                    if (_cts.IsCancellationRequested || waited >= ParseWaitMilliseconds)
                    {
                        // 解析还没结束就放手。注意<b>不能</b>在这里直接 Dispose：
                        // libvlc 可能仍处于回调栈内，就地释放会变成访问违例。
                        // 把释放挂到解析任务自己身上，它完成时在<b>线程池</b>上执行
                        // （ContinueWith 不会内联到完成它的那个线程），既安全又不再泄漏。
                        var abandoned = media;
                        media = null;
                        _ = parseTask.ContinueWith(_ => abandoned.Dispose(), TaskScheduler.Default);

                        // 这一条以前是无声的：界面上只看到"某一项时长未知"，
                        // 到底卡在哪个文件、卡了多久，只能靠猜。
                        AppLog.Warn($"时长解析超时（等了 {waited} ms），稍后还会再试：{path}");

                        return TimeSpan.Zero;
                    }
                }

                var milliseconds = media.Duration;
                var duration = milliseconds > 0 ? TimeSpan.FromMilliseconds(milliseconds) : TimeSpan.Zero;

                AppLog.Debug(duration > TimeSpan.Zero
                    ? $"时长解析：{TimeFormatter.Format(duration)}，用了 {Environment.TickCount64 - started} ms —— {path}"
                    : $"时长解析：媒体没给出时长，用了 {Environment.TickCount64 - started} ms —— {path}");

                return duration;
            }
            catch (Exception ex)
            {
                AppLog.Swallowed("时长解析失败（多半不是有效的媒体文件）：" + path, ex);
                return TimeSpan.Zero;
            }
            finally
            {
                media?.Dispose();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                _cts.Cancel();
                _channel.Writer.TryComplete();
                _worker.Wait(TimeSpan.FromSeconds(2));
            }
            catch (Exception) { }

            _cts.Dispose();
        }
    }
}
