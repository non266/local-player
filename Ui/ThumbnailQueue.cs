using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using 播放器.Core;

namespace 播放器.Ui
{
    /// <summary>后台队列交回来的一张缩略图；<see cref="Bitmap"/> 为 <c>null</c> 表示"这个文件没有可用的封面"。</summary>
    internal readonly struct ThumbnailResult
    {
        internal ThumbnailResult(string key, string path, int version, Bitmap? bitmap, bool aborted = false)
        {
            Key = key;
            Path = path;
            Version = version;
            Bitmap = bitmap;
            Aborted = aborted;
        }

        /// <summary>缓存键（路径 + 大小 + 修改时间）；失败结果也用同一个键记负缓存。</summary>
        internal string Key { get; }

        internal string Path { get; }

        /// <summary>发起请求时的代际号；取件方拿它对当前代际，不符就丢掉。</summary>
        internal int Version { get; }

        internal Bitmap? Bitmap { get; }

        /// <summary>
        /// 这一次是"被取消"而不是"没有封面"：视图要移除排队标记、保留预算，
        /// <b>不能</b>把它记进负缓存（否则滚回来也不会重试）。
        /// </summary>
        internal bool Aborted { get; }
    }

    /// <summary>
    /// 文件页缩略图的后台队列：<b>一条专用线程</b>、FIFO、有上限、可整批取消。
    /// <para>
    /// 为什么不用线程池：读盘会卡（离线共享上一次读能等几十秒），占住线程池的线程会连累
    /// 别的后台任务（与 1.19.0 的标签读取队列、1.24.0 的目录读取同一个理由）。
    /// 为什么不在界面线程上等：队列只负责"排进去"，界面线程立刻返回。
    /// </para>
    /// <para>
    /// 所有权只有一条线：位图由本队列生产、<see cref="Ready"/> 交付；交付不成就由这里释放。
    /// 取消只清队列（正在解码的那一张允许跑完，取件方按代际号丢掉），
    /// 因为文件读取本身不可取消。
    /// </para>
    /// </summary>
    internal sealed class ThumbnailQueue : IDisposable
    {
        /// <summary>排队上限。超了直接丢请求：视图按每目录预算已经截过，这里只防失控。</summary>
        internal const int MaxPending = 400;

        /// <summary>一条请求。</summary>
        private sealed class Job
        {
            internal Job(string key, string path, long size, long modifiedTicks, int edge, int version, bool isVideo)
            {
                Key = key;
                Path = path;
                Size = size;
                ModifiedTicks = modifiedTicks;
                Edge = edge;
                Version = version;
                IsVideo = isVideo;
            }

            internal string Key { get; }

            internal string Path { get; }

            /// <summary>文件大小与修改时间：磁盘缓存键要它们（换了内容就必须重做）。</summary>
            internal long Size { get; }

            internal long ModifiedTicks { get; }

            internal int Edge { get; }

            internal int Version { get; }

            /// <summary>没有封面时允不允许去截视频首帧（1.28.0）。</summary>
            internal bool IsVideo { get; }
        }

        private readonly CoverArtLoader _loader;
        private readonly ThumbnailCache? _cache;
        private readonly IVideoFrameSource? _frameSource;
        private readonly object _gate = new object();

        /// <summary>两条队列：封面先做、帧后做——一个视频的 6 秒超时不能挡住后面歌曲的封面。</summary>
        private readonly Queue<Job> _coverQueue = new Queue<Job>();
        private readonly Queue<Job> _frameQueue = new Queue<Job>();

        private readonly ManualResetEventSlim _wake = new ManualResetEventSlim(false);
        private readonly Thread _thread;

        private bool _disposed;
        private bool _busy;

        /// <summary>
        /// 帧代际：每次取消 / 整批取消都 +1。正在跑的截帧按这个判断"还要不要"，
        /// 免得一次 2.5 秒的等待把后面新目录的封面全堵住。
        /// </summary>
        private int _frameEpoch;

        internal ThumbnailQueue(CoverArtLoader loader, ThumbnailCache? cache = null, IVideoFrameSource? frames = null)
        {
            _loader = loader ?? throw new ArgumentNullException(nameof(loader));
            _cache = cache;
            _frameSource = frames;

            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "文件页缩略图"
            };

            _thread.Start();
        }

