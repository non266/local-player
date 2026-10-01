// 一个"假 LrcApi"：本地 TCP 说最小可用的 HTTP/1.1，让端到端检查不依赖外网。

namespace SmokeTest
{
    internal static partial class Program
    {
        /// <summary>
        /// 一个"假 LrcApi"：用 <see cref="System.Net.Sockets.TcpListener"/> 说几句最小可用的 HTTP/1.1。
        /// <para>
        /// 端到端检查因此完全不依赖外网，也不会因为对方改协议而变红；
        /// 反过来，真要确认对方协议变了没有，用 播放器_TEST_LRCAPI=1 打真接口。
        /// </para>
        /// </summary>
        private sealed class FakeLrcApiServer : IDisposable
        {
            private readonly System.Net.Sockets.TcpListener _listener;
            private readonly Thread _thread;
            private volatile bool _running = true;

            public FakeLrcApiServer()
            {
                // 端口交给系统挑一个空闲的，免得和别的东西撞上
                _listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
                _listener.Start();

                Port = ((System.Net.IPEndPoint)_listener.LocalEndpoint).Port;

                _thread = new Thread(Serve) { IsBackground = true, Name = "fake-lrcapi" };
                _thread.Start();
            }

            public int Port { get; }

            public string BaseUrl => $"http://127.0.0.1:{Port}";

            /// <summary>收到的请求数（用来验"同一首不该重复联网"）。</summary>
            public int RequestCount;

            /// <summary>收到的请求路径（排查用）。</summary>
            public readonly List<string> Paths = new List<string>();

            /// <summary>旧版 <c>/lyrics</c> 是否可用（默认可用，和手上那台自建实例一致）。</summary>
            public volatile bool LegacyLyrics = true;

            /// <summary>搜索结果里是否带封面地址（默认带：实测只有这条封面路是通的）。</summary>
            public volatile bool CoverInSearch = true;

            /// <summary>新版封面接口是否可用（默认不可用：实测它 302 到一个拼错的地址再 404）。</summary>
            public volatile bool CoverApi;

            /// <summary>旧版 <c>/cover</c> 是否可用（默认不可用：实测那台要鉴权，返回 401）。</summary>
            public volatile bool LegacyCover;

            /// <summary>封面接口是否要求鉴权（模仿手上那台：歌词接口不用，封面接口要）。</summary>
            public volatile bool RequireAuth;

            /// <summary>服务端期望的鉴权密钥。</summary>
            public volatile string AuthKey = string.Empty;

            /// <summary>最近一次收到的 Authorization 头（<c>null</c> 表示没带）。</summary>
            public volatile string? LastAuthorization;

            /// <summary>带鉴权头就 500：模拟"鉴权实现有 bug"的自建实例（实测是个 Python TypeError）。</summary>
            public volatile bool BrokenOnAuth;

            /// <summary>歌词接口一律 500（用来验"服务端报错要说出来"）。</summary>
            public volatile bool AlwaysFailLyrics;

            private void Serve()
            {
                while (_running)
                {
                    try
                    {
                        using var client = _listener.AcceptTcpClient();
                        client.ReceiveTimeout = 8000;
                        client.SendTimeout = 8000;

                        using var stream = client.GetStream();

                        var header = ReadRequest(stream);
                        if (header == null) continue;

                        Interlocked.Increment(ref RequestCount);

                        var requestLine = header.Split('\n').FirstOrDefault() ?? string.Empty;
                        var path = requestLine.Split(' ').ElementAtOrDefault(1) ?? "/";

                        var authorization = ReadHeader(header, "Authorization");

                        LastAuthorization = authorization;

                        lock (Paths) Paths.Add(path);

                        var (status, contentType, body) = Route(path, authorization);

                        WriteResponse(stream, status, contentType, body);

                        // 半关（发 FIN）而不是直接 Dispose 掉就走：
                        // 让客户端把响应读完再看到连接结束
                        try
                        {
                            client.Client.Shutdown(System.Net.Sockets.SocketShutdown.Send);
                        }
                        catch (Exception)
                        {
                            // 已经断了
                        }
                    }
                    catch (Exception)
                    {
                        if (!_running) return;

                        // 一次连接出问题不该把整个服务器弄死
                        Thread.Sleep(20);
                    }
                }
            }

