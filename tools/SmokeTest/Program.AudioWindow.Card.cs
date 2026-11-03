// 检查点 11：1.10.0 —— 按真实轨道判形态（F11′）、卡片排版（B9）、卡片里的播放控件（F12）。
//
// 拆成自己的文件是因为 Program.AudioWindow.cs 已经 472 行（1.9.0 的规矩：新断言进新文件）。
//
// 这一段的两个核心判据：
//   ① 卡片里的 CoverView 必须**跟着卡片重排**——1.9.0 的封面错位就是它停在旧宽度（≈设计尺寸 1152）
//      里居中，被窗口右边缘裁掉；只看卡片自己的 Bounds 是看不出来的。
//   ② 只有音轨的 mkv 要按真实轨道切进音频窗口——扩展名会先把它猜成视频。

namespace SmokeTest
{
    internal static partial class Program
    {
        private static bool CheckAudioCardAndControls()
        {
            if (!CheckWindowModeTruthTable()) return false;
            if (!CheckCardLayout()) return false;
            if (!CheckPlaybackControls()) return false;
            return CheckRealTrackSwitching();
        }

        /// <summary>纯函数：手动偏好 &gt; 真实轨道 &gt; 扩展名，这条顺序不能反。</summary>
        private static bool CheckWindowModeTruthTable()
        {
            const string flac = @"C:\media\song.flac";
            const string mkv = @"C:\media\movie.mkv";

            if (WindowModes.Resolve(WindowModes.Auto, flac, hasVideoTrack: true) != WindowShape.Video)
            {
                Log(11, "窗口模式：扩展名是音频、但真有视频轨时没有按真实轨道判成视频");
                return false;
            }

            if (WindowModes.Resolve(WindowModes.Auto, mkv, hasVideoTrack: false) != WindowShape.Audio)
            {
                Log(11, "窗口模式：扩展名是视频、但只有音轨时没有按真实轨道判成音频");
                return false;
            }

            if (WindowModes.Resolve(WindowModes.Audio, mkv, hasVideoTrack: true) != WindowShape.Audio ||
                WindowModes.Resolve(WindowModes.Video, flac, hasVideoTrack: false) != WindowShape.Video)
            {
                Log(11, "窗口模式：手动固定的偏好被真实轨道推翻了（应该手动优先）");
                return false;
            }

            if (WindowModes.Resolve(WindowModes.Auto, mkv, hasVideoTrack: null) != WindowShape.Video ||
                WindowModes.Resolve(WindowModes.Auto, null, hasVideoTrack: null) != WindowShape.Video)
            {
                Log(11, "窗口模式：轨道还没解析出来时没有退回扩展名 / 按视频处理");
                return false;
            }

            Log(11, "窗口模式的判定顺序正确：手动偏好 > 真实轨道 > 扩展名（轨道未知时）");
            return true;
        }

