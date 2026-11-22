// 检查点 37（第 32 个运行步骤）：数据目录与便携模式（1.29.0）。
//
//   ① 纯判定矩阵：环境变量最优先 / portable.ini → <程序目录>\data / 都没有 → %AppData%\播放器 /
//      便携目录建不出来 → 退回 %AppData% 并说明 / 同名目录不算 marker；
//   ② 本进程：冒烟靠环境变量隔离，不能被 marker 抢走（SettingsDirectory == smoke-data）；
//   ③ 界面：设置路径与「关于」里写的是同一个目录。

namespace SmokeTest
{
    internal static partial class Program
    {
        private static bool TestDataDirectory()
        {
            var folder = Path.Combine(AppContext.BaseDirectory, "data-dir-check");

            TryDeleteDirectory(folder);

            try
            {
                if (!CheckDataDirectoryResolve(folder)) return false;

                // ② 本进程：环境变量优先（冒烟整套都靠这条隔离）
                var current = DataDirectory.Current;

                if (current.Source != DataDirectorySource.Environment)
                {
                    Log(37, $"数据目录：冒烟进程的数据目录来源是 {current.Source}，期望环境变量（{current.Directory}）");
                    return false;
                }

                if (!PathsEqual(AppSettings.SettingsDirectory, current.Directory))
                {
                    Log(37, $"数据目录：SettingsDirectory 是「{AppSettings.SettingsDirectory}」，"
                            + $"判定结果是「{current.Directory}」");
                    return false;
                }

                if (!AppSettings.SettingsFilePath.StartsWith(current.Directory, StringComparison.OrdinalIgnoreCase))
                {
                    Log(37, $"数据目录：设置文件「{AppSettings.SettingsFilePath}」不在数据目录「{current.Directory}」下面");
                    return false;
                }

                // ③「关于」里写的是同一个路径（用户按"关于"就能找到数据在哪儿）
                using var form = new 播放器.MainForm(Array.Empty<string>());
                form.Show();
                PumpMessages(150);

                var about = form.BuildAboutText();

                if (!about.Contains(current.Directory, StringComparison.OrdinalIgnoreCase))
                {
                    Log(37, "数据目录：「关于」里写的设置路径不是当前数据目录（"
                            + current.Directory + "）");
                    return false;
                }

                Log(37, "数据目录正常：环境变量最优先、portable.ini 落到 <程序目录>\\data、"
                        + "便携目录建不出来时退回 %AppData% 并说明、同名目录不算 marker；"
                        + "冒烟自己的数据目录仍是环境变量指定的临时目录，「关于」里写的是同一处");
                return true;
            }
            catch (Exception ex)
            {
                Log(37, "数据目录检查失败: " + ex);
                return false;
            }
            finally
            {
                TryDeleteDirectory(folder);
            }
        }

        /// <summary>纯判定矩阵（不碰进程里的缓存，全部喂路径）。</summary>
        private static bool CheckDataDirectoryResolve(string folder)
        {
            var exe = Path.Combine(folder, "exe");
            var appData = Path.Combine(folder, "appdata");

            Directory.CreateDirectory(exe);
            Directory.CreateDirectory(appData);

            var marker = Path.Combine(exe, DataDirectory.PortableMarkerFileName);
            var appFolder = Path.Combine(appData, "播放器");

            // 环境变量优先（哪怕 marker 在）
            File.WriteAllText(marker, "; portable");
            var env = DataDirectory.Resolve(exe, appData, @"D:\env-data");

            if (env.Source != DataDirectorySource.Environment || !PathsEqual(env.Directory, @"D:\env-data"))
            {
                Log(37, $"数据目录：环境变量没有最优先（来源 {env.Source}、目录「{env.Directory}」）");
                return false;
            }

            if (env.Notice != null)
            {
                Log(37, "数据目录：环境变量模式下不该有提示，却说「" + env.Notice + "」");
                return false;
            }

            // marker → <exe>\data（真的建出来）
            var portable = DataDirectory.Resolve(exe, appData, null, directory =>
            {
                Directory.CreateDirectory(directory);
                return true;
            });

            var expected = Path.Combine(exe, "data");

            if (portable.Source != DataDirectorySource.Portable || !PathsEqual(portable.Directory, expected))
            {
                Log(37, $"数据目录：有 portable.ini 却没落到「{expected}」（来源 {portable.Source}、"
                        + $"目录「{portable.Directory}」）");
                return false;
            }

            if (!Directory.Exists(expected))
            {
                Log(37, "数据目录：判定为便携模式，但 <程序目录>\\data 没有真的建出来");
                return false;
            }

            if (portable.Notice == null || !portable.Notice.Contains("便携模式", StringComparison.Ordinal))
            {
                Log(37, "数据目录：便携模式没有启动提示（用户不知道数据搬了地方）");
                return false;
            }

            // 便携目录不可写 → 退回 %AppData%\播放器 并说明
            var fallback = DataDirectory.Resolve(exe, appData, null, _ => false);

            if (fallback.Source != DataDirectorySource.PortableFallback ||
                !PathsEqual(fallback.Directory, appFolder))
            {
                Log(37, $"数据目录：便携目录写不进去时没有退回「{appFolder}」（来源 {fallback.Source}、"
                        + $"目录「{fallback.Directory}」）");
                return false;
            }

            if (fallback.Notice == null || !fallback.Notice.Contains("退回", StringComparison.Ordinal))
            {
                Log(37, "数据目录：便携目录写不进去时没有提示（应该说明退回到哪里）");
                return false;
            }

            // 没有 marker → %AppData%\播放器，无提示
            File.Delete(marker);

            var plain = DataDirectory.Resolve(exe, appData, null);

            if (plain.Source != DataDirectorySource.AppData || !PathsEqual(plain.Directory, appFolder))
            {
                Log(37, "数据目录：没有 marker 时没有落到 %AppData%\\播放器");
                return false;
            }

            if (plain.Notice != null)
            {
                Log(37, "数据目录：普通模式下不该有提示，却说「" + plain.Notice + "」");
                return false;
            }

            // 同名目录不算 marker
            Directory.CreateDirectory(marker);

            var asDirectory = DataDirectory.Resolve(exe, appData, null);

            if (asDirectory.Source != DataDirectorySource.AppData)
            {
                Log(37, "数据目录：把 portable.ini 建成目录也被当成了便携开关");
                return false;
            }

            return true;
        }

        private static bool PathsEqual(string? left, string? right)
        {
            if (left == null || right == null) return ReferenceEquals(left, right);

            return string.Equals(
                Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
    }
}
