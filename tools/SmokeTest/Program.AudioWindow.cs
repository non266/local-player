// 检查点 11：1.9.0 F11 —— 音频窗口 / 视频窗口。
//
// 规矩（判据都写在 docs/1.9.0计划.md 的 2.6）：
//   ① 纯函数 Resolve / Normalize；② 音频窗口的布局；③ 两种窗口各记各的几何、来回切不丢；
//   ④ auto 按扩展名自动切；⑤ 音频窗口里进不了全屏；⑥ 画面右键菜单不弹；⑦ 偏好是 audio 时启动就是音频窗口。
//
// 刻意不播媒体：形态只看路径的扩展名（WindowModes.Resolve），所以不需要真解码——
// 这一段跑得快，也不会和别的检查抢媒体。

namespace SmokeTest
{
    internal static partial class Program
    {
        private static bool CheckAudioWindow()
        {
            if (!CheckWindowModeRules()) return false;
            return CheckAudioWindowLayoutAndGeometry();
        }

        /// <summary>纯函数：三个偏好 × 几类路径，加上"认不出来的值归成 auto"。</summary>
        private static bool CheckWindowModeRules()
        {
            const string audio = @"C:\media\song.flac";
            const string video = @"C:\media\movie.mkv";
            const string stream = "https://example.com/live.m3u8";

            if (WindowModes.Resolve(WindowModes.Auto, audio) != WindowShape.Audio)
            {
                Log(11, "音频窗口：auto 下 FLAC 没有被判成音频窗口");
                return false;
            }

            if (WindowModes.Resolve(WindowModes.Auto, video) != WindowShape.Video)
            {
                Log(11, "音频窗口：auto 下 MKV 没有被判成视频窗口");
                return false;
            }

            if (WindowModes.Resolve(WindowModes.Auto, stream) != WindowShape.Video ||
                WindowModes.Resolve(WindowModes.Auto, null) != WindowShape.Video)
            {
                Log(11, "音频窗口：auto 下串流 / 空路径应该按视频窗口（没有稳定扩展名可判）");
                return false;
            }

            if (WindowModes.Resolve(WindowModes.Audio, video) != WindowShape.Audio)
            {
                Log(11, "音频窗口：「总是音频窗口」下放视频没有进音频窗口（只听声音那条规则没生效）");
                return false;
            }

            if (WindowModes.Resolve(WindowModes.Video, audio) != WindowShape.Video)
            {
                Log(11, "音频窗口：「总是视频窗口」下放音乐还是进了音频窗口");
                return false;
            }

            foreach (var raw in new[] { null, string.Empty, "   ", "weird", "AUDIO", " audio " })
            {
                var normalized = WindowModes.Normalize(raw);

                if (raw != null && raw.Trim().Equals(WindowModes.Audio, StringComparison.OrdinalIgnoreCase))
                {
                    if (normalized != WindowModes.Audio)
                    {
                        Log(11, $"音频窗口：「{raw}」没有被归一成 audio");
                        return false;
                    }

                    continue;
                }

                if (normalized != WindowModes.Auto)
                {
                    Log(11, $"音频窗口：认不出来的值「{raw}」没有归成 auto（现在 {normalized}）");
                    return false;
                }
            }

            Log(11, "窗口模式的判定规则正确：auto 按扩展名、串流与空路径算视频、认不出来的值归 auto");
            return true;
        }

