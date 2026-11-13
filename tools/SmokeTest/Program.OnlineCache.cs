// 检查点 32：在线歌词 / 封面的磁盘缓存（1.20.0）。
//
// 核心判据只有一条：同一个文件第二次启动**一个请求都不该再发**（改之前每次启动都要
// 重新联网：内存缓存一关程序就没了）。其余几条是围绕它的失败模式：
//   - 缓存文件坏了 → 回退联网并重写，不能把坏文件当歌词用；
//   - 超过上限 → 最旧的先删，不能无限堆；
//   - 「重新获取」→ 旧条目必须当场被忘掉（哪怕新的这次没要到）；
//   - 落盘再读回来 → 时间轴不能丢（存的是生效之后的 LRC 原文）。
//
// 缓存目录跟着 播放器_DATA_DIR（Program 启动时指到 smoke-data），所以这里直接
// 对着 AppContext.BaseDirectory\smoke-data\online-cache 断言，不碰使用者的真实配置。

namespace SmokeTest
{
    internal static partial class Program
    {
        private static string OnlineCacheRoot =>
            Path.Combine(AppContext.BaseDirectory, "smoke-data", "online-cache");

        private static bool CheckOnlineCache()
        {
            var folder = Path.Combine(AppContext.BaseDirectory, "smoke-online-cache");
            TryDeleteDirectory(folder);
            Directory.CreateDirectory(folder);

            var media = Path.Combine(folder, "缓存测试曲目.wav");
            WriteWav(media, seconds: 2, frequency: 440);

            TryDeleteDirectory(OnlineCacheRoot);

            using var server = new FakeLrcApiServer();

            // 每次传一份**新的**标签：ApplyMetadata 之后封面会被写进这个对象，
            // 复用同一份会让下一个窗体的 Decide 以为"本地已经有封面了"（检查 16 踩过同一个坑）。
            MediaTags Tags() => new MediaTags
            {
                Title = "测试歌曲",
                Artist = "测试歌手",
                Album = "测试专辑"
            };

            List<TimeSpan> firstTimes;
            int afterFirst;

            // =================================================================
            // 1) 第一次：联网要一份，并且真的落盘
            // =================================================================
            Section("第一次取");

            using (var form = new 播放器.MainForm(Array.Empty<string>()))
            {
                form.Show();
                PumpMessages(300);
                ConfigureOnlineCacheForm(form, server.BaseUrl);

                form.ApplyMetadata(media, Tags(), LyricsDocument.Empty);

                var lyrics = WaitFor(
                    () => SessionOf(form).Lyrics,
                    document => document is { IsSynchronized: true } &&
                                document.Source.Contains("LrcApi", StringComparison.Ordinal),
                    15);

                if (lyrics == null)
                {
                    Log(32, "在线缓存：第一次就没要到在线歌词（假服务器收到 " + server.RequestCount
                            + " 个请求；状态栏：「" + ReadStatus(form) + "」）");
                    return false;
                }

                var cover = WaitFor(() => SessionOf(form).Tags, value => value is { HasCoverArt: true }, 15);

                if (cover is not { HasCoverArt: true })
                {
                    Log(32, "在线缓存：第一次就没要到在线封面（假服务器收到 " + server.RequestCount + " 个请求）");
                    return false;
                }

                afterFirst = server.RequestCount;
                firstTimes = lyrics.Lines.Select(line => line.Time).ToList();

                if (afterFirst < 2)
                {
                    Log(32, $"在线缓存：第一次只发了 {afterFirst} 个请求（歌词 + 封面至少 2 个）");
                    return false;
                }

                var files = CacheFiles();

                if (files.Length != 2)
                {
                    Log(32, $"在线缓存：第一次之后缓存目录里应该有 2 条，现在是 {files.Length} 条"
                            + $"（{OnlineCacheRoot}）");
                    return false;
                }

                Log(32, $"在线缓存：第一次联网 {afterFirst} 个请求，落盘 2 条"
                        + $"（{files.Sum(file => new FileInfo(file).Length)} 字节）");

                // ---- 菜单：两条入口都该在，清空那条写着条数 ----
                var menu = ((播放器.MainForm)form).MenuOnlineLookup;

                if (menu == null || FindOnlineItem(menu, "重新获取当前曲目") == null)
                {
                    Log(32, "在线缓存：菜单里没有「重新获取当前曲目（忽略缓存）」");
                    return false;
                }

                var clearItem = FindOnlineItem(menu, "清空在线缓存");

                if (clearItem == null)
                {
                    Log(32, "在线缓存：菜单里没有「清空在线缓存」");
                    return false;
                }

                ((播放器.MainForm)form).RefreshOnlineLookupMenu();

                if (!clearItem.Enabled || !clearItem.Text.Contains("2 条", StringComparison.Ordinal))
                {
                    Log(32, $"在线缓存：清空那条菜单不对（可点 {clearItem.Enabled}，文字「{clearItem.Text}」）");
                    return false;
                }
            }

            // =================================================================
            // 2) 第二次启动：歌词 / 封面从磁盘来，一个请求都不发
            // =================================================================
            Section("第二次启动");

            using (var form = new 播放器.MainForm(Array.Empty<string>()))
            {
                form.Show();
                PumpMessages(300);
                ConfigureOnlineCacheForm(form, server.BaseUrl);

                var before = server.RequestCount;

                form.ApplyMetadata(media, Tags(), LyricsDocument.Empty);
                PumpMessages(500);

                var cachedLyrics = SessionOf(form).Lyrics;

                if (cachedLyrics is not { IsSynchronized: true })
                {
                    Log(32, "在线缓存：第二次启动没有从磁盘拿出带时间轴的歌词（状态栏：「"
                            + ReadStatus(form) + "」）");
                    return false;
                }

                if (server.RequestCount != before)
                {
                    Log(32, $"在线缓存：第二次启动还是联网去要了（请求数 {before} → {server.RequestCount}，期望 0 个）");
                    return false;
                }

                if (!SessionOf(form).Tags.HasCoverArt)
                {
                    Log(32, "在线缓存：第二次启动没有从磁盘把封面拿出来");
                    return false;
                }

                // 存的是"生效之后的 LRC 原文"：时间轴逐行往返要一模一样
                var secondTimes = cachedLyrics.Lines.Select(line => line.Time).ToList();

                if (secondTimes.Count != firstTimes.Count ||
                    secondTimes.Where((time, index) => time != firstTimes[index]).Any())
                {
                    Log(32, "在线缓存：从磁盘读回来的时间轴和原来不一样了（"
                            + $"现在 {secondTimes.Count} 行 / 原来 {firstTimes.Count} 行）");
                    return false;
                }

                if (!ReadStatus(form).Contains("本地缓存", StringComparison.Ordinal))
                {
                    Log(32, "在线缓存：用磁盘缓存补上时状态栏没说「本地缓存」（状态栏：「"
                            + ReadStatus(form) + "」）");
                    return false;
                }

                Log(32, "在线缓存：第二次启动 0 个请求，歌词与封面都从磁盘出来，时间轴逐行相同");
            }

            // =================================================================
            // 3) 缓存文件坏掉：回退联网并重写，不能当歌词用
            // =================================================================
            Section("坏文件");

            foreach (var file in CacheFiles()) File.WriteAllText(file, "这不是 JSON");

            using (var form = new 播放器.MainForm(Array.Empty<string>()))
            {
                form.Show();
                PumpMessages(300);
                ConfigureOnlineCacheForm(form, server.BaseUrl);

                var before = server.RequestCount;

                form.ApplyMetadata(media, Tags(), LyricsDocument.Empty);

                var lyrics = WaitFor(
                    () => SessionOf(form).Lyrics,
                    document => document is { IsSynchronized: true } &&
                                document.Source.Contains("LrcApi", StringComparison.Ordinal),
                    15);

                if (lyrics == null)
                {
                    Log(32, "在线缓存：缓存文件坏掉之后没有回退联网（状态栏：「" + ReadStatus(form) + "」）");
                    return false;
                }

                if (server.RequestCount <= before)
                {
                    Log(32, "在线缓存：缓存文件坏掉之后还是把坏文件当歌词用上了（请求数没变）");
                    return false;
                }

                // 封面是另一条请求，得等它回来再核对文件（否则会看到"写到一半"的假红）
                WaitFor(() => SessionOf(form).Tags, value => value is { HasCoverArt: true }, 10);

                var rewritten = PumpUntil(
                    () => CacheFiles().Length == 2 && CacheFiles().All(IsJsonObject),
                    6000);

                if (!rewritten)
                {
                    var bad = CacheFiles().Where(file => !IsJsonObject(file))
                        .Select(Path.GetFileName)
                        .ToArray();

                    Log(32, "在线缓存：坏掉的缓存文件没有被重新写好（" + string.Join(" / ", bad)
                            + $"；状态栏「{ReadStatus(form)}」；请求数 {before} → {server.RequestCount}；"
                            + "路径 " + string.Join(" | ", server.Paths) + "）");
                    return false;
                }

                Log(32, $"在线缓存：坏文件被删掉并重新联网（{before} → {server.RequestCount}），两条都重写好了");

                // =============================================================
                // 4) 「清空在线缓存」：内存 + 磁盘一起清，菜单当场变成"空的"
                // =============================================================
                Section("清空");

                var counted = OnlineCacheOf(form).Count;

                if (counted != 2)
                {
                    Log(32, $"在线缓存：清空之前应该有 2 条，现在是 {counted} 条");
                    return false;
                }

                if (!((播放器.MainForm)form).ClearOnlineCache(confirm: false))
                {
                    Log(32, "在线缓存：清空没有成功");
                    return false;
                }

                if (OnlineCacheOf(form).Count != 0 || CacheFiles().Length != 0)
                {
                    Log(32, $"在线缓存：清空之后还剩 {OnlineCacheOf(form).Count} 条 / "
                            + $"{CacheFiles().Length} 个文件");
                    return false;
                }

                if (!ReadStatus(form).Contains("已清空在线缓存", StringComparison.Ordinal))
                {
                    Log(32, "在线缓存：清空之后状态栏没有说清删了多少（「" + ReadStatus(form) + "」）");
                    return false;
                }

                // ---- 「重新获取」在联网正常时把缓存又写回来 ----
                Section("重新获取");

                var beforeRelookup = server.RequestCount;

                ((播放器.MainForm)form).RelookupCurrentTrackIgnoringCache();

                var relooked = WaitFor(
                    () => SessionOf(form).Lyrics,
                    document => document is { IsSynchronized: true },
                    15);

                if (relooked == null || server.RequestCount <= beforeRelookup)
                {
                    Log(32, "在线缓存：重新获取没有真的去联网（"
                            + $"请求数 {beforeRelookup} → {server.RequestCount}）");
                    return false;
                }

                if (CacheFiles().Length != 2)
                {
                    Log(32, $"在线缓存：重新获取之后缓存没有写回来（现在 {CacheFiles().Length} 条）");
                    return false;
                }

                Log(32, "在线缓存：清空后菜单变成「现在是空的」，重新获取把两条又写回来了");
            }

            // =================================================================
            // 5) 「重新获取」碰上连不上：旧条目必须当场忘掉（不许悄悄留着）
            //    顺便钉住"失败的结果不写缓存"
            // =================================================================
            Section("重新获取失败");

            using (var form = new 播放器.MainForm(Array.Empty<string>()))
            {
                form.Show();
                PumpMessages(300);

                // 先摆好缓存：用正常地址取一次
                ConfigureOnlineCacheForm(form, server.BaseUrl);
                form.ApplyMetadata(media, Tags(), LyricsDocument.Empty);

                var seeded = WaitFor(
                    () => SessionOf(form).Lyrics,
                    document => document is { IsSynchronized: true },
                    15);

                if (seeded == null)
                {
                    Log(32, "在线缓存：铺缓存那一步就没成功（状态栏：「" + ReadStatus(form) + "」）");
                    return false;
                }

                // 指向一个没人监听的端口：连接立刻被拒，失败很快就回来
                SettingsOf(form).LrcApiBaseUrl = "http://127.0.0.1:1";

                ((播放器.MainForm)form).RelookupCurrentTrackIgnoringCache();
                PumpUntil(() => ReadStatus(form).Contains("在线歌词", StringComparison.Ordinal), 8000);

                var left = OnlineCacheOf(form).Count;

                if (left != 0)
                {
                    Log(32, $"在线缓存：点了「重新获取」之后旧缓存还在（{left} 条）——"
                            + "要么没被忘掉，要么失败的结果被写了进去");
                    return false;
                }

                if (!ReadStatus(form).Contains("在线歌词", StringComparison.Ordinal))
                {
                    Log(32, "在线缓存：连不上时没有说清失败原因（状态栏：「" + ReadStatus(form) + "」）");
                    return false;
                }

                // 再切一次同一首：缓存已经没了，不许"假装拿得到"
                form.ApplyMetadata(media, Tags(), LyricsDocument.Empty);
                PumpMessages(400);

                if (SessionOf(form).Lyrics is { IsSynchronized: true } ||
                    OnlineCacheOf(form).Count != 0)
                {
                    Log(32, "在线缓存：连不上之后又切回这一首，界面却拿出了旧歌词");
                    return false;
                }

                Log(32, "在线缓存：重新获取失败时旧条目被清掉，失败结果没有落盘");
            }

            // =================================================================
            // 6) 上限：超过就按最旧的先删（纯 OnlineCache 单测，给一个小上限）
            // =================================================================
            Section("上限");

            var pruneRoot = Path.Combine(folder, "prune-cache");
            var small = new OnlineCache(pruneRoot, maximumBytes: 400);

            for (var i = 0; i < 5; i++)
                small.WriteLyrics(Path.Combine(folder, "prune-" + i + ".flac"),
                    LyricsDocument.Parse($"[00:00.10]第 {i} 句歌词"));

            var kept = Directory.GetFiles(pruneRoot, "*.json", SearchOption.AllDirectories).Length;
            var oldest = small.TryReadLyrics(Path.Combine(folder, "prune-0.flac"), out _);
            var newest = small.TryReadLyrics(Path.Combine(folder, "prune-4.flac"), out _);

            if (kept >= 5 || oldest || !newest)
            {
                Log(32, $"在线缓存：超过上限没有按最旧的先删（现在 {kept} 条；最旧还在 {oldest}、最新还在 {newest}）");
                return false;
            }

            Log(32, $"在线缓存：上限 400 字节时写 5 条只剩 {kept} 条，最旧的被删、最新的还在");

            Log(32, "在线缓存正常：第二次启动 0 请求、坏文件回退、清空 / 重新获取 / 失败不留、上限按最旧先删");
            return true;
        }

        private static bool IsJsonObject(string file)
        {
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
                return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string[] CacheFiles()
        {
            try
            {
                return Directory.Exists(OnlineCacheRoot)
                    ? Directory.GetFiles(OnlineCacheRoot, "*.json", SearchOption.AllDirectories)
                    : Array.Empty<string>();
            }
            catch (Exception)
            {
                return Array.Empty<string>();
            }
        }

        private static ToolStripMenuItem? FindOnlineItem(ToolStripMenuItem menu, string prefix) =>
            menu.DropDownItems
                .OfType<ToolStripMenuItem>()
                .FirstOrDefault(item => item.Text.StartsWith(prefix, StringComparison.Ordinal));

        private static void ConfigureOnlineCacheForm(Form form, string baseUrl)
        {
            var settings = SettingsOf(form);

            settings.OnlineLookup = true;
            settings.LrcApiBaseUrl = baseUrl;
            settings.LrcApiToken = string.Empty;
        }
    }
}
