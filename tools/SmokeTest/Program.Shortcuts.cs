// 检查点 36（第 31 个运行步骤）：快捷键自定义（1.29.0）。
//
//   ① 文本往返：Format ↔ TryParse（Space / Ctrl+← / PgUp / F11 / Ctrl+Shift+S / Ctrl+]），坏文本要认不出；
//   ② 覆盖层：Resolve / BuildEntries（只存差异、清空 = "Id="）/ Validate（撞车、保留键）；
//   ③ 真窗体：改了键之后新键执行、旧键不执行，菜单键串与工具栏提示跟着变，F1 说明跟着变；
//   ④ 对话框：列出全部命令、控件不越界、冲突挡在「确定」之前；
//   ⑤ 收尾复原：目录 Reset、设置写回去（不能让后面的步骤踩着改过的键跑）。

namespace SmokeTest
{
    internal static partial class Program
    {
        private static bool TestShortcutSettings()
        {
            var saved = AppSettings.Load().ShortcutOverrides;

            try
            {
                if (!CheckShortcutText()) return false;
                if (!CheckShortcutBindingLogic()) return false;
                if (!CheckShortcutForm()) return false;
                if (!CheckShortcutDialog()) return false;

                Log(36, "快捷键设置正常：键串往返对、只存差异、撞车与保留键都被挡住；"
                        + "改了键之后新键执行 / 旧键不执行，菜单键串、工具栏提示、F1 说明都跟着变；"
                        + "对话框列出全部命令、无控件越界");
                return true;
            }
            catch (Exception ex)
            {
                Log(36, "快捷键设置检查失败: " + ex);
                return false;
            }
            finally
            {
                PlayerCommandCatalog.Reset();

                var restore = AppSettings.Load();
                restore.ShortcutOverrides = saved;
                restore.Save();
            }
        }

        // ---- ① 文本往返 -------------------------------------------------------

        private static bool CheckShortcutText()
        {
            var cases = new (Keys Key, string Text)[]
            {
                (Keys.Space, "Space"),
                (Keys.Left, "←"),
                (Keys.Control | Keys.Left, "Ctrl+←"),
                (Keys.PageUp, "PgUp"),
                (Keys.F11, "F11"),
                (Keys.Control | Keys.Shift | Keys.S, "Ctrl+Shift+S"),
                (Keys.Control | Keys.Alt | Keys.M, "Ctrl+Alt+M"),
                (Keys.M, "M"),
                (Keys.D5, "5"),
                (Keys.Control | Keys.OemCloseBrackets, "Ctrl+]")
            };

            foreach (var (key, text) in cases)
            {
                if (ShortcutKeyText.Format(key) != text)
                {
                    Log(36, $"快捷键文本：{key} 画出来是「{ShortcutKeyText.Format(key)}」，期望「{text}」");
                    return false;
                }

                if (!ShortcutKeyText.TryParse(text, out var parsed) || parsed != key)
                {
                    Log(36, $"快捷键文本：「{text}」认回来是 {parsed}，期望 {key}");
                    return false;
                }
            }

            foreach (var bad in new[] { "", "   ", "Ctrl+", "Nope", "Ctrl+Nope", "Ctrl+Shift", "Ctrl+Alt" })
            {
                if (ShortcutKeyText.TryParse(bad, out var parsed))
                {
                    Log(36, $"快捷键文本：「{bad}」不该认得出来，却认成了 {parsed}");
                    return false;
                }
            }

            return true;
        }

        // ---- ② 覆盖层纯逻辑 ---------------------------------------------------

