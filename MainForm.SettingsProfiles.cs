using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using 播放器.Core;
using 播放器.Ui;

namespace 播放器
{
    /// <summary>
    /// 主窗口的「设置方案」：把当前这一套设置命名存下来（<c>数据目录\设置方案\*.json</c>），
    /// 随时切过去。
    /// <para>
    /// 和「播放列表」那边的分工一样：<b>方案库</b>管自己常用的那几套（固定目录、有名字、
    /// 菜单里一行一个）；<b>文件</b>管外来的、外发的（导入 / 导出到任意位置）。
    /// 存的就是一份 settings.json，所以拷给别人就是拷一个文件。
    /// </para>
    /// <para>
    /// 三条关键约定：
    /// </para>
    /// <list type="number">
    /// <item><b>载入时可以只挑几组</b>：一份方案里什么都有，但用户常常只想拿其中一块
    /// （"把这套外观搬过来、音量别动"）。勾选框见 <see cref="SettingsLoadDialog"/>。</item>
    /// <item><b>有"当前方案"</b>：载入 / 另存为之后就是它，之后的改动跟着设置一起
    /// <b>自动写回方案文件</b>（退出时、以及切换方案前各写一次），不需要再点一次"保存方案"。</item>
    /// <item><b>没在用方案时一切照旧</b>：设置仍然只存 settings.json。
    /// 停用方案不会删掉方案文件，只是不再往它里面写。</item>
    /// </list>
    /// </summary>
    public partial class MainForm : Form
    {
        /// <summary>下拉菜单里最多直接列几份方案；再多就让用户去文件夹里看。</summary>
        private const int MaximumSettingsMenuEntries = 20;

        /// <summary>只读入口：文件菜单里的「设置方案」（设计器字段，给冒烟测试用）。</summary>
        internal ToolStripMenuItem MenuFileSettings => menuFileSettings;

        /// <summary>当前在用哪份方案（空 = 没有方案，只有 settings.json）。</summary>
        internal string CurrentSettingsProfileName => _settings.CurrentSettingsProfileName ?? string.Empty;

        // =====================================================================
        // 菜单
        // =====================================================================

        /// <summary>
        /// 建好「设置方案」子菜单：内容在展开的那一刻重建（方案文件可能被资源管理器改过）。
        /// </summary>
        private void ConfigureSettingsProfileMenu()
        {
            menuFileSettings.DropDownItems.Add(new ToolStripMenuItem("（点开显示方案）") { Enabled = false });

            menuFileSettings.DropDownOpening += (s, e) => RebuildSettingsProfileMenu(menuFileSettings.DropDownItems);
        }

