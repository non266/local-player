// 检查点 18：歌词时间轴偏移（纯函数 / 文档平移 / 按文件记忆 / 主窗体联动）。

namespace SmokeTest
{
    internal static partial class Program
    {
        // -----------------------------------------------------------------
        // 18) 歌词时间轴偏移
        //
        // 歌词和声音差半拍是最常见的抱怨，所以这一项验的是"整条链路都按同一份偏移后的时间走"：
        // 数值本身（步长 / 范围 / 解析）、给文档做平移、按文件记住、侧栏与桌面歌词两边一致、
        // 菜单与角落提示有没有把当前值说出来、以及"没有时间轴时不要装模作样"。
        // -----------------------------------------------------------------

        private static bool TestLyricsOffset()
        {
            if (!CheckLyricsOffsetPure()) return false;
            if (!CheckLyricsDocumentShift()) return false;
            if (!CheckLyricsOffsetHistory()) return false;
            if (!CheckLyricsOffsetInMainForm()) return false;

            Log(18, "歌词偏移正常：一档 0.5 秒、范围 ±30 秒（到顶会说明而不是没反应），"
                    + "提前 / 延后与 LRC 的 [offset:] 同号（正值 = 提前，时间戳整体减掉它，不会出现负时间码），"
                    + "「0.5 / -1.5 / 750ms」这些写法都认，"
                    + "偏移按文件记进 history.json（重启后自己回来，清除最近播放不会把它清掉），"
                    + "侧栏歌词与桌面歌词用的是同一份时间轴，菜单与信息页都写出当前值，"
                    + "纯文本歌词（没有时间轴）时菜单如实禁用");
            return true;
        }

        /// <summary>偏移的数值与文字：纯函数，先单独钉住。</summary>
        private static bool CheckLyricsOffsetPure()
        {
            if (LyricsOffset.Step(0, 1) != 500 || LyricsOffset.Step(0, -1) != -500)
            {
                Log(18, $"歌词偏移检查：一档不是 0.5 秒（{LyricsOffset.Step(0, 1)} / {LyricsOffset.Step(0, -1)}）");
                return false;
            }

            // 到极限要停住，而不是绕回去
            if (LyricsOffset.Step(LyricsOffset.LimitMilliseconds, 1) != LyricsOffset.LimitMilliseconds ||
                LyricsOffset.Step(-LyricsOffset.LimitMilliseconds, -1) != -LyricsOffset.LimitMilliseconds)
            {
                Log(18, "歌词偏移检查：加减到极限之后没有停住");
                return false;
            }

            if (LyricsOffset.Clamp(999_999) != LyricsOffset.LimitMilliseconds ||
                LyricsOffset.Clamp(-999_999) != -LyricsOffset.LimitMilliseconds ||
                !LyricsOffset.IsAtLimit(LyricsOffset.LimitMilliseconds) ||
                LyricsOffset.IsAtLimit(LyricsOffset.LimitMilliseconds - LyricsOffset.StepMilliseconds))
            {
                Log(18, "歌词偏移检查：范围钳制 / 到顶判定不对");
                return false;
            }

            if (LyricsOffset.Describe(0) != "不偏移" ||
                LyricsOffset.Describe(500) != "提前 0.5 秒" ||
                LyricsOffset.Describe(-1500) != "延后 1.5 秒")
            {
                Log(18, $"歌词偏移检查：文字描述不对（{LyricsOffset.Describe(0)} / "
                        + $"{LyricsOffset.Describe(500)} / {LyricsOffset.Describe(-1500)}）");
                return false;
            }

            if (LyricsOffset.DescribeShort(0).Length != 0 ||
                LyricsOffset.DescribeShort(1500) != "+1.5s" ||
                LyricsOffset.DescribeShort(-500) != "−0.5s")
            {
                Log(18, $"歌词偏移检查：角落提示不对（{LyricsOffset.DescribeShort(0)} / "
                        + $"{LyricsOffset.DescribeShort(1500)} / {LyricsOffset.DescribeShort(-500)}）");
                return false;
            }

            // 手输入的几种写法
            var accepted = new (string Text, long Expected)[]
            {
                ("0.5", 500), ("-1.5", -1500), ("+2", 2000), (" 0.25 ", 250),
                ("750ms", 750), ("0.5s", 500), ("1.5秒", 1500), ("500毫秒", 500), ("0", 0)
            };

            foreach (var (text, expected) in accepted)
            {
                if (LyricsOffset.TryParse(text, out var parsed, out var error) && parsed == expected) continue;

                Log(18, $"歌词偏移检查：「{text}」应当解析成 {expected}（得到 {parsed}，理由「{error}」）");
                return false;
            }

            // 看不懂的、以及超出范围的，都要挡住并给出人话
            foreach (var bad in new[] { "", "   ", "abc", "60", "-60", "1e9" })
            {
                if (LyricsOffset.TryParse(bad, out _, out var error) || error.Length == 0)
                {
                    Log(18, $"歌词偏移检查：「{bad}」应当被拒绝并说明原因");
                    return false;
                }
            }

            if (LyricsOffset.TryParse("60", out _, out var rangeError) ||
                !rangeError.Contains("30", StringComparison.Ordinal))
            {
                Log(18, $"歌词偏移检查：超出范围时没有说清楚上限（「{rangeError}」）");
                return false;
            }

            Log(18, "歌词偏移数值正常：一档 0.5 秒、范围 ±30 秒（到顶停住并说明），"
                    + "提前 / 延后与 0.5 / -1.5 / 750ms / 1.5秒 这些写法都对，看不懂的会被挡住并说明理由");
            return true;
        }

