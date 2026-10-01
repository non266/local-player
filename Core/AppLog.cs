using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace 播放器.Core
{
    public enum LogLevel
    {
        /// <summary>只为排查用的细节（"这里吞掉了一个预期内的异常"之类）。</summary>
        Debug = 0,

        /// <summary>正常事件（启动、切歌、导入歌单……）。</summary>
        Info = 1,

        /// <summary>预期内的失败，但用户可能想知道（文件没了、连不上服务）。</summary>
        Warning = 2,

        /// <summary>不该发生的错误。</summary>
        Error = 3
    }

    /// <summary>
    /// 极小的日志：内存里留最近若干条，同时按需落盘。
    /// <para>
    /// 为什么要有它：这个程序以前的"诊断手段"只有状态栏那一行字，一旦用户报
    /// "歌词没出来"、"设置没保存"，我拿不到任何上下文——尤其是那些
    /// <c>catch (Exception) { }</c> 吞掉的地方，出了事连"吞了什么"都不知道。
    /// </para>
    /// <para>
    /// 两条刻意的设计：
    /// <list type="number">
    ///   <item><b>不抛异常</b>。写日志失败绝不能让程序出错，所有写入都在 try 里。</item>
    ///   <item><b>落盘可控且封顶</b>。日志按"追加 + 到 1 MB 就轮转一次"写，
    ///   不会无限长大；也可以在设置里关掉（<see cref="Enabled"/>）。</item>
    /// </list>
    /// </para>
    /// <para>
    /// ⚠ <b>不要把敏感内容写进来</b>：这里会记录文件路径之类的东西，
    /// 但 LrcAPI 的鉴权密钥一类的凭据<b>永远不能</b>进日志（只记"有没有配"）。
    /// </para>
    /// </summary>
    public static class AppLog
    {
        /// <summary>内存里保留多少条（"刚刚发生了什么"随取随看）。</summary>
        public const int BufferLimit = 500;

        /// <summary>日志文件封顶大小；超过就轮转一次（旧的那份改名成 <c>.1</c>）。</summary>
        public const long FileLimitBytes = 1024 * 1024;

        private sealed class LogEntry
        {
            public DateTime Time;
            public LogLevel Level;
            public string Message = string.Empty;
        }

        private static readonly object Gate = new object();
        private static readonly Queue<LogEntry> Buffer = new Queue<LogEntry>();

        /// <summary>日志文件路径；<c>null</c> 表示只记在内存里、不落盘。</summary>
        private static string? _filePath;

        private static bool _crashHandlersInstalled;
        private static string? _lastError;

        /// <summary>总开关；关掉之后连内存也不记（给"我不想留任何记录"的用户）。</summary>
        public static bool Enabled { get; set; } = true;

        /// <summary>默认的日志文件路径（数据目录下的 <c>播放器.log</c>）。</summary>
        public static string DefaultFilePath => Path.Combine(AppSettings.SettingsDirectory, "播放器.log");

        /// <summary>现在往哪个文件写；只记内存时返回 <c>null</c>。</summary>
        public static string? FilePath
        {
            get { lock (Gate) return _filePath; }
        }

        /// <summary>最近一条 Error（给"关于"对话框和状态提示用）。</summary>
        public static string? LastError
        {
            get { lock (Gate) return _lastError; }
        }

        /// <summary>
        /// 启动日志：写一行启动信息，并按需落盘。
        /// <para>刻意<b>不</b>清空已经记下的内容：启动早期（单实例、libvlc 初始化）那几条同样有用。</para>
        /// </summary>
        public static void Begin(bool toFile = true)
        {
            SetFileOutput(toFile);

            Info($"日志开始（{Process.GetCurrentProcess().ProcessName}，"
                 + $"版本 {typeof(AppLog).Assembly.GetName().Version}，"
                 + $"{Environment.OSVersion.VersionString}，.NET {Environment.Version}）");
        }

        /// <summary>
        /// 开关"写文件"。关掉之后仍然记在内存里（「帮助 → 查看日志」还能看），
        /// 只是磁盘上不留记录。
        /// </summary>
        public static void SetFileOutput(bool enabled)
        {
            lock (Gate) _filePath = enabled ? DefaultFilePath : null;
        }

        /// <summary>停止落盘（退出前调用）。</summary>
        public static void Stop()
        {
            lock (Gate) _filePath = null;
        }

        public static void Debug(string message, Exception? exception = null) =>
            Write(LogLevel.Debug, message, exception);

        public static void Info(string message, Exception? exception = null) =>
            Write(LogLevel.Info, message, exception);

        public static void Warn(string message, Exception? exception = null) =>
            Write(LogLevel.Warning, message, exception);

        public static void Error(string message, Exception? exception = null) =>
            Write(LogLevel.Error, message, exception);

        /// <summary>
        /// "这个异常我们决定不管它"——把<b>为什么可以不管</b>写在 <paramref name="reason"/> 里。
        /// <para>
        /// 它就是给那些 <c>catch (Exception) { }</c> 用的：行为不变（照样继续跑），
        /// 但出问题时能在日志里看到"这里吞过一次、原因是什么"，而不是一片空白。
        /// </para>
        /// </summary>
        public static void Swallowed(string reason, Exception? exception = null) =>
            Write(LogLevel.Debug, "已忽略：" + reason, exception);

        /// <summary>取最近的日志（新的在后）。</summary>
        public static IReadOnlyList<string> Snapshot(int max = 200)
        {
            lock (Gate)
            {
                var lines = new List<string>(Buffer.Count);

                foreach (var entry in Buffer)
                    lines.Add(Format(entry));

                if (max > 0 && lines.Count > max)
                    lines.RemoveRange(0, lines.Count - max);

                return lines;
            }
        }

        /// <summary>把最近若干条拼成一段文本（"查看日志"对话框用）。</summary>
        public static string SnapshotText(int max = 200) => string.Join(Environment.NewLine, Snapshot(max));

        public static void Clear()
        {
            lock (Gate)
            {
                Buffer.Clear();
                _lastError = null;
            }
        }

        /// <summary>
        /// 装上"最后一道兜底"：UI 线程未处理异常 / 其它线程未处理异常 / 未观察的 Task 异常。
        /// <para>只装一次；重复调用是安全的。</para>
        /// </summary>
        public static void InstallCrashHandlers()
        {
            if (_crashHandlersInstalled) return;
            _crashHandlersInstalled = true;

            Application.ThreadException += (s, e) =>
            {
                Error("界面线程上未处理的异常", e.Exception);

                try
                {
                    MessageBox.Show(
                        BuildCrashText("程序遇到了一个未处理的错误，操作可能没有完成。", e.Exception),
                        "出错了",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
                catch (Exception)
                {
                    // 连对话框都弹不出来（比如窗口正在销毁）就只能靠日志了
                }
            };

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                Error("未处理的异常（进程即将结束）", e.ExceptionObject as Exception);

            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                // 观察掉它，别让一个后台任务的异常把进程带下去
                Error("后台任务里未观察到的异常", e.Exception);
                e.SetObserved();
            };
        }

        /// <summary>崩溃提示的正文：错误信息 + 日志在哪。</summary>
        public static string BuildCrashText(string headline, Exception? exception)
        {
            var text = new StringBuilder(headline);

            if (exception != null)
                text.Append(Environment.NewLine).Append(Environment.NewLine).Append(exception.Message);

            var path = FilePath;

            text.Append(Environment.NewLine).Append(Environment.NewLine)
                .Append(path != null ? "详情已写进日志：" + path : "（本次没有开启日志落盘）");

            return text.ToString();
        }

        // ---- 内部 -------------------------------------------------------------

        private static void Write(LogLevel level, string message, Exception? exception)
        {
            if (!Enabled) return;

            var entry = new LogEntry
            {
                Time = DateTime.Now,
                Level = level,
                Message = text(message, exception)
            };

            string? path;

            lock (Gate)
            {
                Buffer.Enqueue(entry);

                while (Buffer.Count > BufferLimit)
                    Buffer.Dequeue();

                if (level == LogLevel.Error) _lastError = entry.Message;

                path = _filePath;
            }

            if (path != null) Append(path, Format(entry));
        }

        private static string text(string message, Exception? exception)
        {
            if (exception == null) return message;

            // 只要类型 + 消息 + 第一层栈帧：完整栈会把日志撑得没法看
            var frame = new StackTrace(exception, false).GetFrame(0);

            var where = frame?.GetMethod() is { } method
                ? $" @ {method.DeclaringType?.Name}.{method.Name}"
                : string.Empty;

            return $"{message} ← {exception.GetType().Name}: {exception.Message}{where}";
        }

        private static string Format(LogEntry entry) =>
            $"{entry.Time:yyyy-MM-dd HH:mm:ss.fff} [{Level(entry.Level)}] {entry.Message}";

        private static string Level(LogLevel level) => level switch
        {
            LogLevel.Debug => "调试",
            LogLevel.Info => "信息",
            LogLevel.Warning => "警告",
            _ => "错误"
        };

        /// <summary>追加一行；文件到顶就轮转一次。任何失败都吞掉（日志不该拖垮程序）。</summary>
        private static void Append(string path, string line)
        {
            lock (Gate)
            {
                try
                {
                    var folder = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

                    if (File.Exists(path) && new FileInfo(path).Length >= FileLimitBytes)
                        File.Move(path, path + ".1", overwrite: true);

                    File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
                }
                catch (Exception)
                {
                    // 日志写不进去就算了：不能因为磁盘满/目录只读把程序搞崩。
                    // 这里刻意不再递归调用 AppLog。
                }
            }
        }
    }
}
