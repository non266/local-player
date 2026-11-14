// 第 29 项检查的一部分：网络流 / 网络位置打开得慢时的等待提示（1.21.0）。
//
// 1.19.0 量到不可达的 http / rtsp 流会一直停在 Opening（60 秒都没有错误事件），
// 界面不冻，但"连不上"这件事程序不会主动说。1.21.0 的做法是"到点说一句，不自动停"：
// 这里验 ① 到点提示一次 ② 停止能退出 ③ 慢而能播的流不会被停 ④ 第二条死流还会提示
// ⑤ 本地文件不提示 ⑥ 日志里不出现流地址。
//
// 全部用本地回环的假服务器，不碰外网；连回环都被安全软件拦了的话打印原因后跳过。

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace SmokeTest
{
    internal static partial class Program
    {
        /// <summary>
        /// 一个"不配合"的 HTTP 服务器：
        /// <para>
        /// <b>黑洞</b>（<c>body == null</c>）：收下请求、读到请求头结束就<b>不回应</b>——
        /// libvlc 会一直等响应头，正好复现"一直停在 Opening"。
        /// </para>
        /// <para>
        /// <b>滴滴</b>：先等 <c>firstByteDelayMilliseconds</c>，再把一个合法的 WAV 一次性发完——
        /// 用来验"慢而能播的流不会被当成死流停掉"。
        /// </para>
        /// </summary>
        private sealed class SlowMediaServer : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly Thread _acceptThread;
            private readonly byte[]? _body;
            private readonly int _delayMilliseconds;
            private readonly ManualResetEventSlim _stop = new ManualResetEventSlim(false);
            private volatile bool _running = true;

            public SlowMediaServer(byte[]? body, int firstByteDelayMilliseconds)
            {
                _body = body;
                _delayMilliseconds = firstByteDelayMilliseconds;

                // 端口交给系统挑，免得和别的东西撞上（和 FakeLrcApiServer 同一套做法）
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();

                Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

                _acceptThread = new Thread(Accept) { IsBackground = true, Name = "slow-media-server" };
                _acceptThread.Start();
            }

            public int Port { get; }

            /// <summary>收到过几个连接（验"libvlc 真的连上来了"）。</summary>
            public int Accepted;

            public string Url(string path) => "http://127.0.0.1:" + Port + path;

            private void Accept()
            {
                while (_running)
                {
                    TcpClient client;

                    try
                    {
                        client = _listener.AcceptTcpClient();
                    }
                    catch (SocketException)
                    {
                        return;
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }
                    catch (InvalidOperationException)
                    {
                        return;
                    }

                    Interlocked.Increment(ref Accepted);

                    var worker = new Thread(() => Handle(client))
                    {
                        IsBackground = true,
                        Name = "slow-media-conn"
                    };

                    worker.Start();
                }
            }

            private void Handle(TcpClient client)
            {
                using (client)
                {
                    try
                    {
                        var stream = client.GetStream();
                        ReadRequestHeaders(stream);

                        if (_body == null)
                        {
                            // 黑洞：收下请求就不吭声（Dispose 时放行）
                            _stop.Wait(TimeSpan.FromSeconds(30));
                            return;
                        }

                        if (_delayMilliseconds > 0) Thread.Sleep(_delayMilliseconds);

                        var header = "HTTP/1.1 200 OK\r\n"
                                     + "Content-Type: audio/wav\r\n"
                                     + "Content-Length: " + _body.Length + "\r\n"
                                     + "Accept-Ranges: none\r\n"
                                     + "Connection: close\r\n\r\n";

                        var headerBytes = Encoding.ASCII.GetBytes(header);
                        stream.Write(headerBytes, 0, headerBytes.Length);
                        stream.Write(_body, 0, _body.Length);
                        stream.Flush();
                    }
                    catch (IOException)
                    {
                        // 客户端先走了（停止 / 换歌）：预期内
                    }
                    catch (SocketException)
                    {
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                }
            }

            /// <summary>读到空行（请求头结束）为止；黑洞模式就靠"读到这里然后不回应"。</summary>
            private static void ReadRequestHeaders(NetworkStream stream)
            {
                var buffer = new byte[1024];
                var total = 0;

                while (total < buffer.Length)
                {
                    var read = stream.Read(buffer, total, buffer.Length - total);
                    if (read <= 0) return;

                    total += read;

                    if (total >= 4 &&
                        buffer[total - 4] == '\r' && buffer[total - 3] == '\n' &&
                        buffer[total - 2] == '\r' && buffer[total - 1] == '\n')
                    {
                        return;
                    }
                }
            }

            public void Dispose()
            {
                _running = false;
                _stop.Set();

                try { _listener.Stop(); } catch { }

                // 只等接受线程；连接线程自己是后台线程，会在读写失败后退出（不阻塞关窗）
                try { _acceptThread.Join(500); } catch { }
            }
        }

        /// <summary>
        /// 第 29 项检查里的"等待提示"那一段。返回 false 只表示"断言红了"；
        /// 环境不成立（本地回环都连不上）时打印原因后返回 true。
        /// </summary>
        private static bool CheckSlowNetworkOpen()
        {
            // ---- 0：纯判定（不需要窗口，也不需要网络） ----
            if (!CheckSlowOpenWatchLogic()) return false;

            var wavPath = Path.Combine(AppContext.BaseDirectory, "slow-open.wav");
            WriteWav(wavPath, seconds: 30, frequency: 440);

            try
            {
                var body = File.ReadAllBytes(wavPath);

                string dripUrl;
                string deadUrl;

                // ---- 1：慢而能播的流：先说"还没播起来"，再真的播起来 ----
                using (var drip = new SlowMediaServer(body, firstByteDelayMilliseconds: 2500))
                using (var form = new 播放器.MainForm(Array.Empty<string>()))
                {
                    form.Show();
                    PumpMessages(200);

                    form.SetSlowOpenHintForTest(1000);

                    dripUrl = drip.Url("/slow.wav");
                    StartTrial(form, dripUrl);

                    if (!PumpUntil(() => drip.Accepted >= 1, 5000))
                    {
                        Log(29, "网络等待：本地回环 HTTP 连不上（可能被安全软件拦了），"
                                + "「等待提示」这一段跳过——这不是功能坏了");
                        return true;
                    }

                    if (!PumpUntil(() => form.StatusText.Contains("还没有播放起来", StringComparison.Ordinal), 8000))
                    {
                        Log(29, "网络等待：打开慢流之后状态栏没有说「还没有播放起来」"
                                + $"（现在「{form.StatusText}」）");
                        return false;
                    }

                    if (!PumpUntil(() => form.Engine.IsPlaying, 15000))
                    {
                        Log(29, form.Engine.State == PlayerState.Stopped
                            ? "网络等待：慢而能播的流被当成死流停掉了（状态 已停止，期望 Playing）"
                            : $"网络等待：慢而能播的流没有播起来（状态 {form.Engine.State}，"
                              + $"状态栏「{form.StatusText}」）");
                        return false;
                    }

                    // 播起来之后那句提示就该被「正在播放：…」盖掉
                    PumpMessages(400);

                    if (form.StatusText.Contains("还没有播放起来", StringComparison.Ordinal))
                    {
                        Log(29, "网络等待：慢流播起来之后状态栏还是「…还没有播放起来…」");
                        return false;
                    }
                }

                // ---- 2：死流：到点提示一次、按停止能退出；再开一次还会提示 ----
                using (var dead = new SlowMediaServer(null, firstByteDelayMilliseconds: 0))
                using (var form = new 播放器.MainForm(Array.Empty<string>()))
                {
                    form.Show();
                    PumpMessages(200);

                    form.SetSlowOpenHintForTest(800);

                    deadUrl = dead.Url("/dead.mp3");
                    StartTrial(form, deadUrl);

                    if (!PumpUntil(() => dead.Accepted >= 1, 5000))
                    {
                        Log(29, "网络等待：本地回环 HTTP 连不上（可能被安全软件拦了），"
                                + "「等待提示」这一段跳过——这不是功能坏了");
                        return true;
                    }

                    if (!PumpUntil(() => form.StatusText.Contains("还没有播放起来", StringComparison.Ordinal), 8000))
                    {
                        Log(29, "网络等待：打开死流之后状态栏没有说「还没有播放起来」"
                                + $"（现在「{form.StatusText}」）");
                        return false;
                    }

                    // 提示不是"停掉它"：状态还必须停在 Opening，等用户自己决定
                    if (form.Engine.State != PlayerState.Opening)
                    {
                        Log(29, $"网络等待：提示之后状态自己变成了 {form.Engine.State}"
                                + "（提示只是说话，不该改变播放状态）");
                        return false;
                    }

                    form.ExecuteCommand(PlayerCommandId.Stop);

                    if (!PumpUntil(() => form.Engine.State == PlayerState.Stopped, 3000))
                    {
                        Log(29, $"网络等待：按了停止之后状态没有变成「已停止」（现在 {form.Engine.State}）");
                        return false;
                    }

                    if (!form.StatusText.Contains("已停止", StringComparison.Ordinal))
                    {
                        Log(29, $"网络等待：按了停止之后状态栏没有说「已停止」（现在「{form.StatusText}」）");
                        return false;
                    }

                    // 第二条死流：上一次的"提示过"不能留到这一次
                    form.Playback.PlayIndex(0, allowFallback: false);

                    if (!PumpUntil(() => form.StatusText.Contains("还没有播放起来", StringComparison.Ordinal), 8000))
                    {
                        Log(29, "网络等待：第二个死流没有提示（只有第一次说了）");
                        return false;
                    }
                }

                // ---- 3：日志里有"等了很久"，但没有地址 ----
                var log = AppLog.SnapshotText();

                if (!log.Contains("仍未播放", StringComparison.Ordinal))
                {
                    Log(29, "网络等待：日志里没有留下「等了很久还没播」这一条（排查时又只剩状态栏）");
                    return false;
                }

                var leaked = log.Split('\n')
                    .Select(line => line.Trim())
                    .FirstOrDefault(line => line.Contains(deadUrl, StringComparison.Ordinal) ||
                                            line.Contains(dripUrl, StringComparison.Ordinal));

                if (leaked != null)
                {
                    Log(29, "网络等待：日志里出现了流的完整地址（可能有凭据，不该写）：" + leaked);
                    return false;
                }

                // ---- 4：gated 探针（只量不判）：libvlc 到底多久才自己报错 ----
                RunSlowOpenProbe();

                Log(29, "网络等待正常：慢流到点提示一次、仍停在 Opening、按停止能退出、"
                        + "慢而能播的流不会被停、第二条死流还会提示、日志里没有完整地址");
                return true;
            }
            finally
            {
                TryDelete(wavPath);
            }
        }

        /// <summary>把一条地址摆进列表并从第一项开始播（不走顺延，免得别的项掺进来）。</summary>
        private static void StartTrial(播放器.MainForm form, string path)
        {
            form.Playlist.Clear();
            form.AddPathsForTest(new[] { path });
            form.Playback.PlayIndex(0, allowFallback: false);
        }

        /// <summary>
        /// 状态机本身的断言：到点只提示一次、新的打开重新计时、本地文件不算。
        /// <para>不依赖窗口与网络，任何机器上都跑得出来。</para>
        /// </summary>
        private static bool CheckSlowOpenWatchLogic()
        {
            const string stream = "http://127.0.0.1:1/a.mp3";

            var watch = new SlowOpenWatch { ThresholdMilliseconds = 1000 };

            if (watch.Tick(1000, PlayerState.Opening, stream))
            {
                Log(29, "网络等待：还没到点就提示了");
                return false;
            }

            if (watch.Tick(1999, PlayerState.Opening, stream))
            {
                Log(29, "网络等待：差 1 毫秒到点就提示了（边界写成了 >）");
                return false;
            }

            if (!watch.Tick(2000, PlayerState.Opening, stream))
            {
                Log(29, "网络等待：到点没有提示（拿 tick 次数当时间了？）");
                return false;
            }

            if (watch.Tick(9000, PlayerState.Opening, stream))
            {
                Log(29, "网络等待：同一个流提示了不止一次");
                return false;
            }

            // 中间播起来了，下一次打开必须重新计时
            watch.Tick(9000, PlayerState.Playing, stream);

            if (watch.Tick(9200, PlayerState.Opening, stream))
            {
                Log(29, "网络等待：新一次打开没有重新计时");
                return false;
            }

            if (!watch.Tick(12000, PlayerState.Opening, stream))
            {
                Log(29, "网络等待：第二个死流没有提示（只有第一次说了）");
                return false;
            }

            // ---- 本地文件永远不算网络流 ----
            if (SlowOpenWatch.Applies(@"C:\temp\a.wav") || SlowOpenWatch.Applies("a.flac"))
            {
                Log(29, "网络等待：本地文件也被当成网络流盯着（wav 判成了 true）");
                return false;
            }

            if (!SlowOpenWatch.Applies(@"\\server\share\a.wav") || !SlowOpenWatch.Applies("rtsp://192.0.2.1/x"))
            {
                Log(29, "网络等待：网络位置 / 流没有被算进等待判定");
                return false;
            }

            var local = new SlowOpenWatch { ThresholdMilliseconds = 100 };
            local.Tick(0, PlayerState.Opening, @"C:\temp\a.wav");

            if (local.Tick(10000, PlayerState.Opening, @"C:\temp\a.wav"))
            {
                Log(29, "网络等待：本地文件也被当成网络流盯着（wav 判成了 true）");
                return false;
            }

            // ---- 日志脱敏：流地址只留 scheme://host:port/… ----
            var redacted = MediaFormats.DescribeForLog("http://user:pass@example.com:8080/a/b?token=secret");

            if (redacted.Contains("user", StringComparison.Ordinal) ||
                redacted.Contains("pass", StringComparison.Ordinal) ||
                redacted.Contains("token", StringComparison.Ordinal) ||
                redacted.Contains("/a/b", StringComparison.Ordinal))
            {
                Log(29, "网络等待：日志脱敏没去掉地址里的用户名 / 口令 / 参数（" + redacted + "）");
                return false;
            }

            if (MediaFormats.DescribeForLog(@"C:\temp\a.wav") != @"C:\temp\a.wav")
            {
                Log(29, "网络等待：本地路径被日志脱敏改掉了（排查时就没法看了）");
                return false;
            }

            return true;
        }

        /// <summary>
        /// 只量不判（<c>播放器_TEST_SLOWOPEN=秒数</c>）：播一条黑洞流，每 5 秒打印一次状态，
        /// 用来量"libvlc 自己多久才放弃"——默认阈值该定多少，靠这条数字，不靠猜。
        /// </summary>
        private static void RunSlowOpenProbe()
        {
            if (Environment.GetEnvironmentVariable("播放器_TEST_SLOWOPEN") is not { Length: > 0 } raw) return;

            var seconds = int.TryParse(raw, out var parsed) && parsed > 0 ? parsed : 10;

            using var dead = new SlowMediaServer(null, firstByteDelayMilliseconds: 0);
            using var form = new 播放器.MainForm(Array.Empty<string>());

            form.Show();
            PumpMessages(200);

            StartTrial(form, dead.Url("/probe.mp3"));

            var clock = Stopwatch.StartNew();
            var nextReport = 0.0;

            while (clock.Elapsed.TotalSeconds < seconds)
            {
                PumpMessages(500);

                if (clock.Elapsed.TotalSeconds < nextReport) continue;

                nextReport += 5;

                Log(29, $"慢流探针（只量不判）：{clock.Elapsed.TotalSeconds:0} 秒，"
                        + $"状态 {form.Engine.State}，状态栏「{form.StatusText}」");
            }
        }
    }
}
