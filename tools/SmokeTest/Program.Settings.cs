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
    }
}