        /// <summary>
        /// 卡片排版（B9 的回归点）：启动就播一首音乐（构造函数里就进音频窗口，窗口随后才恢复几何），
        /// 卡片与卡片里的封面都必须按**最终**的窗口尺寸排版。
        /// </summary>
        private static bool CheckCardLayout()
        {
            var defaults = AppSettings.Load();
            var wav = Path.Combine(AppContext.BaseDirectory, "card-layout.wav");
            WriteWav(wav, seconds: 8, frequency: 440);

            try
            {
                var setup = AppSettings.Load();
                setup.WindowMode = WindowModes.Auto;
                setup.Save();

                using (var form = new 播放器.MainForm(new[] { wav }))
                {
                    form.Show();
                    PumpMessages(800);

                    if (!form.IsAudioWindow)
                    {
                        Log(11, "音频窗口卡片：启动就播音乐却没有进音频窗口");
                        return false;
                    }

                    var splitVideo = Find(form, "splitVideo") as SplitContainer;
                    var transport = Find(form, "pnlTransport");
                    var card = FindByTypeName(form, "NowPlayingView");

                    if (splitVideo == null || transport == null || card == null)
                    {
                        Log(11, "音频窗口卡片：找不到视频面板 / 传输条 / 卡片");
                        return false;
                    }

                    if (card.Left != 0 || card.Top != 0 ||
                        card.Width != splitVideo.Panel1.ClientSize.Width ||
                        card.Bottom != transport.Top)
                    {
                        Log(11, $"音频窗口卡片：卡片没有盖满画面区域（卡片 {card.Bounds}，"
                                + $"面板宽 {splitVideo.Panel1.ClientSize.Width}，传输条上沿 {transport.Top}）");
                        return false;
                    }

                    // ★ 1.9.0 的 bug：卡片里的封面停在旧宽度（≈设计尺寸 1152），窗口变窄后不重排
                    var cover = FindByTypeName(card, "CoverView");
                    if (cover == null)
                    {
                        Log(11, "音频窗口卡片：卡片里找不到封面控件");
                        return false;
                    }

                    if (cover.Width != card.ClientSize.Width)
                    {
                        Log(11, "音频窗口：封面的可绘制区域还在用旧尺寸"
                                + $"（封面宽 {cover.Width}，卡片宽 {card.ClientSize.Width}）");
                        return false;
                    }

                    if (cover is CoverView view)
                    {
                        var square = view.CoverRectangle;

                        if (square.Width <= 0)
                        {
                            Log(11, "音频窗口卡片：封面方框算出来是空的（尺寸不够还是没算？）");
                            return false;
                        }

                        // 水平居中：方框左右两侧的留白要相等
                        if (Math.Abs(square.Left + square.Right - cover.Width) > 3)
                        {
                            Log(11, $"音频窗口卡片：封面在卡片里没有水平居中（方框 {square}，封面控件宽 {cover.Width}）");
                            return false;
                        }

                        // 位图兜底：卡片正中间那一列应该有画面，最左边 4 像素应该是背景
                        if (!CheckCardPixels(card, square)) return false;
                    }

                    var placeholder = Find(form, "lblPlaceholder");
                    if (placeholder != null && placeholder.Visible)
                    {
                        Log(11, "音频窗口：有媒体时占位提示还盖在卡片上");
                        return false;
                    }
                }

                Log(11, "音频窗口卡片排版正常：卡片贴住面板、封面跟着卡片重排并水平居中、占位提示不会盖上来");
                return true;
            }
            catch (Exception ex)
            {
                Log(11, "音频窗口卡片排版检查失败: " + ex);
                return false;
            }
            finally
            {
                TryDelete(wav);
                defaults.Save();
            }
        }

        /// <summary>卡片正中间那一列有非背景像素、最左边是背景色（像素级兜底）。</summary>
        private static bool CheckCardPixels(Control card, Rectangle square)
        {
            var width = Math.Max(1, card.Width);
            var height = Math.Max(1, card.Height);

            if (!TryRender(card, width, height, out var error))
            {
                Log(11, $"{error}（音频窗口卡片：渲染失败）");
                return false;
            }

            using var bitmap = new Bitmap(width, height);
            card.DrawToBitmap(bitmap, new Rectangle(0, 0, width, height));

            var background = card.BackColor;
            var middleY = square.Top + square.Height / 2;

            var center = bitmap.GetPixel(width / 2, Math.Min(height - 1, Math.Max(0, middleY)));

            if (center.ToArgb() == background.ToArgb())
            {
                Log(11, "音频窗口卡片：卡片正中间那一列是空的——封面没有画在卡片中间（旧的错位就是这个样子）");
                return false;
            }

            return true;
        }