        /// <summary>把偏移套到歌词上：时间整体平移，别的地方都不许变。</summary>
        private static bool CheckLyricsDocumentShift()
        {
            var document = LyricsDocument.Parse("[ti:测试曲目]\n[00:01.00]第一句\n[00:05.50]第二句\n", "测试.lrc");

            if (!document.IsSynchronized || document.Lines.Count != 2 || document.Title != "测试曲目")
            {
                Log(18, "歌词偏移检查：样本歌词没解析对");
                return false;
            }

            // 提前 0.5 秒：每一行都早 0.5 秒出现
            var earlier = document.Shifted(TimeSpan.FromMilliseconds(500));

            if (earlier.Lines[0].Time != TimeSpan.FromMilliseconds(500) ||
                earlier.Lines[1].Time != TimeSpan.FromMilliseconds(5000))
            {
                Log(18, $"歌词偏移检查：提前之后的时间不对（{earlier.Lines[0].Time} / {earlier.Lines[1].Time}）");
                return false;
            }

            // 定位也跟着走：0.4 秒时还没到第一句，0.5 秒时正好到
            if (earlier.LineIndexAt(TimeSpan.FromMilliseconds(400)) != -1 ||
                earlier.LineIndexAt(TimeSpan.FromMilliseconds(500)) != 0)
            {
                Log(18, "歌词偏移检查：平移之后按时间定位不对（点歌词跳转会因此跳错地方）");
                return false;
            }

            if (earlier.Title != "测试曲目" || earlier.Source != "测试.lrc")
            {
                Log(18, "歌词偏移检查：平移把歌词的其它信息弄丢了");
                return false;
            }

            // 延后 1 秒
            var later = document.Shifted(TimeSpan.FromMilliseconds(-1000));

            if (later.Lines[0].Time != TimeSpan.FromSeconds(2))
            {
                Log(18, $"歌词偏移检查：延后之后的时间不对（{later.Lines[0].Time}）");
                return false;
            }

            // 0 偏移不该白建一份
            if (!ReferenceEquals(document, document.Shifted(TimeSpan.Zero)))
            {
                Log(18, "歌词偏移检查：偏移为 0 时也重建了一份文档");
                return false;
            }

            // 提前太多时压在 0 上：时间码不能变成负数
            var extreme = document.Shifted(TimeSpan.FromSeconds(5));

            if (extreme.Lines[0].Time != TimeSpan.Zero ||
                extreme.Lines[1].Time != TimeSpan.FromMilliseconds(500))
            {
                Log(18, $"歌词偏移检查：提前过头时没有压在 0 上（{extreme.Lines[0].Time} / {extreme.Lines[1].Time}）");
                return false;
            }

            // 纯文本歌词没有时间轴，原样返回
            var plain = LyricsDocument.Parse("第一句\n第二句");

            if (!ReferenceEquals(plain, plain.Shifted(TimeSpan.FromSeconds(1))) || plain.PlainText.Length == 0)
            {
                Log(18, "歌词偏移检查：纯文本歌词被平移了（它根本没有时间轴）");
                return false;
            }

            Log(18, "歌词平移正常：整行时间一起走、定位与其它信息都不受影响、"
                    + "提前过头会压在 0 上（不出负时间码）、0 偏移与纯文本歌词都原样返回");
            return true;
        }

