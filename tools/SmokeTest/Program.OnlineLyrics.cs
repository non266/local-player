// 检查点 16：在线歌词与封面（LrcApi）。默认打本地假服务器，真接口要显式开。

namespace SmokeTest
{
    internal static partial class Program
    {
        // -----------------------------------------------------------------
        // 16) 在线歌词与封面（LrcApi）
        //
        // 分成三半：
        //   1. 纯函数（URL 组装 / JSON 解析 / 挑最像的一条 / 图片判定）——不联网也钉得死；
        //   2. 服务本身（缺不缺、缓存上限、同一句话只说一次）——UI 无关，也不用联网；
        //   3. 端到端——在本机起一个"假 LrcApi"（TcpListener 说几句 HTTP），
        //      让真窗体按真实路径去要歌词和封面。
        //
        // 之所以自己搭假服务器而不是打真接口：测试不该依赖外网。
        // 真要打真接口（验证对方有没有改协议）时用环境变量开：播放器_TEST_LRCAPI=1
        // -----------------------------------------------------------------

        private static bool TestOnlineLyrics()
        {
            if (!CheckLrcApiPureFunctions()) return false;
            if (!CheckOnlineServicePure()) return false;
            if (!CheckOnlineLookupEndToEnd()) return false;

            var real = Environment.GetEnvironmentVariable("播放器_TEST_LRCAPI");

            if (!string.IsNullOrWhiteSpace(real))
                return CheckRealLrcApi();

            Log(16, "在线歌词：没设 播放器_TEST_LRCAPI，跳过真实接口那一段"
                    + "（本次只验本地假服务器；要打真服务就设成 1 或它的地址）");
            return true;
        }

