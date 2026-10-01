// 入口与调度：步骤清单、按名字筛选、启动前置。具体检查在 Program.*.cs 里（partial class）。

namespace SmokeTest
{
    /// <summary>
    /// 冒烟测试：验证这个播放器最容易被环境问题击穿的三处。
    /// <list type="number">
    ///   <item>原生 libvlc 是否随生成正确部署并能真实播放（进度推进 + 播完事件）。</item>
    ///   <item>主窗体（设计器布局 + DPI 缩放 + VideoView 绑定 + 生命周期）能否正常显示与关闭。</item>
    ///   <item>设置文件的 JSON 往返是否正确（含中文与枚举）。</item>
    /// </list>
    /// 不依赖任何外部媒体文件。
    /// <para>用法：先构建主工程，再构建本工程，然后在主程序输出目录执行 <c>dotnet SmokeTest.dll</c>。</para>
    /// <para>
    /// 这个类按功能拆在多个文件里（<c>Program.*.cs</c>）：每份只管一段检查，
    /// 共用的骨架在 <c>Program.Harness.cs</c>。拆分只搬家，没有改任何检查的行为。
    /// </para>
    /// </summary>
    internal static partial class Program
    {
        private const int StepCount = 20;

        /// <summary>一步检查：名字用于筛选，<c>Needs</c> 声明它依赖的前置检查。</summary>
        private sealed class SmokeStep
        {
            public SmokeStep(string name, Func<bool> run, params string[] needs)
            {
                Name = name;
                Run = run;
                Needs = needs;
            }

            public string Name { get; }

            public Func<bool> Run { get; }

            public string[] Needs { get; }
        }

        [STAThread]
        private static int Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            // 可选筛选：只跑名字里含这个关键字的检查（连它依赖的前置检查一起）。
            // 全量一次 50 秒，只改了一个功能时没必要每次都等；能单独跑也逼着每一步
            // 自己准备前置条件，而不是偷偷依赖前面某一步留下的状态。
            var filter = args.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a) && !a.StartsWith("-", StringComparison.Ordinal));

            // 把数据目录指到临时位置：这个测试会真的走保存设置 / 写播放历史的流程，
            // 不能让它动到使用者在 %AppData% 里的真实配置。
            var dataDir = Path.Combine(AppContext.BaseDirectory, "smoke-data");
            TryDeleteDirectory(dataDir);
            Directory.CreateDirectory(dataDir);
            Environment.SetEnvironmentVariable("播放器_DATA_DIR", dataDir);

            // 默认关掉"在线歌词与封面"：那会在切歌时真的去联网，
            // 既让测试依赖网络、又会给每一步平白加上几秒。
            // 第 16 步会自己打开它、并把它指向本地假服务器。
            try
            {
                var settings = AppSettings.Load();
                settings.OnlineLookup = false;
                settings.Save();
            }
            catch (Exception)
            {
                // 写不进去也没关系：第 16 步以外的那几步最多慢一点
            }

            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var steps = new[]
            {
                new SmokeStep("播放内核", TestPlayback),
                new SmokeStep("主窗体与主题", TestMainForm),
                new SmokeStep("时长扫描", TestDurationScanner),
                new SmokeStep("设置持久化", TestSettingsRoundTrip),
                new SmokeStep("标签与歌词", TestTagAndLyrics),
                new SmokeStep("均衡器与引擎", TestEqualizerAndEngine),
                new SmokeStep("侧栏与播放列表", TestSidebarRendering, "标签与歌词"),
                new SmokeStep("单实例", TestSingleInstance),
                new SmokeStep("视频功能与逐帧", TestVideoFeatures),
                new SmokeStep("回归点", TestRegressionFixes),
                new SmokeStep("桌面歌词", TestDesktopLyrics, "标签与歌词"),
                new SmokeStep("在线歌词与封面", TestOnlineLyrics),
                new SmokeStep("歌单库", TestPlaylistLibrary),
                new SmokeStep("歌词偏移", TestLyricsOffset, "标签与歌词"),
                new SmokeStep("日志与错误处理", TestLogging),
                new SmokeStep("设置方案", TestSettingsProfiles)
            };

            var selected = SelectSteps(steps, filter);

            if (selected.Count == 0)
            {
                Console.WriteLine($"没有名字含「{filter}」的检查。可用："
                                  + string.Join(" / ", steps.Select(s => s.Name)));
                return 2;
            }

            if (filter != null)
            {
                Console.WriteLine($"只跑 {selected.Count} 项：{string.Join(" / ", selected.Select(s => s.Name))}");
                Console.WriteLine();
            }

            try
            {
                LibVLCSharp.Shared.Core.Initialize();
                Log(1, "Core.Initialize 成功");
            }
            catch (Exception ex)
            {
                Log(1, "Core.Initialize 失败: " + ex);
                return 1;
            }

            foreach (var step in selected)
            {
                if (!step.Run()) return 1;
            }


            TryDeleteDirectory(dataDir);

            Console.WriteLine();
            Console.WriteLine("SMOKE TEST PASSED");
            return 0;
        }

        /// <summary>按关键字挑出要跑的检查，并把它们依赖的前置检查一并带上。</summary>
        private static List<SmokeStep> SelectSteps(IReadOnlyList<SmokeStep> steps, string filter)
        {
            if (string.IsNullOrEmpty(filter)) return new List<SmokeStep>(steps);

            var wanted = new List<SmokeStep>();

            void Require(SmokeStep step)
            {
                if (wanted.Contains(step)) return;

                foreach (var need in step.Needs)
                {
                    var prerequisite = steps.FirstOrDefault(s => s.Name == need);
                    if (prerequisite != null) Require(prerequisite);
                }

                wanted.Add(step);
            }

            foreach (var step in steps)
            {
                if (step.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) Require(step);
            }

            // 按声明顺序跑，前置检查自然在前
            return steps.Where(wanted.Contains).ToList();
        }
    }
}
