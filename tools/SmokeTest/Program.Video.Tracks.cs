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

                Log(13, $"音轨记忆正常：选了音轨 {chosenAudio} / 字幕轨 {chosenSubtitle}，"
                        + "关掉重开都自己回来了；「关闭字幕」也记得住（-1 与「没记过」分得开）");
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

        /// <summary>
        /// 合成多轨样本：8 秒蓝画面 + 两条 8 秒 WAV + 两条 8 秒 SRT，混成一个 mkv
        /// （2 音轨 + 2 字幕轨）。路径约定与其它 ffmpeg 素材一致（<c>播放器_TEST_FFMPEG</c>）。
        /// </summary>
        private static bool TryCreateMultiTrackClip(out string path, out string reason)
        {
            path = Path.Combine(AppContext.BaseDirectory, "tracks-sample.mkv");
            reason = string.Empty;

            var ffmpeg = Environment.GetEnvironmentVariable("播放器_TEST_FFMPEG");

            if (string.IsNullOrWhiteSpace(ffmpeg))
                ffmpeg = @"D:\ffmpeg-8.0-full_build\bin\ffmpeg.exe";

            if (!File.Exists(ffmpeg))
            {
                reason = "没有 ffmpeg（设 播放器_TEST_FFMPEG 指一个）";
                return false;
            }

            var first = Path.Combine(AppContext.BaseDirectory, "tracks-a1.wav");
            var second = Path.Combine(AppContext.BaseDirectory, "tracks-a2.wav");
            var subtitle1 = Path.Combine(AppContext.BaseDirectory, "tracks-s1.srt");
            var subtitle2 = Path.Combine(AppContext.BaseDirectory, "tracks-s2.srt");

            WriteWav(first, seconds: 8, frequency: 440);
            WriteWav(second, seconds: 8, frequency: 880);

            var srt = "1" + Environment.NewLine
                      + "00:00:00,000 --> 00:00:04,000" + Environment.NewLine
                      + "First" + Environment.NewLine + Environment.NewLine
                      + "2" + Environment.NewLine
                      + "00:00:04,000 --> 00:00:08,000" + Environment.NewLine
                      + "Second" + Environment.NewLine;

            File.WriteAllText(subtitle1, srt, new System.Text.UTF8Encoding(false));
            File.WriteAllText(subtitle2, srt, new System.Text.UTF8Encoding(false));

            TryDelete(path);

            var arguments = new[]
            {
                "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", "color=c=blue:s=320x240:d=8:r=10",
                "-i", first,
                "-i", second,
                "-i", subtitle1,
                "-i", subtitle2,
                "-map", "0:v", "-map", "1:a", "-map", "2:a", "-map", "3:s", "-map", "4:s",
                "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
                "-c:a", "aac", "-b:a", "96k",
                "-c:s", "srt",
                "-metadata:s:a:0", "language=eng", "-metadata:s:a:0", "title=English",
                "-metadata:s:a:1", "language=jpn", "-metadata:s:a:1", "title=Japanese",
                "-metadata:s:s:0", "language=eng", "-metadata:s:s:0", "title=EnglishSub",
                "-metadata:s:s:1", "language=chi", "-metadata:s:s:1", "title=ChineseSub",
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

                if (!process.WaitForExit(60000))
                {
                    try { process.Kill(entireProcessTree: true); } catch (Exception) { /* 杀不掉就算了 */ }

                    reason = "ffmpeg 60 秒没跑完";
                    return false;
                }

                if (process.ExitCode != 0 || !File.Exists(path))
                {
                    reason = $"ffmpeg 合成多轨样本失败（退出码 {process.ExitCode}）";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                reason = "调 ffmpeg 失败：" + ex.Message;
                return false;
            }
            finally
            {
                TryDelete(first);
                TryDelete(second);
                TryDelete(subtitle1);
                TryDelete(subtitle2);
            }
        }
    }
}
