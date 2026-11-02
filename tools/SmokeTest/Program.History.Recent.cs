// 检查点 8：1.9.0 A4 —— 「最近播放」与「记住播放进度」解耦。
//
// 现场（所有者报「最近播放没有记录」）：settings.json 里 ResumePlayback=false，
// history.json 里 23 条记录的 LastPlayedUtc 全是默认值（都是"按文件记住音轨 / 延迟"顺手建的空壳）。
// 根因是 MainForm.RecordCurrentPosition 的第一行 `if (!_settings.ResumePlayback) return;`——
// 一个开关同时管住了"记不记位置"和"记不记听过"。
//
// 这里两段：先验 PlaybackHistory 的规矩（纯内存），再验主窗体真的按开关分流。

namespace SmokeTest
{
    internal static partial class Program
    {
        /// <summary>A4 的两段检查：内存规矩 + 主窗体接线。</summary>
        private static bool CheckRecentPlayback()
        {
            if (!CheckMarkPlayedRules()) return false;
            return CheckRecentPlayedWithoutResume();
        }

        /// <summary>
        /// 只记「听过」（<c>MarkPlayed</c>）的规矩：
        /// 进最近播放、<b>不</b>写位置、不抹掉已有的位置、网络流与空路径不记。
        /// </summary>
        private static bool CheckMarkPlayedRules()
        {
            var history = new PlaybackHistory();

            const string justHeard = @"C:\media\album\heard.mp3";
            const string hasPosition = @"C:\media\album\position.mp3";

            // 1) 只记听过：进最近播放，但没有续播位置
            history.MarkPlayed(justHeard);

            if (history.Recent.Count != 1 || history.Recent[0] != justHeard)
            {
                Log(8, $"最近播放：只记「听过」的文件没有进最近播放（{history.Recent.Count} 条）");
                return false;
            }

            if (history.TryGetResumePosition(justHeard, out _))
            {
                Log(8, "最近播放：只记「听过」的时候连续播位置也写了");
                return false;
            }

            // 2) 只记听过不许抹掉已经记下的位置（开关关着播两下，打开之后还要能接着看）
            history.Record(hasPosition, TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(10));

            if (!history.TryGetResumePosition(hasPosition, out var before) ||
                Math.Abs(before.TotalSeconds - 180) > 0.5)
            {
                Log(8, "最近播放：准备数据时没记下 3 分钟的位置（检查本身有问题）");
                return false;
            }

            var stampBefore = history.GetLastPlayed(hasPosition);
            history.MarkPlayed(hasPosition);

            if (!history.TryGetResumePosition(hasPosition, out var after) ||
                Math.Abs(after.TotalSeconds - 180) > 0.5)
            {
                Log(8, $"最近播放：只记「听过」把已经记下的续播位置抹掉了（现在 {after.TotalSeconds:0}s，期望 180s）");
                return false;
            }

            var stampAfter = history.GetLastPlayed(hasPosition);
            if (stampBefore == null || stampAfter == null || stampAfter <= stampBefore)
            {
                Log(8, "最近播放：只记「听过」没有把「最近播过」的时间往前推");
                return false;
            }

            // 3) 网络流与空路径都不记（和 Record 同一条规矩）
            var count = history.Count;
            history.MarkPlayed("https://example.com/live.m3u8");
            history.MarkPlayed("   ");

            if (history.Count != count)
            {
                Log(8, $"最近播放：只记「听过」的时候把网络流 / 空路径也记了（{count} → {history.Count} 条）");
                return false;
            }

            Log(8, "最近播放的规矩正确：只记「听过」就进最近播放、不写位置、不抹已有位置，网络流与空路径不记");
            return true;
        }