        internal void RebuildSettingsProfileMenu(ToolStripItemCollection items)
        {
            ClearMenuItems(items);

            var current = CurrentSettingsProfileName;

            var save = new ToolStripMenuItem("保存当前设置为方案…")
            {
                ToolTipText = "把现在这套设置存成一份方案（数据目录的「设置方案」文件夹里），并设为当前方案"
            };
            save.Click += (s, e) => SaveCurrentSettingsAsProfile();
            items.Add(save);

            if (current.Length > 0)
            {
                var caption = MenuEscape(current);

                items.Add(new ToolStripMenuItem($"当前方案：「{caption}」（改动自动写回）") { Enabled = false });

                var rename = new ToolStripMenuItem($"重命名「{caption}」…");
                rename.Click += (s, e) => RenameCurrentSettingsProfile();
                items.Add(rename);

                var delete = new ToolStripMenuItem($"删除「{caption}」…");
                delete.Click += (s, e) => DeleteSettingsProfile(current, confirm: true);
                items.Add(delete);

                var deactivate = new ToolStripMenuItem("停用当前方案（只存 settings.json）")
                {
                    ToolTipText = "设置改回只写 settings.json；方案文件留着不动，以后还能再载入"
                };
                deactivate.Click += (s, e) => DeactivateSettingsProfile();
                items.Add(deactivate);
            }
            else
            {
                items.Add(new ToolStripMenuItem("（当前没有在用方案）") { Enabled = false });
            }

            items.Add(new ToolStripSeparator());

            var profiles = SettingsLibrary.List();

            if (profiles.Count == 0)
            {
                items.Add(new ToolStripMenuItem("（还没有保存过方案）") { Enabled = false });
            }
            else
            {
                foreach (var profile in profiles.Take(MaximumSettingsMenuEntries))
                {
                    var name = profile.Name;
                    var isCurrent = string.Equals(name, current, StringComparison.CurrentCultureIgnoreCase);

                    // 菜单里的 & 是加速键前缀，方案名里带着的话必须转义。
                    var item = new ToolStripMenuItem(MenuEscape(SettingsLibrary.Describe(profile)))
                    {
                        ToolTipText = profile.FilePath,
                        Checked = isCurrent
                    };

                    item.Click += (s, e) => LoadSettingsProfileDialog(name);
                    items.Add(item);
                }

                if (profiles.Count > MaximumSettingsMenuEntries)
                {
                    items.Add(new ToolStripMenuItem(
                        $"（还有 {profiles.Count - MaximumSettingsMenuEntries} 个，见「打开方案文件夹」）")
                    {
                        Enabled = false
                    });
                }
            }

            items.Add(new ToolStripSeparator());

            var import = new ToolStripMenuItem("从文件导入方案…")
            {
                ToolTipText = "把别处的设置文件收进方案库（原文件不动）"
            };
            import.Click += (s, e) => ImportSettingsProfileDialog();
            items.Add(import);

            var export = new ToolStripMenuItem("导出当前设置到文件…")
            {
                ToolTipText = "把当前这套设置写到你挑的位置（备份、拷给另一台机器）"
            };
            export.Click += (s, e) => ExportSettingsDialog();
            items.Add(export);

            var folder = new ToolStripMenuItem("打开方案文件夹");
            folder.Click += (s, e) => OpenSettingsProfileFolder();
            items.Add(folder);
        }

        // =====================================================================
        // 保存 / 重命名 / 删除 / 停用
        // =====================================================================