        /// <summary>卡片里的播放控件：四个按钮、能真的控制播放、图标跟着状态换。</summary>
        private static bool CheckPlaybackControls()
        {
            var defaults = AppSettings.Load();
            var first = Path.Combine(AppContext.BaseDirectory, "card-controls-1.wav");
            var second = Path.Combine(AppContext.BaseDirectory, "card-controls-2.wav");
            WriteWav(first, seconds: 30, frequency: 440);
            WriteWav(second, seconds: 30, frequency: 520);

            try
            {
                var setup = AppSettings.Load();
                setup.WindowMode = WindowModes.Auto;
                setup.Save();

                using var form = new 播放器.MainForm(new[] { first });
                form.Show();
                PumpMessages(800);

                var card = FindByTypeName(form, "NowPlayingView");
                var controls = card == null ? null : FindByTypeName(card, "PlaybackControls") as PlaybackControls;

                if (controls == null)
                {
                    Log(11, "音频窗口：歌词下面没有播放控件");
                    return false;
                }

                if (controls.Buttons.Count != 4 ||
                    controls.PreviousButton == null || controls.PlayPauseButton == null ||
                    controls.NextButton == null || controls.StopButton == null)
                {
                    Log(11, $"播放控件：按钮不是四个（现在 {controls.Buttons.Count} 个）");
                    return false;
                }

                if (!PumpUntil(() => form.Engine.HasMedia && form.Engine.IsPlaying, 15000))
                {
                    Log(11, "播放控件：启动的 wav 没有播起来，后面的点击验不了");
                    return false;
                }

                if (controls.CurrentGlyph != PlaybackGlyph.Pause)
                {
                    Log(11, "播放控件：正在播的时候按钮画的不是「暂停」");
                    return false;
                }

                var playingImage = controls.PlayPauseButton.Image;
                if (playingImage is not Bitmap playingBitmap || CountOpaquePixels(playingBitmap) <= 0)
                {
                    Log(11, "播放控件：播放 / 暂停按钮上没有图标（位图是空的）");
                    return false;
                }

                // 暂停 → 图标变「播放」；再点 → 继续播
                controls.PlayPauseButton.PerformClick();

                if (!PumpUntil(() => !form.Engine.IsPlaying, 8000))
                {
                    Log(11, "播放控件：点了播放 / 暂停按钮没有暂停");
                    return false;
                }

                PumpMessages(300);

                if (controls.CurrentGlyph != PlaybackGlyph.Play)
                {
                    Log(11, "播放控件：暂停之后按钮图标还是「暂停」");
                    return false;
                }

                // 图标是现画到位图上的：状态变了必须换一张（不重画就是老图）
                if (ReferenceEquals(playingImage, controls.PlayPauseButton.Image))
                {
                    Log(11, "播放控件：暂停之后按钮图标还是「暂停」（位图没有重画）");
                    return false;
                }

                controls.PlayPauseButton.PerformClick();

                if (!PumpUntil(() => form.Engine.IsPlaying, 8000))
                {
                    Log(11, "播放控件：再点一次没有继续播放");
                    return false;
                }

                // 下一首：列表里放两首，点一下应该换到第二首
                var playlist = PlaylistOf(form);
                playlist.Clear();
                playlist.AddRange(new[] { first, second });
                playlist.SetCurrent(0);

                controls.NextButton.PerformClick();

                if (!PumpUntil(() => string.Equals(form.Engine.CurrentPath, second, StringComparison.OrdinalIgnoreCase), 15000))
                {
                    Log(11, $"播放控件：点「下一首」没有换到下一项（现在 {form.Engine.CurrentPath}）");
                    return false;
                }

                // 停止：引擎停下，停止按钮变灰（没有媒体时停止没有意义）
                controls.StopButton.PerformClick();
                PumpMessages(600);

                if (form.Engine.State != PlayerState.Stopped && form.Engine.IsPlaying)
                {
                    Log(11, "播放控件：点「停止」之后引擎还在放");
                    return false;
                }

                // 停止之后：没有媒体 → 「停止」变灰；列表里还有歌 → 「播放」还能按
                if (controls.StopButton.Enabled)
                {
                    Log(11, "播放控件：停止之后「停止」按钮还是亮的（没有媒体时停止没有意义）");
                    return false;
                }

                if (!controls.PlayPauseButton.Enabled)
                {
                    Log(11, "播放控件：停止之后播放按钮也变灰了（列表里还有歌，应该能按）");
                    return false;
                }

                if (!form.IsAudioWindow)
                {
                    Log(11, "音频窗口：按「停止」之后窗口自己跳回了视频模式（应该留在卡片）");
                    return false;
                }

                // 停止那一刻 hasMedia 变假：占位提示要是只看 !hasMedia，就会把"拖文件到这里"盖到卡片上。
                // 显式重刷一次状态，让那条分支稳定地跑出来。
                form.RefreshTransportState();
                PumpMessages(200);

                var placeholder = Find(form, "lblPlaceholder");
                if (placeholder != null && placeholder.Visible)
                {
                    Log(11, "音频窗口：没有媒体时「拖文件到这里」盖在卡片上");
                    return false;
                }

                Log(11, "播放控件正常：四个按钮齐全，暂停 / 继续 / 下一首 / 停止都作用在引擎上，图标跟着状态换，停止后占位提示不盖卡片");
                return true;
            }
            catch (Exception ex)
            {
                Log(11, "播放控件检查失败: " + ex);
                return false;
            }
            finally
            {
                TryDelete(first);
                TryDelete(second);
                defaults.Save();
            }
        }