        /// <summary>
        /// 主窗体：关掉「记住播放进度」之后播一个文件，它要照样进「文件 → 最近播放」，
        /// 但不许记进度、菜单里也不许显示「· 位置」；开关打开之后两样都回来。
        /// </summary>
        private static bool CheckRecentPlayedWithoutResume()
        {
            var defaults = AppSettings.Load();
            var path = Path.Combine(AppContext.BaseDirectory, "recent-off.wav");

            // 60 秒 + 往后跳 30 秒：真记了位置的话，它会超过"值得续播"的 15 秒门槛，
            // 用 3 秒的小样本验不出"有没有偷记位置"（3 秒本来就不到门槛）。
            WriteWav(path, seconds: 60, frequency: 330);

            try
            {
                var settings = AppSettings.Load();
                settings.ResumePlayback = false;
                settings.Save();

                using var form = new 播放器.MainForm(Array.Empty<string>());
                form.Show();
                PumpMessages(300);

                var playMenu = FindTopMenuItem(form, "播放");
                var resumeItem = playMenu == null ? null : FindMenuItem(playMenu.DropDownItems, "记住播放进度");
                var fileMenu = FindTopMenuItem(form, "文件");
                var recentItem = fileMenu == null ? null : FindMenuItem(fileMenu.DropDownItems, "最近播放");

                if (resumeItem == null || recentItem == null)
                {
                    Log(8, "最近播放：找不到「播放 → 记住播放进度」或「文件 → 最近播放」菜单项");
                    return false;
                }

                if (resumeItem.Checked)
                {
                    Log(8, "最近播放：设置里明明关着「记住播放进度」，菜单却是勾上的（下面的检查会变成假验）");
                    return false;
                }

                form.Engine.Open(path);
                form.Engine.Play();

                if (!PumpUntil(() => form.Engine.HasMedia && form.Engine.Time > 0, 15000))
                {
                    Log(8, "最近播放：60 秒的 wav 夹具没播起来（引擎没进 HasMedia）");
                    return false;
                }

                form.Engine.SeekTo(30000);
                PumpMessages(900);

                if (!form.History.Recent.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    Log(8, $"最近播放：关掉「记住播放进度」之后播过的文件没有进最近播放（{form.History.Recent.Count} 条）");
                    return false;
                }

                if (form.History.TryGetResumePosition(path, out var leaked))
                {
                    Log(8, $"最近播放：关掉开关时还是记了进度（现在 {leaked.TotalSeconds:0}s，期望一条都不记）");
                    return false;
                }

                // 补一条"上次开关开着时记下的位置"：没有位置的话，下面两段（显示 / 不显示「·」）
                // 两种实现看起来都一样，验不出菜单到底看没看开关。
                form.History.Record(path, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60));
                form.RefreshRecentMenu();

                var texts = recentItem.DropDownItems
                    .OfType<ToolStripMenuItem>()
                    .Select(item => item.Text)
                    .ToList();

                if (texts.Any(text => text.Contains("·", StringComparison.Ordinal)))
                {
                    Log(8, "最近播放：开关关着，菜单里还是显示了续播位置（" + string.Join(" / ", texts) + "）");
                    return false;
                }

                if (!texts.Any(text => text.IndexOf(
                        Path.GetFileName(path), StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    Log(8, "最近播放：菜单里没有刚才播过的那个文件（" + string.Join(" / ", texts) + "）");
                    return false;
                }

                // 打开开关：同一条记录上的位置要重新显示出来（菜单跟着开关走，不留旧样子）
                SetMenuChecked(resumeItem, true);
                form.RefreshRecentMenu();

                if (!recentItem.DropDownItems.OfType<ToolStripMenuItem>()
                        .Any(item => item.Text.Contains("·", StringComparison.Ordinal)))
                {
                    Log(8, "最近播放：开关打开之后，菜单里没显示续播位置（记着 30s）");
                    return false;
                }

                Log(8, "最近播放与开关解耦正常：关掉「记住播放进度」照样记「听过」、不记位置、菜单不显示「· 位置」；打开之后两样都回来");
                return true;
            }
            catch (Exception ex)
            {
                Log(8, "最近播放检查失败: " + ex);
                return false;
            }
            finally
            {
                TryDelete(path);

                // 这一段把 ResumePlayback 摆成 false 了：还原，后面的检查还要用这份设置
                defaults.Save();
            }
        }
    }
}
