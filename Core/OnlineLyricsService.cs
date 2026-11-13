using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace 播放器.Core
{
    /// <summary>
    /// 在线查找的结果要落到哪儿去。窗体实现它（把结果画到侧栏 / 桌面歌词 / 状态栏），
    /// 测试可以换一个假的——这样"查什么、什么时候不查、失败了说什么"就能脱离窗口单独验。
    /// </summary>
    public interface IOnlineLookupHost
    {
        /// <summary>宿主还活着吗（窗体销毁 / 句柄没建好时，结果直接丢掉）。</summary>
        bool IsAlive { get; }

        /// <summary>把回调送回宿主线程（窗体这边就是 UI 线程）。</summary>
        void Post(Action action);

        /// <summary>状态栏那一行。</summary>
        void Status(string message);

        /// <summary>歌词到手了（回调时已经在宿主线程上）。</summary>
        void ApplyLyrics(string path, LyricsDocument document, string message);

        /// <summary>封面到手了（回调时已经在宿主线程上）。</summary>
        void ApplyCover(string path, byte[] data, string mime, string message);
    }

    /// <summary>一次在线查找：查哪首、按什么查。</summary>
    public readonly struct OnlineLookupRequest
    {
        public OnlineLookupRequest(
            string path,
            string? title,
            string? album,
            string? artist,
            bool lyrics,
            bool cover,
            bool silent)
        {
            Path = path;
            Title = title;
            Album = album;
            Artist = artist;
            Lyrics = lyrics;
            Cover = cover;
            Silent = silent;
        }

        public string Path { get; }

        public string? Title { get; }

        public string? Album { get; }

        public string? Artist { get; }

        /// <summary>要歌词吗。</summary>
        public bool Lyrics { get; }

        /// <summary>要封面吗。</summary>
        public bool Cover { get; }

        /// <summary>自动触发（切歌顺手查的）：失败不抢状态栏，交给"同一句只说一次"那条路。</summary>
        public bool Silent { get; }
    }

    /// <summary>还缺什么：决定这一次到底要不要联网。</summary>
    public readonly struct OnlineLookupNeed
    {
        public OnlineLookupNeed(bool lyrics, bool cover)
        {
            Lyrics = lyrics;
            Cover = cover;
        }

        public bool Lyrics { get; }

        public bool Cover { get; }

        /// <summary>有一样缺就得联网。</summary>
        public bool Any => Lyrics || Cover;
    }

    /// <summary>
    /// 在线歌词与封面：会话缓存、要不要联网的判断、取消、请求编排、"同一句话只说一次"。
    /// <para>
    /// 原来这些都长在 <c>MainForm.OnlineLyrics.cs</c> 里：缓存是两个私有字典，判断是一串
    /// <c>&&</c>，取消是一个 <see cref="CancellationTokenSource"/> 字段，编排和界面刷新揉在一个
    /// 方法里。结果是这段最需要"能单独摆出来推演"的逻辑，只能靠开一个真窗口 + 假服务器来验。
    /// </para>
    /// <para>
    /// 这里<b>不碰界面、不碰菜单、不弹对话框</b>：结果通过 <see cref="IOnlineLookupHost"/> 交回去。
    /// 也别指望它记"哪些源已经死了"——那是 <see cref="LrcApi"/> 的事（它自己按 endpoint + 源记着）。
    /// </para>
    /// <para>
    /// <b>写盘要从构造时传进来</b>：<see cref="OnlineCache"/> 负责"落在数据目录里、
    /// 上限多大、怎么清"；这里只决定"什么时候读、什么时候写"。不传就是只在本次运行里用
    /// （冒烟测试里那些纯服务检查就是这么造的）。<b>永远不往用户的音乐目录里丢文件。</b>
    /// </para>
    /// </summary>
    public sealed class OnlineLyricsService
    {
        /// <summary>歌词缓存上限：一晚上听几百首也不该把内存堆起来。</summary>
        public const int LyricsCacheLimit = 200;

        /// <summary>封面缓存上限（每张几十 KB ~ 一两 MB，所以比歌词紧得多）。</summary>
        public const int CoverCacheLimit = 20;

        private readonly object _sync = new object();

        private readonly Dictionary<string, LyricsDocument> _lyrics =
            new Dictionary<string, LyricsDocument>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, (byte[] Data, string Mime)> _covers =
            new Dictionary<string, (byte[] Data, string Mime)>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 这一批里有哪些歌词 / 封面是"从磁盘缓存读出来的"（不是这次联网要来的）。
        /// <para>界面上要分别说「本地缓存」还是「本次运行已取过」——光看"有没有"分不出来：
        /// <see cref="Decide"/> 为了回答"缺不缺"会先读一次磁盘，等到界面再取时就已经是内存命中。</para>
        /// </summary>
        private readonly HashSet<string> _lyricsFromDisk = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private readonly HashSet<string> _coversFromDisk = new HashSet<string>(StringComparer.OrdinalIgnoreCase);


        /// <summary>目录里有没有 <c>cover.jpg</c> 之类——那是文件系统的事，由调用方告诉它。</summary>
        private readonly Func<string, bool> _hasSidecarCover;

        /// <summary>磁盘缓存（1.20.0）；<c>null</c> 就是"只在本次运行里用"（测试用这个形态）。</summary>
        private readonly OnlineCache? _cache;

        /// <summary>当前这次查找的取消源：切歌 / 关掉开关时取消，别让旧结果盖到新歌上。</summary>
        private CancellationTokenSource? _cancellation;

        private string? _lastReport;

        public OnlineLyricsService(Func<string, bool> hasSidecarCover, OnlineCache? cache = null)
        {
            _hasSidecarCover = hasSidecarCover ?? throw new ArgumentNullException(nameof(hasSidecarCover));
            _cache = cache;
        }

        /// <summary>本次运行缓存了多少首歌词。</summary>
        public int LyricsCount
        {
            get { lock (_sync) return _lyrics.Count; }
        }

        /// <summary>本次运行缓存了多少张封面。</summary>
        public int CoverCount
        {
            get { lock (_sync) return _covers.Count; }
        }

        /// <summary>上一次自动查找报出来的原因（同一条不重复报）。</summary>
        public string? LastReport
        {
            get { lock (_sync) return _lastReport; }
        }

        // =====================================================================
        // 缓存：内存（本次运行）+ 磁盘（1.20.0，重启还能用）
        // =====================================================================

        public bool HasLyrics(string path) => TryGetLyrics(path, out _);

        public bool TryGetLyrics(string path, out LyricsDocument? document) =>
            TryGetLyrics(path, out document, out _);

        /// <summary>
        /// 取已经取过的歌词：先内存、再磁盘。
        /// <para>
        /// 磁盘命中会顺手填回内存——所以 <see cref="Decide"/> 问完"缺不缺"之后，
        /// 界面那边再取一次走的是内存，不会重复读盘。
        /// </para>
        /// </summary>
        public bool TryGetLyrics(string path, out LyricsDocument? document, out bool fromDisk)
        {
            fromDisk = false;

            lock (_sync)
            {
                if (_lyrics.TryGetValue(path, out var found))
                {
                    document = found;
                    return true;
                }
            }

            if (_cache != null && _cache.TryReadLyrics(path, out var cached) && cached != null)
            {
                RememberLyrics(path, cached);

                document = cached;
                fromDisk = true;
                return true;
            }

            document = null;
            return false;
        }

        public void StoreLyrics(string path, LyricsDocument document)
        {
            lock (_sync)
            {
                if (_lyrics.Count >= LyricsCacheLimit) _lyrics.Clear();

                _lyrics[path] = document;
                _lyricsFromDisk.Remove(path);

                // 有新的东西进来了：下次再失败还要说（不然一句"没找到"会被永远压住）
                _lastReport = null;
            }

            // 落盘放在锁外面：写文件不该挡住别的线程问"有没有"
            _cache?.WriteLyrics(path, document);
        }

        private void RememberLyrics(string path, LyricsDocument document)
        {
            lock (_sync)
            {
                if (_lyrics.Count >= LyricsCacheLimit) _lyrics.Clear();

                _lyrics[path] = document;
                _lyricsFromDisk.Add(path);
            }
        }

        public bool HasCover(string path) => TryGetCover(path, out _, out _);

        public bool TryGetCover(string path, out byte[]? data, out string? mime) =>
            TryGetCover(path, out data, out mime, out _);

        public bool TryGetCover(string path, out byte[]? data, out string? mime, out bool fromDisk)
        {
            fromDisk = false;

            lock (_sync)
            {
                if (_covers.TryGetValue(path, out var found))
                {
                    data = found.Data;
                    mime = found.Mime;
                    return true;
                }
            }

            if (_cache != null && _cache.TryReadCover(path, out var cachedData, out var cachedMime) &&
                cachedData != null)
            {
                RememberCover(path, cachedData, cachedMime ?? string.Empty);

                data = cachedData;
                mime = cachedMime ?? string.Empty;
                fromDisk = true;
                return true;
            }

            data = null;
            mime = null;
            return false;
        }

        public void StoreCover(string path, byte[] data, string mime)
        {
            lock (_sync)
            {
                if (_covers.Count >= CoverCacheLimit) _covers.Clear();

                _covers[path] = (data, mime);
                _coversFromDisk.Remove(path);
                _lastReport = null;
            }

            _cache?.WriteCover(path, data, mime);
        }

        private void RememberCover(string path, byte[] data, string mime)
        {
            lock (_sync)
            {
                if (_covers.Count >= CoverCacheLimit) _covers.Clear();

                _covers[path] = (data, mime);
                _coversFromDisk.Add(path);
            }
        }

        /// <summary>忘掉取过的歌词（内存与磁盘都忘；封面那份不动）。</summary>
        public void ClearLyrics()
        {
            lock (_sync)
            {
                _lyrics.Clear();
                _lyricsFromDisk.Clear();
            }

            _cache?.ClearLyrics();
        }

        /// <summary>忘掉取过的封面（内存与磁盘都忘；歌词那份不动）。</summary>
        public void ClearCovers()
        {
            lock (_sync)
            {
                _covers.Clear();
                _coversFromDisk.Clear();
            }

            _cache?.ClearCovers();
        }

        /// <summary>两份缓存都忘掉（磁盘也清空）。</summary>
        public void Clear()
        {
            lock (_sync)
            {
                _lyrics.Clear();
                _covers.Clear();
                _lyricsFromDisk.Clear();
                _coversFromDisk.Clear();
            }

            _cache?.Clear();
        }

        /// <summary>只忘掉某一首（界面上「重新获取」用它：这条旧缓存不许再被用回来）。</summary>
        public void ClearEntry(string path)
        {
            lock (_sync)
            {
                _lyrics.Remove(path);
                _covers.Remove(path);
                _lyricsFromDisk.Remove(path);
                _coversFromDisk.Remove(path);
            }

            _cache?.ClearEntry(path);
        }

        /// <summary>这一首的歌词是不是从磁盘缓存拿到的（界面上用它选文案）。</summary>
        public bool IsLyricsFromDisk(string path)
        {
            lock (_sync) return _lyricsFromDisk.Contains(path);
        }

        /// <summary>这一首的封面是不是从磁盘缓存拿到的。</summary>
        public bool IsCoverFromDisk(string path)
        {
            lock (_sync) return _coversFromDisk.Contains(path);
        }

        /// <summary>磁盘缓存里有多少条。</summary>
        public int CachedCount => _cache?.Count ?? 0;

        /// <summary>磁盘缓存占多少字节。</summary>
        public long CachedBytes => _cache?.Bytes ?? 0;

        // =====================================================================
        // 要不要联网
        // =====================================================================

        /// <summary>
        /// 本地真的缺什么：歌词缺 = 歌词是空的、缓存里也没有；
        /// 封面缺 = 标签里没有、目录里没有、缓存里也没有。
        /// <para>不在这里判断"视频要不要查"——那是调用方的策略（菜单上也是按同一条策略禁用的）。</para>
        /// </summary>
        public OnlineLookupNeed Decide(string path, MediaTags tags, LyricsDocument lyrics) =>
            new OnlineLookupNeed(
                lyrics.IsEmpty && !HasLyrics(path),
                !tags.HasCoverArt && !_hasSidecarCover(path) && !HasCover(path));

        // =====================================================================
        // 取消
        // =====================================================================

        /// <summary>切歌 / 关闭开关时把还在飞的请求丢掉。</summary>
        public void Cancel()
        {
            var cts = _cancellation;
            _cancellation = null;

            if (cts == null) return;

            try
            {
                cts.Cancel();
            }
            catch (Exception ex)
            {
                // 已经取消了
                AppLog.Swallowed("已经取消了", ex);
            }
        }

        // =====================================================================
        // 编排
        // =====================================================================

        /// <summary>
        /// 真的去要一次。歌词与封面各自独立：一个失败不影响另一个，失败也要说清楚原因
        /// （不然用户只看到"封面有了、歌词没有"，没法知道是没填地址、要鉴权、还是没找到）。
        /// </summary>
        public async Task LookupAsync(
            OnlineLookupRequest request,
            LrcApi.Endpoint endpoint,
            IOnlineLookupHost host)
        {
            Cancel();

            var cts = new CancellationTokenSource();
            _cancellation = cts;

            if (!request.Silent) host.Status("正在从 LrcApi 查找…");

            try
            {
                var fetchedLyrics = false;

                if (request.Lyrics)
                {
                    var result = await LrcApi.FetchLyricsAsync(
                        endpoint, request.Title, request.Album, request.Artist, cts.Token);

                    if (cts.IsCancellationRequested || !host.IsAlive) return;

                    if (result.Ok && result.Value is { } document)
                    {
                        StoreLyrics(request.Path, document);
                        host.Post(() => host.ApplyLyrics(request.Path, document, result.Message));
                        fetchedLyrics = true;
                    }
                    else
                    {
                        var message = "在线歌词：" + result.Message;

                        host.Post(() =>
                        {
                            if (request.Silent)
                            {
                                if (ShouldReport(message)) host.Status(message);
                            }
                            else
                            {
                                host.Status(message);
                            }
                        });
                    }
                }

                if (request.Cover)
                {
                    var result = await LrcApi.FetchCoverAsync(
                        endpoint, request.Title, request.Album, request.Artist, cts.Token);

                    if (cts.IsCancellationRequested || !host.IsAlive) return;

                    if (result.Ok && result.Value is { } image)
                    {
                        var mime = LrcApi.ImageMimeType(image);
                        StoreCover(request.Path, image, mime);
                        host.Post(() => host.ApplyCover(request.Path, image, mime, result.Message));
                    }
                    else
                    {
                        var message = "在线封面：" + result.Message;

                        host.Post(() =>
                        {
                            if (request.Silent)
                            {
                                if (ShouldReport(message)) host.Status(message);
                            }
                            else
                            {
                                host.Status(message);
                            }
                        });
                    }
                }

                // 手动要了歌词 + 封面，歌词一个字都没拿到：与其只报单独两条失败，不如一句总结
                if (request.Lyrics && request.Cover && !fetchedLyrics && !request.Silent)
                    host.Post(() => host.Status("LrcApi 没有找到这首歌的歌词与封面"));
            }
            catch (Exception ex)
            {
                // 网络问题在 LrcApi 里已经变成"失败 + 一句人话"了，能走到这里的都是我们自己的 bug，
                // 所以不管是不是自动触发都报出来（静默吞掉只会让问题更难查）。
                host.Post(() => host.Status("在线查找失败：" + ex.Message));
            }
            finally
            {
                if (ReferenceEquals(_cancellation, cts)) _cancellation = null;

                cts.Dispose();
            }
        }

        /// <summary>
        /// 这条自动查找的失败原因要不要报出去：<b>同一句话只报一次</b>。
        /// <para>
        /// 自动获取失败原来是完全静默的，结果是用户只看到"封面有了、歌词没有"，
        /// 没法知道是没填地址、要鉴权、还是单纯没找到。现在失败也说，
        /// 但同一条原因不重复刷（成功拿到东西之后清掉，下次再失败还会说）。
        /// </para>
        /// <para>状态栏里同一条只报一次，但日志里每一条都要留：事后排查靠的就是它。</para>
        /// </summary>
        public bool ShouldReport(string message)
        {
            AppLog.Info("在线查找（自动）：" + message);

            lock (_sync)
            {
                if (string.Equals(_lastReport, message, StringComparison.Ordinal)) return false;

                _lastReport = message;
                return true;
            }
        }
    }
}
