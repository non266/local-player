// 检查点 8：设置文件 JSON 往返、损坏内容降级。

namespace SmokeTest
{
    internal static partial class Program
    {
        // -----------------------------------------------------------------
        // 4) 设置持久化
        // -----------------------------------------------------------------

        private static bool TestSettingsRoundTrip()
        {
            // 这里刻意写到程序目录而不是 %AppData%：既能验证同一套序列化逻辑，
            // 又不会污染用户真实的设置文件。
            var folder = Path.Combine(AppContext.BaseDirectory, "settings-roundtrip");
            var file = Path.Combine(folder, "settings.json");

            try
            {
                var source = new AppSettings
                {
                    Volume = 42,
                    Muted = true,
                    Rate = 1.5f,
                    RepeatMode = RepeatMode.All,
                    Shuffle = true,
                    AspectRatio = "16:9",
                    ShowPlaylist = false,
                    AlwaysOnTop = true,
                    WindowWidth = 1234,
                    WindowHeight = 567,
                    LastDirectory = @"D:\媒体\无损音乐",
                    LastPlaylist = { @"D:\媒体\测试 视频.mp4", @"D:\媒体\歌曲.flac" },
                    LastPlaylistIndex = 1,
                    OnlineLookup = true,
                    LrcApiBaseUrl = "http://127.0.0.1:28883/",
                    LrcApiToken = "  smoke-token  "
                };

                source.SaveTo(file);

                if (!File.Exists(file))
                {
                    Log(8, "设置文件没有写出来");
                    return false;
                }

                var loaded = AppSettings.LoadFrom(file);

                var problems = new System.Collections.Generic.List<string>();
                if (loaded.Volume != 42) problems.Add("Volume");
                if (!loaded.Muted) problems.Add("Muted");
                if (Math.Abs(loaded.Rate - 1.5f) > 0.001f) problems.Add("Rate");
                if (loaded.RepeatMode != RepeatMode.All) problems.Add("RepeatMode");
                if (!loaded.Shuffle) problems.Add("Shuffle");
                if (loaded.AspectRatio != "16:9") problems.Add("AspectRatio");
                if (loaded.ShowPlaylist) problems.Add("ShowPlaylist");
                if (!loaded.AlwaysOnTop) problems.Add("AlwaysOnTop");
                if (loaded.WindowWidth != 1234 || loaded.WindowHeight != 567) problems.Add("窗口尺寸");
                if (loaded.LastDirectory != @"D:\媒体\无损音乐") problems.Add("中文路径");
                if (loaded.LastPlaylist.Count != 2) problems.Add("播放列表");
                if (loaded.LastPlaylistIndex != 1) problems.Add("LastPlaylistIndex");

                // 在线歌词 / 封面这几项：地址会被"收拾"（去尾斜杠），密钥会被去掉空白
                if (!loaded.OnlineLookup) problems.Add("OnlineLookup");
                if (loaded.LrcApiBaseUrl != "http://127.0.0.1:28883") problems.Add("LrcApiBaseUrl");
                if (loaded.LrcApiToken != "smoke-token") problems.Add("LrcApiToken");

                if (problems.Count > 0)
                {
                    Log(8, "往返后不一致的字段: " + string.Join(", ", problems));
                    return false;
                }

                // 损坏内容必须安全降级为默认值，而不是抛异常。
                var broken = AppSettings.FromJson("{ this is not json }");
                if (broken.Volume != new AppSettings().Volume)
                {
                    Log(8, "损坏的 JSON 没有降级为默认值");
                    return false;
                }

                if (!CheckPlaybackHistory()) return false;
                if (!CheckPerFileAdjustments()) return false;
                if (!CheckSettingsSalvage()) return false;

                Log(8, "设置 JSON 往返正确，损坏内容可安全降级；播放历史与按文件记住的调整都正确");
                return true;
            }
            catch (Exception ex)
            {
                Log(8, "设置往返测试失败: " + ex.Message);
                return false;
            }
            finally
            {
                TryDeleteDirectory(folder);
            }
        }