        /// <summary>
        /// 在线查找服务本身：缺不缺、缓存上限、"同一句话只说一次"。
        /// <para>
        /// 这三样本来看不出对错，但都会在真实使用里咬人：该联网时不联网 / 不该联网时反复联网、
        /// 缓存不封顶（听一晚上把内存堆起来）、同一条失败每切一首歌刷一次状态栏。
        /// 以前它们长在窗体里，只能靠"假服务器数请求数"间接验；服务独立之后可以直接摆出来。
        /// </para>
        /// </summary>
        private static bool CheckOnlineServicePure()
        {
            var service = new OnlineLyricsService(path => path.EndsWith("有外挂封面.flac", StringComparison.Ordinal));

            var tags = new MediaTags { Title = "测试歌曲" };
            var lyrics = LyricsDocument.Parse("[00:00:01.000]第一句", "测试");

            // ---- 缺什么：歌词空 + 没有内嵌封面 + 缓存没有 → 两样都要 ----
            var need = service.Decide("a.flac", tags, LyricsDocument.Empty);

            if (!need.Lyrics || !need.Cover || !need.Any)
            {
                Log(16, $"在线服务：本地什么都没有时反而说不用查（歌词 {need.Lyrics} / 封面 {need.Cover}）");
                return false;
            }

            // 缓存里有歌词 → 歌词不用再查
            service.StoreLyrics("a.flac", lyrics);

            need = service.Decide("a.flac", tags, LyricsDocument.Empty);

            if (need.Lyrics || !need.Cover)
            {
                Log(16, $"在线服务：缓存里已经有歌词了还说要查（歌词 {need.Lyrics} / 封面 {need.Cover}）");
                return false;
            }

            // 有内嵌封面 → 封面不用查
            need = service.Decide("b.flac", new MediaTags { Title = "x", CoverArt = new byte[] { 1, 2, 3 }, CoverMimeType = "image/png" }, LyricsDocument.Empty);

            if (need.Cover)
            {
                Log(16, "在线服务：标签里已经有封面了还说要查");
                return false;
            }

            // 目录里有外挂封面 → 封面不用查（这是窗体告诉它的那条环境事实）
            need = service.Decide("有外挂封面.flac", tags, LyricsDocument.Empty);

            if (need.Cover)
            {
                Log(16, "在线服务：目录里已经有外挂封面了还说要查");
                return false;
            }

            // 本地已经有好歌词 → 两样都不缺
            need = service.Decide("b.flac", new MediaTags { Title = "x", CoverArt = new byte[] { 1 }, CoverMimeType = "image/png" }, lyrics);

            if (need.Any)
            {
                Log(16, "在线服务：本地歌词和封面都有了，还说缺东西");
                return false;
            }

            // ---- 缓存上限：到顶就整体丢掉，别无限堆 ----
            service.Clear();

            for (var i = 0; i < OnlineLyricsService.LyricsCacheLimit; i++)
                service.StoreLyrics($"song{i}.flac", lyrics);

            if (service.LyricsCount != OnlineLyricsService.LyricsCacheLimit)
            {
                Log(16, $"在线服务：歌词缓存条数不对（{service.LyricsCount} / 上限 {OnlineLyricsService.LyricsCacheLimit}）");
                return false;
            }

            service.StoreLyrics("overflow.flac", lyrics);

            if (service.LyricsCount != 1 || !service.HasLyrics("overflow.flac"))
            {
                Log(16, $"在线服务：歌词缓存到上限之后没有清掉旧的（现在 {service.LyricsCount} 条）");
                return false;
            }

            for (var i = 0; i < OnlineLyricsService.CoverCacheLimit; i++)
                service.StoreCover($"song{i}.flac", new byte[] { 1 }, "image/png");

            service.StoreCover("overflow.flac", new byte[] { 2 }, "image/png");

            if (service.CoverCount != 1 || !service.TryGetCover("overflow.flac", out var cover, out var mime) ||
                cover == null || cover.Length != 1 || cover[0] != 2 || mime != "image/png")
            {
                Log(16, $"在线服务：封面缓存到上限之后没有清掉旧的（现在 {service.CoverCount} 条）");
                return false;
            }

            // ---- 同一句话只说一次；有新东西进来之后要重新算 ----
            if (!service.ShouldReport("连不上"))
            {
                Log(16, "在线服务：第一次失败反而不报");
                return false;
            }

            if (service.ShouldReport("连不上"))
            {
                Log(16, "在线服务：同一条失败原因报了两遍（切歌时会刷状态栏）");
                return false;
            }

            // 拿到东西 = 状态变了：同一条原因要重新允许报（否则"没找到"会被永远压住）
            service.StoreLyrics("again.flac", lyrics);

            if (!service.ShouldReport("连不上"))
            {
                Log(16, "在线服务：拿到新东西之后，同一条失败原因就不报了（应当重新算）");
                return false;
            }

            if (!service.ShouldReport("需要鉴权"))
            {
                Log(16, "在线服务：换了一条不一样的原因反而不报");
                return false;
            }

            if (service.ShouldReport("需要鉴权"))
            {
                Log(16, "在线服务：换过一条之后，上一条又被重复报了");
                return false;
            }

            Log(16, "在线服务正常：缺什么算得对（含外挂封面 / 内嵌封面 / 缓存）、缓存到上限会整体清掉、同一条失败只说一次");
            return true;
        }

