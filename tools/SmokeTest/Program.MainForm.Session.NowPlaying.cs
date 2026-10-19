// B6：播放列表里「正在播」那一行必须跟着换歌走（加粗 + 主题里的"在播"色）。
//
// 从 Program.MainForm.Session.cs 按 ARCHITECTURE 17.2 的规矩整块搬过来：成员一个字没动。

namespace SmokeTest
{
    internal static partial class Program
    {
        /// <summary>
        /// 播放列表里"正在播的那一行"（加粗 + 主题里的"在播"色）必须跟着换歌走。
        /// <para>
        /// <b>所有者实际踩到</b>：列表里加粗的是<b>上一首</b>，真正在播的是下一首
        /// （标题栏、状态栏、时长都对，只有加粗那一行没动），截图上就是
        /// "选中的是在播那首、加粗的是上一首"——看着就是"播放列表对不上"。
        /// </para>
        /// <para>
        /// 机制（<b>每次换歌都会发生，不是偶发</b>）：<c>PlayIndex</c> 里的顺序是
        /// ① <c>_playlist.SetCurrent(item)</c> → 触发 <c>Changed</c> → <b>整体重建列表</b>，
        /// 而这一刻引擎的路径<b>还是上一首</b> → 加粗就画在上一首上；
        /// ② 之后才 <c>_engine.Open()</c> 把路径换成新的。选中项之所以是对的，
        /// 是因为它由 ③ 的 <c>HighlightPlayingItem()</c> 按引擎路径单独摆一次。
        /// 只有"错误标记变化"那条支路会在 Open 之后再重建一次，那时候才是对的。
        /// </para>
        /// <para>
        /// 所以这里<b>不需要</b>摆什么特别的触发条件：只要连着换两首，
        /// 加粗就必须跟着走，全列表也只能有一行加粗。
        /// </para>
        /// </summary>
        private static bool CheckNowPlayingRowFollowsTrack()
        {
            var first = Path.Combine(AppContext.BaseDirectory, "nowplaying-a.wav");
            var second = Path.Combine(AppContext.BaseDirectory, "nowplaying-b.wav");
            var third = Path.Combine(AppContext.BaseDirectory, "nowplaying-c.wav");

            WriteWav(first, seconds: 6, frequency: 440);
            WriteWav(second, seconds: 6, frequency: 480);
            WriteWav(third, seconds: 6, frequency: 520);

            try
            {
                using var form = new 播放器.MainForm(new[] { first, second, third });
                form.Show();
                PumpMessages(400);

                var list = Find(form, "listViewPlaylist") as ListView;
                var search = Find(form, "txtPlaylistSearch") as TextBox;
                var engine = form.Engine;

                if (list == null || search == null)
                {
                    Log(6, "播放列表加粗检查：找不到列表视图或搜索框");
                    return false;
                }

                if (!PumpUntil(() => engine.IsPlaying && string.Equals(engine.CurrentPath, first, StringComparison.OrdinalIgnoreCase), 15000))
                {
                    Log(6, "播放列表加粗检查：第一首没播起来（现在 "
                            + $"{Path.GetFileName(engine.CurrentPath ?? string.Empty)}）");
                    return false;
                }

                // 等时长都回填完：回填也会重建列表，不等的话下面那次重建"是不是我摆的"就说不清
                if (!PumpUntil(() => PlaylistOf(form).Items.All(item => item.DurationText != "--:--:--"), 8000))
                {
                    Log(6, "播放列表加粗检查：时长一直没回填完，这一段验不下去");
                    return false;
                }

                // 顺手把"整体重建之后标记还在不在"也验一条：在播第一首的时候重建一次列表
                // （搜索框来回清一次就是一次整体重建，走的是用户真会做的操作）
                search.Text = "nowplaying";
                PumpMessages(250);
                search.Text = string.Empty;
                PumpMessages(250);

                if (!NowPlayingRowIs(list, first))
                {
                    Log(6, "播放列表加粗检查：重建之后加粗的不是在播那一首（加粗的是 "
                            + DescribeNowPlayingRows(list) + $"，在播「{Path.GetFileName(first)}」）");
                    return false;
                }

                // 换到第二首：加粗必须跟着走
                var play = FindTopMenuItem(form, "播放");
                var next = play == null ? null : FindMenuItem(play.DropDownItems, "下一个");

                if (next == null)
                {
                    Log(6, "播放列表加粗检查：找不到「播放 → 下一个」");
                    return false;
                }

                next.PerformClick();

                if (!PumpUntil(() => string.Equals(engine.CurrentPath, second, StringComparison.OrdinalIgnoreCase), 15000))
                {
                    Log(6, "播放列表加粗检查：换到第二首没成功（现在在播 "
                            + $"{Path.GetFileName(engine.CurrentPath ?? string.Empty)}）");
                    return false;
                }

                if (!PumpUntil(() => NowPlayingRowIs(list, second), 5000))
                {
                    Log(6, "播放列表：换歌之后加粗的还是上一首"
                            + $"（在播「{Path.GetFileName(second)}」，加粗的是 {DescribeNowPlayingRows(list)}）");
                    return false;
                }

                // 再换一首（这次不摆重建）：加粗同样要跟上，且全列表只有一行加粗
                next.PerformClick();

                if (!PumpUntil(() => string.Equals(engine.CurrentPath, third, StringComparison.OrdinalIgnoreCase), 15000))
                {
                    Log(6, "播放列表加粗检查：换到第三首没成功（现在在播 "
                            + $"{Path.GetFileName(engine.CurrentPath ?? string.Empty)}）");
                    return false;
                }

                if (!PumpUntil(() => NowPlayingRowIs(list, third), 5000))
                {
                    Log(6, "播放列表：连着换歌之后加粗的那一行没跟上"
                            + $"（在播「{Path.GetFileName(third)}」，加粗的是 {DescribeNowPlayingRows(list)}）");
                    return false;
                }

                // 停下来之后：列表里已经"没有在播的了"，按设计退回"加粗列表当前项"
                // （那是"接着从哪儿播"的位置，不是"刚才在播那一首"的残影）。
                // 这里钉住两条：全列表仍然只有一行加粗，而且那一行就是列表的当前项。
                var stop = FindMenuItem(play.DropDownItems, "停止");

                if (stop == null)
                {
                    Log(6, "播放列表加粗检查：找不到「播放 → 停止」");
                    return false;
                }

                stop.PerformClick();
                PumpMessages(400);

                var current = PlaylistOf(form).Current;
                var expectedAfterStop = current?.FilePath ?? string.Empty;

                if (!PumpUntil(() => NowPlayingRowIs(list, expectedAfterStop), 5000))
                {
                    Log(6, "播放列表：停下来之后加粗的那一行不是列表当前项"
                            + $"（加粗的是 {DescribeNowPlayingRows(list)}，当前项是 "
                            + $"{Path.GetFileName(expectedAfterStop)}）");
                    return false;
                }

                Log(6, "播放列表加粗正常：正在播的那一行跟着换歌走（重建之后也认得出在播哪一首），"
                        + "连着换歌都跟得上，全列表始终只有一行加粗；停下来之后退回加粗「列表当前项」"
                        + "（那是「接着从哪儿播」的位置，不是上一首的残影）");
                return true;
            }
            finally
            {
                TryDelete(first);
                TryDelete(second);
                TryDelete(third);
            }
        }

        /// <summary>列表里加粗的那些行对应的文件路径（加粗 = 界面在说"这一首正在播"）。</summary>
        private static List<string> BoldPlaylistRows(ListView list)
        {
            var result = new List<string>();

            foreach (ListViewItem row in list.Items)
            {
                if (row.Font is { Bold: true }) result.Add(row.ToolTipText);
            }

            return result;
        }

        /// <summary>加粗的那一行正好是在播的那一份，而且只有一行加粗。</summary>
        private static bool NowPlayingRowIs(ListView list, string path)
        {
            var bold = BoldPlaylistRows(list);

            return bold.Count == 1 && string.Equals(bold[0], path, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>把加粗那几行说成人看得懂的名字（失败信息里用；一行都没有时说「没有」）。</summary>
        private static string DescribeNowPlayingRows(ListView list)
        {
            var bold = BoldPlaylistRows(list);

            if (bold.Count == 0) return "没有";

            return string.Join(" / ", bold.Select(Path.GetFileName));
        }

    }
}
