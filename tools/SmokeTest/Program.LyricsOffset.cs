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


        private static long CurrentOffset(Form form) =>
            SessionOf(form).LyricsOffsetMilliseconds;

        /// <summary>直接从磁盘上读这个文件的歌词偏移（读不到就是 0）。失败信息里用它区分"谁把偏移弄丢了"。</summary>
        private static long ReadDiskLyricsOffset(string path) =>
            PlaybackHistory.Load().TryGetLyricsOffset(path, out var milliseconds) ? milliseconds : 0;
    }
}
