
// 播放历史、按文件的调整、会话镜像（写盘相关）。

namespace SmokeTest
{
    internal static partial class Program
    {
        /// <summary>
        /// 播放记忆的判定规则。
        /// <para>
        /// 刻意用 <c>new PlaybackHistory()</c> 而不是 <c>Load()</c>，避免动到使用者真实的 history.json。
        /// </para>
        /// </summary>
        private static bool CheckPlaybackHistory()
        {
            var history = new PlaybackHistory();

            const string normal = @"C:\media\album\a.mp3";
            const string tooEarly = @"C:\media\album\b.mp3";
            const string nearlyDone = @"C:\media\album\c.mp3";

            history.Record(normal, TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(10));
            if (!history.TryGetResumePosition(normal, out var position) ||
                Math.Abs(position.TotalSeconds - 180) > 0.5)
            {
                Log(8, "播放历史：正常进度没有取回续播位置");
                return false;
            }

            // 开头十几秒不值得续播
            history.Record(tooEarly, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(10));
            if (history.TryGetResumePosition(tooEarly, out _))
            {
                Log(8, "播放历史：刚开头几秒不应该续播");
                return false;
            }

            // 已经播到结尾附近，当作看完，下次从头播
            history.Record(nearlyDone, TimeSpan.FromSeconds(595), TimeSpan.FromMinutes(10));
            if (history.TryGetResumePosition(nearlyDone, out _))
            {
                Log(8, "播放历史：接近结尾时不应该续播");
                return false;
            }

            // 网络流不记录（地址没有稳定标识）
            history.Record("https://example.com/live.m3u8", TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10));
            if (history.TryGetResumePosition("https://example.com/live.m3u8", out _))
            {
                Log(8, "播放历史：网络流不应该被记录");
                return false;
            }

            if (history.Recent.Count != 3)
            {
                Log(8, $"播放历史：最近播放应为 3 条，实际 {history.Recent.Count}");
                return false;
            }

            if (history.Recent[0] != nearlyDone)
            {
                Log(8, "播放历史：最近播放没有按时间倒序");
                return false;
            }

            // 清进度但保留最近播放
            history.ClearPositions();
            if (history.TryGetResumePosition(normal, out _))
            {
                Log(8, "播放历史：清除进度后仍能取回续播位置");
                return false;
            }

            if (history.Recent.Count != 3)
            {
                Log(8, "播放历史：清除进度不应该影响最近播放列表");
                return false;
            }

            // 清空
            history.Clear();
            if (history.Count != 0 || history.Recent.Count != 0)
            {
                Log(8, "播放历史：清空后仍有残留记录");
                return false;
            }

            return true;
        }

