
// 只 Dispose 的表单 / 系统媒体控件（SMTC）/ WndProc 大句柄健壮性。

namespace SmokeTest
{
    internal static partial class Program
    {
        /// <summary>
        /// 只 Dispose 没 Close 的表单必须收干净。
        /// <para>
        /// 这是 1.3.0 查"第 18 步偶发红"时揪出来的：界面计时器以前<b>只在 FormClosed 里停</b>，
        /// 而冒烟里到处是 <c>using var form</c>（只 Dispose、从不 Close），于是那个表单的
        /// 200 ms 计时器<b>一直跑下去</b>——每 200 ms 读一次引擎、每 100 拍（20 秒）把
        /// <b>自己那份旧历史</b>写回 <c>history.json</c>。历史是"一个实例一份内存副本、共写同一个文件"，
        /// 所以僵尸那一下会把别的实例刚写进去的记录盖掉：第 18 步"偏移没写进 history.json"
        /// 就是这么偶发红的（单跑这一步从来是绿的——那会儿还没有僵尸）。
        /// </para>
        /// </summary>
        private static bool CheckDisposedFormStopsTimers()
        {
            var wav = Path.Combine(AppContext.BaseDirectory, "dispose-form.wav");
            WriteWav(wav, seconds: 30, frequency: 440);

            try
            {
                var form = new 播放器.MainForm(new[] { wav });

                try
                {
                    form.Show();
                    PumpMessages(400);

                    if (!form.UiTimerRunning)
                    {
                        Log(6, "只 Dispose 检查：刚打开窗体时界面计时器就没在跑");
                        return false;
                    }

                    PumpUntil(() => form.Engine.HasMedia && form.Engine.IsPlaying, 10000);

                    // 冒烟里最常见的那种用法：只 Dispose，不 Close
                    form.Dispose();
                    PumpMessages(300);

                    if (form.UiTimerRunning)
                    {
                        Log(6, "只 Dispose 的表单还在跑界面计时器"
                                + "（它会每 20 秒把自己那份旧历史写回磁盘，盖掉别人刚写的记录）");
                        return false;
                    }

                    Log(6, "只 Dispose 的表单收尾正常：界面计时器停了"
                            + "（以前它会一直跑，每 20 秒把旧历史写回磁盘）");
                    return true;
                }
                finally
                {
                    form.Dispose();
                }
            }
            finally
            {
                TryDelete(wav);
            }
        }