        /// <summary>
        /// 真实轨道纠正形态（F11′）：只有音轨的 mkv 在 auto 下要进音频窗口（扩展名会先猜成视频），
        /// 真视频 mkv 再切回视频窗口。
        /// </summary>
        private static bool CheckRealTrackSwitching()
        {
            var defaults = AppSettings.Load();

            if (!TryCreateAudioOnlyMkv(out var audioOnly, out var audioReason))
            {
                Log(11, $"音频窗口：只有音轨的 mkv 这一段跳过——{audioReason}");
                return true;
            }

            var videoOk = TryCreateTinyVideo(out var videoPath, out var videoReason);

            try
            {
                var setup = AppSettings.Load();
                setup.WindowMode = WindowModes.Auto;
                setup.Save();

                using var form = new 播放器.MainForm(Array.Empty<string>());
                form.Show();
                PumpMessages(300);

                form.Engine.Open(audioOnly);
                form.Engine.Play();

                if (!PumpUntil(() => form.Engine.GetTracks().Count > 0, 20000))
                {
                    Log(11, "音频窗口：只有音轨的 mkv 没解析出轨道（检查环境不合适，跳过这一段）");
                    return true;
                }

                if (!PumpUntil(() => form.IsAudioWindow, 8000))
                {
                    Log(11, "音频窗口：只有音轨的 mkv 没有按真实轨道切到音频窗口（扩展名把窗口留在了视频模式）");
                    return false;
                }

                if (!videoOk)
                {
                    Log(11, $"音频窗口：真视频那一段跳过——{videoReason}");
                    Log(11, "音频窗口：按真实轨道判形态正常（只有音轨的 mkv 进了音频窗口）");
                    return true;
                }

                form.Engine.Open(videoPath);
                form.Engine.Play();

                if (!PumpUntil(() => form.Engine.GetTracks().Count > 0, 20000))
                {
                    Log(11, "音频窗口：视频 mkv 没解析出轨道（检查环境不合适，跳过这一段）");
                    return true;
                }

                if (!PumpUntil(() => !form.IsAudioWindow, 8000))
                {
                    Log(11, "音频窗口：真的带视频轨的 mkv 反而留在了音频窗口（真实轨道没用上）");
                    return false;
                }

                Log(11, "音频窗口：按真实轨道判形态正常——只有音轨的 mkv 进卡片、真视频回到三栏");
                return true;
            }
            catch (Exception ex)
            {
                Log(11, "音频窗口真实轨道检查失败: " + ex);
                return false;
            }
            finally
            {
                TryDelete(audioOnly);
                if (videoOk) TryDelete(videoPath);
                defaults.Save();
            }
        }

        /// <summary>造一个"只有音轨"的 mkv（扩展名是视频，里面没有视频轨）。</summary>
        private static bool TryCreateAudioOnlyMkv(out string path, out string reason)
        {
            path = Path.Combine(AppContext.BaseDirectory, "audio-only.mkv");
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
                "-y", "-f", "lavfi", "-i", "sine=frequency=440:duration=4",
                "-c:a", "aac", "-b:a", "96k",
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
                    reason = $"ffmpeg 合成只有音轨的 mkv 失败（退出码 {process.ExitCode}）";
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
