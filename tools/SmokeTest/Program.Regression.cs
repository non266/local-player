// 检查点 14：本轮修复过的回归点（扫描器忘路径、事件跨线程封送）。

namespace SmokeTest
{
    internal static partial class Program
    {
        // -----------------------------------------------------------------
        // 14) 本轮修复的回归点
        //
        // 这些都是"改之前是坏的、但平时看不出来"的地方，必须钉住：
        // 设置/历史的原子写入与损坏备份、批量删除只通知一次、
        // 路径索引取首次出现、以及时长扫描器必须忘掉已扫过的路径。
        // -----------------------------------------------------------------

        private static bool TestRegressionFixes()
        {
            var folder = Path.Combine(AppContext.BaseDirectory, "regression");
            var problems = new List<string>();

            try
            {
                Directory.CreateDirectory(folder);

                // ---- 1. 原子写：内容替换成功，且不留下临时文件 ----
                var target = Path.Combine(folder, "atomic.json");
                File.WriteAllText(target, "旧的");

                var settings = new AppSettings { Volume = 42 };
                settings.SaveTo(target);

                if (File.ReadAllText(target) != settings.ToJson())
                    problems.Add("SaveTo 没有写入新内容");

                var leftovers = Directory.GetFiles(folder, "atomic.json.*")
                    .Where(p => p.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (leftovers.Count > 0)
                    problems.Add($"原子写留下了临时文件：{Path.GetFileName(leftovers[0])}");

                // ---- 2. 设置损坏：留备份 + 给出提示，而不是静默清零 ----
                var broken = Path.Combine(folder, "broken.json");
                File.WriteAllText(broken, "{ 这不是合法的 JSON ");

                var recovered = AppSettings.LoadFrom(broken);

                if (recovered.Volume != new AppSettings().Volume)
                    problems.Add("损坏的设置没有退回默认值");

                if (!File.Exists(broken + ".corrupt"))
                    problems.Add("损坏的设置文件没有被备份成 .corrupt");

                if (string.IsNullOrEmpty(recovered.LoadWarning))
                    problems.Add("损坏的设置没有给出 LoadWarning（用户会以为设置凭空消失）");

                // 能读的正常文件不应该带警告
                if (!string.IsNullOrEmpty(AppSettings.LoadFrom(target).LoadWarning))
                    problems.Add("正常设置被误判为损坏");

                // ---- 3. 批量删除只触发一次 Changed ----
                var playlist = new Playlist();
                playlist.AddRange(Enumerable.Range(0, 10).Select(i => $@"C:\媒体\track-{i:00}.mp3"));

                var notifications = 0;
                playlist.Changed += (s, e) => notifications++;

                playlist.RemoveMany(new[] { 1, 2, 3, 4, 5 });

                if (notifications != 1)
                    problems.Add($"RemoveMany 触发了 {notifications} 次 Changed（应为 1 次）");

                if (playlist.Count != 5)
                    problems.Add($"RemoveMany 之后剩余 {playlist.Count} 项（应为 5）");

                if (playlist.Items[0].FilePath != @"C:\媒体\track-00.mp3" ||
                    playlist.Items[4].FilePath != @"C:\媒体\track-09.mp3")
                    problems.Add("RemoveMany 删错了条目");

                // 当前项在删除范围内 → 应该变成"无"
                playlist.SetCurrent(0);
                notifications = 0;
                playlist.RemoveMany(new[] { 0 });
                if (playlist.CurrentIndex != -1)
                    problems.Add("删掉正在播放的条目后 CurrentIndex 应为 -1");
                if (notifications != 1)
                    problems.Add($"RemoveMany 删当前项时触发了 {notifications} 次 Changed（应为 1 次）");

                // 当前项在删除范围之后 → 索引要跟着前移
                var playlist2 = new Playlist();
                playlist2.AddRange(Enumerable.Range(0, 6).Select(i => $@"C:\媒体\b-{i}.mp3"));
                playlist2.SetCurrent(4);
                playlist2.RemoveMany(new[] { 0, 1 });
                if (playlist2.CurrentIndex != 2)
                    problems.Add($"当前项应前移 2 位变成 2，实际是 {playlist2.CurrentIndex}");

                // ---- 4. 路径索引取"首次出现" ----
                var dup = new Playlist();
                dup.Add(@"C:\媒体\同一首.mp3");
                dup.Add(@"C:\媒体\另一首.mp3");
                dup.Add(@"C:\媒体\同一首.mp3");

                if (dup.IndexOfPath(@"C:\媒体\同一首.mp3") != 0)
                    problems.Add("重复路径没有返回首次出现的索引");

                if (dup.IndexOfPath(@"c:\媒体\另一首.MP3") != 1)
                    problems.Add("路径查找没有忽略大小写");

                if (dup.IndexOfPath(@"C:\媒体\不存在.mp3") != -1)
                    problems.Add("不存在的路径应返回 -1");

                // 删除能让索引重建正确
                dup.RemoveAt(0);
                if (dup.IndexOfPath(@"C:\媒体\同一首.mp3") != 1)
                    problems.Add("删除之后路径索引没有重建");

                // ---- 5. 时长扫描器必须忘掉已排过队的路径 ----
                var scanProblem = CheckScannerForgetsPaths(folder);
                if (scanProblem != null) problems.Add(scanProblem);

                // ---- 6. 引擎事件必须在 UI 线程上派发（窗体还没建句柄时也不能例外） ----
                var marshalProblem = CheckEventMarshalling();
                if (marshalProblem != null) problems.Add(marshalProblem);

                if (problems.Count == 0)
                {
                    Log(14, "原子写 / 损坏备份 / 批量删除单次通知 / 路径索引 / 扫描器遗忘 全部正确");
                    return true;
                }

                foreach (var problem in problems)
                    Log(14, problem);

                return false;
            }
            catch (Exception ex)
            {
                Log(14, "回归测试异常: " + ex);
                return false;
            }
            finally
            {
                TryDeleteDirectory(folder);
            }
        }

        /// <summary>
        /// 清空播放列表后重新添加同一个文件，时长必须能重新扫出来。
        /// <para>
        /// 挡的是这个 bug：<c>DurationScanner._seen</c> 只记"排过队"，解析成功后不移除，
        /// 而 <c>ClearPending</c> 以前只清队列不清它。于是"清空列表 → 再添加同一个目录"
        /// 之后一条都不会重新排队，新条目永远停在 <c>--:--</c>，非得重启程序才恢复。
        /// </para>
        /// <para>
        /// 返回失败原因；通过时返回 <c>null</c>。两种情况要分开报：
        /// "第一次就扫不出时长"是环境问题（文件被占用 / 解析失败），
        /// 那和"忘记路径"这条回归是两回事，混在一起会误导排查方向。
        /// </para>
        /// </summary>
        private static string CheckScannerForgetsPaths(string folder)
        {
            var wav = Path.Combine(folder, "scanner-forget.wav");
            WriteWav(wav, seconds: 2, frequency: 440);

            var found = 0;

            using var libvlc = new LibVLC("--quiet");
            using var scanner = new DurationScanner(libvlc);

            scanner.DurationFound += (s, e) =>
            {
                if (string.Equals(e.FilePath, wav, StringComparison.OrdinalIgnoreCase))
                    Interlocked.Increment(ref found);
            };

            scanner.Enqueue(new[] { wav });

            if (!WaitForCount(() => Volatile.Read(ref found), 1, out var firstMs))
            {
                return $"扫描器 {firstMs}ms 内没有解析出时长（{Path.GetFileName(wav)}），"
                       + "无法判断 ClearPending 是否生效（这更像环境问题，不是忘记路径）";
            }

            // 清空播放列表（会调 ClearPending），再重新添加同一个文件。
            scanner.ClearPending();
            scanner.Enqueue(new[] { wav });

            if (!WaitForCount(() => Volatile.Read(ref found), 2, out var secondMs))
            {
                return $"ClearPending 之后同一个文件没有重新排队"
                       + $"（第一次解析用了 {firstMs}ms，第二次等了 {secondMs}ms）";
            }

            return null;
        }

        /// <summary>
        /// 引擎事件必须在 UI 线程上派发，窗体<b>还没创建句柄</b>时也不能例外。
        /// <para>
        /// 挡的是这个洞：<c>Control.InvokeRequired</c> 在句柄还没创建时返回 <c>false</c>，
        /// 于是 <c>if (InvokeRequired) BeginInvoke(...) else action()</c> 这种写法会把
        /// libvlc 回调线程上来的事件<b>就地执行</b>——那是在跨线程操作 ToolStrip / ListView。
        /// 主窗口的构造过程正好处在这个窗口里（构造函数里就 Open 了命令行传入的媒体），
        /// 表现为偶发的跨线程异常。修复后改用"构造时记下的 UI 线程 id"判断，
        /// 并把暂时派发不出去的事件排队、等句柄就绪后补发。
        /// </para>
        /// </summary>
        private static string CheckEventMarshalling()
        {
            using var owner = new Form();   // 刻意不显示，句柄还没有建立

            if (owner.IsHandleCreated)
                return "测试前提不成立：新建的 Form 不该已经有句柄";

            // 先确认这个坑在框架层面确实存在，否则下面那条断言就是空的
            var invokeRequiredFromOtherThread = true;
            var probe = new Thread(() =>
                invokeRequiredFromOtherThread =
                    ((System.ComponentModel.ISynchronizeInvoke)owner).InvokeRequired)
            { IsBackground = true };

            probe.Start();
            probe.Join();

            if (invokeRequiredFromOtherThread)
            {
                return "测试前提不成立：无句柄时 InvokeRequired 竟然是 true，"
                       + "框架行为变了，这条回归测试需要重写";
            }

            using var engine = new PlayerEngine(owner);

            var handlerThread = 0;
            var stateDelivered = false;

            engine.StateChanged += (s, state) =>
            {
                handlerThread = Environment.CurrentManagedThreadId;
                stateDelivered = true;
            };

            // 模拟 libvlc 在它的原生线程上回调
            var callbackThread = 0;
            var worker = new Thread(() =>
            {
                callbackThread = Environment.CurrentManagedThreadId;
                engine.SetState(PlayerState.Playing);
            })
            { IsBackground = true };

            worker.Start();
            worker.Join();

            if (stateDelivered && handlerThread == callbackThread)
            {
                return $"引擎事件在原生回调线程（{callbackThread}）上就地执行了、没有派发回 UI 线程"
                       + " —— 这正是跨线程操作控件的那条路径";
            }

            // 句柄就绪后必须把攒下的事件补发出来，不能丢
            owner.CreateControl();
            engine.FlushPendingUiActions();

            if (!stateDelivered)
                return "UI 线程就绪后事件没有被补发（构造期间的事件被丢掉了）";

            if (handlerThread != Environment.CurrentManagedThreadId)
                return $"补发时事件仍然不在 UI 线程上（{handlerThread}）";

            return null;
        }
    }
}