        /// <summary>LrcApi 客户端里不需要联网的那部分。</summary>
        private static bool CheckLrcApiPureFunctions()
        {
            var url = LrcApi.BuildUrl("api.lrc.cx/", "/api/v1/lyrics/single",
                ("title", "海阔天空"), ("album", null), ("artist", "Beyond"));

            if (url != "https://api.lrc.cx/api/v1/lyrics/single?title=%E6%B5%B7%E9%98%94%E5%A4%A9%E7%A9%BA&artist=Beyond")
            {
                Log(16, "在线歌词检查：URL 组装不对 → " + url);
                return false;
            }

            // 空参数不能留下 "album=&"
            if (LrcApi.BuildUrl("http://x", "/y", ("a", " ")).Contains("a="))
            {
                Log(16, "在线歌词检查：空参数没有被丢掉");
                return false;
            }

            // ---- 服务地址必须自己填：没有地址就什么都不该发出去 ----
            if (LrcApi.NormalizeBaseUrl("  ") != string.Empty ||
                LrcApi.LooksLikeBaseUrl("") ||
                LrcApi.LooksLikeBaseUrl("这不是地址") ||
                LrcApi.BuildUrl("", "/api/v1/lyrics/single", ("title", "x")) != string.Empty)
            {
                Log(16, "在线歌词检查：没填地址时的处理不对（空地址应当拼不出 URL、也不该被当成可用地址）");
                return false;
            }

            if (!LrcApi.LooksLikeBaseUrl("127.0.0.1:28883") ||
                LrcApi.NormalizeBaseUrl("127.0.0.1:28883") != "https://127.0.0.1:28883")
            {
                Log(16, "在线歌词检查：少了协议头的地址没有被补成 https://");
                return false;
            }

            var unconfigured = LrcApi.FetchLyricsAsync(new LrcApi.Endpoint(string.Empty, null), "海阔天空", null, "Beyond")
                .GetAwaiter().GetResult();

            if (unconfigured.Ok || !unconfigured.Message.Contains("地址", StringComparison.Ordinal))
            {
                Log(16, "在线歌词检查：没填服务地址时应当直接失败并提示去填地址（现在是「"
                        + unconfigured.Message + "」）");
                return false;
            }

            // ---- 鉴权密钥：只发给用户自己配的那台服务 ----
            var server = new LrcApi.Endpoint("http://127.0.0.1:28883", "  secret-key  ");

            if (server.Token != "secret-key")
            {
                Log(16, "在线歌词检查：密钥两边的空白没有被去掉（现在 " + (server.Token ?? "null") + "）");
                return false;
            }

            if (!LrcApi.ShouldSendToken(server, "http://127.0.0.1:28883/api/v1/cover/music"))
            {
                Log(16, "在线歌词检查：发给配置的那台服务时应当带上鉴权头");
                return false;
            }

            // 封面图常挂在第三方主机上：密钥绝不能跟着过去
            if (LrcApi.ShouldSendToken(server, "https://p2.music.126.net/abc.jpg") ||
                LrcApi.ShouldSendToken(server, "http://127.0.0.1:28884/cover.png") ||
                LrcApi.ShouldSendToken(new LrcApi.Endpoint("http://127.0.0.1:28883", null),
                    "http://127.0.0.1:28883/api/v1/cover/music"))
            {
                Log(16, "在线歌词检查：鉴权头被发到了不该发的地方（第三方主机 / 别的端口 / 没配密钥）");
                return false;
            }

            if (LrcApi.NormalizeToken("   ") != null || LrcApi.NormalizeToken(null) != null)
            {
                Log(16, "在线歌词检查：空密钥应当当作没填（不能发一个空的 Authorization 头出去）");
                return false;
            }

            const string lyricsJson = """
                [
                  {"id":"1","title":"海阔天空","artist":"Beyond","lyrics":"[00:01.00]今天我","cover":null},
                  {"id":"2","title":"海阔天空（Live）","artist":"别人","lyrics":"纯文本歌词",
                   "cover":"https://example.com/b.jpg"}
                ]
                """;

            var hits = LrcApi.ParseLyricsJson(lyricsJson);

            if (hits.Count != 2 || hits[0].Title != "海阔天空" || !hits[0].IsSynchronized ||
                hits[1].CoverUrl != "https://example.com/b.jpg")
            {
                Log(16, $"在线歌词检查：歌词 JSON 解析不对（{hits.Count} 条，封面字段 "
                        + $"{hits.ElementAtOrDefault(1)?.CoverUrl ?? "null"}）");
                return false;
            }

            // 一段 HTML 错误页不能被当成 JSON 崩掉
            if (LrcApi.ParseLyricsJson("<html>502 Bad Gateway</html>").Count != 0)
            {
                Log(16, "在线歌词检查：HTML 错误页被当成了歌词列表");
                return false;
            }

            var best = LrcApi.PickBest(hits, "海阔天空", "Beyond");

            if (best == null || best.Id != "1")
            {
                Log(16, $"在线歌词检查：挑出来的是「{best?.Title} / {best?.Artist}」，应当是 Beyond 那条带轴的");
                return false;
            }

            if (LrcApi.PickBest(Array.Empty<LrcApi.LyricHit>(), "x", "y") != null)
            {
                Log(16, "在线歌词检查：没有候选时应当返回 null");
                return false;
            }

            // ---- 封面接口的响应（字段名是 img）----
            var cover = LrcApi.ParseCoverJson("""{"title":"海阔天空","img":"https://example.com/a.jpg"}""");

            if (cover != "https://example.com/a.jpg")
            {
                Log(16, "在线歌词检查：封面 JSON 里的 img 没取出来 → " + (cover ?? "null"));
                return false;
            }

            // 搜索结果里的字段名是 cover，解析器两个都得认
            var fromSearch = LrcApi.ParseLyricsJson("""[{"title":"x","lyrics":"[00:01.00]y","cover":"https://e/c.jpg"}]""");

            if (fromSearch.Count != 1 || fromSearch[0].CoverUrl != "https://e/c.jpg")
            {
                Log(16, "在线歌词检查：搜索结果里的 cover 字段没解析出来");
                return false;
            }

            if (LrcApi.ParseCoverJson("""{"error":"not found"}""") != null)
            {
                Log(16, "在线歌词检查：没有 img 的响应应当返回 null");
                return false;
            }

            var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00 };
            var html = Encoding.UTF8.GetBytes("<html>404</html>");

