using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

// 崩溃取证（1.20.0）。
//
// M8 那一族原生崩（0xC0000005）是进程直接没，AppLog 的托管兜底根本轮不到，
// 事后连"哪次崩的、崩在什么场景"都无从查起。这里做两件事：
//   1) 用 Windows 自带的 WER "本地转储" 给本程序开一份迷你转储（HKCU，不需要管理员）；
//   2) 每次正常退出留一个"干净退出"记号；下次启动如果发现比记号更新的转储 / 致命标记，
//      就在状态栏说一句「之前有一次没有正常退出」——把"静默死掉"变成"可查的死"。
//
// 实测（2026-11-13，这台 Win10 19045）：HKCU 下写 DumpFolder **不生效**（WER 仍然写它
// 默认的 %LOCALAPPDATA%\CrashDumps），但 DumpType / DumpCount 是认的。所以这里只配
// "类型 + 份数"，路径交给 Windows 的默认位置——那个位置本来就是我们自己的用户目录，
// 卸载也不该删（里面可能还有别的程序的转储）。
//
// 本地**没有** cdb / windbg，所以这一版只保证"证据留下来了、能定位到哪次崩溃"，
// 不假装能现场分析（写进 README 的已知限制）。

namespace 播放器.Core
{
    /// <summary>
    /// 崩溃转储的开关（WER LocalDumps）与"上次是不是正常退出"的判断。
    /// <para>所有失败都只记日志、不抛：取证是安全网，它坏了不该影响播放。</para>
    /// </summary>
    public static class CrashDump
    {
        /// <summary>注册表里用的程序名（按 exe 名匹配，和当前进程名无关，冒烟跑在 dotnet.exe 下也一样）。</summary>
        public const string ProductExeName = "播放器.exe";

        private const string RegistryRoot =
            @"Software\Microsoft\Windows\Windows Error Reporting\LocalDumps\";

        private const string CleanExitFile = "last-exit.txt";

        private const string ReportedFile = "dumps-reported.txt";

        private const string FatalFile = "fatal.txt";

        /// <summary>迷你转储（1 = mini）。完整转储动辄几百 MB，对"崩在哪一句"没有更多帮助。</summary>
        private const int DumpType = 1;

        /// <summary>最多留 3 份：够看"是不是同一个崩点"，又不至于把磁盘塞满。</summary>
        private const int DumpCount = 3;

        /// <summary>
        /// 转储放在哪儿。Windows 在 HKCU 配置下固定用这个目录（见文件头注释），
        /// 测试可以改成临时目录，不去碰真正的崩溃现场。
        /// </summary>
        internal static string? DumpsFolderOverride;

        public static string DumpsDirectory =>
            DumpsFolderOverride ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrashDumps");

        private static string RegistryPath(string exeName) => RegistryRoot + exeName;

        // =====================================================================
        // 注册表：开关
        // =====================================================================

        /// <summary>本程序当前有没有开本地转储（只看键在不在）。</summary>
        public static bool IsEnabled(string exeName)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegistryPath(exeName));
                return key != null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>读回类型与份数（冒烟测试拿它核对写进去的到底是什么）。</summary>
        public static bool TryReadSettings(string exeName, out int dumpType, out int dumpCount)
        {
            dumpType = 0;
            dumpCount = 0;

            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegistryPath(exeName));
                if (key == null) return false;

                dumpType = key.GetValue("DumpType") is int type ? type : 0;
                dumpCount = key.GetValue("DumpCount") is int count ? count : 0;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>打开本地转储（幂等）。</summary>
        public static bool Enable(string exeName, out string error)
        {
            error = string.Empty;

            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RegistryPath(exeName), writable: true);

                if (key == null)
                {
                    error = "建不出注册表键（HKCU 下没有写权限？）";
                    return false;
                }

                key.SetValue("DumpType", DumpType, RegistryValueKind.DWord);
                key.SetValue("DumpCount", DumpCount, RegistryValueKind.DWord);

