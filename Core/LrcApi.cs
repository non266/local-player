using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace 播放器.Core
{
    /// <summary>
    /// <b>LrcAPI</b> 客户端：拿曲名 / 专辑 / 歌手去换回歌词与封面。
    /// <para>
    /// 服务端是 <see href="https://github.com/HisAtri/LrcApi">HisAtri/LrcApi</see>
    /// （一个聚合各家歌词源的 Flask 服务），接口见
    /// <see href="https://docs.lrc.cx/docs/APIv1/lyrics">歌词</see> 与
    /// <see href="https://docs.lrc.cx/docs/APIv1/cover">封面</see>。
    /// </para>
    /// <para>
    /// 这里的原则是"<b>永远不抛异常</b>"：网络不通、服务没开、对面返回一堆 HTML、
    /// 自建实例自己崩了，都只是"这次没拿到"（返回 <c>null</c> + 一句人话，能带出服务端的报错就带出来），
    /// 由界面决定要不要提示。一个歌词接口不该把播放器搞崩。
    /// </para>
    /// <para>
    /// 服务地址<b>必须由用户自己填</b>：这是要联网的第三方服务，程序不替用户挑。
    /// </para>
    /// </summary>
    public static partial class LrcApi
    {
        /// <summary>官方演示服务，<b>只是文档里的示例</b>，不会自动使用。</summary>
        public const string ExampleBaseUrl = "https://api.lrc.cx";

        /// <summary>没填服务地址时的说法（界面直接拿去显示）。</summary>
        public const string MissingBaseUrlMessage =
            "还没填 LrcApi 服务地址：视图 → 在线歌词与封面 → 服务地址…";

        /// <summary>服务端要求鉴权、而用户还没填密钥时的说法。</summary>
        public const string AuthRequiredMessage =
            "这个服务的封面/歌词接口需要鉴权（HTTP 401）：在「视图 → 在线歌词与封面 → 鉴权密钥…」里填上密钥";

        /// <summary>网络超时。歌词接口偶尔会很慢，但也不能让用户干等。</summary>
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

        private static readonly HttpClient Client = CreateClient();

        /// <summary>请求里带上的来源标记（有些服务会挡掉没有 User-Agent 的请求）。</summary>
        private const string UserAgent = "播放器/1.0 (LrcApi client)";

        /// <summary>旧版封面接口在"死来源"表里的名字。</summary>
        private const string LegacyCoverSource = "legacy-cover";

        /// <summary>新版封面接口在"死来源"表里的名字。</summary>
        private const string ApiCoverSource = "api-cover";

        // =====================================================================
        // 状态：鉴权方式、坏掉的服务、坏掉的来源
        // 全部按"端点 + 密钥"记：换了地址或者改了密钥，就重新学一遍
        // =====================================================================

        /// <summary>"这个服务的鉴权实现有问题"（带了鉴权头反而 5xx）。</summary>
        private static readonly HashSet<string> TokenToxicKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 已经学明白"这个服务要带鉴权头"。
        /// <para>
        /// 默认是<b>不带</b>的：很多实例的歌词接口根本不需要鉴权，无脑带上反而会在服务端日志里刷
        /// <c>ERROR:mod.auth:Unauthorized access: 未经授权的用户请求</c>（真实反馈）。
        /// 第一次收到 401 时再带上重试，然后记住"这台要带"。
        /// </para>
        /// </summary>
        private static readonly HashSet<string> AuthNeededKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 已经确认不可用的来源（封面接口、歌词路径……）。
        /// <para>
        /// ⚠ <b>404 不算"不可用"</b>：LrcApi 在没有结果时返回的就是 404
        /// （响应体写着 "Lyrics not found."），那是"这一首没有"，不是"这个接口没有"。
        /// 只有 401/403/405/410/5xx 这种接口级失败才记进来。
        /// </para>
        /// </summary>
        private static readonly Dictionary<string, HashSet<string>> DeadSources =
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        private static readonly object StateLock = new object();

        private static volatile bool _tokenDroppedNotice;

        private static string StateKey(Endpoint endpoint) => endpoint.BaseUrl + "|" + (endpoint.Token ?? string.Empty);

        // =====================================================================
        // 找歌词
        // =====================================================================

        /// <summary>
        /// 单曲歌词端点，<b>按顺序试</b>。
        /// <para>
        /// 同一个 LrcAPI 在不同版本 / 部署下暴露的路径不一样：手上两台自建实例，
        /// 一台 <c>/api/v1/lyrics/single</c> 与 <c>/lyrics</c> 都能用，
        /// 另一台两个都返回 404 "Lyrics not found."（它上游没有结果）。
        /// 所以两种都试，谁先给出带时间轴的歌词就用谁。
        /// </para>
        /// </summary>
        private static readonly (string Path, string Label)[] LyricTextPaths =
        {
            ("/api/v1/lyrics/single", "新版单曲接口"),
            ("/lyrics", "旧版 /lyrics")
        };

        /// <summary>
        /// 找歌词：先试各个单曲端点（返回 LRC 文本），都不给带时间轴的版本时，
        /// 再去批量搜索里自己挑一条最好的。
        /// </summary>
        public static async Task<Result<LyricsDocument>> FetchLyricsAsync(
            Endpoint endpoint,
            string? title,
            string? album,
            string? artist,
            CancellationToken cancellationToken = default)
        {
            if (!endpoint.IsConfigured) return Result<LyricsDocument>.Fail(MissingBaseUrlMessage);

            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(artist))
                return Result<LyricsDocument>.Fail("没有曲名也没有歌手，没法搜");

            try
            {
                LyricsDocument? plain = null;
                var authRequired = false;
                string? serverError = null;

                // 每个接口各自什么结果，失败时一起报出来（"哪一步倒下的"才是排查的关键）
                var chain = new List<string>();

                foreach (var (path, label) in LyricTextPaths)
                {
                    if (IsSourceDead(endpoint, path))
                    {
                        chain.Add($"{label} 已确认不可用（不再尝试）");
                        continue;
                    }

                    var url = BuildUrl(endpoint.BaseUrl, path, ("title", title), ("album", album), ("artist", artist));
                    var (text, status, error) = await GetStringAsync(url, endpoint, cancellationToken).ConfigureAwait(true);

                    if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) authRequired = true;

                    serverError ??= error;

                    if (IsSourceUnusable(status)) MarkSourceDead(endpoint, path);

                    if (!LooksLikeLyrics(text))
                    {
                        chain.Add($"{label} {(int)status}");
                        continue;
                    }

                    var document = LyricsDocument.Parse(text, SourceText(label));

                    if (document.IsSynchronized)
                        return Result<LyricsDocument>.Success(document, Note("在线歌词（带时间轴）"));

                    // 只有纯文本：留着，继续碰运气看有没有带轴的版本
                    chain.Add($"{label} 只有纯文本歌词");
                    plain ??= document;
                }

                var hits = await SearchAsync(endpoint, title, album, artist, cancellationToken,
                    note: text => serverError ??= text).ConfigureAwait(true);

                if (hits.Count == 0) chain.Add("批量搜索没有候选");

                var best = PickBest(hits, title, artist);

                if (best != null && LooksLikeLyrics(best.Lyrics))
                {
                    var document = LyricsDocument.Parse(best.Lyrics, SourceText(SafeDetail(best)));

                    if (document.IsSynchronized || plain == null)
                    {
                        return Result<LyricsDocument>.Success(
                            document, Note("在线歌词" + (document.IsSynchronized ? "（带时间轴）" : "（纯文本）")));
                    }
                }

                if (plain != null) return Result<LyricsDocument>.Success(plain, Note("在线歌词（纯文本）"));

                var tail = chain.Count > 0 ? "（试过：" + string.Join(" → ", chain) + "）" : string.Empty;

                if (authRequired) return Result<LyricsDocument>.Fail(DescribeAuth(endpoint) + tail);

                if (serverError != null) return Result<LyricsDocument>.Fail("歌词服务返回错误 " + serverError + tail);

                return Result<LyricsDocument>.Fail(Note("LrcApi 没有找到这首歌的歌词") + tail);

                string Note(string message) => ConsumeTokenDroppedNotice()
                    ? message + "（该服务的鉴权实现有问题：带鉴权会被 500，已自动不带鉴权重试）"
                    : message;
            }
            catch (OperationCanceledException)
            {
                return Result<LyricsDocument>.Fail("已取消");
            }
            catch (Exception ex)
            {
                return Result<LyricsDocument>.Fail("连接歌词服务失败：" + ex.Message);
            }
        }

        // =====================================================================
        // 找封面
        // =====================================================================

        /// <summary>
        /// 找封面。三条路按可用性排过序（都是实测结果）：
        /// <list type="number">
        ///   <item><b>搜索结果里附带的</b> <c>cover</c> 字段——最稳的一条；</item>
        ///   <item>新版封面接口 <c>/api/v1/cover/music|album|artist</c> → JSON 里的 <c>img</c>，
        ///   实测有的实例会 302 到一个拼错的地址再 404；</item>
        ///   <item>旧版 <c>/cover?title=&amp;album=&amp;artist=</c> 直接返回图片字节——
        ///   有的实例要鉴权，填了密钥就能用（实测 858 KB 的 JPEG 正常返回）。</item>
        /// </list>
        /// 任何一步拿回来的东西不像图片（HTML 错误页之类）都会被丢掉，继续试下一条。
        /// 已经试出"这个来源不可用"的会记下来，之后不再白敲一遍（也免得刷对方的未授权日志）。
        /// </summary>
        public static async Task<Result<byte[]>> FetchCoverAsync(
            Endpoint endpoint,
            string? title,
            string? album,
            string? artist,
            CancellationToken cancellationToken = default)
        {
            if (!endpoint.IsConfigured) return Result<byte[]>.Fail(MissingBaseUrlMessage);

            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(album) && string.IsNullOrWhiteSpace(artist))
                return Result<byte[]>.Fail("没有曲名 / 专辑 / 歌手，没法搜封面");

            try
            {
                string? problem = null;
                var authRequired = false;

                // 三条路各自的结局，失败时一起报出来
                var chain = new List<string>();

                void Note(string text) => problem ??= text;

                void CheckAuth(HttpStatusCode status)
                {
                    if (status is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)) return;

                    authRequired = true;
                    Note(DescribeAuth(endpoint));
                }

                // 1) 搜索结果里附带的封面
                var hits = await SearchAsync(endpoint, title, album, artist, cancellationToken,
                    note: text => problem ??= text).ConfigureAwait(true);

                var best = PickBest(hits, title, artist);

                var fromSearch = best?.CoverUrl ??
                                 hits.FirstOrDefault(hit => !string.IsNullOrWhiteSpace(hit.CoverUrl))?.CoverUrl;

                if (string.IsNullOrWhiteSpace(fromSearch))
                {
                    chain.Add(hits.Count == 0 ? "搜索结果为空" : "搜索结果里没有封面地址");
                }
                else
                {
                    var image = await TryGetImageAsync(fromSearch!, endpoint, cancellationToken, Note, CheckAuth)
                        .ConfigureAwait(true);

                    if (LooksLikeImage(image))
                        return Result<byte[]>.Success(image!, "在线封面（来自搜索结果）");

                    chain.Add("搜索结果里的封面下载失败");
                }

                // 2) 新版封面接口
                var attempts = new List<(string Path, (string Name, string? Value)[] Query, string What)>();

                if (!string.IsNullOrWhiteSpace(title))
                {
                    attempts.Add(("/api/v1/cover/music",
                        new (string Name, string? Value)[] { ("title", title), ("album", album), ("artist", artist) },
                        "歌曲封面"));
                }

                if (!string.IsNullOrWhiteSpace(album))
                {
                    attempts.Add(("/api/v1/cover/album",
                        new (string Name, string? Value)[] { ("album", album), ("artist", artist) }, "专辑封面"));
                }

                if (!string.IsNullOrWhiteSpace(artist))
                {
                    attempts.Add(("/api/v1/cover/artist",
                        new (string Name, string? Value)[] { ("artist", artist) }, "歌手头像"));
                }

                if (IsSourceDead(endpoint, ApiCoverSource))
                {
                    chain.Add("新版封面接口 已确认不可用（不再尝试）");
                }
                else
                {
                    foreach (var (path, query, what) in attempts)
                    {
                        var (json, status, error) = await GetStringAsync(
                            BuildUrl(endpoint.BaseUrl, path, query), endpoint, cancellationToken).ConfigureAwait(true);

                        CheckAuth(status);
                        problem ??= error;

                        if (IsSourceUnusable(status)) MarkSourceDead(endpoint, ApiCoverSource);

                        var imageUrl = ParseCoverJson(json);

                        if (imageUrl == null)
                        {
                            chain.Add($"{what}接口 {(int)status}");
                            continue;
                        }

                        var image = await TryGetImageAsync(imageUrl, endpoint, cancellationToken, Note, CheckAuth)
                            .ConfigureAwait(true);

                        if (!LooksLikeImage(image))
                        {
                            chain.Add($"{what}接口给的地址下不下来");
                            continue;
                        }

                        return Result<byte[]>.Success(image!, "在线" + what + "（LrcApi）");
                    }
                }

                // 3) 旧版封面接口：直接给图片字节（有的实例这里正是要鉴权的地方）
                if (IsSourceDead(endpoint, LegacyCoverSource))
                {
                    chain.Add("旧版 /cover 已确认不可用（不再尝试）");
                }
                else
                {
                    var legacyUrl = BuildUrl(endpoint.BaseUrl, "/cover",
                        ("title", title), ("album", album), ("artist", artist));

                    var (legacy, legacyStatus, legacyError) = await GetBytesAsync(legacyUrl, endpoint, cancellationToken)
                        .ConfigureAwait(true);

                    CheckAuth(legacyStatus);
                    problem ??= legacyError;

                    if (IsSourceUnusable(legacyStatus)) MarkSourceDead(endpoint, LegacyCoverSource);

                    if (LooksLikeImage(legacy))
                        return Result<byte[]>.Success(legacy!, "在线封面（旧版接口）");

                    chain.Add($"旧版 /cover {(int)legacyStatus}");
                }

                var tail = chain.Count > 0 ? "（试过：" + string.Join(" → ", chain) + "）" : string.Empty;

                if (authRequired) return Result<byte[]>.Fail(DescribeAuth(endpoint) + tail);

                return Result<byte[]>.Fail((problem ?? "LrcApi 没有找到这张封面") + tail);
            }
            catch (OperationCanceledException)
            {
                return Result<byte[]>.Fail("已取消");
            }
            catch (Exception ex)
            {
                return Result<byte[]>.Fail("连接封面服务失败：" + ex.Message);
            }
        }

        /// <summary>批量搜索：把候选列表拿回来（歌词与封面都可能在里面）。</summary>
        private static async Task<IReadOnlyList<LyricHit>> SearchAsync(
            Endpoint endpoint,
            string? title,
            string? album,
            string? artist,
            CancellationToken cancellationToken,
            Action<string>? note = null)
        {
            var url = BuildUrl(endpoint.BaseUrl, "/api/v1/lyrics/advance",
                ("title", title), ("album", album), ("artist", artist));

            var (json, _, error) = await GetStringAsync(url, endpoint, cancellationToken).ConfigureAwait(true);

            if (error != null) note?.Invoke(error);

            return ParseLyricsJson(json);
        }
    }
}
