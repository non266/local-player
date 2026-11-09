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
            if (!CheckDiscMenuRules()) return false;
            if (!CheckDiscMenuFallback()) return false;
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
                // 1.16.0：DVD 默认打开菜单（dvd://）；"直接播放主标题"用另一条 dvdsimple://。
                if (OpticalDisc.BuildMrl(DiscKind.DvdVideo, @"L:\") != "dvd:///L:")
                {
                    Log(21, "光盘：DVD 打开时没有走菜单（MRL 是「"
                            + OpticalDisc.BuildMrl(DiscKind.DvdVideo, @"L:\") + "」，期望 dvd:///L:）");
                    return false;
                }

                if (OpticalDisc.BuildSimpleMrl(DiscKind.DvdVideo, @"L:\") != "dvdsimple:///L:")
                {
                    Log(21, "光盘：「直接播放主标题」的 MRL 不是 dvdsimple:///L:（拼出来的是「"
                            + OpticalDisc.BuildSimpleMrl(DiscKind.DvdVideo, @"L:\") + "」）");
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
                if (!MediaFormats.IsDiscLocation("dvd:///L:") ||
                    !MediaFormats.IsLocationUri("dvd:///L:") ||
                    !MediaFormats.IsOpenable("dvd:///L:"))
                {
                    Log(21, "光盘：菜单的 MRL 没有被当成「可打开的位置」（播放列表会把它标成打不开）");
                    return false;
                }

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

                // 1.16.0：菜单那一版 MRL 的显示名也必须一样——用户在列表里看到的是同一张盘
                var menuDiscItem = new PlaylistItem("dvd:///L:");

                if (!menuDiscItem.IsDisc || menuDiscItem.DisplayName != "DVD 光盘 (L:)")
                {
                    Log(21, "光盘：菜单那一版 MRL 的显示名不对（得到「" + menuDiscItem.DisplayName + "」）");
                    return false;
                }

                // ---- m3u 往返：光盘条目不能被当成非法路径丢掉 ----
                var wav = Path.Combine(root, "plain.wav");
                WriteWav(wav, seconds: 1, frequency: 440);

                var items = new List<PlaylistItem> { discItem, menuDiscItem, new PlaylistItem(wav) };
                var text = PlaylistFile.BuildText(items, root);
                var m3u = Path.Combine(root, "disc.m3u8");
                File.WriteAllText(m3u, "\uFEFF" + text, new UTF8Encoding(false));

                var reloaded = PlaylistFile.ReadPaths(m3u);

                if (!reloaded.Any(p => p == "dvdsimple:///L:") || !reloaded.Any(p => p == "dvd:///L:"))
                {
                    Log(21, "光盘：存进 m3u 之后再读回来，dvd 的 MRL 丢了");
                    return false;
                }

                Log(21, "光盘判定正常：VIDEO_TS / BDMV / *.cda / 数据盘 / 空目录都认得，"
                        + "MRL 是 dvd:///L:（菜单）与 dvdsimple:///L:（直接主标题）、cdda:///L:，"
                        + "显示名「DVD 光盘 (L:)」，m3u 往返也留得住");
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

            // 1.16.0：默认打开的是菜单（dvd://），不是直接放正片
            if (!PumpUntil(() => form.IsDiscMenuOnScreen, 8000))
            {
                Log(21, "光盘真盘检查：打开之后没有停在菜单上（路径「" + form.Engine.CurrentPath + "」，"
                        + "标题 " + form.Engine.Title + " / " + form.Engine.TitleCount
                        + "，状态栏「" + form.StatusText + "」）");
                return false;
            }

            // 菜单上没有"进度"可言：进度条应当被置灰（显示 --:--:--，见 UpdateProgress）
            if (!PumpUntil(() => !form.SeekBarEnabled, 3000))
            {
                Log(21, "光盘真盘检查：停在菜单上时进度条还能拖（菜单 title 只有几百毫秒，拖了只会乱跳）");
                return false;
            }

            // 画面右键菜单里的「光盘菜单」：全屏时菜单栏收起来了，这是唯一入口
            form.RefreshVideoMenu();
            var discMenu = form.VideoDiscMenu;
            var navUp = discMenu?.DropDownItems.OfType<ToolStripMenuItem>()
                .FirstOrDefault(i => i.Text == "菜单：上");

            if (discMenu == null || navUp == null || !navUp.Enabled)
            {
                Log(21, "光盘真盘检查：画面右键菜单里没有可用的「光盘菜单 → 菜单：上」"
                        + "（全屏时就没有回菜单 / 选菜单的入口了）");
                return false;
            }

            // 真盘证据：在菜单上按回车（真的走 ProcessCmdKey → 菜单导航），默认高亮是「play all」，
            // 于是会进第一个正片——标题号与总时长都会变。这不是"看截图猜高亮动没动"：
            // 菜单背景本身一直在动，两张截图天然不同（变异验证时发现的，别再用 SHA 比）。
            // 先把焦点从搜索框挪开：焦点在文本框里时按键归文本框（打字优先），这是既有规矩。
            // 冒烟里窗口没有被真的点过，焦点可能还停在播放列表的搜索框上，所以直接把活动控件清空。
            form.ActiveControl = null;
            PumpMessages(150);

            if (!form.ProcessShortcut(Keys.Enter))
            {
                Log(21, "光盘真盘检查：菜单在屏上时按回车没有被窗口接住（回车掉到别处去了）");
                return false;
            }

            if (!PumpUntil(() => form.Engine.Title == 1, 20000))
            {
                Log(21, "光盘真盘检查：在菜单上按回车之后没有进正片（现在标题 " + form.Engine.Title
                        + " / " + form.Engine.TitleCount + "，状态栏「" + form.StatusText + "」）");
                return false;
            }

            if (!PumpUntil(() => form.Engine.Length > 1_000_000, 20000))
            {
                Log(21, "光盘真盘检查：进了正片但总时长不对（" + form.Engine.Length + " ms，正片应该 > 1,000,000）");
                return false;
            }

            // 进了正片，进度条要恢复
            if (!PumpUntil(() => form.SeekBarEnabled, 5000))
            {
                Log(21, "光盘真盘检查：进了正片之后进度条还是拖不动");
                return false;
            }

            // 标题：从「播放 → 光盘 → 标题」点进去（验的是菜单接线，不是直接调引擎）
            form.RebuildDiscPlaybackMenu();
            var titles = form.MenuDiscPlayback?.DropDownItems
                .OfType<ToolStripMenuItem>()
                .FirstOrDefault(i => i.Name == "menuDiscTitles");
            var title2 = titles?.DropDownItems
                .OfType<ToolStripMenuItem>()
                .FirstOrDefault(i => i.Text.StartsWith("标题 2", StringComparison.Ordinal));

            if (title2 == null)
            {
                Log(21, "光盘真盘检查：「播放 → 光盘 → 标题」里没有「标题 2」（这张盘应该有 12 个正片标题）");
                return false;
            }

            title2.PerformClick();

            if (!PumpUntil(() => form.Engine.Title == 2, 15000))
            {
                Log(21, "光盘真盘检查：点了「标题 2」之后标题没变（现在 " + form.Engine.Title + "）");
                return false;
            }

            if (!PumpUntil(() => form.Engine.Length > 1_000_000, 15000))
            {
                Log(21, "光盘真盘检查：切到标题 2 之后总时长不对（" + form.Engine.Length
                        + " ms，正片应该 > 1,000,000）");
                return false;
            }

            // 章节（0 基：1 是第 2 章）
            form.Engine.SetChapter(1);

            if (!PumpUntil(() => form.Engine.Time > 10_000, 15000))
            {
                Log(21, "光盘真盘检查：跳到第 2 章之后进度没有前进（现在 " + form.Engine.Time + " ms）");
                return false;
            }

            // 回到菜单：走「播放 → 光盘 → 回到菜单」
            form.RebuildDiscPlaybackMenu();
            var back = form.MenuDiscPlayback?.DropDownItems
                .OfType<ToolStripMenuItem>()
                .FirstOrDefault(i => i.Name == "menuDiscBack");
            back?.PerformClick();

            if (!PumpUntil(() => form.IsDiscMenuOnScreen, 15000))
            {
                Log(21, "光盘真盘检查：点了「回到菜单」之后没有回到菜单（标题 " + form.Engine.Title + "）");
                return false;
            }

            // 直接播放主标题：列表里仍然只能有一条
            form.RebuildDiscPlaybackMenu();
            var main = form.MenuDiscPlayback?.DropDownItems
                .OfType<ToolStripMenuItem>()
                .FirstOrDefault(i => i.Name == "menuDiscMainTitle");
            main?.PerformClick();

            if (!PumpUntil(() => (form.Engine.CurrentPath ?? string.Empty)
                    .StartsWith("dvdsimple://", StringComparison.OrdinalIgnoreCase), 15000))
            {
                Log(21, "光盘真盘检查：点了「直接播放主标题」之后没有换成 dvdsimple://（现在「"
                        + form.Engine.CurrentPath + "」）");
                return false;
            }

            if (form.Playlist.Count != 1)
            {
                Log(21, "光盘真盘检查：在菜单 / 主标题之间切换之后列表里多出了条目（现在 "
                        + form.Playlist.Count + " 条）");
                return false;
            }

            if (!PumpUntil(() => form.Engine.Length > 0, 15000))
            {
                Log(21, "光盘真盘检查：主标题播起来了，但总时长读不出来");
                return false;
            }

            Log(21, "光盘真盘检查正常：" + drive.KindText + " 停在菜单上（标题 " + form.Engine.TitleCount
                    + " 个），按回车真的进了正片；标题 2 / 第 2 章 / 回到菜单 / 直接播放主标题都走通，"
                    + "列表仍只有 1 条；主标题总时长 "
                    + TimeFormatter.FormatWithHours(TimeSpan.FromMilliseconds(form.Engine.Length)));

            return true;
        }
    }
}
