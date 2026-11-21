using System;
using System.IO;
using System.Threading;

namespace 播放器.Core
{
    /// <summary>
    /// 盯着"当前目录"的 <see cref="FileSystemWatcher"/>（1.28.0）：把一批文件系统事件收敛成
    /// 一次回调。
    /// <para>
    /// 三条有界：<b>只盯一层</b>（递归 watcher 会把一个正在解压的目录变成事件风暴）、
    /// <b>尾部去抖 500 ms + 两次回调至少隔 1 秒</b>（解压 / 下载时一秒几十个事件，不能每个都刷列表）、
    /// <b>缓冲溢出只报有限的几次</b>（连续 3 次就让调用方停掉自动刷新，不再无限重建）。
    /// </para>
    /// <para>
    /// 网络位置<b>不建</b> watcher：离线共享上连"目录是否存在"都要等 SMB 超时，事件更不可信；
    /// 与缩略图、递归搜索用的是同一条离线共享理由。
    /// </para>
    /// </summary>
    internal sealed class FolderWatch : IDisposable
    {
        /// <summary>尾部去抖：最后一次事件之后安静这么久才回调。</summary>
        internal const int DebounceMilliseconds = 500;

        /// <summary>两次回调之间的最小间隔：一波未平一波又起时也不会刷屏。</summary>
        internal const int MinimumIntervalMilliseconds = 1000;

        /// <summary>事件缓冲：默认 8 KB 在解压时很容易溢出，这里给 64 KB。</summary>
        internal const int BufferBytes = 64 * 1024;

        /// <summary>连续溢出几次就放弃（调用方停掉自动刷新）。</summary>
        internal const int OverflowLimit = 3;

        private readonly object _gate = new object();
        private readonly string _path;

        private FileSystemWatcher? _watcher;
        private Timer? _timer;
        private DateTime _lastRaiseUtc = DateTime.MinValue;

        private int _overflows;
        private bool _armed;
        private bool _disposed;

        internal FolderWatch(string path) => _path = path;

        /// <summary>一批变化（已去抖、已限频）。在计时器线程上触发。</summary>
        internal event Action? Changed;

        /// <summary>缓冲溢出连续超过上限：调用方应当停掉自动刷新。</summary>
        internal event Action? OverflowExhausted;

        /// <summary>这个路径能不能盯：本地、存在、不是网络位置。</summary>
        internal static bool CanWatch(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            if (MediaFormats.IsNetworkPath(path)) return false;

            try
            {
                return Path.IsPathFullyQualified(path) && Directory.Exists(path);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>开始盯；盯不上返回 <c>false</c>（权限 / 路径消失），调用方安静降级。</summary>
        internal bool Start()
        {
            if (_disposed) return false;

            try
            {
                var watcher = new FileSystemWatcher(_path)
                {
                    NotifyFilter = NotifyFilters.FileName
                                   | NotifyFilters.DirectoryName
                                   | NotifyFilters.Size
                                   | NotifyFilters.LastWrite
                                   | NotifyFilters.CreationTime,
                    IncludeSubdirectories = false,
                    InternalBufferSize = BufferBytes,
                    Filter = "*"
                };

                watcher.Created += OnAnyChange;
                watcher.Deleted += OnAnyChange;
                watcher.Changed += OnAnyChange;
                watcher.Renamed += OnAnyChange;
                watcher.Error += OnWatcherError;

                watcher.EnableRaisingEvents = true;

                lock (_gate)
                {
                    if (_disposed)
                    {
                        watcher.Dispose();
                        return false;
                    }

                    _watcher = watcher;
                    _timer = new Timer(OnTimer, null, Timeout.Infinite, Timeout.Infinite);
                }

                return true;
            }
            catch (Exception ex)
            {
                AppLog.Swallowed("文件页自动刷新：盯不上这个目录 " + _path, ex);
                return false;
            }
        }

        /// <summary>已经溢出过几次（测试 / 状态栏用）。</summary>
        internal int Overflows => _overflows;

        private void OnAnyChange(object sender, FileSystemEventArgs e)
        {
            lock (_gate)
            {
                if (_disposed) return;

                _armed = true;
                _timer?.Change(DebounceMilliseconds, Timeout.Infinite);
            }
        }

        private void OnWatcherError(object sender, ErrorEventArgs e)
        {
            var count = Interlocked.Increment(ref _overflows);

            AppLog.Swallowed("文件页自动刷新：事件缓冲溢出（第 " + count + " 次）", e.GetException());

            if (count < OverflowLimit) return;

            Changed?.Invoke();
            OverflowExhausted?.Invoke();
        }

        private void OnTimer(object? state)
        {
            lock (_gate)
            {
                if (_disposed || !_armed) return;

                _armed = false;

                var sinceLast = (DateTime.UtcNow - _lastRaiseUtc).TotalMilliseconds;

                if (sinceLast < MinimumIntervalMilliseconds)
                {
                    // 还在最小间隔里：把这次变化并进下一次。
                    _armed = true;
                    _timer?.Change((int)(MinimumIntervalMilliseconds - sinceLast), Timeout.Infinite);
                    return;
                }

                _lastRaiseUtc = DateTime.UtcNow;
            }

            Changed?.Invoke();
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;

                _disposed = true;
            }

            Timer? timer;
            FileSystemWatcher? watcher;

            lock (_gate)
            {
                timer = _timer;
                watcher = _watcher;
                _timer = null;
                _watcher = null;
            }

            if (watcher != null)
            {
                try
                {
                    watcher.EnableRaisingEvents = false;
                    watcher.Created -= OnAnyChange;
                    watcher.Deleted -= OnAnyChange;
                    watcher.Changed -= OnAnyChange;
                    watcher.Renamed -= OnAnyChange;
                    watcher.Error -= OnWatcherError;
                    watcher.Dispose();
                }
                catch (Exception ex)
                {
                    AppLog.Swallowed("文件页自动刷新：释放 watcher 失败", ex);
                }
            }

            try
            {
                timer?.Dispose();
            }
            catch (Exception ex)
            {
                AppLog.Swallowed("文件页自动刷新：释放计时器失败", ex);
            }
        }
    }
}
