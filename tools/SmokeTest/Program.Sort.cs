// 检查点 30：大列表排序的重建次数与自绘控件的重复工作量（1.18.0）。
//
// 1.18.0 先量了 50,000 条上"按名称排序"的 3,063 ms：拆开看模型排序只有 45 ms，
// 建行 1,116 ms——多出来的那 1.9 秒是**同一次点击整建了两遍**（模型事件一遍 + 显式调用一遍）。
// 这里钉住三件事：
//   ① 一次排序点击只整建一次（不论 10k 还是 50k）；
//   ② 排序结果正确，且"当前项"还是同一个对象（续播 / 高亮靠它）；
//   ③ CoverView 一次绘制只把文字量一遍、圆角路径同尺寸只建一次。

namespace SmokeTest
{
    internal static partial class Program
    {
        private static bool CheckSortAndPaint()
        {
            // 自己建窗体：理由和大列表探针一样——那一步的主窗体已经被关过，
            // 拿一个已释放的引擎去刷新列表会直接原生崩（实测 0xC0000005）。
            var form = new 播放器.MainForm(Array.Empty<string>());

            try
            {
                form.Show();
                PumpMessages(300);

                const int count = 50_000;

                // 倒序插入：按名称升序排一次会整张表倒过来（顺便让"当前项按引用找回"这条断言有牙——
                // 正序插入的话排序是空操作，当前项本来就不会动）。
                var paths = Enumerable.Range(0, count)
                    .Select(i => $@"C:\bench\song-{count - 1 - i:D5}.mp3")
                    .ToList();

                form.Playlist.Clear();
                form.Playlist.AddRange(paths);
                PumpMessages(100);

                if (form.PlaylistView.Items.Count != count)
                {
                    Log(30, $"大列表没建起来（{form.PlaylistView.Items.Count} 行 / 期望 {count}）");
                    return false;
                }

                // 当前项放中间：排序之后它必须还是**同一个对象**（按引用找回，不是按下标猜）。
                form.Playlist.SetCurrent(count / 2);
                var current = form.Playlist.Items[form.Playlist.CurrentIndex];

                // ---- ① 按名称升序：一次点击只整建一次 ----
                var before = form.PlaylistRebuildCount;
                form.ClickPlaylistColumn(1);
                var rebuilds = form.PlaylistRebuildCount - before;


                if (rebuilds != 1)
                {
                    Log(30, $"点一次「按名称排序」整建了 {rebuilds} 次（期望 1 次——多出来的那遍是白花的）");
                    return false;
                }

                if (!IsSortedByName(form, 200))
                {
                    Log(30, "大列表按名称升序之后顺序不对（抽查前 200 项）");
                    return false;
                }

                if (!ReferenceEquals(form.Playlist.Items[form.Playlist.CurrentIndex], current))
                {
                    var now = form.Playlist.CurrentIndex >= 0 && form.Playlist.CurrentIndex < form.Playlist.Count
                        ? form.Playlist.Items[form.Playlist.CurrentIndex].DisplayName
                        : "（没有当前项）";

                    Log(30, $"排序之后当前项换了对象（续播位置 / 在播高亮会跟着错）："
                            + $"排序前是「{current.DisplayName}」，现在 CurrentIndex = {form.Playlist.CurrentIndex} 指着「{now}」");
                    return false;
                }

                // ---- 恢复原始顺序：同样只整建一次 ----
                before = form.PlaylistRebuildCount;
                form.ClickPlaylistColumn(0);
                rebuilds = form.PlaylistRebuildCount - before;

                if (rebuilds != 1)
                {
                    Log(30, $"点「#」恢复原始顺序整建了 {rebuilds} 次（期望 1 次）");
                    return false;
                }

                // ---- ② CoverView：一次绘制只量一遍 ----
                using (var cover = new CoverView { Size = new Size(360, 470) })
                {
                    cover.ShowTrack(null, new MediaTags
                    {
                        Title = "预热用的标题",
                        Artist = "某位艺术家",
                        Album = "某张专辑的名字"
                    });

                    // 预热一帧再清零：`DrawToBitmap` 会先把句柄建出来，那一帧的 DPI / 尺寸
                    // 还没定下来（全量跑与单跑结果不一样，就是这一帧算进去了）。
                    DrawOnce(cover);

                    // 换一段文案：缓存键变了，下一帧**必须**重新排版（这才是"一次绘制量几遍"的场景）。
                    cover.ShowTrack(null, new MediaTags
                    {
                        Title = "一段中等长度的曲目标题",
                        Artist = "某位艺术家",
                        Album = "某张专辑的名字"
                    });

                    cover.ResetPaintCounters();
                    DrawOnce(cover);

                    // 三行文案 = 三次测量。以前算布局量一遍、绘制又量一遍，会是 6 次。
                    if (cover.TextMeasureCount != 3)
                    {
                        Log(30, $"封面画一帧量了 {cover.TextMeasureCount} 次字（期望 3 次：三行各一次）");
                        return false;
                    }

                    // 同一文案同一尺寸再画 5 帧：一次都不该再量（排版结果被缓存了）。
                    for (var i = 0; i < 5; i++) DrawOnce(cover);

                    if (cover.TextMeasureCount != 3)
                    {
                        Log(30, $"封面重复绘制又量了字（一共 {cover.TextMeasureCount} 次，期望还是 3 次）");
                        return false;
                    }

                    // ---- ③ 圆角路径：同尺寸不重建，换尺寸才重建 ----
                    // 用"跨帧增量"而不是绝对次数：第一帧建几次由 DPI / 首次布局决定，
                    // 那不是这条断言要管的事；要钉的是"之后每帧都不再建"。
                    var builds = cover.PathBuildCount;

                    DrawOnce(cover);

                    if (cover.PathBuildCount != builds)
                    {
                        Log(30, $"封面同尺寸再画又建了圆角路径（{builds} → {cover.PathBuildCount} 次；"
                                + $"上次是 {cover.LastPathSquare} r={cover.LastPathRadius:0.###}）");
                        return false;
                    }

                    cover.Size = new Size(320, 470);
                    DrawOnce(cover);

                    if (cover.PathBuildCount != builds + 1)
                    {
                        Log(30, $"封面换了尺寸之后没有重建圆角路径（{builds} → {cover.PathBuildCount} 次，期望 +1）");
                        return false;
                    }

                    DrawOnce(cover);

                    if (cover.PathBuildCount != builds + 1)
                    {
                        Log(30, $"封面同尺寸再画又建了圆角路径（{cover.PathBuildCount} 次，期望还是 {builds + 1} 次）");
                        return false;
                    }
                }

                Log(30, "大列表排序正常：一次点击只整建一次、顺序正确、当前项按引用找回；"
                        + "封面一帧只量一遍字、同尺寸只建一次圆角路径");
                return true;
            }
            catch (Exception ex)
            {
                Log(30, "大列表排序检查失败: " + ex);
                return false;
            }
            finally
            {
                form.Playlist.Clear();
                PumpMessages(50);
                form.Close();
                PumpMessages(200);
                form.Dispose();
            }
        }

        /// <summary>抽查前 <paramref name="take"/> 项：显示名按当前文化升序（与排序用的比较器一致）。</summary>
        private static bool IsSortedByName(播放器.MainForm form, int take)
        {
            var items = form.Playlist.Items;
            var limit = Math.Min(take, items.Count - 1);

            for (var i = 0; i < limit; i++)
            {
                if (StringComparer.CurrentCultureIgnoreCase.Compare(items[i].DisplayName, items[i + 1].DisplayName) > 0)
                    return false;
            }

            return true;
        }

        /// <summary>画一帧（DrawToBitmap 会把 WM_PRINT 转成一次 OnPaint）。</summary>
        private static void DrawOnce(Control control)
        {
            var _ = control.Handle;

            using var bitmap = new Bitmap(control.Width, control.Height);
            control.DrawToBitmap(bitmap, new Rectangle(0, 0, control.Width, control.Height));
        }
    }
}