        /// <summary>把当前设置存成一份方案（同名时先问一句要不要覆盖）。</summary>
        private void SaveCurrentSettingsAsProfile()
        {
            var raw = InputDialog.Show(
                this,
                "保存为设置方案",
                "给这套设置起个名字吧。方案会存到数据目录的「设置方案」文件夹里，"
                + "以后在「文件 → 设置方案」里点一下就切过去；之后的改动会自动写回它。",
                SuggestSettingsProfileName());

            if (raw == null) return;

            if (!SettingsLibrary.TryNormalizeName(raw, out var name, out var error))
            {
                Warn("方案名不可用", error);
                return;
            }

            if (SettingsLibrary.Exists(name))
            {
                var answer = MessageBox.Show(
                    this,
                    $"已经有一个叫「{name}」的方案了，要覆盖它吗？\n\n"
                    + "（覆盖会把它原来的内容换成当前这套设置，不能撤销。）",
                    "覆盖方案",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                if (answer != DialogResult.Yes) return;
            }

            SaveCurrentSettingsToProfile(name, "已保存方案");
        }

        /// <summary>
        /// 把当前设置写进指定方案，并把它设为当前方案（不做任何询问）。
        /// <para>对话框那步已经确认过覆盖了，这里只负责写、记状态、报错。</para>
        /// </summary>
        internal bool SaveCurrentSettingsToProfile(string name, string action)
        {
            try
            {
                var saved = SettingsLibrary.Save(name, _settings);

                _settings.CurrentSettingsProfileName = saved.Name;
                SetStatus($"{action}「{saved.Name}」：{SettingsProfile.All.Count} 组设置");

                return true;
            }
            catch (Exception ex)
            {
                ShowError("保存设置方案失败", ex);
                return false;
            }
        }

        /// <summary>
        /// 把当前设置写回方案文件（没有当前方案时什么也不做）。
        /// <para>
        /// 写失败只记日志、不弹窗：这条路径会在退出时走一遍，
        /// 那时候弹一个"保存方案失败"除了添乱没有别的用；磁盘上的 settings.json
        /// 那边已经有自己的错误提示了。
        /// </para>
        /// </summary>
        internal bool SaveCurrentSettingsProfileQuietly()
        {
            var name = CurrentSettingsProfileName;
            if (name.Length == 0) return false;

            try
            {
                SettingsLibrary.Save(name, _settings);
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Swallowed("写回设置方案失败（退出时还会再试一次）。", ex);
                return false;
            }
        }

        private void RenameCurrentSettingsProfile()
        {
            var old = CurrentSettingsProfileName;
            if (old.Length == 0) return;

            var raw = InputDialog.Show(this, "重命名方案", "新的名字：", old);
            if (raw == null) return;

            RenameCurrentSettingsProfile(raw);
        }

        /// <summary>
        /// 把当前方案改名（<b>不弹输入框</b>）；菜单走上面那个。
        /// <para>改的正好是当前方案时，关联跟着一起走（设置本身不动）。</para>
        /// </summary>
        internal bool RenameCurrentSettingsProfile(string newName)
        {
            var old = CurrentSettingsProfileName;
            if (old.Length == 0) return false;

            if (!SettingsLibrary.Rename(old, newName, out var actual, out var error))
            {
                Warn("重命名方案失败", error);
                return false;
            }

            _settings.CurrentSettingsProfileName = actual;

            SetStatus($"方案「{old}」已改名为「{actual}」");
            return true;
        }

        /// <summary>
        /// 删掉一份方案。<b>只删这个文件</b>：settings.json、播放历史、字体……一个都不动。
        /// 删的正是当前方案时，当前的设置保留，只是不再对应方案。
        /// </summary>
        internal bool DeleteSettingsProfile(string name, bool confirm)
        {
            if (confirm)
            {
                var answer = MessageBox.Show(
                    this,
                    $"要删除方案「{name}」吗？\n\n"
                    + "（只删这一个方案文件；当前这套设置和 settings.json 都不会动。）",
                    "删除方案",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                if (answer != DialogResult.Yes) return false;
            }

            if (!SettingsLibrary.Delete(name, out var error))
            {
                Warn("删除方案失败", error);
                return false;
            }

            if (string.Equals(name, CurrentSettingsProfileName, StringComparison.CurrentCultureIgnoreCase))
            {
                _settings.CurrentSettingsProfileName = string.Empty;
                SetStatus($"已删除方案「{name}」；当前设置还在，只是不再对应方案");
            }
            else
            {
                SetStatus($"已删除方案「{name}」");
            }

            return true;
        }

        /// <summary>停用当前方案：设置改回只存 settings.json，方案文件留着（写回最后一次改动）。</summary>
        internal void DeactivateSettingsProfile()
        {
            var name = CurrentSettingsProfileName;
            if (name.Length == 0) return;

            SaveCurrentSettingsProfileQuietly();
            _settings.CurrentSettingsProfileName = string.Empty;

            SetStatus($"已停用方案「{name}」：之后的改动只存 settings.json，方案文件留着不动");
        }

        // =====================================================================
        // 载入
        // =====================================================================

        /// <summary>点菜单里的某份方案：先问"要拿哪几组"，再应用。</summary>
        private void LoadSettingsProfileDialog(string name)
        {
            if (!SettingsLibrary.TryLoad(name, out var incoming, out var error))
            {
                Warn("载入方案失败", error);
                return;
            }

            var sections = SettingsLoadDialog.Show(this, _palette, _settings, incoming, name);
            if (sections == null) return;

            ApplySettingsProfile(name, incoming, sections);
        }

        /// <summary>
        /// 载入方案里选中的那几组（<b>不弹窗口</b>）：菜单走
        /// <see cref="LoadSettingsProfileDialog"/>，自动化测试直接调这里。
        /// </summary>
        internal bool LoadSettingsProfile(string name, IReadOnlyList<SettingsSection> sections)
        {
            if (!SettingsLibrary.TryLoad(name, out var incoming, out var error))
            {
                Warn("载入方案失败", error);
                return false;
            }

            return ApplySettingsProfile(name, incoming, sections);
        }

        private bool ApplySettingsProfile(
            string name, AppSettings incoming, IReadOnlyList<SettingsSection> sections)
        {
            // 先把现在这套写回**旧**方案：换过去之后再写就写到新方案上了。
            //
            // 注意：这次载入要是正好就是"当前方案"，上面这次写回会把当前值存进那份文件。
            // 这是"当前方案 = 你现在这一套设置"的必然结果——只勾了一部分分组时，
            // 没勾的那些保留当前值，于是那份文件也被更新成合并后的样子。
            // 换句话说：方案文件永远等于"现在这一套"，不是"上次保存时的快照"。
            SaveCurrentSettingsProfileQuietly();

            var merged = SettingsProfile.Merge(_settings, incoming, sections);

            ApplyLoadedSettings(merged);

            // "当前方案"记的是"我现在用的是哪一份"，所以跟着这次载入走，
            // 不由方案文件里存着的旧值决定（那是它被存下来时的情况）。
            _settings.CurrentSettingsProfileName = name;
            SaveCurrentSettingsProfileQuietly();

            var description = DescribeSections(sections);

            SetStatus($"已载入方案「{name}」：{description}");
            AppLog.Info($"载入设置方案：{name}（{description}）");
            return true;
        }

        private static string DescribeSections(IReadOnlyList<SettingsSection> sections)
        {
            if (sections == null || sections.Count == 0) return "没有选中任何一组";

            if (sections.Count >= SettingsProfile.All.Count) return $"全部 {sections.Count} 组设置";

            return string.Join("、", sections.Select(SettingsProfile.Name));
        }

        /// <summary>
        /// 把一份合并好的设置装到界面上。
        /// <para>
        /// 顺序有讲究：<b>主题先</b>（它决定配色与工具栏图标），再让
        /// <see cref="ApplySettings"/> 把各项摆到界面上，<b>最后</b>才处理尺寸相关的——
        /// 窗口位置要等布局定下来才摆得准（和启动时 OnLoad 里做的事一样）。
        /// </para>
        /// </summary>
        private void ApplyLoadedSettings(AppSettings merged)
        {
            // 整份换掉而不是逐个字段赋值：设置对象只有主窗口持有（别的类都只读它的字段值），
            // 换掉之后所有读取点自然拿到新值，也就不会漏掉某一项。
            _settings = merged;

            ApplyTheme(_settings.Theme);

            // 现在可能正放着一首歌，不需要因为换了一套设置就把侧栏清成空状态
            ApplySettings(resetTrackPanel: false);

            RestoreWindowPlacement();
            ClampToWorkingArea();
            ApplyFloatingWindowsFromSettings();
            ApplyDesktopLyricsFromSettings();
        }

        /// <summary>按设置把"分离出去的窗口"摆成该有的样子（该飘的飘、该归位的归位）。</summary>
        private void ApplyFloatingWindowsFromSettings()
        {
            // 两个方向都要处理：方案里可能是"分离"，也可能是"归位"
            if ((_sidebarWindow != null) != _settings.SidebarFloating) SetSidebarFloating(_settings.SidebarFloating);

            if ((_playlistWindow != null) != _settings.PlaylistFloating) SetPlaylistFloating(_settings.PlaylistFloating);
        }

        /// <summary>按设置把桌面歌词调成该有的样子（开 / 关、样式、位置）。</summary>
        private void ApplyDesktopLyricsFromSettings()
        {
            NormalizeDesktopLyricsSettings();
            SyncDesktopLyricsChecks();
            ApplyDesktopLyricsOptions();

            if (!_settings.DesktopLyricsEnabled)
            {
                if (_desktopLyrics != null) SetDesktopLyricsEnabled(false, announce: false);
                return;
            }

            if (_desktopLyrics == null) SetDesktopLyricsEnabled(true, announce: false);

            // 已经开着的窗口要当场挪过去（刚创建的那种，SetDesktopLyricsEnabled 已经按设置摆好了）
            if (_desktopLyrics != null && _settings.DesktopLyricsPlaced)
                _desktopLyrics.MoveTo(new Point(_settings.DesktopLyricsX, _settings.DesktopLyricsY));
        }

        // =====================================================================
        // 导入 / 导出 / 文件夹
        // =====================================================================

        private void ImportSettingsProfileDialog()
        {
            using var dialog = new OpenFileDialog
            {
                Title = "导入设置方案",
                Filter = "设置文件 (*.json)|*.json|所有文件|*.*",
                InitialDirectory = SettingsLibrary.Directory
            };

            if (dialog.ShowDialog(this) != DialogResult.OK) return;

            // 同名先问一句：导入是"把它收进方案库"，覆盖的也是库里的那一份
            var raw = Path.GetFileNameWithoutExtension(dialog.FileName);
            var exists = SettingsLibrary.TryNormalizeName(raw, out var clean, out _) && SettingsLibrary.Exists(clean);

            if (exists)
            {
                var answer = MessageBox.Show(
                    this,
                    $"已经有一个叫「{clean}」的方案了，要用这个文件覆盖它吗？\n\n"
                    + "（覆盖不能撤销；你选的那个文件本身不会被改动。）",
                    "导入方案",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                if (answer != DialogResult.Yes) return;
            }

            if (!SettingsLibrary.Import(dialog.FileName, exists, out var name, out var error))
            {
                Warn("导入方案失败", error);
                return;
            }

            SetStatus($"已导入方案「{name}」：" + SettingsLibrary.ResolvePath(name));
        }

        private void ExportSettingsDialog()
        {
            using var dialog = new SaveFileDialog
            {
                Title = "导出当前设置",
                Filter = "设置文件 (*.json)|*.json|所有文件|*.*",
                DefaultExt = "json",
                FileName = (CurrentSettingsProfileName.Length > 0 ? CurrentSettingsProfileName : "播放器设置") + ".json",
                InitialDirectory = Directory.Exists(_settings.LastDirectory) ? _settings.LastDirectory : string.Empty
            };

            if (dialog.ShowDialog(this) != DialogResult.OK) return;

            ExportSettingsToFile(dialog.FileName);
        }

        /// <summary>把当前设置写到一个文件（<b>不弹对话框</b>；菜单走 <see cref="ExportSettingsDialog"/>）。</summary>
        internal bool ExportSettingsToFile(string path)
        {
            if (!SettingsLibrary.ExportTo(_settings, path, out var error))
            {
                Warn("导出设置失败", error);
                return false;
            }

            SetStatus("设置已导出：" + path);
            return true;
        }

        private void OpenSettingsProfileFolder()
        {
            try
            {
                var folder = SettingsLibrary.EnsureDirectory();

                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
                SetStatus("设置方案文件夹：" + folder);
            }
            catch (Exception ex)
            {
                ShowError("打开方案文件夹失败", ex);
            }
        }

        /// <summary>新方案的默认名：我的设置 / 我的设置 2 / …（避开已经用掉的名字）。</summary>
        private static string SuggestSettingsProfileName()
        {
            const string stem = "我的设置";

            var candidate = stem;

            for (var suffix = 2; suffix <= 99 && SettingsLibrary.Exists(candidate); suffix++)
                candidate = $"{stem} {suffix}";

            return candidate;
        }
    }
}
