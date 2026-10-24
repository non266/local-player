// 检查点 13 的多轨部分：按文件记住音轨 / 字幕轨（1.7.0 F3）。
//
// 素材自己用 ffmpeg 合成（一段蓝画面 + 两条音轨 + 两条字幕轨），
// 不依赖 播放器_TEST_VIDEO；没有 ffmpeg 就打印原因后跳过（照第 13 步的规矩）。

namespace SmokeTest
{
    internal static partial class Program
    {
        /// <summary>
        /// F3：选过音轨 / 字幕轨（含「关闭字幕」）之后，**同一个文件下次打开要自己选回去**；
        /// 声音 / 字幕都必须落到引擎上（不是"菜单里有勾"）。
        /// </summary>
        private static bool CheckTrackMemory()
        {
            if (!TryCreateMultiTrackClip(out var clip, out var reason))
            {
                Log(13, "音轨记忆检查：" + reason + "（跳过）");
                return true;
            }

            try
            {
                // ---- 第一段：选第二条音轨 + 第二条字幕轨 ----
                int chosenAudio;
                int chosenSubtitle;

                using (var form = new 播放器.MainForm(new[] { clip }))
                {
                    form.Show();

                    // 等两件事都成立：引擎认出了 2 音轨 + 2 字幕轨，**并且菜单已经按它们重建过**
                    // （菜单是在 TracksChanged 里建的，可能比引擎属性晚一拍）。
                    ToolStripMenuItem? audioMenu = null;
                    ToolStripMenuItem? subtitleMenu = null;

                    if (!PumpUntil(() =>
                        {
                            if (!form.Engine.HasMedia) return false;

                            var view = FindTopMenuItem(form, "视图");
                            var subtitleRoot = FindTopMenuItem(form, "字幕");

                            audioMenu = view == null ? null : FindMenuItem(view.DropDownItems, "音轨");
                            subtitleMenu = subtitleRoot == null
                                ? null
                                : FindMenuItem(subtitleRoot.DropDownItems, "字幕轨道");

                            return audioMenu != null && subtitleMenu != null
                                   && audioMenu.DropDownItems.OfType<ToolStripMenuItem>().Count() >= 2
                                   && subtitleMenu.DropDownItems.OfType<ToolStripMenuItem>().Count() >= 3;
                        }, 20000))
                    {
                        Log(13, "音轨记忆检查：等不到「2 条音轨 + 2 条字幕轨」的菜单"
                                + $"（引擎里是 {form.Engine.GetAudioTracks().Count} / {form.Engine.GetSubtitleTracks().Count}，"
                                + $"菜单是 {audioMenu?.DropDownItems.Count ?? -1} / {subtitleMenu?.DropDownItems.Count ?? -1} 项）");
                        return false;
                    }

                    var audioItems = audioMenu!.DropDownItems.OfType<ToolStripMenuItem>().ToList();
                    var subtitleItems = subtitleMenu!.DropDownItems.OfType<ToolStripMenuItem>().ToList();

                    // 字幕菜单：第 0 项是「关闭字幕」，之后才是轨道
                    if (audioItems.Count < 2 || subtitleItems.Count < 3)
                    {
                        Log(13, $"音轨记忆检查：菜单项不够（音轨 {audioItems.Count} 项、字幕 {subtitleItems.Count} 项）");
                        return false;
                    }

                    // libvlc 的轨道清单里有 «Disable»（id = -1）：它不是一条真轨道，
                    // 字幕那边与「关闭字幕」重复，音轨那边点下去是"全静音"。
                    if (audioItems.Any(item => item.Tag is int id && id < 0) ||
                        subtitleItems.Any(item => item.Tag is int id && id < 0))
                    {
                        Log(13, "音轨记忆检查：菜单里混进了 libvlc 的「Disable」假轨道（id = -1）——"
                                + $"字幕 {string.Join("/", subtitleItems.Select(i => i.Text))}、"
                                + $"音轨 {string.Join("/", audioItems.Select(i => i.Text))}");
                        return false;
                    }

                    // 故意挑**和默认不一样**的那一条：不然"恢复"和"本来就是它"分不开，变异会假绿
                    var defaultAudio = form.Engine.CurrentAudioTrack;
                    var defaultSubtitle = form.Engine.CurrentSubtitleTrack;

                    var audioTarget = audioItems.FirstOrDefault(i => i.Tag is int id && id >= 0 && id != defaultAudio);
                    var subtitleTarget = subtitleItems.FirstOrDefault(i => i.Tag is int id && id >= 0 && id != defaultSubtitle);

                    if (audioTarget == null || subtitleTarget == null)
                    {
                        Log(13, "音轨记忆检查：找不到与默认不同的音轨 / 字幕轨"
                                + $"（默认 音轨 {defaultAudio} / 字幕 {defaultSubtitle}；"
                                + $"菜单 音轨 {string.Join("/", audioItems.Select(i => i.Text))}、"
                                + $"字幕 {string.Join("/", subtitleItems.Select(i => i.Text))}）");
                        return false;
                    }

                    Log(13, $"音轨记忆：默认 音轨 {defaultAudio} / 字幕 {defaultSubtitle}，"
                            + $"准备选「{audioTarget.Text}」与「{subtitleTarget.Text}」");

                    audioTarget.PerformClick();
                    PumpMessages(300);
                    subtitleTarget.PerformClick();
                    PumpMessages(300);

                    chosenAudio = form.Engine.CurrentAudioTrack;
                    chosenSubtitle = form.Engine.CurrentSubtitleTrack;

                    if (chosenAudio == defaultAudio || chosenSubtitle == defaultSubtitle)
                    {
                        Log(13, "音轨记忆检查：点了之后引擎没换过去"
                                + $"（音轨 {defaultAudio}→{chosenAudio}、字幕 {defaultSubtitle}→{chosenSubtitle}）");
                        return false;
                    }

                    form.Close();
                    PumpMessages(300);
                }

                // ---- 第二段：重新打开同一个文件，应该自己回到刚才那两个 id ----
                using (var form = new 播放器.MainForm(new[] { clip }))
                {
                    form.Show();

                    if (!PumpUntil(() => form.Engine.HasMedia
                                         && form.Engine.CurrentAudioTrack == chosenAudio
                                         && form.Engine.CurrentSubtitleTrack == chosenSubtitle, 20000))
                    {
                        Log(13, "音轨记忆：重新打开之后没有回到上次选的音轨 / 字幕轨"
                                + $"（现在是 音轨 {form.Engine.CurrentAudioTrack} / 字幕 {form.Engine.CurrentSubtitleTrack}，"
                                + $"期望 {chosenAudio} / {chosenSubtitle}；HasMedia={form.Engine.HasMedia}）");
                        return false;
                    }

                    // 再等一会儿，确认不是"短暂闪一下又变回去"
                    PumpMessages(1200);

                    if (form.Engine.CurrentAudioTrack != chosenAudio ||
                        form.Engine.CurrentSubtitleTrack != chosenSubtitle)
                    {
                        Log(13, "音轨记忆：恢复出来的音轨 / 字幕轨没有稳住"
                                + $"（现在 音轨 {form.Engine.CurrentAudioTrack} / 字幕 {form.Engine.CurrentSubtitleTrack}，"
                                + $"期望 {chosenAudio} / {chosenSubtitle}；HasMedia={form.Engine.HasMedia}）");
                        return false;
                    }

                    form.Close();
                    PumpMessages(300);
                }

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

                // ---- 第五段：画面右键菜单（全屏时菜单栏收起来了，这是唯一能选字幕 / 音轨的入口）----
                using (var form = new 播放器.MainForm(new[] { clip }))
                {
                    form.Show();

                    if (!PumpUntil(() => form.Engine.HasMedia
                                         && form.Engine.GetAudioTracks().Count >= 2
                                         && form.Engine.GetSubtitleTracks().Count >= 2, 20000))
                    {
                        Log(13, "画面右键菜单：没等到 2 音轨 + 2 字幕轨");
                        return false;
                    }

                    if (form.VideoMenu == null || form.VideoSubtitleMenu == null || form.VideoAudioMenu == null)
                    {
                        Log(13, "画面右键菜单：没建出来（VideoMenu / 字幕轨道 / 音轨 有一个是空的）");
                        return false;
                    }

                    form.RefreshVideoMenu();

                    var videoSubtitles = form.VideoSubtitleMenu.DropDownItems.OfType<ToolStripMenuItem>().ToList();
                    var videoAudios = form.VideoAudioMenu.DropDownItems.OfType<ToolStripMenuItem>().ToList();

                    if (videoSubtitles.Count < 3 || videoAudios.Count < 2)
                    {
                        Log(13, "画面右键菜单：子菜单项不够"
                                + $"（字幕 {videoSubtitles.Count} 项、音轨 {videoAudios.Count} 项）");
                        return false;
                    }

                    if (videoSubtitles.Any(item => item.Tag is int id && id < 0) ||
                        videoAudios.Any(item => item.Tag is int id && id < 0))
                    {
                        Log(13, "画面右键菜单：混进了 libvlc 的「Disable」假轨道（id = -1）");
                        return false;
                    }

                    // 点一条音轨：声音要真的换过去
                    var audioFrom = form.Engine.CurrentAudioTrack;
                    var audioTarget = videoAudios.FirstOrDefault(item => item.Tag is int id && id >= 0 && id != audioFrom);

                    if (audioTarget == null)
                    {
                        Log(13, "画面右键菜单：找不到与当前不同的音轨"
                                + $"（当前 {audioFrom}；菜单 {string.Join("/", videoAudios.Select(i => i.Text))}）");
                        return false;
                    }

                    audioTarget.PerformClick();
                    PumpMessages(500);

                    if (form.Engine.CurrentAudioTrack != (int)audioTarget.Tag!)
                    {
                        Log(13, "画面右键菜单：点了音轨之后没换过去"
                                + $"（{audioFrom} → {form.Engine.CurrentAudioTrack}，期望 {audioTarget.Tag}）");
                        return false;
                    }

                    // 点一条字幕：要真的打开
                    var subtitleFrom = form.Engine.CurrentSubtitleTrack;
                    var subtitleTarget = videoSubtitles.FirstOrDefault(item => item.Tag is int id && id >= 0 && id != subtitleFrom);

                    if (subtitleTarget == null)
                    {
                        Log(13, "画面右键菜单：找不到可打开的字幕轨"
                                + $"（当前 {subtitleFrom}；菜单 {string.Join("/", videoSubtitles.Select(i => i.Text))}）");
                        return false;
                    }

                    subtitleTarget.PerformClick();
                    PumpMessages(500);

                    if (form.Engine.CurrentSubtitleTrack != (int)subtitleTarget.Tag!)
                    {
                        Log(13, "画面右键菜单：点了字幕轨之后没打开"
                                + $"（{subtitleFrom} → {form.Engine.CurrentSubtitleTrack}，期望 {subtitleTarget.Tag}）");
                        return false;
                    }

                    // 菜单要弹得出来（这里直接给坐标，不依赖合成鼠标）
                    form.ShowVideoMenu(form.PointToScreen(new Point(form.Width / 2, form.Height / 2)));
                    PumpMessages(300);

                    if (!form.VideoMenu.Visible)
                    {
                        Log(13, "画面右键菜单：调用了却弹不出来");
                        return false;
                    }

                    form.VideoMenu.Close();
                    PumpMessages(300);

                    // 合成右键：视频窗口会把鼠标消息吞掉，所以这里验的是"轮询这条路走不走得通"
                    var view = FindVideoView(form);

                    if (view != null)
                    {
                        form.Activate();
                        PumpMessages(300);

                        if (Form.ActiveForm == form)
                        {
                            Cursor.Position = view.PointToScreen(new Point(view.Width / 2, view.Height / 2));
                            PumpMessages(200);

                            mouse_event(MouseEventRightDown, 0, 0, 0, IntPtr.Zero);
                            PumpMessages(120);
                            mouse_event(MouseEventRightUp, 0, 0, 0, IntPtr.Zero);
                            PumpMessages(700);

                            if (!form.VideoMenu.Visible)
                            {
                                Log(13, "画面右键菜单：在画面上按了右键，菜单没弹出来");
                                return false;
                            }

                            form.VideoMenu.Close();
                            PumpMessages(300);
                        }
                        else
                        {
                            Log(13, "画面右键菜单：窗口没被激活，合成右键这一条跳过（菜单内容已单独验过）");
                        }
                    }

                    form.Close();
                    PumpMessages(300);
                }

                Log(13, $"音轨记忆正常：选了音轨 {chosenAudio} / 字幕轨 {chosenSubtitle}，"
                        + "关掉重开都自己回来了；「关闭字幕」也记得住（-1 与「没记过」分得开），"
                        + "关掉之后还能再打开（V 也行），画面右键菜单里能直接选音轨 / 字幕轨");
                return true;
            }
            catch (Exception ex)
            {
                Log(13, "音轨记忆检查失败: " + ex.Message);
                return false;
            }
            finally
            {
                TryDelete(clip);
            }
        }

        /// <summary>失败时把「字幕轨道」菜单的内容一起打出来：菜单空了 / 只剩「关闭字幕」都是线索。</summary>
        private static string DescribeTrackMenu(ToolStripMenuItem? menu)
        {
            if (menu == null) return "（没有「字幕轨道」菜单）";

            var parts = menu.DropDownItems.Cast<ToolStripItem>()
                .Select(item => item is ToolStripSeparator
                    ? "---"
                    : item.Text + (item.Tag == null ? string.Empty : $"({item.Tag})"))
                .ToList();

            return parts.Count == 0 ? "（空）" : string.Join("/", parts);
        }

        /// <summary>找主窗体里那个 <see cref="LibVLCSharp.WinForms.VideoView"/>（设计器字段是私有的）。</summary>
        private static LibVLCSharp.WinForms.VideoView? FindVideoView(Control parent)
        {
            foreach (Control child in parent.Controls)
            {
                if (child is LibVLCSharp.WinForms.VideoView view) return view;

                var nested = FindVideoView(child);

                if (nested != null) return nested;
            }

            return null;
        }
    }
}
