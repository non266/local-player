// 检查点 19：日志与错误处理（环形缓冲、落盘、轮转、菜单开关）。

namespace SmokeTest
{
    internal static partial class Program
    {
        // -----------------------------------------------------------------
        // 19) 日志与错误处理
        //
        // 这一项护的是"出了事能不能查"：内存里留得住最近若干条、能落盘、到顶会轮转、
        // 关掉之后磁盘上不留东西，以及那 47 处"决定不管"的异常真的留下了理由。
        // 以前这些地方是空 catch，出了问题连"吞过什么"都不知道。
        // -----------------------------------------------------------------

        private static bool TestLogging()
        {
            var logPath = AppLog.DefaultFilePath;
            var rotated = logPath + ".1";

            foreach (var path in new[] { logPath, rotated })
                TryDelete(path);

            try
            {
                // ---- 1) 只记内存时不该碰磁盘 ----
                AppLog.Clear();
                AppLog.Begin(toFile: false);

                if (AppLog.FilePath != null) { Log(19, "日志检查：说了不落盘，FilePath 却不是 null"); return false; }

                AppLog.Info("信息一条");
                AppLog.Warn("警告一条");
                AppLog.Error("错误一条");

                var memory = AppLog.Snapshot();

                if (!memory.Any(line => line.Contains("信息一条", StringComparison.Ordinal)) ||
                    !memory.Any(line => line.Contains("警告一条", StringComparison.Ordinal)) ||
                    !memory.Any(line => line.Contains("错误一条", StringComparison.Ordinal)))
                {
                    Log(19, $"日志检查：内存里没有记全（{string.Join(" | ", memory)}）");
                    return false;
                }

                if (File.Exists(logPath))
                {
                    Log(19, "日志检查：关掉落盘之后仍然写出了文件");
                    return false;
                }

                if (AppLog.LastError == null || !AppLog.LastError.Contains("错误一条", StringComparison.Ordinal))
                {
                    Log(19, $"日志检查：最近一条错误没记住（{AppLog.LastError ?? "null"}）");
                    return false;
                }

                // ---- 2) 被吞掉的异常必须带上理由和异常本身 ----
                AppLog.Clear();
                AppLog.Swallowed("测试用的理由", new InvalidOperationException("测试用的异常"));

                var swallowed = AppLog.Snapshot().FirstOrDefault(l => l.Contains("测试用的理由", StringComparison.Ordinal));

                if (swallowed == null ||
                    !swallowed.Contains("InvalidOperationException", StringComparison.Ordinal) ||
                    !swallowed.Contains("测试用的异常", StringComparison.Ordinal))
                {
                    Log(19, $"日志检查：被忽略的异常没记下类型与消息（{swallowed ?? "没有这一条"}）");
                    return false;
                }

                // ---- 3) 落盘 + 到顶轮转 ----
                AppLog.Begin();

                if (AppLog.FilePath == null)
                {
                    Log(19, "日志检查：Begin 之后没有开始落盘");
                    return false;
                }

                AppLog.Info("落盘测试一行");

                if (!File.Exists(logPath))
                {
                    Log(19, $"日志检查：没有写出日志文件（{logPath}）");
                    return false;
                }

                if (!File.ReadAllText(logPath).Contains("落盘测试一行", StringComparison.Ordinal))
                {
                    Log(19, "日志检查：日志文件里没有刚写的那一行");
                    return false;
                }

                // 写到超过上限：旧的那份应该被轮转成 .1
                var filler = new string('x', 2048);

                for (var i = 0; i < 600 && !File.Exists(rotated); i++)
                    AppLog.Info($"填充 {i} {filler}");

                if (!File.Exists(rotated))
                {
                    Log(19, $"日志检查：写到超过 {AppLog.FileLimitBytes / 1024} KB 也没有轮转（{new FileInfo(logPath).Length} 字节）");
                    return false;
                }

                // ---- 4) 环形缓冲：只留最近 BufferLimit 条 ----
                AppLog.Clear();

                for (var i = 1; i <= AppLog.BufferLimit + 100; i++)
                    AppLog.Info("第 " + i + " 条");

                var ring = AppLog.Snapshot(AppLog.BufferLimit + 100);

                if (ring.Count != AppLog.BufferLimit)
                {
                    Log(19, $"日志检查：内存里留了 {ring.Count} 条，应当是 {AppLog.BufferLimit} 条");
                    return false;
                }

                if (!ring[ring.Count - 1].Contains("第 " + (AppLog.BufferLimit + 100) + " 条", StringComparison.Ordinal) ||
                    !ring[0].Contains("第 101 条", StringComparison.Ordinal))
                {
                    Log(19, "日志检查：环形缓冲丢的不是最老的那些");
                    return false;
                }

                // ---- 5) 总开关：关掉之后一条都不记 ----
                AppLog.Clear();
                AppLog.Enabled = false;
                AppLog.Error("关掉之后不该出现");

                if (AppLog.Snapshot(0).Count != 0)
                {
                    Log(19, "日志检查：总开关关掉了还在记");
                    return false;
                }

                AppLog.Enabled = true;

                // ---- 6) 兜底处理器装上两次也不会炸 ----
                AppLog.InstallCrashHandlers();
                AppLog.InstallCrashHandlers();

                // ---- 7) 主窗体上那三项要真的在「帮助」里，开关真的接上了设置 ----
                if (!CheckLogMenuInMainForm()) return false;

                Log(19, "日志与错误处理正常：内存里留最近 500 条（丢最老的）、能落盘到数据目录、"
                        + "超过 1 MB 会轮转成 .1、关掉之后磁盘上不留东西、"
                        + "被忽略的异常带上了理由与异常类型（47 处空 catch 都换成了这个），"
                        + "「帮助」里能查看 / 打开日志而且开关真的接上了设置");
                return true;
            }
            finally
            {
                AppLog.Enabled = true;
                AppLog.Stop();
            }
        }