        /// <summary>
        /// B2：设置文件有一处读不出来时，<b>能救的项要保住</b>，而不是整份清零。
        /// <para>
        /// 两种坏法都要验：① JSON 语法是好的、只是某个字段类型不对（比如把音量写成了字符串）；
        /// ② 语法本身就坏了（截断 / 手改错了）——这时只能救坏点之前的部分，而且要说清
        /// "后面读不下去了"，不能装作完整。
        /// </para>
        /// <para>另外两条不能退化：整份都是垃圾时照旧退回默认值 + 备份；完好文件不该有提示。</para>
        /// </summary>
        private static bool CheckSettingsSalvage()
        {
            var folder = Path.Combine(AppContext.BaseDirectory, "settings-salvage");
            TryDeleteDirectory(folder);
            Directory.CreateDirectory(folder);

            try
            {
                // ---- ① 有一个字段类型不对：其它项必须原样保住 ----
                var file = Path.Combine(folder, "settings.json");

                File.WriteAllText(file,
                    """
                    {
                      "Volume": "大声点",
                      "Theme": "Dark",
                      "WindowWidth": 1234,
                      "LastDirectory": "D:\\媒体\\无损",
                      "AlwaysOnTop": true
                    }
                    """);

                var brokenJson = File.ReadAllText(file);
                var loaded = AppSettings.LoadFrom(file);

                // 直接问一次"能救几项"：这比只看字段值更容易定位（是没救回来，还是救回来又丢了）
                var probe = AppSettings.TrySalvage(brokenJson, out var keptCount, out var lostNames, out _);

                if (probe == null || keptCount < 3)
                {
                    Log(8, "设置容错检查：能救的项没有救回来"
                            + $"（保住 {keptCount} 项、丢了 [{string.Join("、", lostNames)}]）");
                    return false;
                }

                if (loaded.Theme != ThemeId.Dark || loaded.WindowWidth != 1234 ||
                    loaded.LastDirectory != @"D:\媒体\无损" || !loaded.AlwaysOnTop)
                {
                    Log(8, "设置容错检查：一个坏字段把好字段也带走了"
                            + $"（Theme={loaded.Theme}、WindowWidth={loaded.WindowWidth}、"
                            + $"LastDirectory=「{loaded.LastDirectory}」、AlwaysOnTop={loaded.AlwaysOnTop}）");
                    return false;
                }

                if (loaded.Volume != new AppSettings().Volume)
                {
                    Log(8, $"设置容错检查：坏掉的那一项没有退回默认值（Volume={loaded.Volume}）");
                    return false;
                }

                if (loaded.LoadWarning == null || !loaded.LoadWarning.Contains("Volume", StringComparison.Ordinal))
                {
                    Log(8, "设置容错检查：提示里没说清是哪一项用了默认值"
                            + $"（「{loaded.LoadWarning ?? "（没有提示）"}」）");
                    return false;
                }

                if (!File.Exists(file + ".corrupt"))
                {
                    Log(8, "设置容错检查：读不出来时没有把原文件留一份备份");
                    return false;
                }

                // ---- ② 语法坏在中间：坏点之前的项要救出来，之后的不能瞎猜 ----
                var brokenFile = Path.Combine(folder, "broken.json");

                File.WriteAllText(brokenFile,
                    """
                    {
                      "Volume": 42,
                      "WindowWidth": 2222,
                      "Theme": DARK_写错了,
                      "AlwaysOnTop": true
                    }
                    """);

                var salvaged = AppSettings.LoadFrom(brokenFile);

                if (salvaged.Volume != 42 || salvaged.WindowWidth != 2222)
                {
                    Log(8, "设置容错检查：语法坏在中间时，坏点之前的好项没保住"
                            + $"（Volume={salvaged.Volume}、WindowWidth={salvaged.WindowWidth}）");
                    return false;
                }

                if (salvaged.AlwaysOnTop)
                {
                    Log(8, "设置容错检查：坏点之后的项居然被读出来了（不该猜）");
                    return false;
                }

                if (salvaged.LoadWarning == null ||
                    !salvaged.LoadWarning.Contains("读不下去", StringComparison.Ordinal))
                {
                    Log(8, "设置容错检查：语法坏掉时提示没说明白"
                            + $"（「{salvaged.LoadWarning ?? "（没有提示）"}」）");
                    return false;
                }

                // ---- ③ 整份都是垃圾：照旧退回默认值 + 备份（不能因为有了容错就丢掉这条退路）----
                var garbageFile = Path.Combine(folder, "garbage.json");
                File.WriteAllText(garbageFile, "这不是 JSON，一个字都不是");

                var fallback = AppSettings.LoadFrom(garbageFile);

                if (fallback.Volume != new AppSettings().Volume ||
                    fallback.LoadWarning == null ||
                    !fallback.LoadWarning.Contains("已损坏", StringComparison.Ordinal) ||
                    !File.Exists(garbageFile + ".corrupt"))
                {
                    Log(8, "设置容错检查：整份垃圾时没有按原来的退路走（默认值 + 备份 + 「已损坏」提示），"
                            + $"现在是 Volume={fallback.Volume}、提示「{fallback.LoadWarning ?? "（没有）"}」");
                    return false;
                }

                // ---- ④ 完好文件：不该有提示，也不该留下备份 ----
                var goodFile = Path.Combine(folder, "good.json");
                new AppSettings { Volume = 7 }.SaveTo(goodFile);

                var good = AppSettings.LoadFrom(goodFile);

                if (good.Volume != 7 || good.LoadWarning != null || File.Exists(goodFile + ".corrupt"))
                {
                    Log(8, "设置容错检查：完好文件被当成了坏文件"
                            + $"（Volume={good.Volume}、提示「{good.LoadWarning ?? "（没有）"}」）");
                    return false;
                }

                Log(8, "设置容错检查正常：一个字段坏掉时其余项原样保住（并说清是哪一项用了默认值），"
                        + "语法坏在中间时救出坏点之前的部分并说明后面读不下去，"
                        + "整份垃圾照旧退回默认值 + 备份，完好文件不误判");
                return true;
            }
            finally
            {
                TryDeleteDirectory(folder);
            }
        }
    }
}