        private static bool CheckShortcutBindingLogic()
        {
            // 正常一条
            var resolved = ShortcutBinding.Resolve(
                new List<string> { "Stop=Ctrl+Shift+K" }, out var warnings);

            if (warnings.Count != 0)
            {
                Log(36, "快捷键覆盖：正常条目也报了说明——" + string.Join("；", warnings));
                return false;
            }

            if (!resolved.TryGetValue(PlayerCommandId.Stop, out var stopKeys) ||
                stopKeys.Length != 1 || stopKeys[0] != (Keys.Control | Keys.Shift | Keys.K))
            {
                Log(36, "快捷键覆盖：「Stop=Ctrl+Shift+K」没有解成一条 Stop 的 Ctrl+Shift+K");
                return false;
            }

            // 坏条目 / 坏键 / 清空
            var messy = ShortcutBinding.Resolve(
                new List<string> { "Nope=Ctrl+K", "Stop=Ctrl+Nope", "Stop=Ctrl+Shift+K", "Mute=" },
                out var messyWarnings);

            if (messyWarnings.Count != 2)
            {
                Log(36, $"快捷键覆盖：坏条目 / 坏键应该各报一句，现在报了 {messyWarnings.Count} 句"
                        + $"（{string.Join("；", messyWarnings)}）");
                return false;
            }

            if (messy.ContainsKey(PlayerCommandId.Mute) == false || messy[PlayerCommandId.Mute].Length != 0)
            {
                Log(36, "快捷键覆盖：「Mute=」没有解成「这条命令没有键」");
                return false;
            }

            if (messy.ContainsKey(PlayerCommandId.PlayPause))
            {
                Log(36, "快捷键覆盖：只写了坏键的命令被当成覆盖了（应该保留默认键）");
                return false;
            }

            // 只存差异
            var map = ShortcutBinding.EffectiveMap();
            map[PlayerCommandId.Stop] = new[] { Keys.Control | Keys.Shift | Keys.K };

            var entries = ShortcutBinding.BuildEntries(map);

            if (entries.Count != 1 || entries[0] != "Stop=Ctrl+Shift+K")
            {
                Log(36, "快捷键覆盖：改一条命令应该只写一条，实际写了 " + entries.Count
                        + $"（{string.Join(" / ", entries)}）");
                return false;
            }

            map[PlayerCommandId.Stop] = Array.Empty<Keys>();

            var cleared = ShortcutBinding.BuildEntries(map);

            if (cleared.Count != 1 || cleared[0] != "Stop=")
            {
                Log(36, "快捷键覆盖：清空一条命令应该写成「Stop=」，实际是「"
                        + string.Join(" / ", cleared) + "」");
                return false;
            }

            // 校验：撞车 + 保留键
            var duplicate = new Dictionary<PlayerCommandId, Keys[]>
            {
                [PlayerCommandId.Stop] = new[] { Keys.Control | Keys.K },
                [PlayerCommandId.Mute] = new[] { Keys.Control | Keys.K }
            };

            if (ShortcutBinding.Validate(duplicate).Count == 0)
            {
                Log(36, "快捷键校验：两条命令用同一个键却放过了");
                return false;
            }

            var reserved = new Dictionary<PlayerCommandId, Keys[]>
            {
                [PlayerCommandId.Stop] = new[] { Keys.F1 }
            };

            if (ShortcutBinding.Validate(reserved).Count == 0)
            {
                Log(36, "快捷键校验：把「停止」绑到 F1 却放过了");
                return false;
            }

            // 目录覆盖层：Configure / TryMap / MenuDisplay / Reset
            if (PlayerCommandCatalog.HasOverrides)
            {
                Log(36, "快捷键覆盖：还没配置，目录里就有覆盖了");
                return false;
            }

            PlayerCommandCatalog.Configure(new Dictionary<PlayerCommandId, Keys[]>
            {
                [PlayerCommandId.PlayPause] = new[] { Keys.Control | Keys.Shift | Keys.Space }
            });

            if (!PlayerCommandCatalog.HasOverrides)
            {
                Log(36, "快捷键覆盖：配置之后目录里没有覆盖");
                return false;
            }

            if (!PlayerCommandCatalog.TryMap(Keys.Control | Keys.Shift | Keys.Space, out var mapped) ||
                mapped != PlayerCommandId.PlayPause)
            {
                Log(36, "快捷键覆盖：新键没有映射到「播放 / 暂停」");
                return false;
            }

            if (PlayerCommandCatalog.TryMap(Keys.Space, out _))
            {
                Log(36, "快捷键覆盖：改了键之后旧键（Space）还在映射");
                return false;
            }

            if (PlayerCommandCatalog.Get(PlayerCommandId.PlayPause).MenuDisplay != "Ctrl+Shift+Space")
            {
                Log(36, "快捷键覆盖：菜单键串没换成 Ctrl+Shift+Space（现在是「"
                        + PlayerCommandCatalog.Get(PlayerCommandId.PlayPause).MenuDisplay + "」）");
                return false;
            }

            PlayerCommandCatalog.Reset();

            if (PlayerCommandCatalog.HasOverrides ||
                !PlayerCommandCatalog.TryMap(Keys.Space, out var restored) ||
                restored != PlayerCommandId.PlayPause)
            {
                Log(36, "快捷键覆盖：Reset 之后没有回到出厂的 Space");
                return false;
            }

            // 落盘往返：写进 settings.json 再读回来，条目一个不差
            var folder = Path.Combine(AppContext.BaseDirectory, "shortcut-roundtrip");
            var file = Path.Combine(folder, "settings.json");

            try
            {
                var written = new AppSettings { ShortcutOverrides = new List<string> { "Stop=Ctrl+Shift+K", "Mute=" } };
                written.SaveTo(file);

                var read = AppSettings.LoadFrom(file);

                if (read.ShortcutOverrides.Count != 2 ||
                    read.ShortcutOverrides[0] != "Stop=Ctrl+Shift+K" ||
                    read.ShortcutOverrides[1] != "Mute=")
                {
                    Log(36, "快捷键覆盖：写进设置的条目读回来不对（"
                            + string.Join(" / ", read.ShortcutOverrides) + "）");
                    return false;
                }
            }
            finally
            {
                TryDeleteDirectory(folder);
            }

            return true;
        }

        // ---- ③ 真窗体 ---------------------------------------------------------