        /// <summary>一张缩略图做完了（成功与失败都会通知）；在后台线程上触发。</summary>
        internal event Action<ThumbnailResult>? Ready;

        /// <summary>还在排队、没开始做的条数。</summary>
        internal int Pending
        {
            get { lock (_gate) return _coverQueue.Count + _frameQueue.Count; }
        }

        /// <summary>当前正在读 / 解码（0 或 1；队列只有一条线程）。</summary>
        internal bool Busy
        {
            get { lock (_gate) return _busy; }
        }

        /// <summary>排一张缩略图；队列满了就丢掉这次请求并返回 <c>false</c>（不抛、不阻塞）。</summary>
        internal bool Request(
            string key, string path, long size, long modifiedTicks, int edge, int version, bool isVideo)
        {
            lock (_gate)
            {
                if (_disposed) return false;
                if (_coverQueue.Count + _frameQueue.Count >= MaxPending) return false;

                (isVideo ? _frameQueue : _coverQueue)
                    .Enqueue(new Job(key, path, size, modifiedTicks, edge, version, isVideo));

                _wake.Set();
                return true;
            }
        }

        /// <summary>
        /// 丢掉还没开始的帧请求，并让正在跑的那一次尽快放弃。
        /// 视口一变就调：封面优先，帧可以晚点再做。
        /// </summary>
        internal void CancelFrames()
        {
            lock (_gate)
            {
                _frameEpoch++;
                _frameQueue.Clear();
            }
        }

        /// <summary>丢掉还没开始的请求；正在做的那一张会照常交回来（取件方按代际号丢弃）。</summary>
        internal void CancelAll()
        {
            lock (_gate)
            {
                _frameEpoch++;
                _coverQueue.Clear();
                _frameQueue.Clear();
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;

                _disposed = true;
                _frameEpoch++;
                _coverQueue.Clear();
                _frameQueue.Clear();
            }

            _wake.Set();

            // 线程可能在 FileStream.Read 里卡着；最多等 200 ms，剩下的交给进程退出
            // （IsBackground = true）。这里也**不 Dispose** _wake：线程醒来时可能还要用它。
            _thread.Join(200);
        }

        private void Loop()
        {
            while (true)
            {
                Job? request;
                var frameEpoch = 0;

                lock (_gate)
                {
                    request = null;

                    if (_coverQueue.Count > 0)
                    {
                        request = _coverQueue.Dequeue();
                    }
                    else if (_frameQueue.Count > 0)
                    {
                        request = _frameQueue.Dequeue();
                        frameEpoch = _frameEpoch;
                    }

                    _busy = request != null;
                }

                if (request == null)
                {
                    lock (_gate)
                    {
                        if (_disposed) return;
                    }

                    _wake.Wait();
                    _wake.Reset();
                    continue;
                }

                Bitmap? bitmap = null;
                var aborted = false;

                try
                {
                    var cacheKey = ThumbnailCache.Key(request.Path, request.Size, request.ModifiedTicks, request.Edge);

                    bitmap = _cache?.TryLoad(cacheKey, request.Edge);

                    if (bitmap == null)
                    {
                        var art = _loader.Load(request.Path);
                        bitmap = CoverThumbnails.Decode(art, request.Edge);

                        if (bitmap == null && request.IsVideo && _frameSource != null)
                        {
                            bool Cancelled()
                            {
                                lock (_gate) return _disposed || _frameEpoch != frameEpoch;
                            }

                            bitmap = _frameSource.Grab(request.Path, request.Edge, Cancelled);
                            aborted = bitmap == null && Cancelled();
                        }

                        if (bitmap != null) _cache?.Save(cacheKey, bitmap);
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Swallowed("文件页缩略图后台读取失败：" + request.Path, ex);
                }

                lock (_gate)
                {
                    _busy = false;

                    if (_disposed)
                    {
                        bitmap?.Dispose();
                        return;
                    }
                }

                var handler = Ready;

                if (handler == null)
                {
                    // 没人接（窗体已经走了）：位图归本队列释放。
                    bitmap?.Dispose();
                    continue;
                }

                try
                {
                    handler(new ThumbnailResult(request.Key, request.Path, request.Version, bitmap, aborted));
                }
                catch (Exception ex)
                {
                    AppLog.Swallowed("文件页缩略图交付失败", ex);
                    bitmap?.Dispose();
                }
            }
        }
    }
}
