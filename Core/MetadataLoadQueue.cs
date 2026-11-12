using System;
using System.Threading;
using System.Threading.Tasks;

// 切歌之后读标签 / 歌词的那条后台队列（1.19.0）。

namespace 播放器.Core
{
    /// <summary>
    /// 一次标签读取的结果：标签 + 歌词（两样都是"读这个文件读出来的"）。
    /// </summary>
    public readonly record struct MetadataResult(MediaTags Tags, LyricsDocument Lyrics);

    /// <summary>
    /// 切歌时的标签读取队列：<b>最多两条在飞、只留一条"最新待读"</b>。
    /// <para>
    /// 为什么要有它：1.18.0 之前每切一首就起一个 <c>Task.Run</c>，没有上限也没有取消——
    /// 在线程池上连切二十首就是二十条并发读；而离线共享上一次读文件能卡几十秒
    /// （1.17.0 实测 <c>File.Exists</c> 要 82～93 秒），被放弃的读取只是"结果丢掉"，
    /// 线程照样占着池里的工作线程。这里把三件事分开：
    /// </para>
    /// <list type="number">
    ///   <item><b>没开始的就别开始</b>：连切时只有"第一个在飞"和"最后一个待读"会被真的读，
    ///   中间那些不读（<see cref="Skips"/> 数着它们）。</item>
    ///   <item><b>在飞有上限</b>（默认 2）：被离线共享卡住的读取最多两条，不会越攒越多。</item>
    ///   <item><b>跑在专用线程上</b>（<see cref="TaskCreationOptions.LongRunning"/>），不占线程池——
    ///   和 1.17.0 的网络探测同一个理由与同一套写法。</item>
    /// </list>
    /// <para>
    /// <b>它不判断"这份结果还属于当前这一首吗"</b>：那是 <see cref="MediaSession"/> 的版本号管的事，
    /// 唯一真值只有一处。队列只按 token 比大小决定"还该不该起"（被更新的一次请求替掉的就不再起）。
    /// 回调在<b>后台线程</b>上触发，调用方自己丢回界面线程。
    /// </para>
    /// </summary>
    public sealed class MetadataLoadQueue : IDisposable
    {
        private sealed class LoadRequest
        {
            internal LoadRequest(string path, int token)
            {
                Path = path;
                Token = token;
            }

            internal string Path { get; }

            internal int Token { get; }
        }

        private readonly object _gate = new object();
        private readonly Func<string, MetadataResult> _read;
        private readonly Action<string, int, MetadataResult?> _completed;
        private readonly int _maxLoads;

        private LoadRequest? _pending;
        private int _inFlight;
        private int _starts;
        private int _skips;
        private bool _closed;

        /// <param name="read">真的去读一个文件（在线程池 / 专用线程上调用）。</param>
        /// <param name="completed">读完了：<c>(路径, token, 结果)</c>；读失败时结果是 <c>null</c>。在后台线程上触发。</param>
        /// <param name="maxLoads">同时在飞最多几条（默认 2：一条在飞 + 一条留给"最新那首"）。</param>
        public MetadataLoadQueue(
            Func<string, MetadataResult> read,
            Action<string, int, MetadataResult?> completed,
            int maxLoads = 2)
        {
            _read = read ?? throw new ArgumentNullException(nameof(read));
            _completed = completed ?? throw new ArgumentNullException(nameof(completed));
            _maxLoads = Math.Max(1, maxLoads);
        }

        /// <summary>真的起过几次读取（被跳过的不算）。</summary>
        public int Starts => Volatile.Read(ref _starts);

        /// <summary>因为"已经有两条在飞"而被替掉的请求数（它们不会被补读）。</summary>
        public int Skips => Volatile.Read(ref _skips);

        /// <summary>现在有几条在飞。</summary>
        public int InFlight => Volatile.Read(ref _inFlight);

        /// <summary>待读位上有没有一条（0 或 1）。</summary>
        public int Pending
        {
            get
            {
                lock (_gate) return _pending == null ? 0 : 1;
            }
        }

        /// <summary>已经关掉了吗（关掉之后不再起新的、也不再回调）。</summary>
        public bool IsClosed
        {
            get
            {
                lock (_gate) return _closed;
            }
        }

        /// <summary>
        /// 请求读一首。有空位就立刻起；没空位就占住"最新待读"这一位（原来那条被替掉）。
        /// <para>在飞的读取不能被中断（<c>FileStream</c> 打开不可取消，见维护手册 D6），
        /// 能省的只是"还没开始的那些"。</para>
        /// </summary>
        public void Request(string path, int token)
        {
            if (string.IsNullOrEmpty(path)) return;

            LoadRequest? start = null;

            lock (_gate)
            {
                if (_closed) return;

                if (_inFlight < _maxLoads)
                {
                    _inFlight++;
                    start = new LoadRequest(path, token);
                }
                else
                {
                    if (_pending != null) _skips++;
                    _pending = new LoadRequest(path, token);
                }
            }

            if (start != null) StartRead(start);
        }

        /// <summary>丢掉"待读"那一条（停止播放 / 切到光盘 / 关窗）。在飞的不动。</summary>
        public void Clear()
        {
            lock (_gate) _pending = null;
        }

        /// <summary>
        /// 关掉队列：不再起新的、"待读"丢掉、之后读完的也不回调。
        /// <b>不等待</b>在飞的那条（可能是离线共享上卡着的几十秒）——线程是后台线程，进程照常退。
        /// </summary>
        public void Dispose()
        {
            lock (_gate)
            {
                _closed = true;
                _pending = null;
            }
        }

        private void StartRead(LoadRequest request)
        {
            Interlocked.Increment(ref _starts);

            // 专用线程：读文件可能卡在 SMB 上几十秒，不能拿线程池的工作线程去顶
            // （1.17.0 的探测就是同一个理由，实测 12 条卡住的池线程会把"第一个空任务"拖到 312 ms）。
            Task.Factory.StartNew(
                () => RunRead(request),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        private void RunRead(LoadRequest request)
        {
            MetadataResult? result = null;

            try
            {
                result = _read(request.Path);
            }
            catch (Exception ex)
            {
                AppLog.Swallowed("读标签失败就当没有标签（标签读不出来绝不能影响播放）", ex);
            }

            LoadRequest? next = null;
            var closed = false;

            lock (_gate)
            {
                _inFlight--;

                if (_closed)
                {
                    closed = true;
                }
                else if (_pending != null)
                {
                    // 先取走再起：起的时候不再动锁，免得 StartRead 里再进一次
                    _inFlight++;
                    next = _pending;
                    _pending = null;
                }
            }

            // 先把下一条起起来（如果界面又切歌了），再回调这一次的结果：
            // 回调要回到界面线程，别让"下一次读"等界面排完队。
            if (next != null) StartRead(next);

            if (closed) return;

            try
            {
                _completed(request.Path, request.Token, result);
            }
            catch (Exception ex)
            {
                AppLog.Swallowed("标签读取结果回填失败（窗体可能正在销毁）", ex);
            }
        }
    }
}
