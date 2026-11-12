// 检查点 31：切歌时的等待（1.19.0）。
//
// 1.18.0 之前，切到一首"读得慢"的歌时，界面在这段时间里挂着的**还是上一首**的封面与词曲
// （标签读回来才回填），而读取本身是"每切一首就起一个线程池任务、没有上限、不可取消"。
// 这里钉住四件事，全部用受闸门控制的假读取（不碰真文件内容）：
//   ① 切歌当场占位：上一首的封面 / 词曲被摘掉，换上「正在读取标签…」+ 新文件名；
//   ② 连切 20 首只读 2 条（第一条在飞 + 最后一条待读），其余 17 条根本不读；
//   ③ 迟到的结果不许覆盖当前这一首；
//   ④ 读取跑在专用线程上（不占线程池）；关窗时不等、也不崩。

namespace SmokeTest
{
    internal static partial class Program
    {
        private static bool CheckMetadataPending()
        {
            var folder = Path.Combine(AppContext.BaseDirectory, "smoke-meta");
            TryDeleteDirectory(folder);
            Directory.CreateDirectory(folder);

            var first = Path.Combine(folder, "meta-a.mp3");
            var second = Path.Combine(folder, "meta-b.mp3");

            // 内容无所谓：下面的读取被接缝换成了假的。文件真的存在只是为了让「媒体信息」那页
            // 在非占位时算得出文件大小。
            try
            {
                File.WriteAllBytes(first, new byte[64]);
                File.WriteAllBytes(second, new byte[64]);
            }
            catch (Exception ex)
            {
                Log(31, "切歌时的等待：建不了夹具：" + ex.Message);
                return false;
            }

            var cover = MakePng();

            try
            {
                // ---- ① 占位：切到第二首时先把上一首的封面 / 词曲摘掉 ----
                using (var form = new 播放器.MainForm(Array.Empty<string>()))
                {
                    form.Show();
                    PumpMessages(300);

                    var sidebar = Find(form, "sidebarPanel");
                    var coverView = sidebar == null ? null : FindByTypeName(sidebar, "CoverView") as CoverView;
                    var info = sidebar == null ? null : FindByTypeName(sidebar, "InfoView") as ListView;

                    if (coverView == null || info == null)
                    {
                        Log(31, "侧栏里找不到封面 / 媒体信息控件");
                        return false;
                    }

                    var gate = new ManualResetEventSlim(false);
                    var block = false;
                    var poolFlag = -1;

                    form.MetadataLoad = path =>
                    {
                        Interlocked.Exchange(ref poolFlag, Thread.CurrentThread.IsThreadPoolThread ? 1 : 0);

                        if (Volatile.Read(ref block)) gate.Wait(30000);

                        return new MetadataResult(
                            new MediaTags
                            {
                                Title = "标题：" + Path.GetFileNameWithoutExtension(path),
                                Artist = "歌手",
                                CoverArt = cover,
                                CoverMimeType = "image/png"
                            },
                            LyricsDocument.Empty);
                    };

                    form.LoadTrackMetadata(first);

                    if (!PumpUntil(() => coverView.TitleText == "标题：meta-a", 8000))
                    {
                        Log(31, $"第一首的标签没有读回来（标题「{coverView.TitleText}」）");
                        return false;
                    }

                    if (coverView.Cover == null)
                    {
                        Log(31, "第一首的封面没有画出来（后面「切歌要摘掉上一首封面」那条就验不出来了）");
                        return false;
                    }

                    // 第二首卡住不返回：宽限（150 ms）过去之后界面必须换成占位
                    Volatile.Write(ref block, true);
                    form.LoadTrackMetadata(second);
                    PumpMessages(500);

                    if (coverView.Cover != null)
                    {
                        Log(31, "切到第二首之后封面还是上一首的（期望空）");
                        return false;
                    }

                    if (coverView.ArtistText != CoverView.PendingText)
                    {
                        Log(31, $"切到第二首之后没写「{CoverView.PendingText}」（现在「{coverView.ArtistText}」）");
                        return false;
                    }

                    if (coverView.TitleText != "meta-b")
                    {
                        Log(31, $"切到第二首之后标题不是新文件名（现在「{coverView.TitleText}」）");
                        return false;
                    }

                    if (!HasPendingInfoRow(info))
                    {
                        Log(31, "侧栏「媒体信息」没有说「正在读取标签…」（第一行是「" + DescribeInfoFirstRow(info) + "」）");
                        return false;
                    }

                    if (form.Metadata.InFlight != 1)
                    {
                        Log(31, $"占位期间在飞的读取是 {form.Metadata.InFlight} 条（期望 1）");
                        return false;
                    }

                    // 放行：占位必须换成真的标签
                    gate.Set();

                    if (!PumpUntil(() => coverView.TitleText == "标题：meta-b", 8000))
                    {
                        Log(31, $"放行之后标签没有回填（标题「{coverView.TitleText}」）");
                        return false;
                    }

                    if (coverView.Cover == null)
                    {
                        Log(31, "回填之后封面没有回来");
                        return false;
                    }

                    if (Volatile.Read(ref poolFlag) != 0)
                    {
                        Log(31, "标签读取跑在线程池上（离线共享卡住时会占着池里的工作线程）");
                        return false;
                    }

                    if (!PumpUntil(() => form.Metadata.InFlight == 0, 3000))
                    {
                        Log(31, "读取都放行了，在飞计数还没回到 0");
                        return false;
                    }

                    gate.Dispose();
                }

                // ---- ②③ 有界 + 最新优先 + 迟到的不覆盖 ----
                var discards = 0;

                using (var form = new 播放器.MainForm(Array.Empty<string>()))
                {
                    form.Show();
                    PumpMessages(300);

                    var gateOld = new ManualResetEventSlim(false);   // 前两条（meta-00 / meta-01）
                    var gateNew = new ManualResetEventSlim(false);   // 最后那条（meta-19）

                    form.MetadataLoad = path =>
                    {
                        // 前两条卡在 gateOld 上；之后被读起来的那条（正常情况就是 meta-19）卡在新闸门上，
                        // 这样才能在"过期结果已经回来、最新那条还没回来"的中间态上做断言。
                        if (path.EndsWith("meta-00.mp3", StringComparison.Ordinal) ||
                            path.EndsWith("meta-01.mp3", StringComparison.Ordinal))
                        {
                            gateOld.Wait(30000);
                        }
                        else
                        {
                            gateNew.Wait(30000);
                        }

                        return new MetadataResult(
                            new MediaTags { Title = "标题：" + Path.GetFileNameWithoutExtension(path) },
                            LyricsDocument.Empty);
                    };

                    // 连切 20 首（全卡在闸门上）
                    for (var i = 0; i < 20; i++)
                        form.LoadTrackMetadata(Path.Combine(folder, $"meta-{i:D2}.mp3"));

                    PumpMessages(200);

                    var metadata = form.Metadata;

                    if (metadata.Starts != 2)
                    {
                        Log(31, $"连切 20 首起了 {metadata.Starts} 条读取（期望 2 条——只有第一条和最后一条该被读）");
                        return false;
                    }

                    if (metadata.InFlight != 2)
                    {
                        Log(31, $"连切 20 首之后在飞是 {metadata.InFlight} 条（期望 2 条上限）");
                        return false;
                    }

                    if (metadata.Pending != 1)
                    {
                        Log(31, $"连切 20 首之后没有留下「最新待读」那一条（Pending = {metadata.Pending}）");
                        return false;
                    }

                    if (metadata.Skips != 17)
                    {
                        Log(31, $"被替掉的读取是 {metadata.Skips} 条（期望 17——20 首里只有 3 条该被记住）");
                        return false;
                    }

                    // 放行前两条（它们已经过期），但把"最新那首"的读取继续卡住：
                    // 这时候界面上必须还是最后那一首的占位，迟到的两份不许盖上来。
                    gateOld.Set();

                    if (!PumpUntil(() => form.Metadata.Starts == 3, 5000))
                    {
                        Log(31, $"待读那一条没有被读起来（一共读了 {form.Metadata.Starts} 条，期望 3）");
                        return false;
                    }

                    PumpMessages(400);

                    if (SessionOf(form).Tags.Title != null)
                    {
                        Log(31, "迟到的结果盖到当前这一首上了（标题变成「" + SessionOf(form).Tags.Title
                                + "」，期望还是占位）");
                        return false;
                    }

                    if (form.MetadataDiscards < 2)
                    {
                        Log(31, $"迟到的结果只丢掉了 {form.MetadataDiscards} 份（期望至少 2 份）");
                        return false;
                    }

                    gateNew.Set();

                    if (!PumpUntil(() => SessionOf(form).Tags.Title == "标题：meta-19", 10000))
                    {
                        Log(31, "放行之后最后停的不是最后一首（标题「" + SessionOf(form).Tags.Title
                                + "」，期望「标题：meta-19」）");
                        return false;
                    }

                    discards = form.MetadataDiscards;

                    if (!PumpUntil(() => form.Metadata.InFlight == 0, 3000))
                    {
                        Log(31, "读取都放行了，在飞计数还没回到 0");
                        return false;
                    }

                    gateOld.Dispose();
                    gateNew.Dispose();
                }

                // ---- ④ 关窗：在飞的那条不等、之后回来的也不许炸 ----
                var teardownGate = new ManualResetEventSlim(false);
                var closing = new 播放器.MainForm(Array.Empty<string>());

                closing.Show();
                PumpMessages(300);

                closing.MetadataLoad = _ =>
                {
                    teardownGate.Wait(30000);
                    return new MetadataResult(new MediaTags { Title = "迟到的那一首" }, LyricsDocument.Empty);
                };

                closing.LoadTrackMetadata(first);
                PumpMessages(200);

                closing.Close();
                closing.Dispose();

                if (!closing.Metadata.IsClosed)
                {
                    Log(31, "关窗之后读取队列没有关掉（还会接着起新的读取）");
                    return false;
                }

                // 已释放的窗体 + 迟到的回调：不崩、不抛
                teardownGate.Set();
                PumpMessages(600);
                teardownGate.Dispose();

                Log(31, $"切歌时的等待正常：读之前先占位（摘掉上一首的封面 / 词曲、写「{CoverView.PendingText}」）、"
                        + $"连切 20 首只读 2 条、迟到的结果丢掉 {discards} 份、读取不在线程池上、关窗不等也不崩");
                return true;
            }
            catch (Exception ex)
            {
                Log(31, "切歌时的等待检查失败: " + ex);
                return false;
            }
            finally
            {
                TryDeleteDirectory(folder);
            }
        }

        /// <summary>「媒体信息」页第一行是不是「状态 / 正在读取标签…」。</summary>
        private static bool HasPendingInfoRow(ListView info) =>
            info.Items.Count > 0 &&
            info.Items[0].SubItems.Count > 1 &&
            info.Items[0].Text == "状态" &&
            info.Items[0].SubItems[1].Text == CoverView.PendingText;

        private static string DescribeInfoFirstRow(ListView info) =>
            info.Items.Count == 0
                ? "（空）"
                : info.Items[0].Text + " / " +
                  (info.Items[0].SubItems.Count > 1 ? info.Items[0].SubItems[1].Text : "（没有第二列）");
    }
}