            if (!LrcApi.LooksLikeImage(png) || LrcApi.LooksLikeImage(html) ||
                LrcApi.ImageMimeType(png) != "image/png")
            {
                Log(16, "在线歌词检查：图片判定 / MIME 判定不对");
                return false;
            }

            if (!LrcApi.LooksLikeLyrics("[00:01.00]abc") || LrcApi.LooksLikeLyrics("<html>404</html>"))
            {
                Log(16, "在线歌词检查：歌词文本判定不对");
                return false;
            }

            Log(16, "在线歌词纯函数正常：地址组装与「必须自己填」的判定（空地址直接拒绝、提示去填地址）、"
                    + "鉴权密钥的判定（只发给配置的那台服务，第三方主机 / 别的端口 / 空密钥都不发）、"
                    + "歌词（含搜索结果里的 cover 字段）与封面 JSON 解析、"
                    + "挑最像的一条（带轴的优先、歌手对得上的优先）、HTML 错误页与图片类型判定都对");
            return true;
        }


        /// <summary>
        /// 按某一条"封面来源"重新要一次封面：清掉手里的封面与缓存，再走菜单那条手动路径。
        /// <para>用来把三条路都验一遍（搜索结果里的 cover、新版封面接口、旧版 /cover）。</para>
        /// </summary>
        private static bool CheckCoverSource(
            Form form, FakeLrcApiServer server, string label, Action configure)
        {
            configure();

            OnlineOf(form).ClearCovers();

            if (SessionOf(form).Tags is MediaTags current)
            {
                current.CoverArt = null;
                current.CoverMimeType = null;
            }

            ((播放器.MainForm)form).LookupCurrentTrackOnline(false, true, false);

            var cover = WaitFor(
                () => SessionOf(form).Tags,
                value => value is { HasCoverArt: true },
                15);

            if (cover is not { HasCoverArt: true })
            {
                string paths;

                lock (server.Paths) paths = string.Join(" | ", server.Paths);

                Log(16, $"在线歌词检查：封面走「{label}」这条路时没拿到（请求：{paths}）");
                return false;
            }

            if (!LrcApi.LooksLikeImage(cover.CoverArt))
            {
                Log(16, $"在线歌词检查：封面走「{label}」时拿回来的不是图片");
                return false;
            }

            return true;
        }