        private static bool CheckAudioWindowLayoutAndGeometry()
        {
            var defaults = AppSettings.Load();
            var audioFile = Path.Combine(AppContext.BaseDirectory, "audio-window.wav");
            WriteWav(audioFile, seconds: 2, frequency: 440);

            var videoSetup = TryCreateTinyVideo(out var videoFile, out var videoReason);

            try
            {
                // 这次从"偏好 auto、视频窗口"开始，后面的数字才好比。
                // 注意别拿 defaults 来改：那是"验完要还原"的快照，改它等于把还原值也改了。
                var setup = AppSettings.Load();
                setup.WindowMode = WindowModes.Auto;
                setup.Save();

                var videoWidth = defaults.WindowWidth;
                var videoHeight = defaults.WindowHeight;

                using (var form = new 播放器.MainForm(Array.Empty<string>()))
                {
                    form.Show();
                    PumpMessages(400);

                    if (form.IsAudioWindow)
                    {
                        Log(11, "音频窗口：偏好是 auto、没有媒体，启动却进了音频窗口");
                        return false;
                    }

                    // 进音频窗口之前的矩形："切回来必须一模一样"的基准
                    var videoBounds = form.Bounds;

                    // ---- ② 布局 ----
                    form.SetWindowMode(WindowModes.Audio);
                    PumpMessages(400);

                    if (!form.IsAudioWindow)
                    {
                        Log(11, "音频窗口：设成「总是音频窗口」之后没有进音频窗口");
                        return false;
                    }

                    var splitMain = Find(form, "splitMain") as SplitContainer;
                    var splitVideo = Find(form, "splitVideo") as SplitContainer;
                    var transport = Find(form, "pnlTransport");
                    var toolbar = Find(form, "toolStripMain");
                    var nowPlaying = FindByTypeName(form, "NowPlayingView");

                    if (splitMain == null || splitVideo == null || transport == null || toolbar == null)
                    {
                        Log(11, "音频窗口：找不到 splitMain / splitVideo / 传输条 / 工具栏");
                        return false;
                    }

                    if (nowPlaying == null || !nowPlaying.Visible)
                    {
                        Log(11, "音频窗口：那张「正在播放」卡片没建出来或没显示");
                        return false;
                    }

                    // 卡片要正好盖住画面区域（不然会露出黑底 / 露出半边画面）：
                    // 它从 (0,0) 起、铺满整宽，下边界贴着传输条的上沿
                    if (nowPlaying.Left != 0 || nowPlaying.Top != 0 ||
                        nowPlaying.Width != splitVideo.Panel1.ClientSize.Width ||
                        nowPlaying.Bottom != transport.Top)
                    {
                        Log(11, $"音频窗口：卡片没有盖满画面区域（卡片 {nowPlaying.Bounds}，"
                                + $"画面宽 {splitVideo.Panel1.ClientSize.Width}，传输条上沿 {transport.Top}）");
                        return false;
                    }

                    if (!transport.Visible)
                    {
                        Log(11, "音频窗口：传输条（进度条 / 音量）被一起收起来了——它该留着");
                        return false;
                    }

                    if (toolbar.Visible)
                    {
                        Log(11, "音频窗口：工具栏还露着（简化窗口不该有那一排按钮）");
                        return false;
                    }

                    if (!splitMain.Panel2Collapsed || !splitVideo.Panel2Collapsed)
                    {
                        Log(11, "音频窗口：播放列表或侧栏还开着（要它们请切回视频窗口）");
                        return false;
                    }

                    var expectedAudioWidth = (int)Math.Round(420 * form.DeviceDpi / 96.0);
                    var expectedAudioHeight = (int)Math.Round(650 * form.DeviceDpi / 96.0);

                    if (Math.Abs(form.Width - expectedAudioWidth) > 2 ||
                        Math.Abs(form.Height - expectedAudioHeight) > 2)
                    {
                        Log(11, $"音频窗口：尺寸没换成简化窗口（现在 {form.Width}×{form.Height}，"
                                + $"期望 {expectedAudioWidth}×{expectedAudioHeight}）");
                        return false;
                    }

                    // 属于视频窗口的那几项这时是灰的
                    var showPlaylist = FindViewMenuItem(form, "显示播放列表");
                    var fullscreen = FindViewMenuItem(form, "全屏");

                    if (showPlaylist == null || fullscreen == null)
                    {
                        Log(11, "音频窗口：「视图」菜单里找不到「显示播放列表」或「全屏」");
                        return false;
                    }

                    if (showPlaylist.Enabled || fullscreen.Enabled)
                    {
                        Log(11, "音频窗口：播放列表 / 全屏那两项还是能点的（该置灰并说明切回去才能用）");
                        return false;
                    }

                    // ---- ⑥ 画面右键菜单不弹：光标落在卡片上，不再算"在画面上" ----
                    if (form.IsScreenPointOnVideo(form.PointToScreen(new Point(form.ClientSize.Width / 2, 80))))
                    {
                        Log(11, "音频窗口：卡片上的点被认成了「在画面上」（右键菜单 / 双击全屏会误弹）");
                        return false;
                    }

                    // ---- ⑤ 全屏被挡 ----
                    form.ProcessShortcut(Keys.F11);
                    PumpMessages(250);

                    if (form.IsFullscreen)
                    {
                        Log(11, "音频窗口：按 F11 还是进了全屏（卡片没有满屏画面可言）");
                        return false;
                    }

                    if (!form.StatusText.Contains("音频窗口没有全屏", StringComparison.Ordinal))
                    {
                        Log(11, "音频窗口：按 F11 被挡住了，但状态栏没说原因（用户会以为坏了）");
                        return false;
                    }

                    // ---- ③ 几何：先在同一次运行里来回切，两种窗口各记各的 ----
                    form.Size = new Size(form.Width + 40, form.Height + 30);
                    PumpMessages(250);

                    form.SetWindowMode(WindowModes.Video);
                    PumpMessages(400);

                    if (form.IsAudioWindow)
                    {
                        Log(11, "音频窗口：切回视频窗口之后还是音频窗口");
                        return false;
                    }

                    if (form.Bounds != videoBounds)
                    {
                        Log(11, $"音频窗口：切回视频窗口之后矩形没还原（现在 {form.Bounds}，"
                                + $"进音频窗口之前是 {videoBounds}）");
                        return false;
                    }

                    if (nowPlaying.Visible)
                    {
                        Log(11, "音频窗口：切回视频窗口之后卡片还盖在画面上");
                        return false;
                    }

                    if (!toolbar.Visible)
                    {
                        Log(11, "音频窗口：切回视频窗口之后工具栏没有回来");
                        return false;
                    }

                    // 再进一次，改个大小，关窗：这一份几何该记在 AudioWindow* 里
                    form.SetWindowMode(WindowModes.Audio);
                    PumpMessages(400);

                    if (!form.IsAudioWindow)
                    {
                        Log(11, "音频窗口：第二次设成音频窗口没有生效");
                        return false;
                    }

                    form.Size = new Size(form.Width + 40, form.Height + 30);
                    PumpMessages(250);

                    var audioBounds = form.Bounds;

                    form.Close();
                    PumpMessages(250);

                    var saved = AppSettings.Load();

                    if (saved.WindowMode != WindowModes.Audio)
                    {
                        Log(11, $"音频窗口：关窗时窗口模式没写进设置（写出来 {saved.WindowMode}）");
                        return false;
                    }

                    if (saved.AudioWindowWidth != audioBounds.Width ||
                        saved.AudioWindowHeight != audioBounds.Height ||
                        saved.AudioWindowX != audioBounds.X || saved.AudioWindowY != audioBounds.Y)
                    {
                        Log(11, "音频窗口：音频窗口的位置 / 大小没写进设置"
                                + $"（界面 {audioBounds}；设置 {saved.AudioWindowX},{saved.AudioWindowY} "
                                + $"{saved.AudioWindowWidth}×{saved.AudioWindowHeight}）");
                        return false;
                    }

                    if (saved.WindowWidth != videoWidth || saved.WindowHeight != videoHeight)
                    {
                        Log(11, "音频窗口：关音频窗口时把视频窗口那份几何覆盖掉了"
                                + $"（{videoWidth}×{videoHeight} → {saved.WindowWidth}×{saved.WindowHeight}）");
                        return false;
                    }

                    // ---- ⑦ 偏好是 audio：新开一个窗体（不带媒体）应当直接是音频窗口 ----
                    using var second = new 播放器.MainForm(Array.Empty<string>());
                    second.Show();
                    PumpMessages(500);

                    if (!second.IsAudioWindow)
                    {
                        Log(11, "音频窗口：偏好是「总是音频窗口」，重新启动却没有进音频窗口");
                        return false;
                    }

                    if (Math.Abs(second.Width - audioBounds.Width) > 2 ||
                        Math.Abs(second.Height - audioBounds.Height) > 2)
                    {
                        Log(11, $"音频窗口：重新启动没有用记住的音频窗口尺寸（现在 {second.Size}，期望 {audioBounds.Size}）");
                        return false;
                    }

                    // ---- ③ 切回视频窗口：布局要回去（几何在同一窗体里已经验过） ----
                    second.SetWindowMode(WindowModes.Video);
                    PumpMessages(400);

                    if (second.IsAudioWindow)
                    {
                        Log(11, "音频窗口：切到「总是视频窗口」之后还是音频窗口");
                        return false;
                    }

                    if (second.Width < second.MinimumSize.Width || second.Height < second.MinimumSize.Height)
                    {
                        Log(11, $"音频窗口：退出之后窗口比视频窗口的最小尺寸还小"
                                + $"（{second.Width}×{second.Height}，最小 {second.MinimumSize}）");
                        return false;
                    }

                    var secondCard = FindByTypeName(second, "NowPlayingView");
                    if (secondCard != null && secondCard.Visible)
                    {
                        Log(11, "音频窗口：切回视频窗口之后卡片还盖在画面上");
                        return false;
                    }

                    var secondToolbar = Find(second, "toolStripMain");
                    if (secondToolbar == null || !secondToolbar.Visible)
                    {
                        Log(11, "音频窗口：切回视频窗口之后工具栏没有回来");
                        return false;
                    }

                    // ---- ④ auto：放音频自动进、放视频自动出 ----
                    second.SetWindowMode(WindowModes.Auto);
                    second.ApplyMetadata(audioFile, new MediaTags(), LyricsDocument.Empty);
                    PumpMessages(350);

                    if (!second.IsAudioWindow)
                    {
                        Log(11, $"音频窗口：放音频文件没有自动切到简化窗口（{Path.GetFileName(audioFile)}）");
                        return false;
                    }

                    if (!videoSetup)
                    {
                        Log(11, $"音频窗口：视频那一段跳过——{videoReason}");
                    }
                    else
                    {
                        second.ApplyMetadata(videoFile, new MediaTags(), LyricsDocument.Empty);
                        PumpMessages(350);

                        if (second.IsAudioWindow)
                        {
                            Log(11, "音频窗口：切到视频文件之后没有回视频窗口");
                            return false;
                        }
                    }

                    // 「总是视频窗口」+ 音频文件：不许进音频窗口
                    second.SetWindowMode(WindowModes.Video);
                    second.ApplyMetadata(audioFile, new MediaTags(), LyricsDocument.Empty);
                    PumpMessages(350);

                    if (second.IsAudioWindow)
                    {
                        Log(11, "音频窗口：设成「总是视频窗口」之后放音乐还是进了音频窗口");
                        return false;
                    }

                    second.Close();
                    PumpMessages(200);
                }

                Log(11, "音频窗口正常：卡片盖满画面（传输条留着、工具栏收起、两栏收起）、"
                        + "两种窗口各记各的几何、auto 按文件自动切、全屏被挡且说明原因、卡片上不误弹画面菜单");
                return true;
            }
            catch (Exception ex)
            {
                Log(11, "音频窗口检查失败: " + ex);
                return false;
            }
            finally
            {
                TryDelete(audioFile);
                if (videoSetup) TryDelete(videoFile);

                // 这一段把窗口模式摆成过 video / auto、还改过窗口几何：还原，后面的检查还要用这份设置
                defaults.Save();
            }
        }