        /// <summary>
        /// 按文件记住的调整（音画 / 字幕延迟、歌词偏移）。
        /// <para>
        /// 这三样和播放进度存在同一个 <c>history.json</c>，但规矩不同：
        /// 进度定期落盘就行，调整是用户一项项调出来的，<b>改一次就该落地</b>。
        /// 以前只有歌词偏移这么做，两个延迟要等 20 秒的定时器（写文件的定时器还只在放歌时才走），
        /// 所以"调完延迟马上关程序"会丢——这一段以前也没有任何检查。
        /// </para>
        /// </summary>
        private static bool CheckPerFileAdjustments()
        {
            const string a = @"C:\media\album\adjust-a.mp4";
            const string b = @"C:\media\album\adjust-b.mp4";
            const string stream = "https://example.com/live.m3u8";

            // ---- 内存里的规矩：三样各记各的、读不建壳，不用碰磁盘 ----
            // （刻意不碰磁盘：落盘是"另一次写入在路上就并进下一次"的合并写，
            //   连着记两笔时第二笔会等下一次调用，用它来验语义会验成时序。）
            var store = new PlaybackHistory();
            var adjustments = new PerFileAdjustments(store);

            if (!adjustments.Get(a).IsEmpty || store.Count != 0)
            {
                Log(8, $"按文件调整：读一个没记过的文件就建了记录（现在 {store.Count} 条）");
                return false;
            }

            adjustments.RecordDelays(a, 150, -250);
            adjustments.RecordLyricsOffset(b, 500);

            var fromA = adjustments.Get(a);
            var fromB = adjustments.Get(b);

            if (fromA.AudioDelayMilliseconds != 150 || fromA.SubtitleDelayMilliseconds != -250 ||
                fromA.LyricsOffsetMilliseconds != 0)
            {
                Log(8, "按文件调整：A 的音画 / 字幕延迟没记对"
                        + $"（{fromA.AudioDelayMilliseconds} / {fromA.SubtitleDelayMilliseconds}"
                        + $" / 歌词 {fromA.LyricsOffsetMilliseconds}）");
                return false;
            }

            if (fromB.LyricsOffsetMilliseconds != 500 ||
                fromB.AudioDelayMilliseconds != 0 || fromB.SubtitleDelayMilliseconds != 0)
            {
                Log(8, $"按文件调整：B 的歌词偏移没记对，或者串到别的文件上了（{fromB.LyricsOffsetMilliseconds}）");
                return false;
            }

            // 网络流没有稳定标识，记了也没意义（和播放进度同一条规矩）
            adjustments.RecordDelays(stream, 300, 300);

            if (!adjustments.Get(stream).IsEmpty)
            {
                Log(8, "按文件调整：网络流也被记下来了");
                return false;
            }

            // 空路径不该炸
            adjustments.RecordLyricsOffset(null, 900);

            // 「清除最近播放」清的是历史，不该顺手把用户一项项调出来的东西也清掉
            store.Clear();

            var clearedA = adjustments.Get(a);

            if (clearedA.AudioDelayMilliseconds != 150 || clearedA.SubtitleDelayMilliseconds != -250)
            {
                Log(8, "按文件调整：「清除最近播放」把音画 / 字幕延迟也清掉了"
                        + $"（{clearedA.AudioDelayMilliseconds} / {clearedA.SubtitleDelayMilliseconds}）");
                return false;
            }

            if (adjustments.Get(b).LyricsOffsetMilliseconds != 500)
            {
                Log(8, "按文件调整：「清除最近播放」把歌词偏移也清掉了");
                return false;
            }

            // ---- 改一次就该落盘：这一条要碰磁盘（用一条自己的路径，免得和别的检查打架）----
            // 这条路径只是 history.json 里的一个键，不需要真的存在文件——
            // 所以别把它挪到 smoke-tags 之类的目录下（那是第 9 步才建的，而这里在第 4 步跑）
            var diskPath = Path.Combine(AppContext.BaseDirectory, "adjust-eager.mp4");
            var onDiskHistory = PlaybackHistory.Load();

            new PerFileAdjustments(onDiskHistory).RecordDelays(diskPath, 150, -250);

            if (!WaitForAdjustmentsOnDisk(diskPath, 150, -250, 5))
            {
                var now = new PerFileAdjustments(PlaybackHistory.Load()).Get(diskPath);

                Log(8, "按文件调整：改了延迟没有马上落盘（等定时器的话，用户当场关程序就丢了）"
                        + $"——磁盘上现在是 {now.AudioDelayMilliseconds} / {now.SubtitleDelayMilliseconds}，"
                        + $"history.json 存在？ {File.Exists(PlaybackHistory.FilePath)}"
                        + $"（{PlaybackHistory.FilePath}）");
                return false;
            }

            // 收尾：这条记录不该留在 history.json 里影响后面的检查
            onDiskHistory.Forget(diskPath);
            onDiskHistory.Save();

            Log(8, "按文件记住的调整正常：音画 / 字幕延迟与歌词偏移各记各的、读不建壳、"
                    + "网络流不记、改一次就已经落盘、「清除最近播放」不碰它们");
            return true;
        }

        /// <summary>等后台那次落盘把值写进磁盘（刻意不主动 Save，验的就是"改一次就落盘"）。</summary>
        private static bool WaitForAdjustmentsOnDisk(string path, long audio, long subtitle, int seconds)
        {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);

            while (DateTime.UtcNow < deadline)
            {
                var fromDisk = new PerFileAdjustments(PlaybackHistory.Load()).Get(path);

                if (fromDisk.AudioDelayMilliseconds == audio &&
                    fromDisk.SubtitleDelayMilliseconds == subtitle)
                    return true;

                Thread.Sleep(50);
            }