        /// <summary>
        /// C-1（1.3.0）：系统媒体控件（SMTC，音量弹窗 / 锁屏里那一块"正在播放"）。
        /// <para>
        /// <b>验到哪一层</b>：这里验的是"接口拿到了、<c>IsEnabled</c> 为真、曲名 / 歌手 / 状态 / 时长
        /// 喂进去之后<b>读得回来</b>"——那几个 getter 是<b>真的去问系统那个对象</b>的，
        /// 不是记我们自己的变量。封面这一版也接上了：验的是"没内嵌图时挑了旁边那张
        /// <c>cover.jpg</c>、交给系统的图片文件系统那边确实读了（<c>CoverSet</c>）、
        /// 切到没有封面的那首会清空"。但<b>系统界面上到底画没画出来，冒烟验不到</b>，只能人眼看一次
        /// （README 与 CHANGELOG 里都写着这一条）。
        /// </para>
        /// <para>
        /// 拿不到接口（老系统 / 被策略禁掉）时<b>打印原因后跳过</b>——照 <c>播放器_TEST_VIDEO</c> 的先例，
        /// 跳过必须出声。
        /// </para>
        /// </summary>
        private static bool CheckSystemMediaControls()
        {
            // 两首各放一个目录：第一首旁边没有封面图，第二首旁边放一张 cover.jpg
            // （外加一张干扰用的 album.jpg）。这样"封面从哪来"这条断言才有靶子
            // ——这两首 WAV 都没有内嵌图。
            var smokeRoot = Path.Combine(AppContext.BaseDirectory, "smoke-smtc");
            var plainFolder = Path.Combine(smokeRoot, "plain");
            var artFolder = Path.Combine(smokeRoot, "art");

            TryDeleteDirectory(smokeRoot);
            Directory.CreateDirectory(plainFolder);
            Directory.CreateDirectory(artFolder);

            var first = Path.Combine(plainFolder, "smtc-a.wav");
            var second = Path.Combine(artFolder, "smtc-b.wav");
            var sidecar = Path.Combine(artFolder, "cover.jpg");

            // 干扰项：按文件名排序 album.jpg 排在 cover.jpg 前面。
            // 挑封面不能看文件系统的枚举顺序，得按"常见命名"的优先级——cover.jpg 赢。
            var rival = Path.Combine(artFolder, "album.jpg");

            WriteWav(first, seconds: 30, frequency: 440);
            WriteWav(second, seconds: 30, frequency: 480);
            File.WriteAllBytes(sidecar, BuildJpeg());
            File.WriteAllBytes(rival, BuildJpeg());

            try
            {
                // 这一段是纯规则（不碰系统媒体控件），所以放在"拿不到接口就跳过"之前：
                // 老系统上 SMTC 会跳过，但"挑哪张外挂封面"照样得验。
                var picked = SidecarCover.FindInFolder(artFolder);

                if (!string.Equals(picked, sidecar, StringComparison.OrdinalIgnoreCase))
                {
                    Log(6, "挑外挂封面：同一目录里 cover.jpg 与 album.jpg 都在时没有按命名优先级选"
                            + $"（选中了「{Path.GetFileName(picked ?? "没有")}」，期望 cover.jpg）");
                    return false;
                }

                if (SidecarCover.HasInFolder(plainFolder))
                {
                    Log(6, "挑外挂封面：没有封面图的目录被判成有封面");
                    return false;
                }

                using var form = new 播放器.MainForm(new[] { first, second });
                form.Show();
                PumpMessages(500);

                var smtc = form.SystemMediaControls;

                if (smtc == null)
                {
                    Log(6, "系统媒体控件检查：跳过（这台机器没接上："
                            + (播放器.Ui.SmtcSession.UnavailableReason ?? "没给理由") + "）");
                    return true;
                }

                if (!smtc.IsEnabled)
                {
                    Log(6, "系统媒体控件检查：接口接上了，但系统那边 IsEnabled 是假");
                    return false;
                }

                if (!PumpUntil(() => form.Engine.IsPlaying, 10000))
                {
                    Log(6, "系统媒体控件检查：WAV 没播起来");
                    return false;
                }

                // 素材没有标签，所以曲名走的是"文件名兜底"，和界面上「标题」那一行同源
                var firstName = Path.GetFileNameWithoutExtension(first);

                if (!PumpUntil(() => smtc.Title == firstName, 6000))
                {
                    Log(6, $"系统媒体控件检查：曲名没喂进去（现在是「{smtc.Title}」，期望「{firstName}」）");
                    return false;
                }

                if (smtc.Status != Windows.Media.MediaPlaybackStatus.Playing)
                {
                    Log(6, $"系统媒体控件检查：正在播，系统那边的状态却是 {smtc.Status}（期望 Playing）");
                    return false;
                }

                // 第一首旁边没有封面图：封面来源必须是空的
                if (smtc.CoverPath != null)
                {
                    Log(6, "系统媒体控件检查：这首旁边没有封面图，封面来源却不是空的（"
                            + Path.GetFileName(smtc.CoverPath) + "）");
                    return false;
                }

                // 那五个按钮：开关都必须是开的（否则弹窗上根本不显示按钮）
                if (!smtc.IsPlayEnabled || !smtc.IsPauseEnabled || !smtc.IsStopEnabled ||
                    !smtc.IsNextEnabled || !smtc.IsPreviousEnabled)
                {
                    Log(6, "系统媒体控件检查：按钮开关没全开"
                            + $"（播放 {smtc.IsPlayEnabled} / 暂停 {smtc.IsPauseEnabled} / 停止 {smtc.IsStopEnabled} /"
                            + $" 上一个 {smtc.IsPreviousEnabled} / 下一个 {smtc.IsNextEnabled}）");
                    return false;
                }

                // 按钮按下之后我们要做的事（系统真的有没有把事件送过来，冒烟验不到——如实写在日志里）
                // 「暂停 / 播放」那一对
                smtc.SimulateButton(播放器.Ui.SmtcButton.Pause);
                PumpMessages(200);

                if (form.Engine.IsPlaying)
                {
                    Log(6, "系统媒体控件检查：按了「暂停」之后还在播");
                    return false;
                }

                smtc.SimulateButton(播放器.Ui.SmtcButton.Play);
                PumpMessages(200);

                if (!form.Engine.IsPlaying)
                {
                    Log(6, "系统媒体控件检查：按了「播放」之后没有继续播");
                    return false;
                }

                // 暂停：状态要跟着变（这条同时验了"状态是往系统那边写的"）
                form.Engine.Pause();

                if (!PumpUntil(() => smtc.Status == Windows.Media.MediaPlaybackStatus.Paused, 6000))
                {
                    Log(6, $"系统媒体控件检查：暂停之后系统那边的状态还是 {smtc.Status}（期望 Paused）");
                    return false;
                }

                form.Engine.Play();
                PumpMessages(200);

                // 时长与进度：系统那侧没有读回来的接口，验"我们喂过、值合理"
                if (!PumpUntil(() => smtc.TimelineUpdated && smtc.DurationMilliseconds > 0, 6000))
                {
                    Log(6, $"系统媒体控件检查：时间轴没喂进去（喂过 {smtc.TimelineUpdated}，"
                            + $"时长 {smtc.DurationMilliseconds} ms）");
                    return false;
                }

                if (smtc.DurationMilliseconds < 25000 || smtc.DurationMilliseconds > 35000)
                {
                    Log(6, "系统媒体控件检查：喂给系统媒体控件的时长不对"
                            + $"（{smtc.DurationMilliseconds} ms，素材是 30 秒）");
                    return false;
                }

                // 切歌：曲名要换成新的（这一条就是那个变异的靶子）
                var play = FindTopMenuItem(form, "播放");
                var next = play == null ? null : FindMenuItem(play.DropDownItems, "下一个");

                if (next == null)
                {
                    Log(6, "系统媒体控件检查：找不到「播放 → 下一个」");
                    return false;
                }

                next.PerformClick();
                PumpMessages(300);

                var secondName = Path.GetFileNameWithoutExtension(second);

                if (!PumpUntil(() => smtc.Title == secondName, 10000))
                {
                    Log(6, $"系统媒体控件检查：切歌之后系统那边的曲名还是「{smtc.Title}」，"
                            + $"期望「{secondName}」");
                    return false;
                }

                // 第二首没有内嵌图，旁边那张 cover.jpg 就得被当成封面用上
                if (!PumpUntil(() => string.Equals(smtc.CoverPath, sidecar, StringComparison.OrdinalIgnoreCase), 6000))
                {
                    Log(6, "系统媒体控件检查：同目录的 cover.jpg 没被当成封面用上（现在的封面来源是「"
                            + (smtc.CoverPath ?? "没有") + "」，期望「" + sidecar + "」）");
                    return false;
                }

                // 交出去的是一张真的图片文件，系统那边得接下（这一步验到"系统读了这张文件"）
                if (!PumpUntil(() => smtc.CoverSet, 6000))
                {
                    Log(6, "系统媒体控件检查：cover.jpg 交给系统之后没被接受（CoverSet 还是假）");
                    return false;
                }

                // 「下一个 / 上一个」：现在在第二首，按「下一个」应当绕回第一首
                smtc.SimulateButton(播放器.Ui.SmtcButton.Next);

                if (!PumpUntil(() => form.Engine.CurrentPath == first, 15000))
                {
                    Log(6, "系统媒体控件检查：按了「下一个」之后没有绕回第一首（现在在播 "
                            + $"{Path.GetFileName(form.Engine.CurrentPath ?? string.Empty)}）");
                    return false;
                }

                if (!PumpUntil(() => smtc.Title == firstName, 10000))
                {
                    Log(6, $"系统媒体控件检查：按「下一个」切歌之后曲名没跟上（现在是「{smtc.Title}」）");
                    return false;
                }

                // 绕回第一首（旁边没有封面图）：封面也得跟着变空——
                // 不清的话音量弹窗里会一直挂着上一首那张 cover.jpg
                if (!PumpUntil(() => smtc.CoverPath == null && !smtc.CoverSet, 6000))
                {
                    Log(6, "系统媒体控件检查：切回没有封面那首之后，封面还挂着上一首的"
                            + $"（来源「{smtc.CoverPath ?? "没有"}」，喂过 {smtc.CoverSet}）");
                    return false;
                }

                smtc.SimulateButton(播放器.Ui.SmtcButton.Previous);

                if (!PumpUntil(() => form.Engine.CurrentPath == second, 15000))
                {
                    Log(6, "系统媒体控件检查：按了「上一个」之后没有回到第二首（现在在播 "
                            + $"{Path.GetFileName(form.Engine.CurrentPath ?? string.Empty)}）");
                    return false;
                }

                // 「上一个」回到第二首（旁边有 cover.jpg）：封面得重新接上那张外挂图
                if (!PumpUntil(() => string.Equals(smtc.CoverPath, sidecar, StringComparison.OrdinalIgnoreCase)
                                     && smtc.CoverSet, 6000))
                {
                    Log(6, "系统媒体控件检查：按「上一个」回到有封面那首之后，封面没接回来"
                            + $"（来源「{smtc.CoverPath ?? "没有"}」，喂过 {smtc.CoverSet}）");
                    return false;
                }

                // 内嵌图优先于目录里的外挂封面：造一个内嵌 PNG 的 mp3（目录里同时有 cover.jpg），
                // 直接喂元数据——不必真的播它
                var embedded = MakePng();
                var tagged = Path.Combine(artFolder, "smtc-带内嵌封面.mp3");

                File.WriteAllBytes(tagged, BuildId3File(3, new List<byte[]>
                {
                    Id3Frame("APIC", PictureBody(3, "image/png", 3, embedded), 3)
                }));

                var taggedTags = TagReader.Read(tagged);

                if (!taggedTags.HasCoverArt)
                {
                    Log(6, "系统媒体控件检查：造出来的内嵌封面素材没读到封面（这一步验不下去）");
                    return false;
                }

                form.ApplyMetadata(tagged, taggedTags, LyricsDocument.Empty, allowOnlineLookup: false);

                if (!PumpUntil(() => smtc.CoverPath != null &&
                                     smtc.CoverPath.EndsWith("smtc-cover.png", StringComparison.OrdinalIgnoreCase) &&
                                     smtc.CoverSet, 6000))
                {
                    Log(6, "系统媒体控件检查：有内嵌图时封面来源不对（现在是「"
                            + (smtc.CoverPath ?? "没有") + "」，期望数据目录里的 smtc-cover.png，"
                            + "而不是同目录那张 cover.jpg）");
                    return false;
                }

                // 落成临时文件的那份必须就是内嵌图本身，不能是目录里那张
                byte[] handedOver;

                try
                {
                    handedOver = File.ReadAllBytes(smtc.CoverPath!);
                }
                catch (Exception ex)
                {
                    Log(6, "系统媒体控件检查：封面来源文件读不回来（" + ex.Message + "）");
                    return false;
                }

                if (!handedOver.AsSpan().SequenceEqual(embedded))
                {
                    Log(6, "系统媒体控件检查：喂给系统的封面不是内嵌那张"
                            + $"（{handedOver.Length} 字节，内嵌图是 {embedded.Length} 字节）");
                    return false;
                }

                // 第五个按钮：停止。放在最后——它会把这一首停掉，前面几条断言都需要它一直在播。
                // 顺带把 SmtcSession.Clear() 那条路也走一遍（停止时系统那边不许还挂着上一首）。
                smtc.SimulateButton(播放器.Ui.SmtcButton.Stop);
                PumpMessages(400);

                if (form.Engine.IsPlaying)
                {
                    Log(6, "系统媒体控件检查：按了「停止」之后还在播");
                    return false;
                }

                if (!PumpUntil(() => smtc.CoverPath == null && !smtc.CoverSet, 6000))
                {
                    Log(6, "系统媒体控件检查：停下来之后系统那边还挂着封面"
                            + $"（来源「{smtc.CoverPath ?? "没有"}」，喂过 {smtc.CoverSet}）");
                    return false;
                }

                Log(6, "系统媒体控件正常：接口接上了、IsEnabled 为真，曲名随歌切换，"
                        + "播放 / 暂停状态与时长都喂了进去并读得回来，五个按钮的开关都开着、"
                        + "按钮回调（播放 / 暂停 / 停止 / 下一个 / 上一个）真的作用在引擎上；"
                        + "封面按「内嵌图 → 同目录外挂封面（cover.jpg 优先于 album.jpg / folder.jpg）」取，"
                        + "切到没封面的那首会清空、回到有封面的那首会接回来，停下来也会清空，"
                        + "交给系统的图片文件系统那边确实读了（CoverSet）"
                        + "（⚠ 系统界面上真的画出来了没有、系统有没有把按钮事件送过来，冒烟验不到——只能人眼看）");
                return true;
            }
            finally
            {
                TryDeleteDirectory(smokeRoot);
            }
        }

