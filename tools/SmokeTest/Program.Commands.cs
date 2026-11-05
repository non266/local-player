// 检查点 22：命令层（1.12.0 R13）。
//
// 这一轮把"同一件事在界面上有六张脸"收成一套：菜单 / 快捷键 / 工具栏 / 托盘 /
// 任务栏缩略图按钮 / 卡片。这里验三件事：
//   1. 目录本身自洽（每条命令取得到、快捷键不撞车、按下键执行的是同一条）；
//   2. 菜单上写的键与目录一致，F1 帮助里一条不少；
//   3. 六张脸真的都落到同一个执行入口（按执行计数与状态变化判定）。
//
// 跨条目的系统集成（任务栏按钮挂载时序、SMTC）仍由第 6 步管，这里只验"点击去了哪儿"。

namespace SmokeTest
{
    internal static partial class Program
    {
        private static bool CheckCommandLayer()
        {
            if (!CheckCommandCatalog()) return false;

            var wav = Path.Combine(AppContext.BaseDirectory, "command-layer.wav");

            try
            {
                WriteWav(wav, seconds: 8, frequency: 440);

                using var form = new 播放器.MainForm(Array.Empty<string>());
                form.Show();
                PumpMessages(200);

                form.AddPathsForTest(new[] { wav });

                return CheckCommandSurfaces(form) && CheckCommandStates(form) && CheckCommandHelp(form);
            }
            catch (Exception ex)
            {
                Log(22, "命令层检查失败: " + ex);
                return false;
            }
            finally
            {
                TryDelete(wav);
            }
        }

        /// <summary>目录自洽：每条命令都取得到、快捷键不撞车、按键映射到同一条。</summary>
        private static bool CheckCommandCatalog()
        {
            var all = PlayerCommandCatalog.All;

            if (all.Count < 20)
            {
                Log(22, $"命令层：命令目录只有 {all.Count} 条（至少该有播放 / 文件 / 视图这一批）");
                return false;
            }

            var ids = new HashSet<PlayerCommandId>();

            foreach (var definition in all)
            {
                if (!ids.Add(definition.Id))
                {
                    Log(22, $"命令层：命令目录里「{definition.Id}」出现了两次");
                    return false;
                }

                if (PlayerCommandCatalog.Get(definition.Id).Id != definition.Id)
                {
                    Log(22, $"命令层：按 id 取「{definition.Id}」取回来的不是它自己");
                    return false;
                }

                if (string.IsNullOrWhiteSpace(definition.HelpText))
                {
                    Log(22, $"命令层：「{definition.Id}」没有帮助文字（F1 里会是一行空白）");
                    return false;
                }

                if (definition.ShowInHelp && definition.Display.Length == 0)
                {
                    Log(22, $"命令层：「{definition.Id}」说要在帮助里出现，却没有快捷键显示串");
                    return false;
                }
            }

            // 快捷键不能撞车：撞了之后按下键走哪条命令就取决于目录顺序，是隐形的顺序依赖。
            var byKey = new Dictionary<Keys, PlayerCommandId>();

            foreach (var definition in all)
            {
                foreach (var shortcut in definition.AllShortcuts)
                {
                    if (byKey.TryGetValue(shortcut, out var owner) && owner != definition.Id)
                    {
                        Log(22, $"命令层：快捷键「{PlayerCommandCatalog.FormatKeys(shortcut)}」被两条命令同时占用"
                                + $"（{owner} 与 {definition.Id}）");
                        return false;
                    }

                    byKey[shortcut] = definition.Id;

                    if (!PlayerCommandCatalog.TryMap(shortcut, out var mapped) || mapped != definition.Id)
                    {
                        Log(22, $"命令层：按下「{PlayerCommandCatalog.FormatKeys(shortcut)}」执行的是「{mapped}」，"
                                + $"不是「{definition.Id}」");
                        return false;
                    }
                }
            }

            Log(22, $"命令目录正常：{all.Count} 条命令、{byKey.Count} 个快捷键，没有撞车，按键都映射到同一条");
            return true;
        }