                // 别留旧的 DumpFolder：实测 HKCU 下它不生效，留着只会让下一个人以为它有用
                key.DeleteValue("DumpFolder", throwOnMissingValue: false);

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>关掉本地转储（键不存在也算成功）。</summary>
        public static bool Disable(string exeName, out string error)
        {
            error = string.Empty;

            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(RegistryPath(exeName), throwOnMissingSubKey: false);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>把注册表跟"要不要记录诊断日志"对齐（启动时与开关里各调一次）。</summary>
        public static void Sync(bool enabled, string exeName)
        {
            bool ok;
            string error;

            if (enabled)
            {
                ok = Enable(exeName, out error);
            }
            else
            {
                ok = Disable(exeName, out error);
            }

            if (!ok)
            {
                AppLog.Warn("崩溃转储：" + (enabled ? "开" : "关") + "不起来（" + error + "）");
            }
        }

        // =====================================================================
        // 上次是不是正常退出
        // =====================================================================

        /// <summary>正常退出时留一个记号（<see cref="ShutdownForm"/> 两个出口都走这里）。</summary>
        public static void MarkCleanExit()
        {
            try
            {
                SafeFile.WriteAllText(
                    Path.Combine(AppSettings.SettingsDirectory, CleanExitFile),
                    DateTime.UtcNow.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            catch (Exception ex)
            {
                AppLog.Swallowed("写干净退出记号失败", ex);
            }
        }

        /// <summary>托管致命异常（进程即将结束）也留个标记：那种情况 WER 不一定给转储。</summary>
        public static void MarkFatal(string reason)
        {
            try
            {
                File.WriteAllText(
                    Path.Combine(AppSettings.SettingsDirectory, FatalFile),
                    DateTime.UtcNow.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + Environment.NewLine + reason);
            }
            catch (Exception)
            {
                // 进程都要没了，写不进去就算了
            }
        }

        /// <summary>
        /// 有没有"一次没正常退出"要报告：返回那句提示（报告一次之后就记住，不重复报），
        /// 没有就返回 <c>null</c>。
        /// </summary>
        public static string? PendingCrashNotice()
        {
            try
            {
                var settings = AppSettings.SettingsDirectory;

                var clean = ReadTicks(Path.Combine(settings, CleanExitFile));
                var reported = ReadTicks(Path.Combine(settings, ReportedFile));

                DateTime? newest = null;

                foreach (var file in FindDumps())
                {
                    var time = File.GetLastWriteTimeUtc(file);

                    if (clean != null && time <= clean) continue;
                    if (reported != null && time <= reported) continue;

                    if (newest == null || time > newest) newest = time;
                }

                var fatal = Path.Combine(settings, FatalFile);

                if (File.Exists(fatal))
                {
                    var time = File.GetLastWriteTimeUtc(fatal);

                    if ((clean == null || time > clean) && (reported == null || time > reported) &&
                        (newest == null || time > newest))
                    {
                        newest = time;
                    }
                }

                if (newest == null) return null;

                SafeFile.WriteAllText(
                    Path.Combine(settings, ReportedFile),
                    newest.Value.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));

                // 第一次启动就崩（那时还没有干净退出记号）也要报：clean 为 null 时只要比"报过"新就算。
                return "程序之前有一次没有正常退出（Windows 已留下崩溃转储，在 " + DumpsDirectory + "）";
            }
            catch (Exception ex)
            {
                AppLog.Swallowed("检查上次是否正常退出失败", ex);
                return null;
            }
        }

        private static IEnumerable<string> FindDumps()
        {
            try
            {
                var folder = DumpsDirectory;
                if (!Directory.Exists(folder)) return Array.Empty<string>();

                return Directory.GetFiles(folder, ProductExeName + ".*.dmp");
            }
            catch (Exception)
            {
                return Array.Empty<string>();
            }
        }

        private static DateTime? ReadTicks(string file)
        {
            try
            {
                if (!File.Exists(file)) return null;

                var text = File.ReadAllText(file).Trim();
                if (!long.TryParse(text, out var ticks)) return null;

                return new DateTime(ticks, DateTimeKind.Utc);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