        /// <summary>合成一个 2 秒的小视频（只为了"扩展名是视频"这一条，不需要真播）。</summary>
        private static bool TryCreateTinyVideo(out string path, out string reason)
        {
            path = Path.Combine(AppContext.BaseDirectory, "audio-window-video.mkv");
            reason = string.Empty;

            var ffmpeg = Environment.GetEnvironmentVariable("播放器_TEST_FFMPEG");

            if (string.IsNullOrWhiteSpace(ffmpeg))
                ffmpeg = @"D:\ffmpeg-8.0-full_build\bin\ffmpeg.exe";

            if (!File.Exists(ffmpeg))
            {
                reason = "没有 ffmpeg（设 播放器_TEST_FFMPEG 指一个）";
                return false;
            }

            var arguments = new[]
            {
                "-y", "-f", "lavfi", "-i", "color=c=blue:s=160x120:r=15:d=2",
                "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
                path
            };

            try
            {
                using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ffmpeg,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    Arguments = string.Join(" ", arguments.Select(QuoteArgument))
                });

                if (process == null)
                {
                    reason = "ffmpeg 起不来";
                    return false;
                }

                if (!process.WaitForExit(30000))
                {
                    try { process.Kill(entireProcessTree: true); } catch (Exception) { /* 杀不掉就算了 */ }

                    reason = "ffmpeg 30 秒没跑完";
                    return false;
                }

                if (process.ExitCode != 0 || !File.Exists(path))
                {
                    reason = $"ffmpeg 合成小视频失败（退出码 {process.ExitCode}）";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                reason = "调 ffmpeg 失败：" + ex.Message;
                return false;
            }
        }
    }
}