        /// <summary>按文件记住：写进历史文件、清最近播放不会连它一起清掉。</summary>
        private static bool CheckLyricsOffsetHistory()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "smoke-tags", "offset-history.mp3");

            var history = PlaybackHistory.Load();
            history.Record(path, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(3));
            history.RecordLyricsOffset(path, 1500);

            if (!history.TryGetLyricsOffset(path, out var saved) || saved != 1500)
            {
                Log(18, $"歌词偏移检查：记下来之后读不回（{(history.TryGetLyricsOffset(path, out var v) ? v.ToString() : "没有")}）");
                return false;
            }

            if (!history.Recent.Contains(path))
            {
                Log(18, "歌词偏移检查：刚记过播放的文件不在最近播放里");
                return false;
            }

            history.Save();

            // 从磁盘读回来：JSON 里真的写了这一项
            var reloaded = PlaybackHistory.Load();

            if (!reloaded.TryGetLyricsOffset(path, out var fromDisk) || fromDisk != 1500)
            {
                Log(18, "歌词偏移检查：偏移没有写进 history.json（重启就丢了）");
                return false;
            }

            // 「清除最近播放」清的是历史，不该顺手把用户一项项调出来的偏移也清掉
            reloaded.Clear();

            if (!reloaded.TryGetLyricsOffset(path, out var kept) || kept != 1500)
            {
                Log(18, "歌词偏移检查：「清除最近播放」把歌词偏移也清掉了");
                return false;
            }

            if (reloaded.Recent.Contains(path))
            {
                Log(18, "歌词偏移检查：清除之后这个文件还在最近播放里（只剩调整的空壳混进来了）");
                return false;
            }

            if (reloaded.TryGetResumePosition(path, out _))
            {
                Log(18, "歌词偏移检查：清除之后还能续播");
                return false;
            }

            // 归零也是一次有效的修改
            reloaded.RecordLyricsOffset(path, 0);

            if (reloaded.TryGetLyricsOffset(path, out _))
            {
                Log(18, "歌词偏移检查：归零之后还读得出旧偏移");
                return false;
            }

            reloaded.Save();