        private static bool CheckShortcutForm()
        {
            var wav = Path.Combine(AppContext.BaseDirectory, "shortcut-settings.wav");

            try
            {
                WriteWav(wav, seconds: 4, frequency: 440);

                var setup = AppSettings.Load();
                setup.ShortcutOverrides = new List<string>
                {
                    "PlayPause=Ctrl+Shift+Space",
                    "Stop=Ctrl+Shift+S"
                };
                setup.Save();

                using var form = new 播放器.MainForm(Array.Empty<string>());
                form.Show();
                PumpMessages(200);

                form.AddPathsForTest(new[] { wav });
                PumpMessages(120);

                var binding = form.CommandBindings
                    .FirstOrDefault(item => item.Id == PlayerCommandId.PlayPause && item.Item is ToolStripMenuItem);

                if (binding?.Item is not ToolStripMenuItem menu)
                {
                    Log(36, "快捷键窗体：菜单里找不到「播放 / 暂停」");
                    return false;
                }

                if (menu.ShortcutKeyDisplayString != "Ctrl+Shift+Space")
                {
                    Log(36, "快捷键窗体：菜单上的键串还是「" + menu.ShortcutKeyDisplayString + "」");
                    return false;
                }

                var tooltip = form.CommandBindings
                    .FirstOrDefault(item => item.Id == PlayerCommandId.Stop && item.Item is ToolStripButton);

                if (tooltip == null || tooltip.Item.ToolTipText != "停止 (Ctrl+Shift+S)")
                {
                    Log(36, "快捷键窗体：工具栏提示没有跟着改（现在是「"
                            + (tooltip?.Item.ToolTipText ?? "（无）") + "」）");
                    return false;
                }

                var text = form.BuildShortcutsText();

                if (!text.Contains("Ctrl+Shift+Space", StringComparison.Ordinal))
                {
                    Log(36, "快捷键窗体：F1 说明里没有新的 Ctrl+Shift+Space");
                    return false;
                }

                // 新键真的执行到命令
                if (!form.ProcessShortcut(Keys.Control | Keys.Shift | Keys.Space))
                {
                    Log(36, "快捷键窗体：按新键没有反应");
                    return false;
                }

                if (form.LastCommand != PlayerCommandId.PlayPause)
                {
                    Log(36, $"快捷键窗体：按新键执行的是 {form.LastCommand}，期望播放 / 暂停");
                    return false;
                }

                // 旧键不再命中命令层（不会再把 LastCommand 换成播放 / 暂停）
                var before = form.LastCommand;
                form.ProcessShortcut(Keys.Space);

                if (form.LastCommand != before)
                {
                    Log(36, "快捷键窗体：旧键 Space 还在执行「播放 / 暂停」");
                    return false;
                }

                return true;
            }
            finally
            {
                TryDelete(wav);
            }
        }

        // ---- ④ 对话框 ---------------------------------------------------------

        private static bool CheckShortcutDialog()
        {
            using var dialog = ShortcutDialog.CreateForTest();

            dialog.StartPosition = FormStartPosition.Manual;
            dialog.Location = new Point(80, 80);
            dialog.Show();
            PumpMessages(150);

            if (dialog.RowCount != PlayerCommandCatalog.Defaults.Count)
            {
                Log(36, $"快捷键对话框：列出 {dialog.RowCount} 条，目录里是 {PlayerCommandCatalog.Defaults.Count} 条");
                return false;
            }

            var scale = dialog.DeviceDpi / 96f;

            foreach (Control control in dialog.Controls)
            {
                if (control.Right > dialog.ClientSize.Width || control.Bottom > dialog.ClientSize.Height)
                {
                    Log(36, $"快捷键对话框在 {scale:0.##} 倍缩放下有控件越界："
                            + $"{control.GetType().Name} {control.Bounds} 超出客户区 {dialog.ClientSize}");
                    return false;
                }
            }

            // 改一条、清一条、再撞起来：校验必须拦住
            dialog.SetKeysForTest(PlayerCommandId.Stop, Keys.Control | Keys.K);
            dialog.SetKeysForTest(PlayerCommandId.Mute, Keys.Control | Keys.K);

            if (dialog.ValidateForTest().Count == 0)
            {
                Log(36, "快捷键对话框：两条命令撞在 Ctrl+K 上，「确定」却不会被挡住");
                return false;
            }

            dialog.SetKeysForTest(PlayerCommandId.Mute, Keys.M);

            if (dialog.ValidateForTest().Count != 0)
            {
                Log(36, "快捷键对话框：冲突清掉之后还报错（" + string.Join("；", dialog.ValidateForTest()) + "）");
                return false;
            }

            if (!dialog.DisplayFor(PlayerCommandId.Stop).Contains("Ctrl+K", StringComparison.Ordinal))
            {
                Log(36, "快捷键对话框：改过的行没有显示新的 Ctrl+K（现在是「"
                        + dialog.DisplayFor(PlayerCommandId.Stop) + "」）");
                return false;
            }

            dialog.ResetAllForTest();

            if (dialog.DisplayFor(PlayerCommandId.Stop) != "S")
            {
                Log(36, "快捷键对话框：「全部恢复默认」之后「停止」不是 S（现在是「"
                        + dialog.DisplayFor(PlayerCommandId.Stop) + "」）");
                return false;
            }

            dialog.Close();
            return true;
        }
    }
}
