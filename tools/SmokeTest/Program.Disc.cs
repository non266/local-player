// 第 17 步（检查点 21）：光盘 —— 类型判定 / MRL / m3u 往返，以及真盘播放（播放器_TEST_DISC）。

namespace SmokeTest
{
    internal static partial class Program
    {
        /// <summary>
        /// 1.11.0 的 F13「打开光盘」。
        /// <para>
        /// 分两段：<b>规则</b>用临时目录装出四种盘来验（不插盘也能跑）；
        /// <b>真盘</b>那一段要环境变量 <c>播放器_TEST_DISC=L:</c> 指定光驱，没有就打印原因跳过——
        /// 照 <c>播放器_TEST_VIDEO</c> 的先例，跳过必须出声。
        /// </para>
        /// </summary>
        private static bool TestOpticalDisc()
        {
            if (!CheckOpticalDiscRules()) return false;
            if (!CheckOpticalDiscPlayback()) return false;

            return true;
        }

        /// <summary>纯规则：四种盘的判定、MRL 拼法、播放列表里的显示名、m3u 往返。</summary>
        private static bool CheckOpticalDiscRules()
        {
            var root = Path.Combine(AppContext.BaseDirectory, "smoke-disc");
            TryDeleteDirectory(root);

            var dvd = Path.Combine(root, "dvd");
            var bluRay = Path.Combine(root, "bluray");
            var audio = Path.Combine(root, "audio-cd");
            var data = Path.Combine(root, "data");
            var empty = Path.Combine(root, "empty");

            try
            {
                Directory.CreateDirectory(Path.Combine(dvd, "VIDEO_TS"));
                File.WriteAllBytes(Path.Combine(dvd, "VIDEO_TS", "VIDEO_TS.IFO"), new byte[] { 1, 2, 3 });

                Directory.CreateDirectory(Path.Combine(bluRay, "BDMV"));
                File.WriteAllBytes(Path.Combine(bluRay, "BDMV", "index.bdmv"), new byte[] { 1 });

                Directory.CreateDirectory(audio);
                File.WriteAllBytes(Path.Combine(audio, "Track01.cda"), new byte[] { 1 });
                File.WriteAllBytes(Path.Combine(audio, "Track02.cda"), new byte[] { 1 });

                Directory.CreateDirectory(data);
                File.WriteAllBytes(Path.Combine(data, "movie.mp4"), new byte[] { 1 });

                Directory.CreateDirectory(empty);

                // ---- 类型判定 ----
                var cases = new[]
                {
                    (Root: dvd, Expected: DiscKind.DvdVideo, Text: "有 VIDEO_TS 的盘"),
                    (Root: bluRay, Expected: DiscKind.BluRay, Text: "有 BDMV 的盘"),
                    (Root: audio, Expected: DiscKind.AudioCd, Text: "只有 *.cda 的盘"),
                    (Root: data, Expected: DiscKind.Data, Text: "只有媒体文件的数据盘"),
                    (Root: empty, Expected: DiscKind.Data, Text: "空目录（按数据盘处理）")
                };

                foreach (var item in cases)
                {
                    var actual = OpticalDisc.Classify(item.Root);

                    if (actual != item.Expected)
                    {
                        Log(21, $"光盘：{item.Text} 没有被判成 {item.Expected}（判成了 {actual}）");
                        return false;
                    }
                }

                if (OpticalDisc.Classify(Path.Combine(root, "does-not-exist")) != DiscKind.None)
                {
                    Log(21, "光盘：不存在的目录没有被判成 None");
                    return false;
                }

                if (OpticalDisc.HasPlayableMedia(audio))
                {
                    Log(21, "光盘：音频 CD 的 *.cda 被当成了可播的媒体文件");
                    return false;
                }

                if (!OpticalDisc.HasPlayableMedia(data))
                {
                    Log(21, "光盘：数据盘里明明有 movie.mp4，却没被认成有可播媒体");
                    return false;
                }

                // ---- MRL 拼法 ----
                if (OpticalDisc.BuildMrl(DiscKind.DvdVideo, @"L:\") != "dvdsimple:///L:")
                {
                    Log(21, "光盘：DVD 的 MRL 不是 dvdsimple:///L:（拼出来的是「"
                            + OpticalDisc.BuildMrl(DiscKind.DvdVideo, @"L:\") + "」）");
                    return false;
                }

                if (OpticalDisc.BuildMrl(DiscKind.AudioCd, @"L:\") != "cdda:///L:")
                {
                    Log(21, "光盘：音频 CD 的 MRL 不是 cdda:///L:（拼出来的是「"
                            + OpticalDisc.BuildMrl(DiscKind.AudioCd, @"L:\") + "」）");
                    return false;
                }

                if (OpticalDisc.BuildMrl(DiscKind.Data, @"L:\").Length != 0)
                {
                    Log(21, "光盘：数据盘不该拼 MRL（它按文件加进列表）");
                    return false;
                }

                // ---- 判定 / 打开 / 显示名 ----
                if (!MediaFormats.IsDiscLocation("dvdsimple:///L:") ||
                    !MediaFormats.IsLocationUri("dvdsimple:///L:") ||
                    !MediaFormats.IsOpenable("dvdsimple:///L:"))
                {
                    Log(21, "光盘：dvd 的 MRL 没有被当成「可打开的位置」（播放列表会把它标成打不开）");
                    return false;
                }

                if (MediaFormats.IsDiscLocation(@"C:\music\a.mp3"))
                {
                    Log(21, "光盘：普通文件路径被误判成光盘位置");
                    return false;
                }

                var discItem = new PlaylistItem("dvdsimple:///L:");

                if (!discItem.IsDisc || discItem.DisplayName != "DVD 光盘 (L:)")
                {
                    Log(21, "光盘：列表里的显示名不对（得到「" + discItem.DisplayName + "」）");
                    return false;
                }

                // ---- m3u 往返：光盘条目不能被当成非法路径丢掉 ----
                var wav = Path.Combine(root, "plain.wav");
                WriteWav(wav, seconds: 1, frequency: 440);

                var items = new List<PlaylistItem> { discItem, new PlaylistItem(wav) };
                var text = PlaylistFile.BuildText(items, root);
                var m3u = Path.Combine(root, "disc.m3u8");
                File.WriteAllText(m3u, "\uFEFF" + text, new UTF8Encoding(false));

                var reloaded = PlaylistFile.ReadPaths(m3u);

                if (!reloaded.Any(p => p == "dvdsimple:///L:"))
                {
                    Log(21, "光盘：存进 m3u 之后再读回来，dvd 的 MRL 丢了");
                    return false;
                }

                Log(21, "光盘判定正常：VIDEO_TS / BDMV / *.cda / 数据盘 / 空目录都认得，"
                        + "MRL 是 dvdsimple:///L: 与 cdda:///L:，显示名「DVD 光盘 (L:)」，m3u 往返也留得住");
                return true;
            }
            finally
            {
                TryDeleteDirectory(root);
            }
        }

        /// <summary>真盘：需要 <c>播放器_TEST_DISC=L:</c>（没有盘就跳过，并打印原因）。</summary>
        private static bool CheckOpticalDiscPlayback()
        {
            var letter = (Environment.GetEnvironmentVariable("播放器_TEST_DISC") ?? string.Empty)
                .Trim().TrimEnd('\\');

            if (letter.Length == 0)
            {
                Log(21, "光盘真盘检查：未指定 播放器_TEST_DISC（例如 L:），跳过这一小段");
                return true;
            }

            var drive = OpticalDisc.Detect()
                .FirstOrDefault(d => string.Equals(d.Letter, letter, StringComparison.OrdinalIgnoreCase));

            if (drive == null)
            {
                Log(21, "光盘真盘检查：这台机器上没有 " + letter + " 这个光驱");
                return false;
            }

            if (!drive.IsReady)
            {
                Log(21, "光盘真盘检查：" + letter + " 里没有光盘（放一张盘再跑，或去掉 播放器_TEST_DISC）");
                return false;
            }

            if (drive.Kind == DiscKind.BluRay)
            {
                Log(21, "光盘真盘检查：" + letter + " 是蓝光（1.11.0 只识别、不支持播放），换一张 DVD");
                return false;
            }

            using var form = new 播放器.MainForm(Array.Empty<string>());
            form.Show();
            PumpMessages(500);

            form.RebuildDiscMenu();

            var item = form.DiscMenu.DropDownItems
                .OfType<ToolStripMenuItem>()
                .FirstOrDefault(i => i.Tag is OpticalDiscDrive d &&
                                     string.Equals(d.Letter, drive.Letter, StringComparison.OrdinalIgnoreCase));

            if (item == null)
            {
                Log(21, "光盘真盘检查：菜单里没有 " + drive.Letter + " 这一项");
                return false;
            }

            item.PerformClick();

            if (!PumpUntil(() => form.Engine.HasMedia && form.Engine.IsPlaying, 30000))
            {
                Log(21, $"光盘真盘检查：{drive.KindText} 没能开播（状态栏：「{form.StatusText}」）");
                return false;
            }

            if (form.Engine.Length <= 0)
            {
                Log(21, "光盘真盘检查：播起来了，但总时长读不出来");
                return false;
            }

            Log(21, "光盘真盘检查正常：" + drive.KindText + " 开播成功，总时长 "
                    + TimeFormatter.FormatWithHours(TimeSpan.FromMilliseconds(form.Engine.Length)));

            return true;
        }
    }
}