            Log(18, "歌词偏移记忆正常：写进 history.json 并读得回来，按文件各记各的，"
                    + "「清除最近播放」只清历史和续播进度、不碰偏移，归零也算一次修改");
            return true;
        }

        /// <summary>主窗体上的整条链路：侧栏、桌面歌词、菜单、角落提示、热键、重启。</summary>
        private static bool CheckLyricsOffsetInMainForm()
        {
            var synced = Path.Combine(AppContext.BaseDirectory, "smoke-tags", "sample.flac");

            if (!File.Exists(synced))
            {
                Log(18, "歌词偏移检查：缺少第 9 步生成的样本文件");
                return false;
            }

            // 自己造一个"没有歌词"的真音频，而不是借第 9 步的样本：
            // 那些样本的歌手 / 曲名和第 9 步留下的外挂 .lrc 撞得上名，会被当成"带轴歌词"，
            // 那样这一条就验不到"没有时间轴时不许调"了（数据目录跑完就删，不用额外清理）
            var scratch = Path.Combine(AppSettings.SettingsDirectory, "lyrics-offset");
            TryDeleteDirectory(scratch);
            Directory.CreateDirectory(scratch);

            var noLyrics = Path.Combine(scratch, "no-lyrics.wav");
            WriteWav(noLyrics, seconds: 1, frequency: 440);

            // 历史文件是这一步之前几步共用的，先把这首的偏移清干净
            var history = PlaybackHistory.Load();
            history.RecordLyricsOffset(synced, 0);
            history.Save();

            using var form = new 播放器.MainForm(Array.Empty<string>());
            form.Show();
            PumpMessages(250);

            form.LoadTrackMetadata(synced);

            if (WaitFor(
                    () => SessionOf(form).Lyrics,
                    document => document.IsSynchronized,
                    6) == null)
            {
                Log(18, "歌词偏移检查：等不到这首的带轴歌词");
                return false;
            }

            PumpMessages(200);

            var view = FindByTypeName(form, "LyricsView") as LyricsView;

            if (view == null)
            {
                Log(18, "歌词偏移检查：侧栏里找不到歌词控件");
                return false;
            }

            var shown = view.Document;

            if (shown == null || !shown.IsSynchronized)
            {
                Log(18, "歌词偏移检查：侧栏歌词不是带轴的");
                return false;
            }

            if (shown.Lines[0].Time != TimeSpan.FromSeconds(1) || view.OffsetHint != null)
            {
                Log(18, $"歌词偏移检查：刚载入就被偏移了（第一句 {shown.Lines[0].Time} / 提示 {view.OffsetHint}）");
                return false;
            }

            var menu = form.MenuLyricsOffset;

            if (menu == null)
            {
                Log(18, "歌词偏移检查：视图菜单里没有「歌词偏移」");
                return false;
            }

            // ---- 提前 0.5 秒 ----
            form.AdjustLyricsOffset(1);
            PumpMessages(150);

            if (CurrentOffset(form) != 500)
            {
                Log(18, $"歌词偏移检查：调了一下不是 +0.5 秒（现在是 {CurrentOffset(form)} 毫秒）");
                return false;
            }

            shown = view.Document;

            if (shown == null || shown.Lines[0].Time != TimeSpan.FromMilliseconds(500))
            {
                Log(18, $"歌词偏移检查：侧栏歌词没跟着偏移（第一句 {shown?.Lines[0].Time}）");
                return false;
            }

            if (view.OffsetHint != "+0.5s")
            {
                Log(18, $"歌词偏移检查：歌词页角落没有写出当前偏移（{view.OffsetHint ?? "空"}）");
                return false;
            }

            // 菜单是展开时才刷新的，这里手动刷一次（等于用户点开菜单）
            form.RefreshLyricsOffsetMenu();

            var valueItem = menu.DropDownItems.OfType<ToolStripMenuItem>()
                .FirstOrDefault(i => (i.Tag as string) == "lyrics-offset-value");

            var advanceItem = menu.DropDownItems.OfType<ToolStripMenuItem>()
                .FirstOrDefault(i => (i.Tag as string) == "lyrics-offset-advance");

            if (valueItem == null || !valueItem.Text.Contains("提前 0.5 秒", StringComparison.Ordinal))
            {
                Log(18, $"歌词偏移检查：菜单上没有写出当前偏移（「{valueItem?.Text}」）");
                return false;
            }

            if (advanceItem == null || !advanceItem.Enabled)
            {
                Log(18, "歌词偏移检查：有带轴歌词时「歌词提前」却是灰的");
                return false;
            }

            // 「媒体信息」页里也要有一行（和音画 / 字幕延迟并排）
            var rows = ReadInfoRows(form, synced);

            if (!rows.Any(r => r.Name == "歌词偏移" && r.Value == "提前 0.5 秒"))
            {
                Log(18, $"歌词偏移检查：媒体信息里没有歌词偏移这一行（{string.Join(" / ", rows.Select(r => r.Name + "=" + r.Value))}）");
                return false;
            }

            // ---- 热键：Ctrl+] 提前、Ctrl+[ 延后 ----
            if (!SendShortcut(form, Keys.Control | Keys.OemCloseBrackets) || CurrentOffset(form) != 1000)
            {
                Log(18, $"歌词偏移检查：Ctrl+] 没有让歌词提前（现在是 {CurrentOffset(form)} 毫秒）");
                return false;
            }

            if (!SendShortcut(form, Keys.Control | Keys.OemOpenBrackets) || CurrentOffset(form) != 500)
            {
                Log(18, $"歌词偏移检查：Ctrl+[ 没有让歌词延后（现在是 {CurrentOffset(form)} 毫秒）");
                return false;
            }

            // ---- 桌面歌词必须是同一份时间轴 ----
            form.SetDesktopLyricsEnabled(true, true);
            PumpMessages(300);

            if (form.DesktopLyricsWindow is { } window)
            {
                var desktop = window.Document;

                if (desktop == null || desktop.Lines[0].Time != TimeSpan.FromMilliseconds(500))
                {
                    Log(18, $"歌词偏移检查：桌面歌词用的不是同一份时间轴（第一句 {desktop?.Lines[0].Time}）");
                    return false;
                }

                form.SetDesktopLyricsEnabled(false, false);
                PumpMessages(150);
            }

            // ---- 换到一首没有歌词的歌：偏移跟着文件走，菜单如实说明为什么不能调 ----
            form.LoadTrackMetadata(noLyrics);

            if (WaitFor(
                    () => SessionOf(form).Lyrics,
                    document => document.IsEmpty,
                    6) == null)
            {
                Log(18, "歌词偏移检查：等不到切到没有歌词的那一首");
                return false;
            }

            PumpMessages(250);

            if (CurrentOffset(form) != 0)
            {
                Log(18, $"歌词偏移检查：换歌之后偏移没有跟着那个文件走（现在是 {CurrentOffset(form)} 毫秒）");
                return false;
            }

            form.RefreshLyricsOffsetMenu();

            if (advanceItem.Enabled)
            {
                Log(18, "歌词偏移检查：这首根本没有歌词，居然还能调偏移");
                return false;
            }

            if (valueItem.Text.IndexOf("没有歌词", StringComparison.Ordinal) < 0)
            {
                Log(18, $"歌词偏移检查：菜单没有说清楚为什么不能调（「{valueItem.Text}」）");
                return false;
            }

            // 没有时间轴时按热键也不该改任何东西
            SendShortcut(form, Keys.Control | Keys.OemCloseBrackets);
            PumpMessages(100);

            if (CurrentOffset(form) != 0)
            {
                Log(18, "歌词偏移检查：没有时间轴时按 Ctrl+] 居然把偏移改了");
                return false;
            }

            // ---- 切回带轴的那首：这个文件自己的偏移要回来 ----
            form.LoadTrackMetadata(synced);

            if (WaitFor(
                    () => SessionOf(form).Lyrics,
                    document => document.IsSynchronized,
                    6) == null)
            {
                Log(18, "歌词偏移检查：切回来之后等不到带轴歌词");
                return false;
            }

            PumpMessages(250);

            if (CurrentOffset(form) != 500)
            {
                Log(18, $"歌词偏移检查：切回来没有恢复这个文件的偏移（现在是 {CurrentOffset(form)} 毫秒）");
                return false;
            }

            // ---- 归零：角落提示也要跟着消失 ----
            form.ResetLyricsOffset();
            PumpMessages(150);

            if (CurrentOffset(form) != 0 || view.OffsetHint != null)
            {
                Log(18, $"歌词偏移检查：归零不干净（偏移 {CurrentOffset(form)} / 提示 {view.OffsetHint ?? "空"}）");
                return false;
            }

            // ---- 落盘：重启之后这个文件的偏移自己回来 ----
            form.AdjustLyricsOffset(1);
            PumpMessages(150);

            if (WaitFor(
                    () =>
                    {
                        var disk = PlaybackHistory.Load();
                        return disk.TryGetLyricsOffset(synced, out var milliseconds) && milliseconds == 500
                            ? disk
                            : null;
                    },
                    _ => true,
                    6) == null)
            {
                Log(18, "歌词偏移检查：偏移没有写进 history.json（重启就丢了）");
                return false;
            }

            var onDiskBeforeClose = ReadDiskLyricsOffset(synced);

            form.Close();
            PumpMessages(250);

            var onDiskAfterClose = ReadDiskLyricsOffset(synced);

            using (var reopened = new 播放器.MainForm(Array.Empty<string>()))
            {
                reopened.Show();
                PumpMessages(300);

                reopened.LoadTrackMetadata(synced);

                if (WaitFor(
                        () => SessionOf(reopened).Lyrics,
                        document => document.IsSynchronized,
                        6) == null)
                {
                    Log(18, "歌词偏移检查：重新启动之后等不到带轴歌词");
                    return false;
                }

                PumpMessages(250);

                var restored = SessionOf(reopened).LyricsOffsetMilliseconds;

                if (restored != 500)
                {
                    // 三种"偏移没回来"要分得开，否则下次只能猜：
                    //   ① 磁盘上本来就没了（关窗时被写掉）→ 落盘链路的问题
                    //   ② 磁盘上是 500，但重开的窗体当前根本不是这首 → 会话被别的东西清掉了
                    //   ③ 磁盘上是 500、当前也确实是这首，却读到 0 → 读回来那一步的问题
                    var nowPath = SessionOf(reopened).Path;

                    Log(18, $"歌词偏移检查：重新启动之后这个文件的偏移没有回来（{restored} 毫秒）。"
                            + $"磁盘上：关窗前 {onDiskBeforeClose}、关窗后 {onDiskAfterClose}、"
                            + $"现在 {ReadDiskLyricsOffset(synced)} 毫秒；"
                            + $"重开窗体的当前文件「{nowPath ?? "（没有）"}」"
                            + (string.Equals(nowPath, synced, StringComparison.OrdinalIgnoreCase)
                                ? "（就是这首，读回来那一步的问题）"
                                : "（不是这首：会话被别的东西清掉/换掉了）"));
                    return false;
                }

                reopened.Close();
                PumpMessages(150);
            }

            Log(18, "歌词偏移在主窗体上正常：调一下侧栏歌词立刻跟着走（时间与角落提示都对）、"
                    + "Ctrl+] / Ctrl+[ 能提前与延后、桌面歌词用的是同一份时间轴、媒体信息与菜单都写出当前值，"
                    + "偏移跟着文件走、纯文本歌词时菜单如实禁用且热键不改任何东西，重启后偏移自己回来");
            return true;
        }

        private static long CurrentOffset(Form form) =>
            SessionOf(form).LyricsOffsetMilliseconds;

        /// <summary>直接从磁盘上读这个文件的歌词偏移（读不到就是 0）。失败信息里用它区分"谁把偏移弄丢了"。</summary>
        private static long ReadDiskLyricsOffset(string path) =>
            PlaybackHistory.Load().TryGetLyricsOffset(path, out var milliseconds) ? milliseconds : 0;
    }
}