        /// <summary>
        /// 窗口消息处理的健壮性。
        /// <para>
        /// 挡的是一个真实发生过的崩溃：<c>WndProc</c> 曾经对<b>每一条</b>消息都调用
        /// <c>m.WParam.ToInt32()</c>。64 位下 wParam / lParam 经常是句柄甚至指针，
        /// 值超出 Int32 范围，于是抛 <c>OverflowException</c> 直接弹错误框。
        /// 正确做法是先按消息类型分流、再用 <c>ToInt64()</c> 取值。
        /// </para>
        /// </summary>
        private static bool CheckWndProcRobustness(Form form)
        {
            var main = form as 播放器.MainForm;

            if (main == null)
            {
                Log(6, "窗口消息检查：这不是主窗体");
                return false;
            }

            var huge = unchecked((long)0xFFFFFFFF00000000);
            var highPointer = unchecked((long)0x7FFFFFFF00000000);

            var cases = new[]
            {
                (Msg: 0x0111, WParam: huge, LParam: 0L, Name: "WM_COMMAND"),
                (Msg: 0x0312, WParam: huge, LParam: 0L, Name: "WM_HOTKEY"),
                (Msg: 0x0319, WParam: 0L, LParam: highPointer, Name: "WM_APPCOMMAND"),
                (Msg: 0x001A, WParam: huge, LParam: highPointer, Name: "WM_SETTINGCHANGE"),
                (Msg: 0x0401, WParam: huge, LParam: highPointer, Name: "WM_USER+1")
            };

            foreach (var item in cases)
            {
                var message = Message.Create(
                    form.Handle, item.Msg, new IntPtr(item.WParam), new IntPtr(item.LParam));

                try
                {
                    main.ProcessWindowMessage(ref message);
                }
                catch (Exception ex)
                {
                    Log(6, $"窗口消息检查：{item.Name} 带大句柄参数时抛了 "
                           + $"{ex.GetType().Name}：{ex.Message}");
                    return false;
                }
            }

            Log(6, "窗口消息处理在 64 位大句柄 / 指针参数下不会溢出");
            return true;
        }
    }
}
