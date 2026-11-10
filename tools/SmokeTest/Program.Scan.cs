// 检查点 28：扫描的上限、取消与进度（1.17.0）。
//
// 网络目录以前是"递归到底、中途不能停、也不告诉你找到多少"（外部评审第 3 条）。
// 这里验三件事，全都不碰真实网络：
//   ① MediaScanner 的深度 / 条数上限与取消（真实临时目录，三层小树）；
//   ② NetworkPathImport 取消之后不再碰下一条路径（假 IsDirectory / WalkFolder）；
//   ③ 主窗体接线：链接可见、「已找到 N 项」在涨、取消之后已找到的条目照常加入、链接消失。

namespace SmokeTest
{
    internal static partial class Program
    {
        /// <summary>造一份"扫出来这些文件"的遍历结果（假 <c>WalkFolder</c> 用）。</summary>
        private static MediaScanResult ScanResultOf(params string[] files)
        {
            var result = new MediaScanResult();
            result.Files.AddRange(files);
            return result;
        }

        private static bool CheckScanLimitsAndCancel()
        {
            var root = Path.Combine(AppContext.BaseDirectory, "scan-fixtures");
            TryDeleteDirectory(root);

            try
            {
                // 三层小树：root/one.mp3、root/a/two.mp3、root/a/b/three.mp3
                // （IsMediaFile 只看扩展名，内容随意）
                var one = Path.Combine(root, "one.mp3");
                var two = Path.Combine(root, "a", "two.mp3");
                var three = Path.Combine(root, "a", "b", "three.mp3");

                foreach (var file in new[] { one, two, three })
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    File.WriteAllBytes(file, new byte[] { 0 });
                }

                // ---- 1) 不设上限：三层都在，而且没被截断 ----
                var all = MediaScanner.Walk(root, MediaScanLimits.Unlimited(), CancellationToken.None, null);

                if (all.Files.Count != 3 || all.Truncated)
                {
                    Log(28, $"无限遍历没有找齐三层（{all.Files.Count} 个，Truncated={all.Truncated}）");
                    return false;
                }

                // ---- 2) 深度上限：MaxDepth = 1 只到 a，而且要说"没扫全" ----
                var shallow = MediaScanner.Walk(
                    root, new MediaScanLimits { MaxDepth = 1 }, CancellationToken.None, null);

                if (shallow.Files.Count != 2 || !shallow.Truncated)
                {
                    Log(28, $"深度上限没生效（{shallow.Files.Count} 个，Truncated={shallow.Truncated}，"
                            + "期望 2 个且被截断）");
                    return false;
                }

                // ---- 3) 条数上限：MaxFiles = 1 只收一个 ----
                var single = MediaScanner.Walk(
                    root, new MediaScanLimits { MaxFiles = 1 }, CancellationToken.None, null);

                if (single.Files.Count != 1 || !single.Truncated)
                {
                    Log(28, $"条数上限没生效（{single.Files.Count} 个，Truncated={single.Truncated}）");
                    return false;
                }

                // ---- 4) 取消：收到第 2 个就停，已经找到的要留着 ----
                using (var cts = new CancellationTokenSource())
                {
                    var seen = 0;

                    var cancelled = MediaScanner.Walk(root, MediaScanLimits.Unlimited(), cts.Token, _ =>
                    {
                        if (++seen == 2) cts.Cancel();
                    });

                    if (cancelled.Files.Count != 2 || !cancelled.Cancelled || cancelled.Truncated)
                    {
                        Log(28, $"取消之后结果不对（{cancelled.Files.Count} 个，"
                                + $"Cancelled={cancelled.Cancelled}，Truncated={cancelled.Truncated}）");
                        return false;
                    }
                }

                // ---- 5) 老签名与 Walk 一致（本地那一侧走的就是这条路） ----
                var legacy = MediaFormats.EnumerateMediaFiles(root, true);

                if (legacy.Count != all.Files.Count ||
                    !legacy.SequenceEqual(all.Files, StringComparer.OrdinalIgnoreCase))
                {
                    Log(28, $"EnumerateMediaFiles 与 Walk 的结果不一致（{legacy.Count} 对 {all.Files.Count}）");
                    return false;
                }

                // ---- 6) 取消之后不再碰下一条路径 ----
                using (var importCts = new CancellationTokenSource())
                {
                    var import = new NetworkPathImport();
                    var directoryChecks = 0;

                    import.IsDirectory = _ =>
                    {
                        directoryChecks++;
                        return true;
                    };

                    import.WalkFolder = (_, _, _, _) =>
                    {
                        // 假装"扫到一半用户按了取消"
                        importCts.Cancel();

                        var partial = ScanResultOf(@"\\192.0.2.7\share\first.mp3");
                        partial.Cancelled = true;
                        return partial;
                    };

                    var imported = import.Run(
                        new[] { @"\\192.0.2.7\share\first", @"\\192.0.2.8\share\second" },
                        importCts.Token,
                        null);

                    if (directoryChecks != 1)
                    {
                        Log(28, $"取消之后还在处理后面的路径（IsDirectory 调了 {directoryChecks} 次，期望 1 次）");
                        return false;
                    }

                    if (!imported.Cancelled || imported.Items.Count != 1)
                    {
                        Log(28, $"取消之后的结果不对（{imported.Items.Count} 项，Cancelled={imported.Cancelled}）");
                        return false;
                    }
                }

                // ---- 7) 主窗体：链接 / 进度 / 取消之后保留已找到的 ----
                using var form = new 播放器.MainForm(Array.Empty<string>());
                form.Show();
                PumpMessages(200);

                form.NetworkImport.IsDirectory = _ => true;

                form.NetworkImport.WalkFolder = (_, _, token, onFile) =>
                {
                    var result = new MediaScanResult();

                    for (var i = 0; i < 50; i++)
                    {
                        if (token.IsCancellationRequested)
                        {
                            result.Cancelled = true;
                            return result;
                        }

                        var path = @"\\192.0.2.9\share\track" + i + ".wav";
                        result.Files.Add(path);
                        onFile?.Invoke(path);

                        Thread.Sleep(25);
                    }

                    return result;
                };

                form.Playlist.Clear();
                form.AddPathsForTest(new[] { @"\\192.0.2.9\share\music" });

                if (!PumpUntil(() => form.ScanCancelVisible, 5000))
                {
                    Log(28, "扫描期间状态栏没有出现「取消扫描」");
                    return false;
                }

                if (!PumpUntil(() => form.ScanFoundCount > 0, 5000))
                {
                    Log(28, "扫描进度一直是 0（「已找到 N 项」没动）");
                    return false;
                }

                if (!PumpUntil(() => (form.ScanCancelText ?? string.Empty).Contains("已找到"), 5000))
                {
                    Log(28, "「取消扫描」后面没有跟上已找到的条数（现在「" + form.ScanCancelText + "」）");
                    return false;
                }

                form.CancelScans();

                if (!PumpUntil(() => !form.IsScanning, 15000))
                {
                    Log(28, "点了取消之后扫描没有收尾（一直挂着）");
                    return false;
                }

                if (form.ScanCancelVisible)
                {
                    Log(28, "扫描收尾之后「取消扫描」链接还留着");
                    return false;
                }

                var added = form.Playlist.Count;

                if (added <= 0 || added >= 50)
                {
                    Log(28, $"取消之后列表里是 {added} 项（期望 0 < n < 50：已经找到的要留下，剩下的不再扫）");
                    return false;
                }

                if (!form.StatusText.Contains("已取消", StringComparison.Ordinal))
                {
                    Log(28, "取消之后状态栏没有说「已取消」（现在「" + form.StatusText + "」）");
                    return false;
                }

                Log(28, $"扫描正常：深度 / 条数上限会置 Truncated；取消保留已找到的 {added} 项、"
                        + "不再碰后面的路径、链接收起来");
                return true;
            }
            catch (Exception ex)
            {
                Log(28, "扫描上限与取消检查失败: " + ex);
                return false;
            }
            finally
            {
                TryDeleteDirectory(root);
            }
        }
    }
}
