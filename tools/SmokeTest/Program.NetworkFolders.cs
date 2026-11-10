// 检查点 26：网络目录与网络 m3u（1.14.0）。
//
// 1.13.0 只保证"播放前不在界面线程查存在性"；拖一个网络**目录**进来仍会被当成单个文件，
// 网络上的 .m3u 也还在界面线程同步读。这一轮把这两件都搬到后台：判目录 / 递归枚举 /
// 读 m3u 全在线程池上，界面只负责把结果收进列表。
//
// 地址一律用 RFC 5737 的 TEST-NET（保留给文档、不可路由）；探测 / 枚举 / 读 m3u
// 全部由假委托接管，绝不碰真实网络。假枚举返回的是**本地**文件——否则时长扫描器
// 会对着不可达地址解析，把测试拖慢。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using 播放器.Core;

namespace SmokeTest
{
    internal static partial class Program
    {
        private static bool CheckNetworkFolderImport()
        {
            var folder = Path.Combine(AppContext.BaseDirectory, "net-folder-fixture");
            var first = Path.Combine(folder, "net-folder-a.wav");
            var second = Path.Combine(folder, "net-folder-b.wav");
            var third = Path.Combine(folder, "net-folder-c.wav");
            var local = Path.Combine(AppContext.BaseDirectory, "net-folder-local.wav");

            const string networkFolder = @"\\192.0.2.3\share\music";
            const string offlineFolder = @"\\192.0.2.4\share\gone";
            const string networkMedia = @"\\192.0.2.3\share\song.wav";
            const string networkM3u = @"\\192.0.2.5\share\list.m3u";

            try
            {
                TryDeleteDirectory(folder);
                Directory.CreateDirectory(folder);
                WriteWav(first, seconds: 2, frequency: 440);
                WriteWav(second, seconds: 2, frequency: 450);
                WriteWav(third, seconds: 2, frequency: 460);
                WriteWav(local, seconds: 2, frequency: 470);

                using var form = new 播放器.MainForm(Array.Empty<string>());
                form.Show();
                PumpMessages(200);

                var uiThread = Environment.CurrentManagedThreadId;

                // ---- 1) 扩展名已经说明是文件的网络路径：不做目录探测 ----
                var probeCalls = new List<string>();
                form.NetworkImport.IsDirectory = path =>
                {
                    probeCalls.Add(path);
                    return false;
                };
                form.NetworkImport.WalkFolder = (_, _, _, _) => ScanResultOf();
                form.NetworkImport.ReadPlaylist = _ => new List<string>();

                form.Playlist.Clear();
                form.AddPathsForTest(new[] { networkMedia });

                if (!PumpUntil(() => form.Playlist.Count == 1, 15000))
                {
                    Log(26, $"网络上的单个文件没有被加进列表（现在 {form.Playlist.Count} 项；"
                            + $"状态栏「{form.StatusText}」）");
                    return false;
                }

                if (probeCalls.Count != 0)
                {
                    Log(26, "对扩展名已经说明是文件的网络路径还做了目录探测（离线时会白等一次超时）");
                    return false;
                }

                // ---- 2) 网络目录：后台探测 / 枚举，界面立刻返回 ----
                var gate = new ManualResetEventSlim(false);
                var probeThread = 0;
                var enumerateThread = 0;

                form.NetworkImport.IsDirectory = path =>
                {
                    probeThread = Environment.CurrentManagedThreadId;

                    if (string.Equals(path, networkFolder, StringComparison.OrdinalIgnoreCase))
                    {
                        // 故意在界面线程上立刻返回：真跑在界面线程时让"线程号"那条断言去抓，
                        // 而不是让闸门把整次调用读成"卡住"（两种红都能说明问题，这里只留一种）。
                        if (probeThread == uiThread) return true;

                        gate.Wait(TimeSpan.FromSeconds(15));
                        return true;
                    }

                    return false;
                };

                form.NetworkImport.WalkFolder = (_, _, _, _) =>
                {
                    enumerateThread = Environment.CurrentManagedThreadId;
                    return ScanResultOf(first, second);
                };

                form.NetworkImport.ReadPlaylist = _ => new List<string>();

                form.Playlist.Clear();

                var watch = Stopwatch.StartNew();
                form.AddPathsForTest(new[] { networkFolder, local });
                watch.Stop();

                if (probeThread == uiThread)
                {
                    Log(26, "网络位置的目录探测跑在界面线程上（离线时会冻住窗口）");
                    return false;
                }

                if (watch.ElapsedMilliseconds > 1000)
                {
                    Log(26, $"拖入网络目录时界面线程被卡住了（{watch.ElapsedMilliseconds} ms）");
                    return false;
                }

                if (!form.Playlist.Items.Any(i =>
                        string.Equals(i.FilePath, local, StringComparison.OrdinalIgnoreCase)))
                {
                    Log(26, "同一批里的本地文件没有立刻进列表（被网络扫描拖住了）");
                    return false;
                }

                if (!form.StatusText.Contains("网络位置", StringComparison.Ordinal))
                {
                    Log(26, "扫描网络位置时状态栏没有说明（现在「" + form.StatusText + "」）");
                    return false;
                }

                gate.Set();

                if (!PumpUntil(() => form.Playlist.Count == 3, 15000))
                {
                    Log(26, $"网络目录没有被展开（列表里只有目录本身，没有里面的 2 个文件；"
                            + $"现在 {form.Playlist.Count} 项；状态栏「{form.StatusText}」）");
                    return false;
                }

                if (form.Playlist.Items.Any(i =>
                        string.Equals(i.FilePath, networkFolder, StringComparison.OrdinalIgnoreCase)))
                {
                    Log(26, "网络目录本身被当成一条媒体塞进了列表");
                    return false;
                }

                if (!string.Equals(form.Playlist.Items[1].FilePath, first, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(form.Playlist.Items[2].FilePath, second, StringComparison.OrdinalIgnoreCase))
                {
                    Log(26, "网络目录里的文件顺序与枚举结果不一致（"
                            + $"「{form.Playlist.Items[1].FilePath}」/「{form.Playlist.Items[2].FilePath}」）");
                    return false;
                }

                if (probeThread == 0 || probeThread == uiThread)
                {
                    Log(26, "网络位置的目录探测跑在界面线程上（离线时会冻住窗口）");
                    return false;
                }

                if (enumerateThread == 0 || enumerateThread == uiThread)
                {
                    Log(26, "网络目录的枚举跑在界面线程上（大目录会冻住窗口）");
                    return false;
                }

                // ---- 3) 打不开的网络位置：保留一项，并说明"可能离线" ----
                form.NetworkImport.IsDirectory = _ => false;
                form.NetworkImport.WalkFolder = (_, _, _, _) => ScanResultOf();

                form.Playlist.Clear();
                form.AddPathsForTest(new[] { offlineFolder });

                if (!PumpUntil(() => form.Playlist.Count == 1, 15000))
                {
                    Log(26, $"打不开的网络位置被丢掉了（现在 {form.Playlist.Count} 项；"
                            + $"状态栏「{form.StatusText}」）");
                    return false;
                }

                if (!string.Equals(form.Playlist.Items[0].FilePath, offlineFolder, StringComparison.OrdinalIgnoreCase))
                {
                    Log(26, "打不开的网络位置收下的不是原路径（「"
                            + form.Playlist.Items[0].FilePath + "」）");
                    return false;
                }

                if (!form.StatusText.Contains("可能离线", StringComparison.Ordinal))
                {
                    Log(26, "打不开的网络位置没有说明可能离线（现在「" + form.StatusText + "」）");
                    return false;
                }

                // ---- 4) 网络 m3u：后台读，条目按追加处理 ----
                var m3uGate = new ManualResetEventSlim(false);
                var readThread = 0;

                form.NetworkImport.ReadPlaylist = _ =>
                {
                    readThread = Environment.CurrentManagedThreadId;

                    // 与目录探测同样的做法：真在界面线程上读就让"线程号"那条断言去抓。
                    if (readThread == uiThread) return new List<string> { first, third };

                    m3uGate.Wait(TimeSpan.FromSeconds(15));
                    return new List<string> { first, third };
                };

                form.Playlist.Clear();

                watch.Restart();
                form.AddPathsForTest(new[] { networkM3u });
                watch.Stop();

                if (readThread == uiThread)
                {
                    Log(26, "网络 m3u 在界面线程上读取（拖进来时会卡住）");
                    return false;
                }

                if (watch.ElapsedMilliseconds > 1000)
                {
                    Log(26, $"拖入网络 m3u 时界面线程被卡住了（{watch.ElapsedMilliseconds} ms）");
                    return false;
                }

                if (form.Playlist.Count != 0)
                {
                    Log(26, "网络 m3u 还没读完就往列表里加了东西");
                    return false;
                }

                m3uGate.Set();

                if (!PumpUntil(() => form.Playlist.Count == 2, 15000))
                {
                    Log(26, $"网络 m3u 的条目没有追加进列表（现在 {form.Playlist.Count} 项；"
                            + $"状态栏「{form.StatusText}」）");
                    return false;
                }

                if (form.Playlist.Items.Any(i => MediaFormats.IsPlaylistFile(i.FilePath)))
                {
                    Log(26, "网络 m3u 被当成一条媒体塞进了列表");
                    return false;
                }

                if (readThread == 0 || readThread == uiThread)
                {
                    Log(26, "网络 m3u 在界面线程上读取（拖进来时会卡住）");
                    return false;
                }

                if (!form.StatusText.Contains("m3u", StringComparison.Ordinal))
                {
                    Log(26, "从网络 m3u 追加之后状态栏没有说是 m3u（现在「" + form.StatusText + "」）");
                    return false;
                }

                // 空 m3u / 读不出来的 m3u：都要有话说，且不能往列表里塞东西
                form.NetworkImport.ReadPlaylist = _ => new List<string>();

                form.Playlist.Clear();
                form.AddPathsForTest(new[] { networkM3u });

                if (!PumpUntil(
                        () => form.StatusText.Contains("没有可用的条目", StringComparison.Ordinal),
                        15000))
                {
                    Log(26, "空网络 m3u 没有给出提示（状态栏「" + form.StatusText + "」）");
                    return false;
                }

                if (form.Playlist.Count != 0)
                {
                    Log(26, $"空的网络 m3u 也往列表里加了东西（现在 {form.Playlist.Count} 项）");
                    return false;
                }

                form.NetworkImport.ReadPlaylist = _ => throw new IOException("测试：离线");

                form.Playlist.Clear();
                form.AddPathsForTest(new[] { networkM3u });

                if (!PumpUntil(
                        () => form.StatusText.Contains("读不出来", StringComparison.Ordinal),
                        15000))
                {
                    Log(26, "读不出来的网络 m3u 没有给出提示（状态栏「" + form.StatusText + "」）");
                    return false;
                }

                if (form.Playlist.Count != 0)
                {
                    Log(26, $"读不出来的网络 m3u 也往列表里加了东西（现在 {form.Playlist.Count} 项）");
                    return false;
                }

                // ---- 5) 空的网络目录：说清楚"没有找到"，不静默 ----
                form.NetworkImport.IsDirectory = _ => true;
                form.NetworkImport.WalkFolder = (_, _, _, _) => ScanResultOf();

                form.Playlist.Clear();
                form.AddPathsForTest(new[] { networkFolder });

                if (!PumpUntil(
                        () => form.StatusText.Contains("没有找到可播放的媒体文件", StringComparison.Ordinal),
                        15000))
                {
                    Log(26, "空的网络目录没有给出提示（状态栏「" + form.StatusText + "」）");
                    return false;
                }

                if (form.Playlist.Count != 0)
                {
                    Log(26, $"空的网络目录也往列表里加了东西（现在 {form.Playlist.Count} 项）");
                    return false;
                }

                Log(26, "网络目录正常：判目录 / 枚举在后台、目录能展开；离线保留并说明；"
                        + "网络 m3u 后台读、按追加处理；空目录 / 空 m3u 都有提示");
                return true;
            }
            catch (Exception ex)
            {
                Log(26, "网络目录检查失败: " + ex);
                return false;
            }
            finally
            {
                TryDelete(first);
                TryDelete(second);
                TryDelete(third);
                TryDelete(local);
                TryDeleteDirectory(folder);
            }
        }
    }
}