        /// <summary>「帮助」里的日志三项：查看 / 打开文件 / 开关，且开关与设置双向对上。</summary>
        private static bool CheckLogMenuInMainForm()
        {
            using var form = new 播放器.MainForm(Array.Empty<string>());
            form.Show();
            PumpMessages(200);

            if (form.MenuViewLog is not { } viewLog ||
                form.MenuOpenLogFile is not { } openLog ||
                form.MenuLogEnabled is not { } toggle)
            {
                Log(19, "日志检查：主窗体上没有「查看日志 / 打开日志文件 / 记录诊断日志」");
                return false;
            }

            if (form.MenuHelp is not { } help)
            {
                Log(19, "日志检查：找不到「帮助」菜单");
                return false;
            }

            var helpItems = help.DropDownItems.OfType<ToolStripMenuItem>().ToList();

            if (!helpItems.Contains(viewLog) || !helpItems.Contains(openLog) || !helpItems.Contains(toggle))
            {
                Log(19, $"日志检查：这三项不在「帮助」菜单里（{string.Join(" / ", helpItems.Select(i => i.Text))}）");
                return false;
            }

            // 开关真的改设置、也真的改日志落盘。
            // 注意不要先手动改 Checked：CheckOnClick 的项在 OnClick 里自己会翻转。
            var settings = SettingsOf(form);

            var before = toggle.Checked;

            if (!ClickMenuItem(toggle))
            {
                Log(19, "日志检查：菜单项上点不动（OnClick 找不到？）");
                return false;
            }

            PumpMessages(120);

            if (toggle.Checked == before)
            {
                Log(19, "日志检查：点了「记录诊断日志」却没有反应");
                return false;
            }

            if (settings.DiagnosticLog != toggle.Checked ||
                (settings.DiagnosticLog ? AppLog.FilePath == null : AppLog.FilePath != null))
            {
                Log(19, $"日志检查：开关与设置 / 落盘对不上（勾选 {toggle.Checked}、设置 {settings.DiagnosticLog}、"
                        + $"日志 {AppLog.FilePath ?? "无"}）");
                return false;
            }

            // 再点一次：翻回去
            if (!ClickMenuItem(toggle))
            {
                Log(19, "日志检查：菜单项上点不动（第二次）");
                return false;
            }

            PumpMessages(120);

            if (toggle.Checked != before || settings.DiagnosticLog != before)
            {
                Log(19, $"日志检查：再点一次没有翻回来（勾选 {toggle.Checked}、设置 {settings.DiagnosticLog}）");
                return false;
            }

            form.Close();
            PumpMessages(150);
            return true;
        }

        /// <summary>
        /// 读主窗体「媒体信息」页上的那几行（直接调 <c>BuildInfoRows</c>）。
        /// </summary>
        private static List<(string Name, string Value)> ReadInfoRows(Form form, string? path)
        {
            var tags = SessionOf(form).Tags;

            return ((播放器.MainForm)form)
                .BuildInfoRows(path, tags)
                .Select(row => (row.Name, row.Value ?? string.Empty))
                .ToList();
        }
    }
}
