// 大列表基准（1.15.0）：只在 播放器_TEST_BIGLIST=1 时跑，只量不判。
//
// 为什么自己开一个窗体：这一步的主窗体在探测之前已经被关掉了（关窗流程本身要验），
// 拿一个已释放的引擎去刷新列表会直接原生崩（实测 0xC0000005）。探针反正也不需要
// 那一步留下的任何状态，自己建、自己关最干净。
//
// 合成路径不碰真实文件，也**不进时长扫描器**（直接 form.Playlist.AddRange，不走 AddPathsToPlaylist）；
// 所以量到的是"建条目 + 建行"的成本，不含后台读标签。

using System;
using System.Diagnostics;
using System.Linq;
using System.Windows.Forms;
using 播放器.Core;

namespace SmokeTest
{
    internal static partial class Program
    {
        private static bool RunBigListProbe()
        {
            if (Environment.GetEnvironmentVariable("播放器_TEST_BIGLIST") is not ("1" or "true"))
            {
                Log(11, "大列表基准：没设 播放器_TEST_BIGLIST，跳过（设成 1 再跑「侧栏」这一步）");
                return true;
            }

            var form = new 播放器.MainForm(Array.Empty<string>());

            try
            {
                form.Show();
                PumpMessages(300);

                var view = form.PlaylistView;

                foreach (var count in new[] { 10_000, 50_000 })
                {
                    var paths = Enumerable.Range(0, count)
                        .Select(i => $@"C:\bench\song-{i:D5}.mp3")
                        .ToList();

                    // 模型：一个没有订阅者的 Playlist，只量"建条目"。
                    var model = new Playlist();
                    var watch = Stopwatch.StartNew();
                    model.AddRange(paths);
                    watch.Stop();
                    var modelMs = watch.ElapsedMilliseconds;

                    // 视图：清空窗体列表再整建（模型 + 视图重建）。
                    form.Playlist.Clear();
                    PumpMessages(50);

                    watch.Restart();
                    form.Playlist.AddRange(paths);
                    watch.Stop();
                    var buildMs = watch.ElapsedMilliseconds;

                    if (view.Items.Count != count)
                    {
                        Log(11, $"大列表基准：{count:N0} 项整建之后列表里有 {view.Items.Count} 行");
                        return false;
                    }

                    // 末尾再追加 1 项（1.15.0 之前这条路会把整张表重建一遍）。
                    watch.Restart();
                    form.Playlist.Add(@"C:\bench\song-extra.mp3");
                    watch.Stop();
                    var appendMs = watch.ElapsedMilliseconds;

                    // 切歌（只改"当前项"）：点列表里另一首时会不会把整张表重建一遍
                    watch.Restart();
                    form.Playlist.SetCurrent(form.Playlist.Count / 2);
                    watch.Stop();
                    var switchMs = watch.ElapsedMilliseconds;

                    // 筛选：约 10% 命中，然后清掉。
                    watch.Restart();
                    form.ApplyPlaylistFilterForTest("0001");
                    watch.Stop();
                    var filterMs = watch.ElapsedMilliseconds;
                    var matched = view.Items.Count;

                    form.ApplyPlaylistFilterForTest("");
                    PumpMessages(20);

                    // 按名称排序，再复位成"原始顺序"。
                    var rebuildsBefore = form.PlaylistRebuildCount;

                    watch.Restart();
                    form.ClickPlaylistColumn(1);
                    watch.Stop();
                    var sortMs = watch.ElapsedMilliseconds;
                    var sortRebuilds = form.PlaylistRebuildCount - rebuildsBefore;

                    form.ClickPlaylistColumn(0);

                    // 选中最后一行并滚过去。
                    watch.Restart();
                    var last = view.Items[view.Items.Count - 1];
                    last.Selected = true;
                    last.EnsureVisible();
                    PumpMessages(20);
                    watch.Stop();
                    var selectMs = watch.ElapsedMilliseconds;

                    var managedMb = GC.GetTotalMemory(forceFullCollection: true) / 1024 / 1024;

                    Log(11, $"大列表基准 {count:N0} 项：模型 {modelMs} ms；整建 {buildMs} ms；"
                            + $"追加 1 项 {appendMs} ms；切歌 {switchMs} ms；筛选 {filterMs} ms（命中 {matched}）；"
                            + $"按名称排序 {sortMs} ms（整建 {sortRebuilds} 次）；选中+滚动 {selectMs} ms；托管内存 {managedMb} MB");

                    // ---- 1.18.0 拆量：把"按名称排序"拆成模型排序 / 建行两段 ----
                    // 纯模型（没有视图订阅）：OrderBy + 文化比较 + 重排，量到的就是能挪到后台的那一段。
                    var sortModel = new Playlist();
                    sortModel.AddRange(paths);

                    watch.Restart();
                    sortModel.Sort(PlaylistSortField.Name, descending: false);
                    watch.Stop();
                    var modelSortMs = watch.ElapsedMilliseconds;

                    // 只量"建行"：模型已经排好，再加进窗体（这一步逃不掉，必须留在界面线程）。
                    form.Playlist.Clear();
                    PumpMessages(50);
                    sortModel.Clear();

                    watch.Restart();
                    form.Playlist.AddRange(paths);
                    watch.Stop();
                    var rebuildMs = watch.ElapsedMilliseconds;

                    // ---- 复用行对象再重排（只量不改，留给下一版）----
                    // 1.15.0 量到"瓶颈是建 50,000 个 ListViewItem 本身"，那这些行能不能直接换个次序塞回去？
                    watch.Restart();
                    view.BeginUpdate();
                    try
                    {
                        var existing = view.Items.Cast<ListViewItem>().ToArray();
                        view.Items.Clear();
                        Array.Reverse(existing);
                        view.Items.AddRange(existing);
                    }
                    finally
                    {
                        view.EndUpdate();
                    }
                    watch.Stop();
                    var reorderRowsMs = watch.ElapsedMilliseconds;

                    Log(11, $"大列表拆量 {count:N0} 项：模型排序 {modelSortMs} ms；建行 {rebuildMs} ms；"
                            + $"复用行重排 {reorderRowsMs} ms（只量不改）");

                    form.Playlist.Clear();
                    PumpMessages(50);
                    model.Clear();
                    GC.Collect();
                }

                return true;
            }
            catch (Exception ex)
            {
                Log(11, "大列表基准失败: " + ex);
                return false;
            }
            finally
            {
                // 探针不能污染后面的步骤：筛选、排序、列表都复原，窗体自己关掉。
                form.ApplyPlaylistFilterForTest("");
                form.Playlist.Clear();
                PumpMessages(50);
                form.Close();
                PumpMessages(200);
            }
        }
    }
}
