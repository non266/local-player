// 在线歌词与封面 —— 端到端（起本地假服务器，把"搜索 → 歌词 → 封面 → 落盘"整条链路走一遍）。
//
// 从 Program.OnlineLyrics.cs 按 ARCHITECTURE 17.2 的规矩整块搬过来：成员一个字没动。

namespace SmokeTest
{
    internal static partial class Program
    {
        /// <summary>
        /// 端到端：本地假 LrcApi + 真窗体，走"本地没有 → 自动去要 → 落到侧栏与封面页"这条完整链路。
        /// </summary>
        private static bool CheckOnlineLookupEndToEnd()
        {
            var folder = Path.Combine(AppContext.BaseDirectory, "smoke-online");
            TryDeleteDirectory(folder);
            Directory.CreateDirectory(folder);

            var media = Path.Combine(folder, "在线测试曲目.wav");
            WriteWav(media, seconds: 2, frequency: 440);

            using var server = new FakeLrcApiServer();

            try
            {
                // 不给初始文件：让播放列表空着，避免测试曲目播完之后
                // "播放列表已结束" 把当前曲目清掉（那是在线结果被丢掉的头号原因）。
                // 当前曲目由下面的 ApplyMetadata 自己摆好，走的仍是真实那条路。
                using var form = new 播放器.MainForm(Array.Empty<string>());

                form.Show();
                PumpMessages(400);

                var settings = SettingsOf(form);

                settings.OnlineLookup = true;
                settings.LrcApiBaseUrl = server.BaseUrl;


                var tags = new MediaTags { Title = "测试歌曲", Artist = "测试歌手", Album = "测试专辑" };

                // 模拟切到一首"既没有标签歌词、也没有内嵌封面"的曲子。
                // 走 ApplyMetadata 而不是直接调查找函数：验的是"切歌之后会不会自动去要"。
                form.ApplyMetadata(media, tags, LyricsDocument.Empty);

                var lyrics = WaitFor(
                    () => SessionOf(form).Lyrics,
                    document => document is { IsSynchronized: true } &&
                                document.Source.Contains("LrcApi", StringComparison.Ordinal),
                    15);

                if (lyrics == null)
                {
                    var status = form.StatusText;

                    string paths;

                    lock (server.Paths) paths = string.Join(" | ", server.Paths);

                    Log(16, "在线歌词检查：等不到在线歌词（假服务器收到 " + server.RequestCount + " 个请求："
                            + paths + "；状态栏：「" + (status ?? "取不到") + "」）");
                    return false;
                }

                // 旧版 /lyrics 给的是带时间轴的 LRC：应当优先用它（而不是纯文本的单曲接口）
                var firstLine = lyrics.Lines.Count > 0 ? lyrics.Lines[0].Text : string.Empty;

                if (firstLine != "旧版接口第一句" || !lyrics.Source.Contains("旧版", StringComparison.Ordinal))
                {
                    Log(16, "在线歌词检查：旧版接口的带轴歌词没有被优先采用"
                            + $"（{lyrics.Source}，第一句「{firstLine}」）");
                    return false;
                }

                // ---- 侧栏歌词页真的用上了这份歌词 ----
                var sidebar = Find(form, "sidebarPanel");
                var lyricsView = sidebar == null ? null : FindByTypeName(sidebar, "LyricsView");
                var shown = (lyricsView as LyricsView)?.Document;

                if (shown == null || shown.Lines.Count != lyrics.Lines.Count)
                {
                    Log(16, "在线歌词检查：侧栏歌词页没有跟着换成在线歌词");
                    return false;
                }

                // ---- 封面（默认这条路：搜索结果里的 cover 字段）----
                var coverTags = WaitFor(
                    () => SessionOf(form).Tags,
                    value => value is { HasCoverArt: true },
                    15);

                if (coverTags is not { HasCoverArt: true })
                {
                    Log(16, "在线歌词检查：等不到在线封面（假服务器收到 " + server.RequestCount + " 个请求）");
                    return false;
                }

                if (!LrcApi.LooksLikeImage(coverTags.CoverArt))
                {
                    Log(16, "在线歌词检查：拿回来的封面不是图片");
                    return false;
                }

                PumpMessages(300);

                var coverView = sidebar == null ? null : FindByTypeName(sidebar, "CoverView");

                if ((coverView as CoverView)?.Cover is not Image)
                {
                    Log(16, "在线歌词检查：封面拿到了，但侧栏封面页没有解出图来");
                    return false;
                }

                // ---- 说好不写盘的：歌词只在本次运行里用 ----
                var strays = Directory.GetFiles(folder)
                    .Where(file => !string.Equals(file, media, StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                if (strays.Length > 0)
                {
                    Log(16, "在线歌词检查：往媒体目录里写了文件（说好只在本次运行里用的）："
                            + string.Join(" / ", strays.Select(Path.GetFileName)));
                    return false;
                }

                // ---- 缓存：同一首再切回来不该再联网 ----
                var before = server.RequestCount;

                form.ApplyMetadata(media, new MediaTags(), LyricsDocument.Empty);
                PumpMessages(400);

                if (server.RequestCount > before)
                {
                    Log(16, $"在线歌词检查：同一首曲子又去联网要了一遍（请求数 {before} → {server.RequestCount}）");
                    return false;
                }

                var cached = SessionOf(form).Lyrics;

                if (cached is not { IsSynchronized: true } || !cached.Source.Contains("LrcApi", StringComparison.Ordinal))
                {
                    Log(16, "在线歌词检查：缓存里的在线歌词没有被用回来");
                    return false;
                }


                // ---- 换个服务形态：旧版 /lyrics 不可用时，要去批量搜索结果里挑带轴的那一条 ----
                // 先把标签恢复成"正常的一首歌"：上一步的缓存检查用的是空标签，
                // 那样搜出来的关键词会变成文件名（在线测试曲目），
                // 和假服务器里的候选（测试歌曲 / 测试歌手）对不上。
                form.ApplyMetadata(media, tags, LyricsDocument.Empty);
                PumpMessages(300);

                server.LegacyLyrics = false;
                OnlineOf(form).ClearLyrics();

                var manualBefore = server.RequestCount;
                form.LookupCurrentTrackOnline(true, false, false);

                var picked = WaitFor(
                    () => SessionOf(form).Lyrics,
                    document => document is { IsSynchronized: true } &&
                                document.Lines.Count > 0 &&
                                document.Lines[0].Text == "在线第一句",
                    15);

                if (picked == null || !picked.Source.Contains("测试歌手", StringComparison.Ordinal))
                {
                    var statusText = form.StatusText;

                    string pickedPaths;

                    lock (server.Paths) pickedPaths = string.Join(" | ", server.Paths);

                    Log(16, "在线歌词检查：旧版接口不可用时没有从搜索结果里挑出正确的那一条"
                            + $"（{picked?.Source ?? "没拿到"}，第一句「"
                            + (picked is { Lines.Count: > 0 } ? picked.Lines[0].Text : "") + "」；状态栏：「"
                            + (statusText ?? "取不到") + "」；请求：" + pickedPaths + "）");
                    return false;
                }

                if (server.RequestCount <= manualBefore)
                {
                    Log(16, "在线歌词检查：手动获取没有真的去联网");
                    return false;
                }

                // ---- 封面另外两条路：新版封面接口 / 旧版 /cover ----
                if (!CheckCoverSource(form, server, "新版封面接口", () =>
                    {
                        server.CoverInSearch = false;
                        server.CoverApi = true;
                        server.LegacyCover = false;
                    }))
                {
                    return false;
                }

                if (!CheckCoverSource(form, server, "旧版 /cover", () =>
                    {
                        server.CoverInSearch = false;
                        server.CoverApi = false;
                        server.LegacyCover = true;
                    }))
                {
                    return false;
                }

                // ---- 鉴权：服务端要密钥时，不填就该明说"需要鉴权"，填了才拿到 ----
                server.RequireAuth = true;
                server.AuthKey = "smoke-secret";
                server.CoverInSearch = false;      // 只剩需要鉴权的两个封面接口
                server.CoverApi = true;
                server.LegacyCover = true;

                settings.LrcApiToken = string.Empty;

                OnlineOf(form).ClearCovers();

                if (SessionOf(form).Tags is MediaTags noAuthTags)
                {
                    noAuthTags.CoverArt = null;
                    noAuthTags.CoverMimeType = null;
                }

                form.LookupCurrentTrackOnline(false, true, false);
                PumpMessages(600);

                var deniedTags = SessionOf(form).Tags;
                var deniedStatus = form.StatusText;

                if (deniedTags is { HasCoverArt: true })
                {
                    Log(16, "在线歌词检查：服务端要鉴权、没填密钥，却还是拿到了封面");
                    return false;
                }

                if (!deniedStatus.Contains("鉴权", StringComparison.Ordinal))
                {
                    Log(16, "在线歌词检查：需要鉴权时没有提示去填密钥（状态栏：「"
                            + (deniedStatus ?? "取不到") + "」）");
                    return false;
                }

                settings.LrcApiToken = "smoke-secret";

                if (!CheckCoverSource(form, server, "需要鉴权（已填密钥）", () => { }))
                {
                    return false;
                }

                if (server.LastAuthorization != "smoke-secret")
                {
                    Log(16, "在线歌词检查：鉴权密钥没有作为 Authorization 头发出去（收到的是「"
                            + (server.LastAuthorization ?? "空") + "」）");
                    return false;
                }

                // ---- 填了密钥还是 401：不能再喊"去填密钥"（真实反馈踩过这个坑）----
                server.AuthKey = "the-right-key";
                settings.LrcApiToken = "wrong-key";

                OnlineOf(form).ClearCovers();

                if (SessionOf(form).Tags is MediaTags wrongKeyTags)
                {
                    wrongKeyTags.CoverArt = null;
                    wrongKeyTags.CoverMimeType = null;
                }

                form.LookupCurrentTrackOnline(false, true, false);
                PumpMessages(800);

                var wrongKeyStatus = ReadStatus(form);

                if (wrongKeyStatus.Contains("填上密钥", StringComparison.Ordinal))
                {
                    Log(16, "在线歌词检查：密钥已经填了却还在提示「去填密钥」（状态栏：「"
                            + wrongKeyStatus + "」）");
                    return false;
                }

                if (!wrongKeyStatus.Contains("密钥没被接受", StringComparison.Ordinal))
                {
                    Log(16, "在线歌词检查：填的密钥被拒绝时没有说清楚（状态栏：「" + wrongKeyStatus + "」）");
                    return false;
                }

                settings.LrcApiToken = string.Empty;
                server.AuthKey = "smoke-secret";

                // ---- 服务端不要求鉴权时，别主动把头塞过去（免得对方日志里刷"未授权请求"）----
                server.RequireAuth = false;
                server.LastAuthorization = null;

                var lazy = LrcApi
                    .FetchLyricsAsync(new LrcApi.Endpoint(server.BaseUrl, "never-challenged-key"), "测试歌曲", "测试专辑", "测试歌手")
                    .GetAwaiter().GetResult();

                if (!lazy.Ok)
                {
                    Log(16, "在线歌词检查：服务端不需要鉴权时反而拿不到歌词了（" + lazy.Message + "）");
                    return false;
                }

                if (server.LastAuthorization != null)
                {
                    Log(16, "在线歌词检查：服务端没要求鉴权，却主动把密钥发了出去（收到「"
                            + server.LastAuthorization + "」）");
                    return false;
                }

                // ---- 已经确认不可用的封面接口不再重复敲：免得在服务端日志里刷"未授权请求" ----
                settings.LrcApiToken = "wrong-key";     // 和上面同一个"端点|密钥"，两个封面来源已记为不可用
                server.CoverInSearch = false;

                OnlineOf(form).ClearCovers();

                if (SessionOf(form).Tags is MediaTags deadTags)
                {
                    deadTags.CoverArt = null;
                    deadTags.CoverMimeType = null;
                }

                int pathIndex;

                lock (server.Paths) pathIndex = server.Paths.Count;

                form.LookupCurrentTrackOnline(false, true, false);
                PumpMessages(700);

                string[] newPaths;

                lock (server.Paths) newPaths = server.Paths.Skip(pathIndex).ToArray();

                var coverHits = newPaths.Count(path => path.Contains("/cover", StringComparison.OrdinalIgnoreCase));

                if (coverHits > 0)
                {
                    Log(16, $"在线歌词检查：已经确认不可用的封面接口又被敲了 {coverHits} 次（"
                            + string.Join(" | ", newPaths) + "）");
                    return false;
                }

                var deadStatus = ReadStatus(form);

                if (!deadStatus.Contains("不再尝试", StringComparison.Ordinal))
                {
                    Log(16, "在线歌词检查：跳过已确认不可用的封面接口时没有说明（状态栏：「"
                            + deadStatus + "」）");
                    return false;
                }

                settings.LrcApiToken = string.Empty;

                // ---- 接口自己坏掉（带不带密钥都 500）时，密钥不能被判成"有毒" ----
                // 实测踩过：批量搜索 500 被当成"鉴权实现有问题"，于是整台服务都不再带密钥，
                // 结果本来能用的封面接口也跟着全 401（对着真实自建实例跑才发现的）。
                server.RequireAuth = true;
                server.AuthKey = "keep-key";
                server.AlwaysFailLyrics = true;
                server.CoverInSearch = true;
                server.CoverApi = true;
                server.LegacyCover = false;

                var keepEndpoint = new LrcApi.Endpoint(server.BaseUrl, "keep-key");

                var brokenAdvance = LrcApi.FetchLyricsAsync(keepEndpoint, "测试歌曲", "测试专辑", "测试歌手")
                    .GetAwaiter().GetResult();

                if (brokenAdvance.Ok)
                {
                    Log(16, "在线歌词检查：服务端批量搜索 500，却还是拿到了歌词（假服务器配错了？）");
                    return false;
                }

                if (!brokenAdvance.Message.Contains("500", StringComparison.Ordinal))
                {
                    Log(16, "在线歌词检查：接口自己 500 时没有把状态码报出来（" + brokenAdvance.Message + "）");
                    return false;
                }

                if (brokenAdvance.Message.Contains("鉴权实现", StringComparison.Ordinal))
                {
                    Log(16, "在线歌词检查：接口自己 500 被误判成了「鉴权实现有问题」（" + brokenAdvance.Message + "）");
                    return false;
                }

                server.LastAuthorization = null;

                var stillAuthorized = LrcApi.FetchCoverAsync(keepEndpoint, "测试歌曲", "测试专辑", "测试歌手")
                    .GetAwaiter().GetResult();

                if (!stillAuthorized.Ok)
                {
                    Log(16, "在线歌词检查：一个接口 500 之后，别的接口也拿不到东西了（" + stillAuthorized.Message + "）");
                    return false;
                }

                if (server.LastAuthorization != "keep-key")
                {
                    Log(16, "在线歌词检查：接口自己 500 之后，密钥被错误地丢掉了（封面请求里带的是「"
                            + (server.LastAuthorization ?? "空") + "」）");
                    return false;
                }

                server.AlwaysFailLyrics = false;
                server.RequireAuth = false;
                server.CoverApi = false;

                // ---- 服务端自己报错（实测那台 Python 实例抛 TypeError）：必须说出来，不能静默 ----
                server.RequireAuth = false;
                server.AlwaysFailLyrics = true;
                server.CoverInSearch = false;
                server.CoverApi = false;
                server.LegacyCover = false;

                // 放一张本地 cover.jpg：这样自动查找只缺歌词，状态栏不会被封面的结果盖掉
                File.WriteAllBytes(Path.Combine(folder, "cover.jpg"), BuildJpeg());

                settings.OnlineLookup = true;
                settings.LrcApiToken = string.Empty;

                OnlineOf(form).Clear();

                form.ApplyMetadata(media, tags, LyricsDocument.Empty);
                PumpMessages(1000);

                var autoStatus = ReadStatus(form);

                if (!autoStatus.Contains("500", StringComparison.Ordinal) ||
                    !autoStatus.Contains("TypeError", StringComparison.Ordinal))
                {
                    Log(16, "在线歌词检查：自动获取失败时没有把服务端的错误说出来（状态栏：「"
                            + autoStatus + "」）");
                    return false;
                }

                // ---- 鉴权实现有问题的服务：带鉴权 500 时，自动去掉鉴权重试一次 ----
                server.AlwaysFailLyrics = false;
                server.BrokenOnAuth = true;

                var recovered = LrcApi
                    .FetchLyricsAsync(new LrcApi.Endpoint(server.BaseUrl, "smoke-secret"), "测试歌曲", "测试专辑", "测试歌手")
                    .GetAwaiter().GetResult();

                if (!recovered.Ok || recovered.Value == null)
                {
                    Log(16, "在线歌词检查：服务端一收到鉴权头就 500 时，没有去掉鉴权重试（"
                            + recovered.Message + "）");
                    return false;
                }

                if (!recovered.Message.Contains("鉴权实现", StringComparison.Ordinal))
                {
                    Log(16, "在线歌词检查：带鉴权被 500、去掉鉴权才好的情况没有说明（"
                            + recovered.Message + "）");
                    return false;
                }

                server.BrokenOnAuth = false;

                // ---- 自动关掉之后，切歌不该再联网 ----
                settings.OnlineLookup = false;

                OnlineOf(form).Clear();

                var autoOffBefore = server.RequestCount;

                form.ApplyMetadata(media, tags, LyricsDocument.Empty);
                PumpMessages(400);

                if (server.RequestCount > autoOffBefore)
                {
                    Log(16, "在线歌词检查：自动获取已经关掉，切歌却还在联网");
                    return false;
                }

                // ---- 没填服务地址时：什么都不该发出去 ----
                settings.OnlineLookup = true;
                settings.LrcApiBaseUrl = string.Empty;
                OnlineOf(form).Clear();

                var noAddressBefore = server.RequestCount;

                form.ApplyMetadata(media, tags, LyricsDocument.Empty);
                PumpMessages(400);

                if (server.RequestCount > noAddressBefore)
                {
                    Log(16, "在线歌词检查：还没填服务地址就发出请求了");
                    return false;
                }

                // ---- 视频不查歌词与封面：自动不查、手动也拦下来 ----
                settings.OnlineLookup = true;
                settings.LrcApiBaseUrl = server.BaseUrl;

                OnlineOf(form).Clear();

                // 对照组：同一条流程换成音频文件是会联网的。
                // 没有这个对照，"视频没联网"就可能是别的原因（缓存、开关、地址）造成的假通过。
                var audioBefore = server.RequestCount;

                form.ApplyMetadata(media, tags, LyricsDocument.Empty);
                PumpMessages(400);

                if (server.RequestCount <= audioBefore)
                {
                    Log(16, "在线歌词检查：对照组失效——音频文件都没去联网，视频那一条就说明不了问题");
                    return false;
                }

                var video = Path.Combine(folder, "电影.2019.1080p.mp4");
                File.WriteAllBytes(video, Encoding.UTF8.GetBytes("不是真的视频，只要有这个文件就行"));

                var videoBefore = server.RequestCount;

                form.ApplyMetadata(video, new MediaTags(), LyricsDocument.Empty);
                PumpMessages(500);

                if (server.RequestCount > videoBefore)
                {
                    Log(16, $"在线歌词检查：视频也去要歌词 / 封面了（请求数 {videoBefore} → {server.RequestCount}）");
                    return false;
                }

                if (form.MenuOnlineLookup is not { } onlineMenu)
                {
                    Log(16, "在线歌词检查：找不到「在线歌词与封面」菜单");
                    return false;
                }

                form.RefreshOnlineLookupMenu();

                var onlineItems = onlineMenu.DropDownItems.OfType<ToolStripMenuItem>().ToList();

                var autoItem = onlineItems.FirstOrDefault(i => i.Text.StartsWith("自动获取", StringComparison.Ordinal));
                var manualItems = onlineItems
                    .Where(i => i.Text.Contains("当前曲目", StringComparison.Ordinal))
                    .ToList();

                if (autoItem == null || !autoItem.Text.Contains("视频不查", StringComparison.Ordinal))
                {
                    Log(16, $"在线歌词检查：视频时菜单没有说明「不查歌词与封面」（「{autoItem?.Text}」）");
                    return false;
                }

                if (manualItems.Count != 2 || manualItems.Any(i => i.Enabled))
                {
                    Log(16, "在线歌词检查：视频时手动获取的两项还点得动（点下去只会捞回一首同名的歌）");
                    return false;
                }

                // 手动那条路也要拦住，并说清楚为什么
                var manualVideoBefore = server.RequestCount;

                form.LookupCurrentTrackOnline(true, true, false);
                PumpMessages(300);

                if (server.RequestCount > manualVideoBefore)
                {
                    Log(16, "在线歌词检查：手动点「获取歌词 / 封面」时视频还是联网了");
                    return false;
                }

                if (ReadStatus(form).IndexOf("视频", StringComparison.Ordinal) < 0)
                {
                    Log(16, $"在线歌词检查：视频时手动获取没有说明原因（状态栏：「{ReadStatus(form)}」）");
                    return false;
                }

                // 换回音频：这两项要恢复可点（别把"视频"这个状态粘住了）
                form.ApplyMetadata(media, tags, LyricsDocument.Empty);
                PumpMessages(300);
                form.RefreshOnlineLookupMenu();

                if (manualItems.Any(i => !i.Enabled))
                {
                    Log(16, "在线歌词检查：换回音频之后手动获取还是灰的");
                    return false;
                }

                // 这一段把自动获取又打开了，收尾恢复成"不联网"：
                // 后面几步会开好几个主窗体，留着自动获取 + 已经关掉的假服务器地址，
                // 只会让它们每次载入曲目都白等一次连接失败
                settings.OnlineLookup = false;
                settings.LrcApiBaseUrl = string.Empty;

                // ---- 失败路径：服务根本不在，不能崩、不能卡 ----
                settings.LrcApiBaseUrl = "http://127.0.0.1:9";     // 9 = discard，本地必然没人听

                var failure = LrcApi.FetchLyricsAsync(
                        new LrcApi.Endpoint("http://127.0.0.1:9", null), "测试歌曲", null, "测试歌手")
                    .GetAwaiter().GetResult();

                if (failure.Ok || string.IsNullOrWhiteSpace(failure.Message))
                {
                    Log(16, "在线歌词检查：服务连不上时应当返回失败并说明原因");
                    return false;
                }

                form.Close();
                PumpMessages(200);

                Log(16, $"在线歌词与封面正常：假服务器共收到 {server.RequestCount} 个请求；"
                        + "旧版 /lyrics 与新版单曲接口都会试、只有纯文本时会去批量里挑带时间轴且歌手对得上的、"
                        + "封面按「搜索结果 cover 字段 → 新版封面接口 → 旧版 /cover」三条路依次回退、"
                        + "服务端要鉴权时不填密钥会明说「需要鉴权」、填了才拿得到封面、"
                        + "服务端不需要鉴权时不会主动把头塞过去、"
                        + "已经确认不可用的封面接口不再重复敲（不再刷对方的未授权日志）、"
                        + "填了密钥还被拒时不会再喊「去填密钥」而是说「密钥没被接受」、"
                        + "服务端自己报错（500 + Python 报错文本）时会**把错误说出来**而不是静默、"
                        + "遇到「带鉴权就崩」的服务会自动去掉鉴权重试并说明、"
                        + "歌词与封面都落到侧栏、同一首不重复联网、自动关掉就不联网、"
                        + "没填服务地址时不发任何请求、连不上时安静失败、"
                        + "视频不查歌词与封面（自动与手动都不查，菜单写明原因，换回音频立刻恢复），"
                        + "且没往媒体目录写任何文件");
                return true;
            }
            catch (Exception ex)
            {
                Log(16, "在线歌词检查失败: " + ex);
                return false;
            }
            finally
            {
                TryDeleteDirectory(folder);
            }
        }
    }
}