        /// <summary>
        /// 六张脸走同一个入口：快捷键 / 菜单 / 托盘 / 任务栏按钮 / 卡片各点一次，
        /// 执行计数各加一，且引擎状态真的按预期变。
        /// </summary>
        private static bool CheckCommandSurfaces(播放器.MainForm form)
        {
            var playPause = PlayerCommandId.PlayPause;

            if (form.CommandExecutionCount(playPause) != 0)
            {
                Log(22, "命令层：还没操作，播放 / 暂停的执行计数就不是 0 了");
                return false;
            }

            // ---- 1. 快捷键：Space 开播（没有媒体时会「播当前项 / 第一项」）----
            if (!SendShortcut(form, Keys.Space))
            {
                Log(22, "命令层：Space 没有被接住（ProcessCmdKey 返回 false）");
                return false;
            }

            if (!PumpUntil(() => form.Engine.HasMedia && form.Engine.IsPlaying, 15000))
            {
                Log(22, "命令层：按 Space 之后没有开始播放");
                return false;
            }

            if (!ExpectCommand(form, playPause, expected: 1, "快捷键")) return false;

            // ---- 2. 菜单：点「播放 / 暂停」→ 暂停 ----
            var menuItem = form.CommandBindings
                .FirstOrDefault(binding => binding.Id == playPause && binding.Item is ToolStripMenuItem)
                ?.Item as ToolStripMenuItem;

            if (menuItem == null)
            {
                Log(22, "命令层：菜单里找不到绑到「播放 / 暂停」的项");
                return false;
            }

            if (!ClickMenuItem(menuItem))
            {
                Log(22, "命令层：菜单「播放 / 暂停」点不动");
                return false;
            }

            if (!PumpUntil(() => !form.Engine.IsPlaying, 8000))
            {
                Log(22, "命令层：点菜单里的「播放 / 暂停」没有暂停");
                return false;
            }

            if (!ExpectCommand(form, playPause, expected: 2, "菜单")) return false;

            // ---- 3. 托盘：点「播放 / 暂停」→ 继续 ----
            var trayItem = form.TrayMenu?.Items
                .OfType<ToolStripMenuItem>()
                .FirstOrDefault(item => item.Text.Contains("播放 / 暂停", StringComparison.Ordinal));

            if (trayItem == null)
            {
                Log(22, "命令层：托盘里找不到「播放 / 暂停」");
                return false;
            }

            if (!ClickMenuItem(trayItem))
            {
                Log(22, "命令层：托盘里的「播放 / 暂停」点不动");
                return false;
            }

            if (!PumpUntil(() => form.Engine.IsPlaying, 8000))
            {
                Log(22, "命令层：点托盘里的「播放 / 暂停」没有继续播放");
                return false;
            }

            if (!ExpectCommand(form, playPause, expected: 3, "托盘")) return false;

            // ---- 4. 任务栏缩略图按钮：喂一条与系统一样的 WM_COMMAND → 暂停 ----
            SendThumbButtonClick(form, TaskbarThumbnailButtons.ButtonPlayPause);

            if (!PumpUntil(() => !form.Engine.IsPlaying, 8000))
            {
                Log(22, "命令层：任务栏按钮的「播放 / 暂停」没有暂停");
                return false;
            }

            if (!ExpectCommand(form, playPause, expected: 4, "任务栏按钮")) return false;

            // ---- 5. 再按一次 Space 继续，然后点音频卡片上的按钮 → 暂停 ----
            SendShortcut(form, Keys.Space);

            if (!PumpUntil(() => form.Engine.IsPlaying, 8000))
            {
                Log(22, "命令层：按 Space 没有继续播放");
                return false;
            }

            form.SetWindowMode(WindowModes.Audio);

            if (!PumpUntil(() => form.IsAudioWindow && form.NowPlaying != null, 8000))
            {
                Log(22, "命令层：没有切进音频窗口（卡片上的按钮验不到）");
                return false;
            }

            var cardButton = form.NowPlaying!.Transport.PlayPauseButton;

            if (!cardButton.Enabled)
            {
                Log(22, "命令层：正在播的时候，卡片上的「播放 / 暂停」却是灰的");
                return false;
            }

            cardButton.PerformClick();

            if (!PumpUntil(() => !form.Engine.IsPlaying, 8000))
            {
                Log(22, "命令层：点卡片上的「播放 / 暂停」没有暂停");
                return false;
            }

            if (!ExpectCommand(form, playPause, expected: 6, "音频卡片")) return false;

            // ---- 6. 工具栏「停止」：也必须是同一条命令 ----
            var stopButton = form.CommandBindings
                .FirstOrDefault(binding => binding.Id == PlayerCommandId.Stop && binding.Item is not ToolStripMenuItem)
                ?.Item;

            if (stopButton == null)
            {
                Log(22, "命令层：工具栏上找不到绑到「停止」的按钮");
                return false;
            }

            stopButton.PerformClick();

            if (!PumpUntil(() => !form.Engine.HasMedia, 8000))
            {
                Log(22, "命令层：点工具栏的「停止」没有停下来");
                return false;
            }

            if (form.CommandExecutionCount(PlayerCommandId.Stop) != 1)
            {
                Log(22, "命令层：工具栏的「停止」没有走统一命令"
                        + $"（执行计数 {form.CommandExecutionCount(PlayerCommandId.Stop)}）");
                return false;
            }

            Log(22, "命令层表面正常：快捷键 / 菜单 / 托盘 / 任务栏按钮 / 音频卡片 / 工具栏都落到同一个执行入口，"
                    + "每次点击执行计数各加一、引擎状态按预期变");
            return true;
        }

