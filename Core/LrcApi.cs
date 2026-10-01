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
    public static class LrcApi
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
        // 数据
        // =====================================================================

        /// <summary>一个 LrcAPI 服务端点：地址 + 可选的鉴权密钥。</summary>
        public readonly struct Endpoint
        {
            public Endpoint(string? baseUrl, string? token)
            {
                BaseUrl = NormalizeBaseUrl(baseUrl);
                Token = NormalizeToken(token);
            }

            /// <summary>服务地址（已补协议头、去掉尾部斜杠）；空表示还没配。</summary>
            public string BaseUrl { get; }

            /// <summary>鉴权密钥；没填就是 <c>null</c>。</summary>
            public string? Token { get; }

            public bool IsConfigured => BaseUrl.Length > 0;

            public bool HasToken => !string.IsNullOrEmpty(Token);
        }

        /// <summary>批量搜索结果里的一条候选。</summary>
        public sealed class LyricHit
        {
            public LyricHit(string? id, string? title, string? artist, string? lyrics, string? coverUrl = null)
            {
                Id = id ?? string.Empty;
                Title = title ?? string.Empty;
                Artist = artist ?? string.Empty;
                Lyrics = lyrics ?? string.Empty;
                CoverUrl = string.IsNullOrWhiteSpace(coverUrl) ? null : coverUrl.Trim();
            }

            public string Id { get; }
            public string Title { get; }
            public string Artist { get; }
            public string Lyrics { get; }

            /// <summary>
            /// 这条结果附带的封面地址（字段名是 <c>cover</c>）。
            /// <para>
            /// ⚠ 实测：<b>封面优先从这里拿</b>。自建实例上封面接口经常是坏的
            /// （新版那个 302 到一个拼错的官方地址、旧版那个要鉴权甚至一律 401），
            /// 而搜索结果里的这个地址指向各家平台的图片，往往是唯一稳的一条路。
            /// </para>
            /// </summary>
            public string? CoverUrl { get; }

            /// <summary>这条候选是否带时间轴（<c>[mm:ss.xx]</c>）。</summary>
            public bool IsSynchronized => Lyrics.Contains('[') && Lyrics.Contains(']');
        }

        /// <summary>一次查找的结果：拿到的东西 + 一句给人看的话。</summary>
        public sealed class Result<T> where T : class
        {
            private Result(T? value, string message)
            {
                Value = value;
                Message = message;
            }

            public T? Value { get; }

            public string Message { get; }

            public bool Ok => Value != null;

            public static Result<T> Success(T value, string message) => new Result<T>(value, message);

            public static Result<T> Fail(string message) => new Result<T>(null, message);
        }

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

        /// <summary>取一次"带鉴权被服务端 500、已改成不带鉴权重试"的提示（取完即清）。</summary>
        public static bool ConsumeTokenDroppedNotice()
        {
            if (!_tokenDroppedNotice) return false;

            _tokenDroppedNotice = false;
            return true;
        }

        private static bool IsTokenToxic(string key)
        {
            lock (StateLock) return TokenToxicKeys.Contains(key);
        }

        private static void MarkTokenToxic(string key)
        {
            lock (StateLock)
            {
                TokenToxicKeys.Add(key);
                AuthNeededKeys.Remove(key);
            }

            _tokenDroppedNotice = true;
        }

        private static bool IsAuthNeeded(string key)
        {
            lock (StateLock) return AuthNeededKeys.Contains(key);
        }

        private static void MarkAuthNeeded(string key)
        {
            lock (StateLock)
            {
                if (!TokenToxicKeys.Contains(key)) AuthNeededKeys.Add(key);
            }
        }

        /// <summary>这个来源（某条歌词路径 / 某个封面接口）是不是已经确认不可用了。</summary>
        internal static bool IsSourceDead(Endpoint endpoint, string source)
        {
            lock (StateLock)
            {
                return DeadSources.TryGetValue(StateKey(endpoint), out var dead) && dead.Contains(source);
            }
        }

        private static void MarkSourceDead(Endpoint endpoint, string source)
        {
            lock (StateLock)
            {
                var key = StateKey(endpoint);

                if (!DeadSources.TryGetValue(key, out var dead))
                {
                    dead = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    DeadSources[key] = dead;
                }

                dead.Add(source);
            }
        }

        /// <summary>
        /// 接口级失败（说明这个来源本身不可用，而不是这次没搜到）：
        /// 鉴权被拒、方法不允许、已下线、服务端报错。<b>404 不算</b>。
        /// </summary>
        private static bool IsSourceUnusable(HttpStatusCode status) =>
            status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                or HttpStatusCode.MethodNotAllowed or HttpStatusCode.Gone
                || (int)status >= 500;

        // =====================================================================
        // 地址与内容判定（纯函数，冒烟测试直接单测这几条）
        // =====================================================================

        /// <summary>
        /// 拼一个请求地址。地址为空时返回空串（调用方会给出"还没填地址"的提示）。
        /// <para>少了协议头就补 <c>https://</c>；参数为空白的一律不带上。</para>
        /// </summary>
        public static string BuildUrl(string? baseUrl, string path, params (string Name, string? Value)[] query)
        {
            var root = NormalizeBaseUrl(baseUrl);
            if (root.Length == 0) return string.Empty;

            var relative = path.StartsWith('/') ? path : "/" + path;

            var builder = new StringBuilder(root).Append(relative);
            var first = true;

            foreach (var (name, value) in query)
            {
                if (string.IsNullOrWhiteSpace(value)) continue;

                builder.Append(first ? '?' : '&');
                first = false;

                builder.Append(name).Append('=').Append(Uri.EscapeDataString(value.Trim()));
            }

            return builder.ToString();
        }

        /// <summary>
        /// 把用户填的服务地址收拾成可用的形式；没填就返回空串。
        /// <para>这里<b>故意没有默认值</b>：地址由用户自己填，程序不会私自把歌词查询发到某个第三方服务上。</para>
        /// </summary>
        public static string NormalizeBaseUrl(string? baseUrl)
        {
            var text = (baseUrl ?? string.Empty).Trim();

            if (text.Length == 0) return string.Empty;

            if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;

            return text.TrimEnd('/');
        }

        /// <summary>
        /// 填的服务地址看着像不像一个能用的地址。
        /// <para>
        /// 只做最基本的形状检查，但有一点必须挡住：<c>Uri.TryCreate</c> 会把
        /// "https://这不是地址" 也认成一个合法的（国际化域名）绝对地址，
        /// 所以主机名要求是 ASCII 的字母数字 / 点 / 短横线 / 端口那一套。
        /// </para>
        /// </summary>
        public static bool LooksLikeBaseUrl(string? baseUrl)
        {
            var text = NormalizeBaseUrl(baseUrl);

            if (text.Length == 0 || text.Contains(' ') || text.Contains('\t')) return false;

            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return false;

            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;

            if (uri.Host.Length == 0) return false;

            foreach (var ch in uri.Host)
            {
                var ok = char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or ':' or '[' or ']';

                if (!ok) return false;
            }

            return true;
        }

        /// <summary>密钥去掉首尾空白；空串当作没填。</summary>
        public static string? NormalizeToken(string? token)
        {
            var text = (token ?? string.Empty).Trim();

            return text.Length == 0 ? null : text;
        }

        /// <summary>
        /// 鉴权密钥只发给<b>用户自己配的那个服务</b>，绝不发给第三方。
        /// <para>
        /// 这一点很重要：搜索结果里的封面图往往挂在 <c>p*.music.126.net</c> 这类<b>外部主机</b>上，
        /// 图省事把 Authorization 一股脑带上，就等于把用户的密钥交给了第三方。
        /// </para>
        /// </summary>
        public static bool ShouldSendToken(Endpoint endpoint, string? url)
        {
            if (!endpoint.HasToken || string.IsNullOrWhiteSpace(url)) return false;

            if (!Uri.TryCreate(url, UriKind.Absolute, out var target)) return false;
            if (!Uri.TryCreate(endpoint.BaseUrl, UriKind.Absolute, out var server)) return false;

            return target.Port == server.Port &&
                   string.Equals(target.Host, server.Host, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 401 时该怎么说。
        /// <para>
        /// 关键区别：<b>没填密钥</b>当然是叫他去填；<b>已经填了还是 401</b> 就不能再喊"去填密钥"——
        /// 那说明这个接口不认我们的密钥（或者密钥不对、或者它只认网页登录的 Cookie）。
        /// </para>
        /// </summary>
        private static string DescribeAuth(Endpoint endpoint) =>
            endpoint.HasToken
                ? "服务端拒绝了鉴权（HTTP 401）：密钥没被接受——检查密钥是否正确，"
                  + "或者这个接口不认 Authorization 头（有的实例只认网页登录的 Cookie）"
                : AuthRequiredMessage;

        /// <summary>响应看着像不像 LRC 歌词（有字，且不是一段 HTML）。</summary>
        public static bool LooksLikeLyrics(string? text) =>
            !string.IsNullOrWhiteSpace(text) &&
            !text.TrimStart().StartsWith("<", StringComparison.Ordinal);

        /// <summary>字节流看着像不像图片（封面接口有可能重定向到一张错误页）。</summary>
        public static bool LooksLikeImage(byte[]? data)
        {
            if (data is not { Length: > 8 }) return false;

            if (data[0] == 0xFF && data[1] == 0xD8) return true;                                      // JPEG
            if (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47) return true; // PNG
            if (data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46) return true;                   // GIF
            if (data[0] == 0x42 && data[1] == 0x4D) return true;                                      // BMP

            return data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46 &&
                   data[8] == 0x57 && data[9] == 0x45 && data[10] == 0x42 && data[11] == 0x50;        // WEBP
        }

        /// <summary>取图片扩展名对应的 MIME（下载回来的封面要交给 GDI+ 解码）。</summary>
        public static string ImageMimeType(byte[]? data)
        {
            if (data is not { Length: > 4 }) return "image/jpeg";

            if (data[0] == 0x89 && data[1] == 0x50) return "image/png";
            if (data[0] == 0x47 && data[1] == 0x49) return "image/gif";
            if (data[0] == 0x42 && data[1] == 0x4D) return "image/bmp";

            if (data[0] == 0x52 && data[1] == 0x49 && data.Length > 11 && data[8] == 0x57)
                return "image/webp";

            return "image/jpeg";
        }

        /// <summary>把 <c>/lyrics/advance</c> 的 JSON 解析成候选列表；解析不了就返回空列表。</summary>
        public static IReadOnlyList<LyricHit> ParseLyricsJson(string? json)
        {
            var hits = new List<LyricHit>();

            if (string.IsNullOrWhiteSpace(json)) return hits;

            try
            {
                using var document = JsonDocument.Parse(json);

                if (document.RootElement.ValueKind != JsonValueKind.Array) return hits;

                foreach (var element in document.RootElement.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.Object) continue;

                    // 封面字段：搜索结果里叫 cover，封面接口的响应里叫 img，两个都认
                    var cover = ReadString(element, "cover") ?? ReadString(element, "img");

                    hits.Add(new LyricHit(
                        ReadString(element, "id"),
                        ReadString(element, "title"),
                        ReadString(element, "artist"),
                        ReadString(element, "lyrics"),
                        cover));
                }
            }
            catch (JsonException)
            {
                // 对面返回的不是 JSON（例如一段 HTML 错误页）：当作没有候选
                return new List<LyricHit>();
            }

            return hits;
        }

        /// <summary>从封面接口的 JSON 里取出 <c>img</c>（图片地址）；取不到返回 <c>null</c>。</summary>
        public static string? ParseCoverJson(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;

            try
            {
                using var document = JsonDocument.Parse(json);

                if (document.RootElement.ValueKind != JsonValueKind.Object) return null;

                var image = ReadString(document.RootElement, "img");

                return string.IsNullOrWhiteSpace(image) ? null : image.Trim();
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// 从候选里挑一条最像的。
        /// <para>
        /// 打分而不是"取第一条"：批量搜索会把各种翻唱、现场版一起返回，歌手对不上的那条经常排在前面。
        /// 带时间轴的优先——没有时间轴的歌词在桌面歌词上只能退化成纯文本，等于半个废品。
        /// </para>
        /// </summary>
        public static LyricHit? PickBest(IReadOnlyList<LyricHit>? hits, string? title, string? artist)
        {
            if (hits == null || hits.Count == 0) return null;

            LyricHit? best = null;
            var bestScore = int.MinValue;

            foreach (var hit in hits)
            {
                if (string.IsNullOrWhiteSpace(hit.Lyrics)) continue;

                var score = (hit.IsSynchronized ? 40 : 0)
                            + Similarity(hit.Title, title) * 2
                            + Similarity(hit.Artist, artist);

                if (score <= bestScore) continue;

                bestScore = score;
                best = hit;
            }

            return best;
        }

        /// <summary>
        /// 0~20 的粗相似度：完全一样 20、去掉了空格标点之后一样 18、互相包含 12。
        /// <para>不追求精确：接口返回的标题经常带 <c>（Live）</c>、<c>- Remastered</c> 之类的尾巴。</para>
        /// </summary>
        private static int Similarity(string? left, string? right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return 0;

            var a = left.Trim();
            var b = right.Trim();

            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return 20;

            if (Normalize(a) == Normalize(b)) return 18;

            if (a.Contains(b, StringComparison.OrdinalIgnoreCase) ||
                b.Contains(a, StringComparison.OrdinalIgnoreCase))
            {
                return 12;
            }

            return 0;
        }

        /// <summary>去掉空格与常见标点、统一大小写，用来比较"看起来一样"的标题。</summary>
        private static string Normalize(string text)
        {
            var builder = new StringBuilder(text.Length);

            foreach (var ch in text)
            {
                if (char.IsWhiteSpace(ch) || char.IsPunctuation(ch) || char.IsSymbol(ch)) continue;

                builder.Append(char.ToLowerInvariant(ch));
            }

            return builder.ToString();
        }

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

        /// <summary>歌词来源文字（显示在侧栏「媒体信息」里，用户能看出这是网上取的）。</summary>
        private static string SourceText(string? detail) =>
            string.IsNullOrWhiteSpace(detail) ? "LrcApi 在线歌词" : "LrcApi 在线歌词 · " + detail.Trim();

        private static string SafeDetail(LyricHit hit)
        {
            var parts = new[] { hit.Title, hit.Artist }
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToArray();

            return parts.Length == 0 ? string.Empty : string.Join(" - ", parts);
        }

        // =====================================================================
        // 网络
        // =====================================================================

        /// <summary>
        /// 发一次 GET。鉴权策略是<b>先不带、被挑战了再带</b>：
        /// <list type="number">
        ///   <item>默认<b>不带</b> <c>Authorization</c>——很多实例的歌词接口压根不需要鉴权，
        ///   无脑带上只会在服务端日志里刷一片"未授权请求"；</item>
        ///   <item>收到 401/403 时带上重试一次，成功就记住"这台服务要带"；</item>
        ///   <item>带了反而被 5xx 时（服务端鉴权实现有 bug）去掉鉴权再试，并记下这台服务。</item>
        /// </list>
        /// <para>
        /// 鉴权头始终<b>逐请求</b>加、且只发给用户自己配的那台服务（见 <see cref="ShouldSendToken"/>）：
        /// 封面图常挂在第三方主机上，密钥不能跟着跑过去。
        /// </para>
        /// </summary>
        private static async Task<HttpResponseMessage> SendAsync(
            string url, Endpoint endpoint, CancellationToken cancellationToken)
        {
            var key = StateKey(endpoint);
            var canAuthenticate = ShouldSendToken(endpoint, url) && !IsTokenToxic(key);
            var sendToken = canAuthenticate && IsAuthNeeded(key);

            var response = await SendOnceAsync(url, sendToken ? endpoint.Token : null, cancellationToken)
                .ConfigureAwait(true);

            if (sendToken && (int)response.StatusCode >= 500)
            {
                var without = await SendOnceAsync(url, null, cancellationToken).ConfigureAwait(true);

                // ⚠ 只有"去掉鉴权就好了"才能断定是鉴权实现的问题。
                // 去掉鉴权也一样不行（比如这个接口自己就 500），那是接口本身坏了，
                // 不能因此把密钥丢掉——那会连累同一台服务上别的正常接口。实测踩过：
                // 批量搜索 500 被判成"鉴权有毒"，结果封面接口也不带头了、全都 401。
                if (without.IsSuccessStatusCode)
                {
                    response.Dispose();
                    MarkTokenToxic(key);

                    return without;
                }

                without.Dispose();

                return response;
            }

            if (!sendToken && canAuthenticate && IsAuthChallenge(response.StatusCode))
            {
                response.Dispose();

                var retry = await SendOnceAsync(url, endpoint.Token, cancellationToken).ConfigureAwait(true);

                // 带上之后不再被挑战，说明这个服务确实需要鉴权（下次直接带）
                if (!IsAuthChallenge(retry.StatusCode)) MarkAuthNeeded(key);

                return retry;
            }

            return response;
        }

        private static bool IsAuthChallenge(HttpStatusCode status) =>
            status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

        private static async Task<HttpResponseMessage> SendOnceAsync(
            string url, string? token, CancellationToken cancellationToken)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);

            if (!string.IsNullOrEmpty(token))
                request.Headers.TryAddWithoutValidation("Authorization", token);

            try
            {
                return await Client
                    .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                    .ConfigureAwait(true);
            }
            finally
            {
                request.Dispose();
            }
        }

        private static async Task<(string? Text, HttpStatusCode Status, string? Error)> GetStringAsync(
            string url, Endpoint endpoint, CancellationToken cancellationToken)
        {
            using var response = await SendAsync(url, endpoint, cancellationToken).ConfigureAwait(true);

            if (!response.IsSuccessStatusCode)
                return (null, response.StatusCode, await DescribeErrorAsync(response, cancellationToken).ConfigureAwait(true));

            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(true);

            return (text, response.StatusCode, null);
        }

        private static async Task<(byte[]? Data, HttpStatusCode Status, string? Error)> GetBytesAsync(
            string url, Endpoint endpoint, CancellationToken cancellationToken)
        {
            using var response = await SendAsync(url, endpoint, cancellationToken).ConfigureAwait(true);

            if (!response.IsSuccessStatusCode)
                return (null, response.StatusCode, await DescribeErrorAsync(response, cancellationToken).ConfigureAwait(true));

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(true);

            return (bytes, response.StatusCode, null);
        }

        /// <summary>
        /// 服务端出错时说一句人话。<b>5xx 要把响应体也读出来</b>——
        /// 自建实例崩掉时会直接吐一段 Python traceback，
        /// 那一行 <c>TypeError: ...</c> 比"HTTP 500"有用得多。
        /// </summary>
        private static async Task<string?> DescribeErrorAsync(
            HttpResponseMessage response, CancellationToken cancellationToken)
        {
            var status = (int)response.StatusCode;

            if (status < 500) return null;

            try
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(true);

                foreach (var line in body.Split('\n'))
                {
                    var text = line.Trim();

                    if (text.Length == 0) continue;
                    if (text.StartsWith("<", StringComparison.Ordinal)) break;   // HTML 错误页，没必要贴出来

                    if (text.Length > 200) text = text[..200] + "…";

                    return $"{status}（{text}）";
                }
            }
            catch (Exception ex)
            {
// 读不出响应体就只报状态码
                AppLog.Swallowed("读不出响应体就只报状态码", ex);
            }

            return status.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 下一张图，<b>失败不算致命</b>：返回 <c>null</c> 并把原因记进 <paramref name="note"/>。
        /// <para>
        /// 封面可能有好几个候选地址（搜索结果里的、封面接口给的），
        /// 其中一个下不下来（DNS、TLS、对方 403）不该让整条链断掉——换下一个继续试才是对的。
        /// </para>
        /// </summary>
        private static async Task<byte[]?> TryGetImageAsync(
            string url,
            Endpoint endpoint,
            CancellationToken cancellationToken,
            Action<string> note,
            Action<HttpStatusCode> checkAuth)
        {
            try
            {
                var (data, status, error) = await GetBytesAsync(url, endpoint, cancellationToken).ConfigureAwait(true);

                checkAuth(status);

                if (error != null) note(error);

                return data;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                note("封面地址拿到了但下载失败：" + ex.Message);
                return null;
            }
        }

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = Timeout };

            try
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
                client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
            }
            catch (Exception ex)
            {
// 头设置失败不影响使用
                AppLog.Swallowed("头设置失败不影响使用", ex);
            }

            return client;
        }

        /// <summary>小工具：从 JSON 对象里读一个字符串字段（不存在 / 类型不对都返回 <c>null</c>）。</summary>
        private static string? ReadString(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out var value)) return null;

            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.ToString(),
                _ => null
            };
        }
    }
}