            /// <summary>
            /// 路由尽量照着手上那台自建 LrcAPI 的真实行为来：
            /// <list type="bullet">
            ///   <item><c>/api/v1/lyrics/single</c> 给的是<b>纯文本</b>（逼客户端去找带轴的版本）；</item>
            ///   <item><c>/lyrics</c>（旧版）给带时间轴的 LRC；</item>
            ///   <item><c>/api/v1/lyrics/advance</c> 给候选列表，里面带 <c>cover</c> 字段；</item>
            ///   <item><c>/api/v1/cover/*</c> 和旧版 <c>/cover</c> 默认都不给东西（前者 302→404，后者 401）。</item>
            /// </list>
            /// </summary>
            private (string Status, string ContentType, byte[] Body) Route(string path, string? authorization)
            {
                // 鉴权实现崩掉的服务：只要带了 Authorization 就 500（实测那台就是这样）
                if (BrokenOnAuth && !string.IsNullOrEmpty(authorization))
                {
                    return ("500 Internal Server Error", "text/plain; charset=utf-8",
                        Encoding.UTF8.GetBytes("TypeError: string indices must be integers, not 'str'"));
                }

                if (AlwaysFailLyrics && path.StartsWith("/api/v1/lyrics", StringComparison.Ordinal))
                {
                    return ("500 Internal Server Error", "text/plain; charset=utf-8",
                        Encoding.UTF8.GetBytes("TypeError: string indices must be integers, not 'str'"));
                }

                var authorized = !RequireAuth ||
                                 string.Equals(authorization, AuthKey, StringComparison.Ordinal);

                // 封面接口要鉴权（歌词接口不要）——和手上那台的行为一致
                var isCoverApi = path.StartsWith("/api/v1/cover/", StringComparison.Ordinal) ||
                                 path.StartsWith("/cover", StringComparison.Ordinal);

                if (isCoverApi && !path.StartsWith("/cover.png", StringComparison.Ordinal) && !authorized)
                {
                    return ("401 Unauthorized", "text/html; charset=utf-8",
                        Encoding.UTF8.GetBytes("<html><title>401 Unauthorized</title></html>"));
                }

                if (path.StartsWith("/api/v1/lyrics/single", StringComparison.Ordinal))
                    return ("200 OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("在线歌词纯文本版\n第二行"));

                if (path.StartsWith("/lyrics", StringComparison.Ordinal))
                {
                    if (!LegacyLyrics) return ("404 Not Found", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("not found"));

                    return ("200 OK", "text/plain; charset=utf-8",
                        Encoding.UTF8.GetBytes("[00:01.00]旧版接口第一句\n[00:03.00]旧版接口第二句"));
                }

                if (path.StartsWith("/api/v1/lyrics/advance", StringComparison.Ordinal))
                {
                    var cover = CoverInSearch ? $"\"cover\":\"http://127.0.0.1:{Port}/cover.png\"," : "\"cover\":null,";

                    var json = $$"""
                        [
                          {"id":"1","title":"测试歌曲","artist":"别人","lyrics":"[00:09.00]错的那一条","cover":null},
                          {"id":"2","title":"测试歌曲","artist":"测试歌手",{{cover}}"lyrics":"[00:01.00]在线第一句\n[00:03.00]在线第二句"}
                        ]
                        """;

                    return ("200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));
                }

                if (path.StartsWith("/api/v1/cover/", StringComparison.Ordinal))
                {
                    if (!CoverApi) return ("404 Not Found", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("not found"));

                    var json = $$"""{"title":"测试歌曲","img":"http://127.0.0.1:{{Port}}/cover.png"}""";

                    return ("200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));
                }

                if (path.StartsWith("/cover.png", StringComparison.Ordinal))
                    return ("200 OK", "image/png", BuildPng());

                if (path.StartsWith("/cover", StringComparison.Ordinal))
                {
                    if (LegacyCover) return ("200 OK", "image/png", BuildPng());

                    // 手上那台的旧版封面接口要鉴权，返回的是 HTML 403
                    return ("403 Forbidden", "text/html; charset=utf-8",
                        Encoding.UTF8.GetBytes("<html><title>403 Forbidden</title></html>"));
                }

                return ("404 Not Found", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("not found"));
            }

            /// <summary>现场画一张真 PNG（不内嵌 base64，免得写错了没人发现）。</summary>
            private static byte[] BuildPng()
            {
                using var bitmap = new Bitmap(8, 8);

                using (var graphics = Graphics.FromImage(bitmap))
                {
                    graphics.Clear(Color.FromArgb(0x33, 0x88, 0xCC));
                    graphics.FillRectangle(Brushes.White, 2, 2, 4, 4);
                }

                using var memory = new MemoryStream();
                bitmap.Save(memory, System.Drawing.Imaging.ImageFormat.Png);

                return memory.ToArray();
            }

            /// <summary>
            /// 读一条请求，返回**整个请求头**（含请求行），并且一定读完。
            /// <para>
            /// ⚠ 必须读完再回响应：只读请求行就把 socket 关掉的话，接收缓冲区里还留着没读的数据，
            /// Windows 会发 RST 而不是正常关闭，客户端读响应体时就会报
            /// <c>Error while copying content to a stream</c>——第一次搭这个假服务器时就是这么踩的，
            /// 现象还很像"客户端没发第二个请求"。
            /// </para>
            /// </summary>
            private static string? ReadRequest(Stream stream)
            {
                var buffer = new List<byte>();
                var single = new byte[1];

                while (buffer.Count < 16384)
                {
                    var read = stream.Read(single, 0, 1);
                    if (read <= 0) return buffer.Count == 0 ? null : Encoding.UTF8.GetString(buffer.ToArray());

                    buffer.Add(single[0]);

                    // 请求头结束（空行）就停
                    if (buffer.Count >= 4 &&
                        buffer[^4] == (byte)'\r' && buffer[^3] == (byte)'\n' &&
                        buffer[^2] == (byte)'\r' && buffer[^1] == (byte)'\n')
                    {
                        break;
                    }
                }

                return Encoding.UTF8.GetString(buffer.ToArray());
            }

            /// <summary>从请求头里取一个字段的值（大小写不敏感）；没有就返回 <c>null</c>。</summary>
            private static string? ReadHeader(string request, string name)
            {
                foreach (var line in request.Split('\n'))
                {
                    var colon = line.IndexOf(':');
                    if (colon <= 0) continue;

                    if (!line[..colon].Trim().Equals(name, StringComparison.OrdinalIgnoreCase)) continue;

                    return line[(colon + 1)..].Trim();
                }

                return null;
            }

            private static void WriteResponse(Stream stream, string status, string contentType, byte[] body)
            {
                var header = new StringBuilder()
                    .Append("HTTP/1.1 ").Append(status).Append("\r\n")
                    .Append("Content-Type: ").Append(contentType).Append("\r\n")
                    .Append("Content-Length: ").Append(body.Length).Append("\r\n")
                    .Append("Connection: close\r\n\r\n")
                    .ToString();

                var headerBytes = Encoding.ASCII.GetBytes(header);

                stream.Write(headerBytes, 0, headerBytes.Length);
                stream.Write(body, 0, body.Length);
                stream.Flush();
            }

            public void Dispose()
            {
                _running = false;

                try
                {
                    _listener.Stop();
                }
                catch (Exception)
                {
                    // 已经停了
                }
            }
        }
    }
}