        /// <summary>可用性 / 勾选态由同一份快照推出：没媒体时停止灰、列表还在时播放能点。</summary>
        private static bool CheckCommandStates(播放器.MainForm form)
        {
            form.RefreshTransportState();

            var availability = form.CurrentCommandAvailability;

            if (availability.HasMedia)
            {
                Log(22, "命令层：已经停止了，状态快照里却还说有媒体");
                return false;
            }

            if (!availability.HasPlaylistItems)
            {
                Log(22, "命令层：列表里还有一条，状态快照却说没有");
                return false;
            }

            var menuStop = form.CommandBindings
                .FirstOrDefault(binding => binding.Id == PlayerCommandId.Stop && binding.Item is ToolStripMenuItem)
                ?.Item;

            var menuPlayPause = form.CommandBindings
                .FirstOrDefault(binding => binding.Id == PlayerCommandId.PlayPause && binding.Item is ToolStripMenuItem)
                ?.Item;

            if (menuStop == null || menuPlayPause == null)
            {
                Log(22, "命令层：菜单里少了「停止」或「播放 / 暂停」");
                return false;
            }

            if (menuStop.Enabled)
            {
                Log(22, "命令层：没有媒体时「停止」还能点");
                return false;
            }

            if (!menuPlayPause.Enabled)
            {
                Log(22, "命令层：列表里还有条目时「播放 / 暂停」却是灰的");
                return false;
            }

            // 菜单上的快捷键显示串必须与目录逐字一致（以前设计器里一份、快捷键 switch 一份、帮助一份）。
            foreach (var binding in form.CommandBindings)
            {
                if (binding.Item is not ToolStripMenuItem item) continue;

                var expected = PlayerCommandCatalog.Get(binding.Id).MenuDisplay;
                if (expected == null) continue;

                if (item.ShortcutKeyDisplayString != expected)
                {
                    Log(22, $"命令层：菜单「{item.Text}」上写的快捷键是「{item.ShortcutKeyDisplayString}」，"
                            + $"目录里是「{expected}」");
                    return false;
                }
            }

            // 可用性：每条绑定项都要和命令层的判断一致（不能"工具栏能点、菜单灰着"）。
            foreach (var binding in form.CommandBindings)
            {
                if (binding.Item.Enabled == form.CanExecuteCommand(binding.Id)) continue;

                Log(22, $"命令层：菜单 / 工具栏「{BindingName(binding)}」的可用性与命令层不一致"
                        + $"（界面 {(binding.Item.Enabled ? "能点" : "是灰的")}，命令层说"
                        + $"{(form.CanExecuteCommand(binding.Id) ? "能点" : "不能点")}）");
                return false;
            }

            Log(22, "命令状态正常：没媒体时「停止」置灰、列表还在时「播放 / 暂停」能点，"
                    + "菜单上的快捷键与目录逐字一致，所有绑定项的可用性都来自同一份快照");
            return true;
        }

        /// <summary>F1 帮助必须从目录生成：每条带键命令都在，显示串也在。</summary>
        private static bool CheckCommandHelp(播放器.MainForm form)
        {
            var text = form.BuildShortcutsText();

            foreach (var definition in PlayerCommandCatalog.All)
            {
                if (!definition.ShowInHelp) continue;

                if (!text.Contains(definition.Display, StringComparison.Ordinal))
                {
                    Log(22, $"命令层：F1 帮助里没有「{definition.HelpText}」（{definition.Display}）");
                    return false;
                }

                if (!text.Contains(definition.HelpText, StringComparison.Ordinal))
                {
                    Log(22, $"命令层：F1 帮助里没有「{definition.HelpText}」这句说明");
                    return false;
                }
            }

            // 不是命令的那几行也要还在（Esc / 双击画面 / Delete …）。
            if (!text.Contains("Esc", StringComparison.Ordinal) ||
                !text.Contains("双击画面", StringComparison.Ordinal) ||
                !text.Contains("Delete", StringComparison.Ordinal))
            {
                Log(22, "命令层：F1 帮助里少了「不是命令」的那几行（Esc / 双击画面 / Delete）");
                return false;
            }

            Log(22, $"快捷键说明正常：{PlayerCommandCatalog.All.Count(definition => definition.ShowInHelp)} 条带键命令全部"
                    + "从目录生成，菜单手势与 Delete / F1 那几行也还在");
            return true;
        }

        /// <summary>绑定项在报错里的名字（工具条按钮没有 Text，只有 Name）。</summary>
        private static string BindingName(播放器.CommandBinding binding)
        {
            var text = binding.Item.Text;

            return string.IsNullOrWhiteSpace(text) ? binding.Item.Name ?? binding.Id.ToString() : text;
        }

        /// <summary>点完之后命令执行计数必须正好加一（不是"状态变了就算"）。</summary>
        private static bool ExpectCommand(
            播放器.MainForm form, PlayerCommandId id, int expected, string surface)
        {
            var actual = form.CommandExecutionCount(id);

            if (actual != expected)
            {
                Log(22, $"命令层：{surface}的「{id}」没有走统一命令（执行计数 {actual}，期望 {expected}）");
                return false;
            }

            if (form.LastCommand != id)
            {
                Log(22, $"命令层：{surface}点完之后最近一条命令是「{form.LastCommand}」，不是「{id}」");
                return false;
            }

            return true;
        }
    }
}