        /// <summary>
        /// 可选的：真去打一次真的 LrcAPI。默认不跑，要用时给环境变量：
        /// <code>set 播放器_TEST_LRCAPI=http://127.0.0.1:28883</code>
        /// （值写成 <c>1</c> 就用 <c>http://127.0.0.1:28883</c>——LrcAPI 默认端口。）
        /// 服务端开了鉴权时再给一个 <code>播放器_TEST_LRCAPI_KEY=&lt;密钥&gt;</code>。
        /// </summary>
        private static bool CheckRealLrcApi()
        {
            var configured = Environment.GetEnvironmentVariable("播放器_TEST_LRCAPI");

            var baseUrl = configured != null && configured.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? configured
                : "http://127.0.0.1:28883";

            var token = Environment.GetEnvironmentVariable("播放器_TEST_LRCAPI_KEY");
            var endpoint = new LrcApi.Endpoint(baseUrl, token);

            try
            {
                var lyrics = LrcApi.FetchLyricsAsync(endpoint, "海阔天空", "海阔天空", "Beyond")
                    .GetAwaiter().GetResult();

                var keyText = endpoint.HasToken ? "，带鉴权密钥" : "，没带密钥";

                // "服务端没有这首歌的歌词"是服务端的事，不是客户端的问题：
                // 有的实例上游就是空的（实测有一台对任何曲子都回 404 "Lyrics not found."），
                // 那种情况下这条检查只报告、不算失败。
                // 只有"客户端这一侧的问题"才算失败：连不上、鉴权不被接受。
                // 服务端自己没数据（404 "Lyrics not found."）或者自己报 500，
                // 那是那台服务的事——如实报出来就好，不该让这条检查变红。
                if (lyrics.Ok && lyrics.Value != null)
                {
                    Log(16, $"在线歌词（真接口 {baseUrl}{keyText}）：歌词 {lyrics.Value.Lines.Count} 行"
                            + $"（{(lyrics.Value.IsSynchronized ? "带时间轴" : "纯文本")}，{lyrics.Value.Source}）");
                }
                else if (IsServerSideMiss(lyrics.Message))
                {
                    Log(16, $"在线歌词（真接口 {baseUrl}{keyText}）：服务端没给歌词（{lyrics.Message}）");
                }
                else
                {
                    Log(16, $"在线歌词检查（真接口 {baseUrl}）失败：{lyrics.Message}");
                    return false;
                }

                var cover = LrcApi.FetchCoverAsync(endpoint, "海阔天空", "海阔天空", "Beyond")
                    .GetAwaiter().GetResult();

                if (cover.Ok && cover.Value != null)
                {
                    Log(16, $"在线封面（真接口 {baseUrl}{keyText}）：{cover.Value.Length / 1024} KB"
                            + $"（{cover.Message}）");
                }
                else if (IsServerSideMiss(cover.Message))
                {
                    Log(16, $"在线封面（真接口 {baseUrl}{keyText}）：服务端没给封面（{cover.Message}）");
                }
                else
                {
                    Log(16, $"在线封面检查（真接口 {baseUrl}）失败：{cover.Message}");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                Log(16, "在线歌词检查（真接口）失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 这条失败信息是不是"服务端自己没数据 / 自己报错"（而不是我们这一侧的问题）。
        /// <para>
        /// 客户端的问题只有两种：连不上、鉴权没被接受。其余（没找到、服务端 500）
        /// 都是那台服务的事，不该让"真接口"这条检查变红——它要盯的是我们的协议实现有没有错。
        /// </para>
        /// </summary>
        private static bool IsServerSideMiss(string message) =>
            !message.Contains("连接歌词服务失败", StringComparison.Ordinal) &&
            !message.Contains("连接封面服务失败", StringComparison.Ordinal) &&
            !message.Contains("密钥没被接受", StringComparison.Ordinal) &&
            !message.Contains("需要鉴权", StringComparison.Ordinal);

        /// <summary>读主窗体状态栏的文字（自动查找失败时的提示就写在这里）。</summary>
        private static string ReadStatus(Form form) => ((播放器.MainForm)form).StatusText;
    }
}