            return false;
        }

        /// <summary>
        /// 退出时"界面 → 设置"的镜像：摆在界面上的状态，关窗时要原样写进 <c>settings.json</c>。
        /// <para>
        /// 这一段以前没人验（只有「当前歌单名」靠第 17 步间接验到）。它的失败方式很隐蔽：
        /// <b>改了设置，重启却没生效</b>——只有用户撞见才知道。加一个设置项时忘了在
        /// <c>SaveSession</c> 里补一行，就是这个结果。
        /// </para>
        /// <para>
        /// 只验不依赖真实媒体的那几项（音量 / 静音 / 置顶 / 侧栏页 / 窗口位置与大小 / 分隔条 /
        /// 播放列表）；速率与纵横比要真的在放媒体才有意义，那两项由第 13 步在真视频上验。
        /// </para>
        /// </summary>
        private static bool CheckSessionMirror()
        {
            // 先记下现在的设置：这一段会把 AlwaysOnTop、播放列表等摆成别的样子，
            // 验完要还原——后面还有十几步会各自建窗体读这份设置
            var defaults = AppSettings.Load();
            var files = new List<string>();

            for (var i = 1; i <= 3; i++)
            {
                // 直接写在程序目录，不借 smoke-tags：那个目录是第 9 步建的，
                // 而这一段在第 6 步跑——干净树上还没有它（这个坑是干净树验证抓出来的：
                // 以前能过，只是因为工作目录里留着上一次的 smoke-tags）
                var path = Path.Combine(AppContext.BaseDirectory, $"session-{i}.mp3");
                File.WriteAllBytes(path, new byte[] { 0x49, 0x44, 0x33, (byte)i });
                files.Add(path);
            }

            try
            {
                using var form = new 播放器.MainForm(Array.Empty<string>());
                form.Show();
                PumpMessages(300);

                var volume = Find(form, "trackVolume");
                var sidebar = Find(form, "sidebarPanel");
                var split = Find(form, "splitVideo") as SplitContainer;

                if (volume == null || sidebar == null || split == null)
                {
                    Log(6, "会话镜像检查：找不到音量条 / 侧栏 / 分隔条");
                    return false;
                }

                // 摆一个"和默认值明显不一样"的状态
                ((FlatSlider)volume).Value = 42;
                ((SidebarPanel)sidebar).ActiveTab = SidebarTab.Info;
                form.TopMost = true;
                form.Bounds = new Rectangle(form.Bounds.X, form.Bounds.Y, 1280, 800);

                form.SetMute(true);

                split.SplitterDistance = Math.Max(split.Panel1MinSize, split.SplitterDistance - 40);

                var playlist = PlaylistOf(form);
                playlist.Clear();
                playlist.AddRange(files);
                playlist.SetCurrent(2);

                var bounds = form.Bounds;
                var splitter = split.SplitterDistance;
                var paths = playlist.GetPaths();
                var index = playlist.CurrentIndex;

                // 关窗 → OnFormClosingInternal → SaveSession
                form.Close();
                PumpMessages(200);

                var saved = AppSettings.Load();

                if (saved.Volume != 42)
                {
                    Log(6, $"会话镜像检查：音量没写进设置（设 42，写出来 {saved.Volume}）");
                    return false;
                }

                if (!saved.Muted)
                {
                    Log(6, "会话镜像检查：静音状态没写进设置");
                    return false;
                }

                if (!saved.AlwaysOnTop)
                {
                    Log(6, "会话镜像检查：置顶状态没写进设置");
                    return false;
                }

                if (saved.SidebarPage != SidebarTab.Info)
                {
                    Log(6, $"会话镜像检查：侧栏停在的页没写进设置（写出来 {saved.SidebarPage}）");
                    return false;
                }

                if (saved.WindowX != bounds.X || saved.WindowY != bounds.Y ||
                    saved.WindowWidth != bounds.Width || saved.WindowHeight != bounds.Height)
                {
                    Log(6, "会话镜像检查：窗口位置 / 大小没写进设置"
                            + $"（界面 {bounds.X},{bounds.Y} {bounds.Width}x{bounds.Height}；"
                            + $"设置 {saved.WindowX},{saved.WindowY} {saved.WindowWidth}x{saved.WindowHeight}）");
                    return false;
                }

                if (saved.SplitterDistance != splitter)
                {
                    Log(6, $"会话镜像检查：分隔条位置没写进设置（界面 {splitter}，写出来 {saved.SplitterDistance}）");
                    return false;
                }

                if (saved.SidebarWidth <= 0)
                {
                    Log(6, $"会话镜像检查：侧栏宽度没写进设置（{saved.SidebarWidth}）");
                    return false;
                }

                if (saved.LastPlaylistIndex != index || !saved.LastPlaylist.SequenceEqual(paths))
                {
                    Log(6, "会话镜像检查：播放列表 / 当前项没写进设置"
                            + $"（界面 {index} 项号、{paths.Count} 项；设置 {saved.LastPlaylistIndex}、{saved.LastPlaylist.Count} 项）");
                    return false;
                }

                Log(6, "会话镜像正常：音量 / 静音 / 置顶 / 侧栏页 / 窗口位置与大小 / 分隔条 / 播放列表都写进了设置");
                return true;
            }
            finally
            {
                foreach (var file in files) TryDelete(file);

                defaults.Save();
            }
        }
    }
}
