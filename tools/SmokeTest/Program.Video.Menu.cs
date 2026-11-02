// 检查点 13：「关闭字幕」的按文件记忆 + V 开关（整块搬自 Program.Video.Tracks.cs）。
//
// 1.8.0 的 B7 就是这一段抓出来的：字幕一关就"打不开"、菜单第一项永远写着「关闭字幕」。
// 搬迁时只把缩进降了一级（原来在大方法的 try 里），语句一个字没改。

namespace SmokeTest
{
    internal static partial class Program
    {
        /// <summary>
        /// 第三～四段：「关闭字幕」要记住（<c>-1</c> 与"没记过"是两件事）、<c>V</c> 关掉之后还能打开。
        /// </summary>
        private static bool CheckSubtitleMemoryAndToggle(string clip)
        {
            // ---- 第三段：「关闭字幕」也要记住（-1 与"没记过"是两件事）----
            using (var form = new 播放器.MainForm(new[] { clip }))
            {
                form.Show();

                if (!PumpUntil(() => form.Engine.HasMedia && form.Engine.GetSubtitleTracks().Count >= 2, 20000))
                {
                    Log(13, "音轨记忆检查：第三段没等到字幕轨");
                    return false;
                }

                var subtitleRoot = FindTopMenuItem(form, "字幕");
                var subtitleMenu = subtitleRoot == null ? null : FindMenuItem(subtitleRoot.DropDownItems, "字幕轨道");
                var off = subtitleMenu == null ? null : FindMenuItem(subtitleMenu.DropDownItems, "关闭字幕");

                if (off == null)
                {
                    Log(13, "音轨记忆检查：字幕菜单里没有「关闭字幕」");
                    return false;
                }

                off.PerformClick();
                PumpMessages(400);

                if (form.Engine.CurrentSubtitleTrack != -1)
                {
                    Log(13, "音轨记忆检查：点了「关闭字幕」之后字幕还开着"
                            + $"（当前 {form.Engine.CurrentSubtitleTrack}）");
                    return false;
                }

                // 关掉之后还要能再打开（用户报过：字幕一关就"打不开"了）。
                // 菜单可能在 TracksChanged 里重建过，所以重新找一遍，别拿旧对象。
                subtitleRoot = FindTopMenuItem(form, "字幕");
                subtitleMenu = subtitleRoot == null ? null : FindMenuItem(subtitleRoot.DropDownItems, "字幕轨道");

                var reopenItems = subtitleMenu?.DropDownItems.OfType<ToolStripMenuItem>()
                    .Where(i => i.Tag is int id && id >= 0).ToList() ?? new List<ToolStripMenuItem>();

                if (reopenItems.Count == 0)
                {
                    Log(13, "字幕重开：关掉字幕之后菜单里没有可选的轨道了"
                            + $"（引擎里还剩 {form.Engine.GetSubtitleTracks().Count} 条；"
                            + $"菜单：{DescribeTrackMenu(subtitleMenu)}）");
                    return false;
                }

                var reopen = reopenItems[0];
                var reopenId = (int)reopen.Tag!;
                reopen.PerformClick();

                if (!PumpUntil(() => form.Engine.CurrentSubtitleTrack == reopenId, 8000))
                {
                    Log(13, "字幕重开：关了字幕之后再也打不开"
                            + $"（点了「{reopen.Text}」，现在 第 {form.Engine.CurrentSubtitleTrack} 条，"
                            + $"期望 {reopenId}；引擎里 {form.Engine.GetSubtitleTracks().Count} 条；"
                            + $"菜单：{DescribeTrackMenu(subtitleMenu)}）");
                    return false;
                }

                // 再关回去：下一段"重新打开还是关的"要用到这份记忆
                subtitleRoot = FindTopMenuItem(form, "字幕");
                subtitleMenu = subtitleRoot == null ? null : FindMenuItem(subtitleRoot.DropDownItems, "字幕轨道");
                var offAgain = subtitleMenu == null ? null : FindMenuItem(subtitleMenu.DropDownItems, "关闭字幕");

                if (offAgain == null)
                {
                    Log(13, "字幕重开：第二次找「关闭字幕」没找到"
                            + $"（菜单：{DescribeTrackMenu(subtitleMenu)}）");
                    return false;
                }

                offAgain.PerformClick();
                PumpMessages(300);

                if (form.Engine.CurrentSubtitleTrack != -1)
                {
                    Log(13, "字幕重开：第二次点「关闭字幕」没关掉"
                            + $"（当前 {form.Engine.CurrentSubtitleTrack}）");
                    return false;
                }

                // ---- V：关掉之后再按一次要能打开（用户报的"字幕关了打不开"）----
                if (FindMenuItem(subtitleRoot!.DropDownItems, "打开字幕") == null)
                {
                    Log(13, "字幕开关：关掉字幕之后，「字幕」菜单第一项还写着「关闭字幕」——"
                            + "再点它一次还是关，用户当然以为坏了");
                    return false;
                }

                form.ToggleSubtitles();
                PumpMessages(400);

                if (form.Engine.CurrentSubtitleTrack != reopenId)
                {
                    Log(13, "字幕开关：按 V 之后字幕没有回到刚才那条"
                            + $"（现在 第 {form.Engine.CurrentSubtitleTrack} 条，期望 {reopenId}）");
                    return false;
                }

                if (FindMenuItem(subtitleRoot.DropDownItems, "关闭字幕") == null)
                {
                    Log(13, "字幕开关：打开之后「字幕」菜单第一项没变回「关闭字幕」");
                    return false;
                }

                // 再按一次 V 关掉：下一段"重新打开还是关的"要用这份记忆
                form.ToggleSubtitles();
                PumpMessages(400);

                if (form.Engine.CurrentSubtitleTrack != -1)
                {
                    Log(13, "字幕开关：再按一次 V 没关掉"
                            + $"（现在 {form.Engine.CurrentSubtitleTrack}）");
                    return false;
                }

                form.Close();
                PumpMessages(300);
            }

            using (var form = new 播放器.MainForm(new[] { clip }))
            {
                form.Show();

                if (!PumpUntil(() => form.Engine.HasMedia && form.Engine.CurrentSubtitleTrack == -1, 20000))
                {
                    Log(13, "字幕记忆：上次特意关掉了字幕，重新打开却又自己选上了"
                            + $"（现在 第 {form.Engine.CurrentSubtitleTrack} 条；HasMedia={form.Engine.HasMedia}）");
                    return false;
                }

                // libvlc 有时会在播放开始后自己选一条字幕：等一会儿再说，别被瞬时值骗了
                PumpMessages(1500);

                if (form.Engine.CurrentSubtitleTrack != -1)
                {
                    Log(13, "字幕记忆：刚恢复成关闭，过一会儿字幕又自己打开了"
                            + $"（现在 第 {form.Engine.CurrentSubtitleTrack} 条；HasMedia={form.Engine.HasMedia}）");
                    return false;
                }

                form.Close();
                PumpMessages(300);
            }
            return true;
        }
    }
}
