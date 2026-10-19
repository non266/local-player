// 设置方案 —— 主窗体上的联动（当前方案、改动自动写回、只勾一组、改名停用删除）。
//
// 从 Program.SettingsProfiles.cs 按 ARCHITECTURE 17.2 的规矩整块搬过来：成员一个字没动，
// 字段一条没搬（这个文件里没有字段）。

namespace SmokeTest
{
    internal static partial class Program
    {
        /// <summary>主窗体上的联动：当前方案、改动自动写回、只勾一组、改名停用删除。</summary>
        private static bool CheckSettingsProfilesInMainForm()
        {
            // 方案里存着的音量（关窗那一刻的值），第二个窗体拿它验"只勾一组"
            var profileVolume = 0;

            using (var form = new 播放器.MainForm(Array.Empty<string>()))
            {
                form.Show();
                PumpMessages(250);

                var fileMenu = form.MenuFile;
                var settingsMenu = form.MenuFileSettings;

                if (fileMenu == null || settingsMenu == null)
                {
                    Log(20, "设置方案检查：文件菜单里没有「设置方案」");
                    return false;
                }

                var topLevel = fileMenu.DropDownItems.OfType<ToolStripMenuItem>()
                    .Select(i => i.Text).ToList();

                if (!topLevel.Any(t => t.Contains("设置方案(&G)", StringComparison.Ordinal)))
                {
                    Log(20, $"设置方案检查：文件菜单里没有「设置方案(&G)」（{string.Join(" / ", topLevel)}）");
                    return false;
                }

                form.RebuildSettingsProfileMenu(settingsMenu.DropDownItems);

                var texts = settingsMenu.DropDownItems.OfType<ToolStripMenuItem>()
                    .Select(i => i.Text).ToList();

                if (!texts.Any(t => t.Contains("保存当前设置为方案", StringComparison.Ordinal)) ||
                    !texts.Any(t => t.Contains("从文件导入方案", StringComparison.Ordinal)) ||
                    !texts.Any(t => t.Contains("导出当前设置到文件", StringComparison.Ordinal)) ||
                    !texts.Any(t => t.Contains("打开方案文件夹", StringComparison.Ordinal)))
                {
                    Log(20, $"设置方案检查：菜单里的几项不全（{string.Join(" / ", texts)}）");
                    return false;
                }

                // ---- 存一份 → 成为当前方案 → 菜单里标出来 ----
                if (!form.SaveCurrentSettingsToProfile("窗体方案", "已保存方案"))
                {
                    Log(20, "设置方案检查：保存当前设置为方案失败");
                    return false;
                }

                if (form.CurrentSettingsProfileName != "窗体方案" || !SettingsLibrary.Exists("窗体方案"))
                {
                    Log(20, $"设置方案检查：保存之后没有成为当前方案（「{form.CurrentSettingsProfileName}」）");
                    return false;
                }

                form.RebuildSettingsProfileMenu(settingsMenu.DropDownItems);

                var entries = settingsMenu.DropDownItems.OfType<ToolStripMenuItem>().ToList();
                var row = entries.FirstOrDefault(i => i.Text.StartsWith("窗体方案（", StringComparison.Ordinal));

                if (row == null || !row.Checked)
                {
                    Log(20, $"设置方案检查：菜单里没有把当前方案标出来（{string.Join(" / ", entries.Select(i => i.Text))}）");
                    return false;
                }

                if (!entries.Any(i => i.Text.Contains("当前方案：「窗体方案」", StringComparison.Ordinal)) ||
                    !entries.Any(i => i.Text.Contains("停用当前方案", StringComparison.Ordinal)))
                {
                    Log(20, $"设置方案检查：当前方案那几项不全（{string.Join(" / ", entries.Select(i => i.Text))}）");
                    return false;
                }

                // ---- 改动要自动写回：设置是退出时落盘的，方案文件跟着一起写 ----
                // 音量走界面那条路（关窗时以滑块为准，直接改设置对象会被它盖掉），
                // 主题不看界面，直接改设置对象。
                var savedVolume = SettingsOf(form).Volume;

                if (!SendShortcut(form, Keys.Up) || SettingsOf(form).Volume == savedVolume)
                {
                    Log(20, "设置方案检查：按音量加键没有改到设置里的音量");
                    return false;
                }

                var changedVolume = SettingsOf(form).Volume;

                SettingsOf(form).Theme = ThemeId.Vlc;
                PumpMessages(150);

                form.Close();
                PumpMessages(250);

                if (!SettingsLibrary.TryLoad("窗体方案", out var writtenBack, out var writeError))
                {
                    Log(20, "设置方案检查：关掉之后读不回方案文件：" + writeError);
                    return false;
                }

                if (writtenBack.Volume != changedVolume || writtenBack.Theme != ThemeId.Vlc)
                {
                    Log(20, "设置方案检查：改动没有自动写回方案文件（音量 "
                            + $"{writtenBack.Volume}，期望 {changedVolume}；主题 {writtenBack.Theme}，期望 {ThemeId.Vlc}）");
                    return false;
                }

                profileVolume = writtenBack.Volume;
            }

            using (var second = new 播放器.MainForm(Array.Empty<string>()))
            {
                second.Show();
                PumpMessages(300);

                // ---- 整份载入：方案里的值都该跟着来（先做这一条，趁方案文件还是刚存下的样子）----
                SettingsOf(second).Volume = 3;
                SettingsOf(second).Theme = ThemeId.Light;
                PumpMessages(150);

                if (!second.LoadSettingsProfile("窗体方案", SettingsProfile.All))
                {
                    Log(20, "设置方案检查：整份载入失败");
                    return false;
                }

                PumpMessages(200);

                if (SettingsOf(second).Volume != profileVolume || SettingsOf(second).Theme != ThemeId.Vlc)
                {
                    Log(20, "设置方案检查：整份载入没有把方案里的值带过来（音量 "
                            + $"{SettingsOf(second).Volume}，期望 {profileVolume}；"
                            + $"主题 {SettingsOf(second).Theme}，期望 {ThemeId.Vlc}）");
                    return false;
                }

                // ---- 只勾一组：没勾的那些原样不动 ----
                SettingsOf(second).Volume = 4;
                SettingsOf(second).Theme = ThemeId.Oled;
                PumpMessages(150);

                if (!second.LoadSettingsProfile("窗体方案", new[] { SettingsSection.Playback }))
                {
                    Log(20, "设置方案检查：只载入「播放」那一组失败");
                    return false;
                }

                PumpMessages(200);

                if (SettingsOf(second).Volume != profileVolume)
                {
                    Log(20, "设置方案检查：勾了「播放」却没把音量带过来（"
                            + $"{SettingsOf(second).Volume}，期望方案里的 {profileVolume}）");
                    return false;
                }

                if (SettingsOf(second).Theme != ThemeId.Oled)
                {
                    Log(20, $"设置方案检查：只勾了「播放」，主题却被一起改了（{SettingsOf(second).Theme}）");
                    return false;
                }

                if (second.CurrentSettingsProfileName != "窗体方案")
                {
                    Log(20, "设置方案检查：载入之后没有把「当前方案」接上");
                    return false;
                }

                // ---- 改名：当前方案的关联跟着走 ----
                if (!second.RenameCurrentSettingsProfile("窗体方案改") ||
                    second.CurrentSettingsProfileName != "窗体方案改")
                {
                    Log(20, $"设置方案检查：重命名之后关联没跟着走（「{second.CurrentSettingsProfileName}」）");
                    return false;
                }

                // ---- 停用：设置留着，方案文件也留着 ----
                second.DeactivateSettingsProfile();

                if (second.CurrentSettingsProfileName.Length != 0)
                {
                    Log(20, "设置方案检查：停用之后还挂着当前方案");
                    return false;
                }

                if (!SettingsLibrary.Exists("窗体方案改"))
                {
                    Log(20, "设置方案检查：停用方案把方案文件删掉了（不该动它）");
                    return false;
                }

                // ---- 删除当前方案：设置还在，只是不再对应方案 ----
                if (!second.LoadSettingsProfile("窗体方案改", SettingsProfile.All))
                {
                    Log(20, "设置方案检查：重新载入方案失败");
                    return false;
                }

                if (!second.DeleteSettingsProfile("窗体方案改", confirm: false))
                {
                    Log(20, "设置方案检查：删除当前方案失败");
                    return false;
                }

                if (second.CurrentSettingsProfileName.Length != 0 || SettingsLibrary.Exists("窗体方案改"))
                {
                    Log(20, "设置方案检查：删掉当前方案之后关联还挂着");
                    return false;
                }

                // ---- 导出（不弹对话框那条路）----
                var exported = Path.Combine(AppContext.BaseDirectory, "smoke-profiles", "导出设置.json");

                if (!second.ExportSettingsToFile(exported) || !File.Exists(exported))
                {
                    Log(20, "设置方案检查：导出当前设置失败");
                    return false;
                }

                if (AppSettings.LoadFrom(exported).Volume != SettingsOf(second).Volume)
                {
                    Log(20, "设置方案检查：导出的内容和当前设置对不上");
                    return false;
                }

                second.Close();
                PumpMessages(150);
            }

            Log(20, "设置方案与主窗体联动正常：文件菜单里有「设置方案」，保存之后成为当前方案并在菜单里标出来，"
                    + "改动随设置一起自动写回方案文件（关窗之后文件里就是新值），"
                    + "载入时可以只勾几组（没勾的保持原样），改名 / 停用 / 删除之后关联都跟着走、"
                    + "方案文件该留的留着");
            return true;
        }
    }
}
