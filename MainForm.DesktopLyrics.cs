using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using 播放器.Core;
using 播放器.Ui;

namespace 播放器
{
    /// <summary>
    /// 桌面歌词：把当前这一句歌词显示成一个置顶的小窗口，盖在别的程序之上。
    /// <para>
    /// 和「侧栏 / 播放列表」的悬浮窗不同，它<b>不设 Owner</b>，全屏时也不收起来——
    /// 它的用处恰恰是"主界面藏起来了也能看歌词"：
    /// 设了 Owner 之后主窗口一最小化（或缩进托盘）就会把它一起带走。
    /// </para>
    /// <para>
    /// 这里只管三件事：什么时候开窗、把设置推给窗口、把窗口的位置存下来。
    /// 窗口自己的外观逻辑都在 <see cref="DesktopLyricsWindow"/> 里。
    /// </para>
    /// </summary>
    public partial class MainForm
    {
        /// <summary>
        /// 菜单项用 Tag 表明身份。
        /// <para>
        /// 「视图」菜单里的子菜单和窗口右键菜单是两套独立的菜单对象
        /// （一个 <see cref="ToolStripItem"/> 只能属于一个父级），
        /// 用 Tag 认领之后，同一个刷新逻辑就能把两边一起对齐。
        /// </para>
        /// </summary>
        private const string DesktopLyricsNextTag = "desktop-lyrics-next";

        private const string DesktopLyricsTranslationTag = "desktop-lyrics-translation";

        private const string DesktopLyricsControlsTag = "desktop-lyrics-controls";

        private const string DesktopLyricsLockTag = "desktop-lyrics-lock";

        /// <summary>「外观…」那一项：文字里带着当前字号与浓度，每次展开菜单时刷新。</summary>
        private const string DesktopLyricsAppearanceTag = "desktop-lyrics-appearance";

        private ToolStripMenuItem? _menuDesktopLyrics;
        private ToolStripMenuItem? _menuDesktopLyricsEnabled;
        private ToolStripMenuItem? _trayDesktopLyrics;

        /// <summary>托盘子菜单里的"启用桌面歌词"（和视图菜单里那一个是两个对象）。</summary>
        private ToolStripMenuItem? _trayDesktopLyricsEnabled;
        private DesktopLyricsWindow? _desktopLyrics;
        private ContextMenuStrip? _desktopLyricsMenu;

        // ---- 给冒烟测试的只读入口（InternalsVisibleTo("SmokeTest")）----
        internal DesktopLyricsWindow? DesktopLyricsWindow => _desktopLyrics;

        internal ToolStripMenuItem? MenuDesktopLyrics => _menuDesktopLyrics;

        internal ToolStripMenuItem? TrayDesktopLyrics => _trayDesktopLyrics;

        /// <summary>
        /// 桌面歌词那一套选项：视图菜单 / 桌面歌词右键 / 托盘各一份（三个独立对象），
        /// 一起刷——以前是手写一段"把这三四个宿主都刷一遍"，漏一处就是某个入口里勾不对。
        /// </summary>
        private MenuSection? _desktopLyricsSection;

        /// <summary>建出来的「字体」子菜单（视图菜单里一个、右键菜单里一个）；导入之后要清掉重建。</summary>
        private readonly List<ToolStripMenuItem> _lyricsFontMenus = new List<ToolStripMenuItem>();

        // =====================================================================
        // 菜单
        // =====================================================================

        private void ConfigureDesktopLyricsMenu()
        {
            _desktopLyricsSection = new MenuSection(RefreshDesktopLyricsItem);

            _menuDesktopLyrics = new ToolStripMenuItem("桌面歌词(&D)");

            _menuDesktopLyricsEnabled = new ToolStripMenuItem("启用桌面歌词")
            {
                CheckOnClick = true,
                ShortcutKeyDisplayString = "Ctrl+D"
            };
            _menuDesktopLyricsEnabled.Click += (s, e) => SetDesktopLyricsEnabled(_menuDesktopLyricsEnabled.Checked);

            _menuDesktopLyrics.DropDownItems.Add(_menuDesktopLyricsEnabled);
            _menuDesktopLyrics.DropDownItems.Add(new ToolStripSeparator());
            BuildDesktopLyricsOptions(_menuDesktopLyrics.DropDownItems, includeClose: false);
            _desktopLyricsSection.Attach(_menuDesktopLyrics);

            InsertDesktopLyricsMenu();

            // 窗口上右键弹出的那一套（多一个"关闭桌面歌词"，末尾再挂上歌词偏移）
            _desktopLyricsMenu = new ContextMenuStrip();

            BuildDesktopLyricsOptions(_desktopLyricsMenu.Items, includeClose: true);
            _desktopLyricsSection.Attach(_desktopLyricsMenu);

            // 桌面歌词是最容易看出"歌词对不上"的地方（它一直摆在眼前），
            // 所以偏移也挂在这儿
            _desktopLyricsMenu.Items.Add(new ToolStripSeparator());
            BuildLyricsOffsetItems(_desktopLyricsMenu.Items);
        }

        /// <summary>插在「侧栏分离为悬浮窗」之后：和"显示 / 分离"那一组放在一起。</summary>
        private void InsertDesktopLyricsMenu()
        {
            if (_menuDesktopLyrics == null) return;

            var anchor = _menuViewSidebarFloating;
            var index = anchor == null ? -1 : menuView.DropDownItems.IndexOf(anchor);

            if (index < 0) menuView.DropDownItems.Add(_menuDesktopLyrics);
            else menuView.DropDownItems.Insert(index + 1, _menuDesktopLyrics);
        }

        /// <summary>
        /// 生成一组桌面歌词选项。
        /// <para>「视图」菜单的子菜单与窗口右键菜单共用这份定义，两边的行为不会走偏。</para>
        /// </summary>
        private void BuildDesktopLyricsOptions(ToolStripItemCollection target, bool includeClose)
        {
            target.Add(CreateDesktopLyricsOption(
                "显示下一句", DesktopLyricsNextTag,
                () => SetDesktopLyricsShowNext(!_settings.DesktopLyricsShowNext)));

            // 双语歌词（同一时间戳的原文 + 译文）：第二行优先放译文，那才是"这一句"的另一半
            target.Add(CreateDesktopLyricsOption(
                "有翻译时第二行显示翻译", DesktopLyricsTranslationTag,
                () => SetDesktopLyricsShowTranslation(!_settings.DesktopLyricsShowTranslation)));

            // 控制条：播放 / 暂停、上一首、下一首、停止。锁定（鼠标穿透）时它不显示。
            target.Add(CreateDesktopLyricsOption(
                "显示控制条（播放 / 上一首 / 下一首 / 停止）", DesktopLyricsControlsTag,
                () => SetDesktopLyricsShowControls(!_settings.DesktopLyricsShowControls)));

            var lockItem = CreateDesktopLyricsOption(
                "锁定（鼠标穿透）", DesktopLyricsLockTag, ToggleDesktopLyricsLock);

            // 把热键写在菜单上：锁定之后这个窗口就点不到了，用户必须提前知道出口在哪
            lockItem.ShortcutKeyDisplayString = AppHotKeys.ToggleDesktopLyricsLockText;
            target.Add(lockItem);

            target.Add(new ToolStripSeparator());

            // 当前值直接写在菜单项上：以前这几项只有"放大 / 降低"，
            // 得点一下再从状态栏看结果才知道现在是多少。
            target.Add(CreateDesktopLyricsOption(AppearanceMenuText(), DesktopLyricsAppearanceTag, EditDesktopLyricsAppearance));
            target.Add(CreateDesktopLyricsOption("放大字号", null, () => StepDesktopLyricsFontSize(1)));
            target.Add(CreateDesktopLyricsOption("缩小字号", null, () => StepDesktopLyricsFontSize(-1)));
            target.Add(CreateDesktopLyricsOption("提高不透明度", null, () => StepDesktopLyricsOpacity(1)));
            target.Add(CreateDesktopLyricsOption("降低不透明度", null, () => StepDesktopLyricsOpacity(-1)));
            target.Add(BuildLyricsFontMenu());
            target.Add(BuildDesktopLyricsColorMenu());

            target.Add(new ToolStripSeparator());
            target.Add(CreateDesktopLyricsOption("居中显示", null, CenterDesktopLyrics));

            if (includeClose)
                target.Add(CreateDesktopLyricsOption("关闭桌面歌词", null, () => SetDesktopLyricsEnabled(false)));
        }

        private static ToolStripMenuItem CreateDesktopLyricsOption(string text, string? tag, Action action)
        {
            var item = new ToolStripMenuItem(text) { Tag = tag };
            item.Click += (s, e) => action();
            return item;
        }

        /// <summary>
        /// 「字体」子菜单。
        /// <para>
        /// 系统上可能装着几百个字体族，枚举一次要上百毫秒，所以列表是<b>第一次展开时</b>才建的，
        /// 建好之后一直复用（勾选状态每次展开刷新）。
        /// </para>
        /// </summary>
        private ToolStripMenuItem BuildLyricsFontMenu()
        {
            var menu = new ToolStripMenuItem("字体");

            _lyricsFontMenus.Add(menu);
            menu.DropDownOpening += (s, e) => EnsureLyricsFontItems(menu);

            return menu;
        }

        internal void EnsureLyricsFontItems(ToolStripMenuItem menu)
        {
            if (menu.DropDownItems.Count == 0) RebuildLyricsFontItems(menu);

            foreach (ToolStripItem item in menu.DropDownItems)
            {
                if (item is ToolStripMenuItem { Tag: string tag } fontItem)
                    fontItem.Checked = string.Equals(tag, _settings.LyricsFontFamily, StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// 铺一遍字体列表。
        /// <para>
        /// <b>只列自己导入的字体</b>，不去枚举系统上装的那几百个：那个列表又长又难找，
        /// 而且桌面歌词要的是"能盖在别的画面上还看得清"的字体，用户带进来的才是他挑过的。
        /// 所以这里是：默认字体 → 导入 → 已导入的字体。
        /// </para>
        /// </summary>
        private void RebuildLyricsFontItems(ToolStripMenuItem menu)
        {
            ClearMenuItems(menu.DropDownItems);

            menu.DropDownItems.Add(CreateLyricsFontItem("默认字体", string.Empty));

            var import = new ToolStripMenuItem("导入字体文件…");
            import.Click += (s, e) => ImportLyricsFonts();
            menu.DropDownItems.Add(import);

            // 删除入口就放在导入的旁边：导入是"只进不出"的话，导错一个就永久多一行
            var manage = new ToolStripMenuItem("管理已导入的字体…");
            manage.Click += (s, e) => ManageLyricsFonts();
            menu.DropDownItems.Add(manage);

            var imported = UiFonts.ImportedFamilies;

            if (imported.Count == 0)
            {
                menu.DropDownItems.Add(new ToolStripSeparator());
                menu.DropDownItems.Add(new ToolStripMenuItem("（还没有导入过字体）") { Enabled = false });
                return;
            }

            menu.DropDownItems.Add(new ToolStripSeparator());

            foreach (var family in imported)
                menu.DropDownItems.Add(CreateLyricsFontItem(family, family));
        }

        private ToolStripMenuItem CreateLyricsFontItem(string text, string family)
        {
            var item = new ToolStripMenuItem(text) { Tag = family };
            item.Click += (s, e) => SetLyricsFont(family);
            return item;
        }

        /// <summary>选字体文件：可以一次选好几个。</summary>
        private void ImportLyricsFonts() => ImportLyricsFonts(this);

        private void ImportLyricsFonts(IWin32Window owner)
        {
            using var dialog = new OpenFileDialog
            {
                Title = "导入字体文件",
                Filter = "字体文件 (*.ttf;*.otf;*.ttc;*.otc)|*.ttf;*.otf;*.ttc;*.otc|所有文件 (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = true
            };

            if (dialog.ShowDialog(owner) != DialogResult.OK) return;

            ImportLyricsFontFiles(dialog.FileNames);
        }

        /// <summary>
        /// 真正导入的那一份：菜单、字体管理对话框、往歌词窗口里拖字体，全都走这里。
        /// <para>
        /// 多个文件里只要有一个成功就继续，失败的逐条列出来——
        /// 用户一次拖进来十几个字体，不该因为其中一个坏了就整批不算。
        /// </para>
        /// </summary>
        private void ImportLyricsFontFiles(IReadOnlyList<string> paths, string? note = null)
        {
            if (paths.Count == 0) return;

            var families = new List<string>();
            var failures = new List<string>();
            var succeeded = 0;

            foreach (var path in paths)
            {
                if (UiFonts.TryImportFontFile(path, out var added, out var error))
                {
                    families.AddRange(added);
                    succeeded++;
                }
                else
                {
                    failures.Add($"{Path.GetFileName(path)}：{error}");
                }
            }

            // 列表里要出现新导入的字体
            RefreshLyricsFontMenus();

            if (succeeded == 0)
            {
                SetStatus("导入失败：" + (failures.Count > 0 ? failures[0] : "没有可导入的字体文件"));

                MessageBox.Show(
                    this,
                    string.Join("\n", failures),
                    "导入字体失败",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);

                return;
            }

            SetLyricsFont(families[0]);

            string summary;

            if (paths.Count == 1)
            {
                summary = "已导入字体：" + families[0];

                if (families.Count > 1)
                    summary = $"已导入字体：{families[0]}（这个文件里还有 {families.Count - 1} 个字体族）";
            }
            else
            {
                summary = $"已导入 {succeeded} 个字体文件、共 {families.Count} 个字体族，当前用：{families[0]}";
            }

            if (failures.Count > 0) summary += $"；{failures.Count} 个文件导入失败";
            if (!string.IsNullOrEmpty(note)) summary += note;

            SetStatus(summary);

            if (failures.Count > 0)
            {
                MessageBox.Show(
                    this,
                    string.Join("\n", failures),
                    "有字体没能导入",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }

        private void RefreshLyricsFontMenus()
        {
            foreach (var menu in _lyricsFontMenus) ClearMenuItems(menu.DropDownItems);
        }

        /// <summary>往桌面歌词窗口上拖字体文件（窗口已经把非字体文件滤掉了）。</summary>
        private void OnFontFilesDropped(string[] fontFiles)
        {
            ImportLyricsFontFiles(fontFiles, note: "（从桌面歌词窗口拖进来的）");
        }

        /// <summary>已导入字体的管理对话框：看字形、删掉不要的。</summary>
        private void ManageLyricsFonts()
        {
            var chosen = FontManagerDialog.Show(
                this,
                _palette,
                _settings.LyricsFontFamily,
                owner => ImportLyricsFonts(owner));

            if (chosen == null) return;     // 用户只是关掉了对话框

            // 删掉的可能正是当前在用的那个：那就退回默认字体
            if (chosen.Length == 0 || !UiFonts.ImportedFamilies.Contains(chosen, StringComparer.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrEmpty(_settings.LyricsFontFamily))
                    SetLyricsFont(string.Empty);

                return;
            }

            SetLyricsFont(chosen);
        }

        /// <summary>换歌词字体（侧栏歌词页与桌面歌词一起换）。</summary>
        internal void SetLyricsFont(string family)
        {
            _settings.LyricsFontFamily = family ?? string.Empty;
            ApplyLyricsFont();
            SyncDesktopLyricsChecks();

            SetStatus(string.IsNullOrEmpty(_settings.LyricsFontFamily)
                ? "歌词已恢复默认字体"
                : "歌词字体：" + _settings.LyricsFontFamily);
        }

        /// <summary>
        /// 把字体设置推到两处歌词上。
        /// <para>侧栏歌词页与桌面歌词共用同一个设置：用户心里的"歌词"就是一处配置。</para>
        /// </summary>
        private void ApplyLyricsFont()
        {
            sidebarPanel.ApplyLyricsFont(_settings.LyricsFontFamily);
            _desktopLyrics?.FontFamilyName = _settings.LyricsFontFamily;
        }

        private ToolStripMenuItem BuildDesktopLyricsColorMenu()
        {
            var menu = new ToolStripMenuItem("文字颜色");

            foreach (var color in DesktopLyricsColors.All)
            {
                var choice = color;

                // Tag 直接放枚举值：刷新勾选状态时按颜色比对，不用再记一堆字段。
                var item = new ToolStripMenuItem(DesktopLyricsColors.DisplayName(choice)) { Tag = choice };
                item.Click += (s, e) => SetDesktopLyricsColor(choice);

                menu.DropDownItems.Add(item);
            }

            return menu;
        }

        /// <summary>
        /// 按当前状态刷一项（字符串 Tag = 开关；"外观"那一项还要把当前字号 / 浓度写进文字）。
        /// <para>递归进子菜单、以及"刷哪些宿主"由 <see cref="MenuSection"/> 管，这里只管一项怎么刷。</para>
        /// </summary>
        private void RefreshDesktopLyricsItem(ToolStripMenuItem menuItem)
        {
            if (menuItem.Tag is string tag)
            {
                menuItem.Checked = tag switch
                {
                    DesktopLyricsNextTag => _settings.DesktopLyricsShowNext,
                    DesktopLyricsTranslationTag => _settings.DesktopLyricsShowTranslation,
                    DesktopLyricsControlsTag => _settings.DesktopLyricsShowControls,
                    DesktopLyricsLockTag => _settings.DesktopLyricsLocked,
                    _ => menuItem.Checked
                };

                // 当前字号 / 浓度写在菜单项上，省得点一下才知道现在是多少
                if (tag == DesktopLyricsAppearanceTag) menuItem.Text = AppearanceMenuText();
            }
            else if (menuItem.Tag is DesktopLyricsColor color)
            {
                menuItem.Checked = _settings.DesktopLyricsColor == color;
            }
        }

        private void SyncDesktopLyricsChecks()
        {
            var enabled = _settings.DesktopLyricsEnabled;

            if (_menuDesktopLyrics != null) _menuDesktopLyrics.Checked = enabled;
            if (_menuDesktopLyricsEnabled != null) _menuDesktopLyricsEnabled.Checked = enabled;
            if (_trayDesktopLyrics != null) _trayDesktopLyrics.Checked = enabled;
            if (_trayDesktopLyricsEnabled != null) _trayDesktopLyricsEnabled.Checked = enabled;

            // 三套菜单（视图 / 桌面歌词右键 / 托盘）里的同一套项，一次全刷
            _desktopLyricsSection?.Refresh();
        }

        /// <summary>
        /// 锁定 / 解锁桌面歌词（全局热键和菜单都走这里）。
        /// <para>
        /// 锁定时弹一次托盘气泡：这一刻之后窗口在鼠标眼里就不存在了，
        /// 而状态栏那句话在主界面缩进托盘时根本看不到——"怎么解开"必须说在用户看得见的地方。
        /// </para>
        /// </summary>
        private void ToggleDesktopLyricsLock() => SetDesktopLyricsLocked(!_settings.DesktopLyricsLocked);

        // =====================================================================
        // 开关
        // =====================================================================

        internal void SetDesktopLyricsEnabled(bool enabled, bool announce = true)
        {
            _settings.DesktopLyricsEnabled = enabled;
            SyncDesktopLyricsChecks();

            if (enabled)
            {
                var window = EnsureDesktopLyricsWindow();

                // 不能用裸的 Show()：它会把前台窗口抢走（见 ShowWithoutTakingFocus 的说明）
                window.ShowWithoutTakingFocus();

                if (announce)
                    SetStatus(_settings.DesktopLyricsLocked
                        ? "桌面歌词已开启（当前是锁定状态：鼠标穿透，右键点不到，用菜单或托盘解除）"
                        : "桌面歌词已开启：右键可调字号 / 颜色 / 锁定，Ctrl+D 关闭");

                return;
            }

            SaveDesktopLyricsBounds();
            _desktopLyrics?.Hide();

            if (announce) SetStatus("桌面歌词已关闭");
        }

        /// <summary>
        /// 建（或复用）桌面歌词窗口。
        /// <para>
        /// 关掉时只是 <c>Hide</c> 而不是销毁：窗口位置、字号这些状态留在原处，
        /// 再开就是原来那副样子；真正的释放放在主窗口关闭时。
        /// </para>
        /// </summary>
        private DesktopLyricsWindow EnsureDesktopLyricsWindow()
        {
            if (_desktopLyrics != null) return _desktopLyrics;

            NormalizeDesktopLyricsSettings();

            var window = new DesktopLyricsWindow { ContextMenuStrip = _desktopLyricsMenu };

            // 往歌词窗口上拖字体文件 = 走和菜单导入完全相同的那条路
            window.FontFilesDropped += OnFontFilesDropped;

            // 控制条上的四个按钮 = 托盘菜单里那四个命令，走的是同一批方法
            window.ControlClicked += OnDesktopLyricsControlClicked;

            window.SetPalette(_palette);
            ApplyDesktopLyricsOptions(window);

            _desktopLyrics = window;

            // 先填内容再摆位置：Show() 之后会立刻重画，先把文字准备好就不会闪一个空框。
            RefreshDesktopLyricsContent();

            if (_settings.DesktopLyricsPlaced)
                window.MoveTo(new Point(_settings.DesktopLyricsX, _settings.DesktopLyricsY));
            else
                window.CenterOnScreen();

            return window;
        }

        /// <summary>启动时恢复：设置读进来可能被人手改过，先钳到合法范围。</summary>
        private void RestoreDesktopLyrics()
        {
            NormalizeDesktopLyricsSettings();
            SyncDesktopLyricsChecks();

            if (_settings.DesktopLyricsEnabled) SetDesktopLyricsEnabled(true, announce: false);
        }

        private void NormalizeDesktopLyricsSettings()
        {
            _settings.DesktopLyricsFontSize = Math.Clamp(
                _settings.DesktopLyricsFontSize,
                DesktopLyricsDefaults.MinimumFontSize,
                DesktopLyricsDefaults.MaximumFontSize);

            _settings.DesktopLyricsOpacity = Math.Clamp(
                _settings.DesktopLyricsOpacity,
                DesktopLyricsDefaults.MinimumOpacity,
                DesktopLyricsDefaults.MaximumOpacity);

            // 设置文件是纯文本，颜色那一项可能被写成一个不认识的数字；
            // 归位成"跟随主题"，菜单里才总会有一项是勾上的。
            if (!Enum.IsDefined(typeof(DesktopLyricsColor), _settings.DesktopLyricsColor))
                _settings.DesktopLyricsColor = DesktopLyricsColor.Theme;
        }

        // =====================================================================
        // 选项
        // =====================================================================

        private void ApplyDesktopLyricsOptions() => ApplyDesktopLyricsOptions(_desktopLyrics);

        private void ApplyDesktopLyricsOptions(DesktopLyricsWindow? window)
        {
            if (window == null) return;

            window.FontSize = _settings.DesktopLyricsFontSize;
            window.OpacityPercent = _settings.DesktopLyricsOpacity;
            window.Locked = _settings.DesktopLyricsLocked;
            window.ShowNextLine = _settings.DesktopLyricsShowNext;
            window.SecondLineIsTranslation = _settings.DesktopLyricsShowTranslation;
            window.ShowControls = _settings.DesktopLyricsShowControls;
            window.Playing = _engine.IsPlaying;
            window.ColorChoice = _settings.DesktopLyricsColor;
            window.FontFamilyName = _settings.LyricsFontFamily;
        }

        /// <summary>「外观…」菜单项上的文字，带着当前值。</summary>
        private string AppearanceMenuText() =>
            $"外观（字号 {_settings.DesktopLyricsFontSize} 磅 · 浓度 {_settings.DesktopLyricsOpacity}%）…";

        /// <summary>
        /// 打开外观对话框（字号 / 浓度两个滑块）。
        /// <para>
        /// 拖动过程中是真的在改歌词窗口——那块屏幕上的歌词就是预览，
        /// 所以「取消」必须把原值写回去，不能只关掉对话框。
        /// </para>
        /// </summary>
        private void EditDesktopLyricsAppearance()
        {
            if (_desktopLyrics == null)
            {
                SetStatus("请先开启桌面歌词，再调节字号与浓度");
                return;
            }

            var originalSize = _settings.DesktopLyricsFontSize;
            var originalOpacity = _settings.DesktopLyricsOpacity;

            var accepted = DesktopLyricsAppearanceDialog.Show(
                this,
                _palette,
                originalSize,
                originalOpacity,
                _settings.LyricsFontFamily,
                _desktopLyrics.TextColor,
                (size, opacity) =>
                {
                    _settings.DesktopLyricsFontSize = size;
                    _settings.DesktopLyricsOpacity = opacity;
                    ApplyDesktopLyricsOptions();
                });

            if (!accepted)
            {
                _settings.DesktopLyricsFontSize = originalSize;
                _settings.DesktopLyricsOpacity = originalOpacity;
                ApplyDesktopLyricsOptions();
            }

            SyncDesktopLyricsChecks();

            SetStatus($"桌面歌词外观：字号 {_settings.DesktopLyricsFontSize} 磅，浓度 {_settings.DesktopLyricsOpacity}%"
                      + (accepted ? string.Empty : "（已取消，恢复原样）"));
        }

        private void StepDesktopLyricsFontSize(int direction)
        {
            var before = _settings.DesktopLyricsFontSize;

            _settings.DesktopLyricsFontSize = Math.Clamp(
                before + direction * DesktopLyricsDefaults.FontSizeStep,
                DesktopLyricsDefaults.MinimumFontSize,
                DesktopLyricsDefaults.MaximumFontSize);

            if (_settings.DesktopLyricsFontSize == before)
            {
                SetStatus(direction > 0 ? "桌面歌词字号已经是最大" : "桌面歌词字号已经是最小");
                return;
            }

            ApplyDesktopLyricsOptions();
            SetStatus("桌面歌词字号 " + _settings.DesktopLyricsFontSize + " 磅");
        }

        private void StepDesktopLyricsOpacity(int direction)
        {
            var before = _settings.DesktopLyricsOpacity;

            _settings.DesktopLyricsOpacity = Math.Clamp(
                before + direction * DesktopLyricsDefaults.OpacityStep,
                DesktopLyricsDefaults.MinimumOpacity,
                DesktopLyricsDefaults.MaximumOpacity);

            if (_settings.DesktopLyricsOpacity == before)
            {
                SetStatus(direction > 0 ? "桌面歌词已经不透明（100%）" : "桌面歌词已经是最淡（20%）");
                return;
            }

            ApplyDesktopLyricsOptions();
            SetStatus("桌面歌词不透明度 " + _settings.DesktopLyricsOpacity + "%");
        }

        private void SetDesktopLyricsShowNext(bool value)
        {
            _settings.DesktopLyricsShowNext = value;
            ApplyDesktopLyricsOptions();
            SyncDesktopLyricsChecks();

            SetStatus(value ? "桌面歌词显示下一句" : "桌面歌词只显示当前句");
        }

        /// <summary>
        /// 第二行在有翻译时显示翻译（双语歌词）。关掉它，第二行就总是"下一句"。
        /// </summary>
        internal void SetDesktopLyricsShowTranslation(bool value)
        {
            _settings.DesktopLyricsShowTranslation = value;
            ApplyDesktopLyricsOptions();
            SyncDesktopLyricsChecks();

            SetStatus(value
                ? "桌面歌词的第二行：有翻译时显示翻译"
                : "桌面歌词的第二行：总是显示下一句");
        }

        /// <summary>
        /// 是否在桌面歌词下面显示控制条（播放 / 暂停、上一首、下一首、停止）。
        /// <para>锁定（鼠标穿透）时它一律不显示——那时候按钮点不到，画出来只会让人误解。</para>
        /// </summary>
        internal void SetDesktopLyricsShowControls(bool value)
        {
            _settings.DesktopLyricsShowControls = value;
            ApplyDesktopLyricsOptions();
            SyncDesktopLyricsChecks();

            SetStatus(value ? "桌面歌词显示控制条" : "桌面歌词不显示控制条（双击歌词仍可播放 / 暂停）");
        }

        internal void SetDesktopLyricsLocked(bool value)
        {
            _settings.DesktopLyricsLocked = value;
            ApplyDesktopLyricsOptions();
            SyncDesktopLyricsChecks();

            if (value)
            {
                // 锁定之后它在鼠标眼里就不存在了，右键菜单自然也点不出来，
                // 只能从菜单 / 托盘 / 全局热键解开——这三点必须一起说清楚。
                var hint = $"再按一次 {AppHotKeys.ToggleDesktopLyricsLockText} 可解锁；"
                           + "也可以在托盘图标的「桌面歌词」子菜单里解除";

                SetStatus("桌面歌词已锁定（鼠标穿透）：" + hint);

                try
                {
                    _trayIcon?.ShowBalloonTip(
                        5000, "桌面歌词已锁定", "鼠标会穿透到下面的程序。" + hint, ToolTipIcon.Info);
                }
                catch (Exception ex)
                {
// 系统关了通知（专注助手）或托盘图标已释放：状态栏那句话还在
                    AppLog.Swallowed("系统关了通知（专注助手）或托盘图标已释放：状态栏那句话还在", ex);
                }

                return;
            }

            SetStatus("桌面歌词已解锁，可以拖动");
        }

        private void SetDesktopLyricsColor(DesktopLyricsColor color)
        {
            _settings.DesktopLyricsColor = color;
            ApplyDesktopLyricsOptions();
            SyncDesktopLyricsChecks();

            SetStatus("桌面歌词颜色：" + DesktopLyricsColors.DisplayName(color));
        }

        private void CenterDesktopLyrics()
        {
            if (_desktopLyrics == null)
            {
                SetStatus("桌面歌词还没有开启");
                return;
            }

            _desktopLyrics.CenterOnScreen();
            SetStatus("桌面歌词已移到屏幕下方居中");
        }

        // =====================================================================
        // 内容与生命周期
        // =====================================================================

        /// <summary>曲名：优先用播放列表里的显示名（也就是列表上写着的那一份）。</summary>
        private string? CurrentTrackTitle()
        {
            var item = _playlist.Current;
            if (item != null) return item.DisplayName;

            return FirstNonEmpty(
                _track.Tags.Title,
                string.IsNullOrEmpty(_track.Path)
                    ? null
                    : Path.GetFileNameWithoutExtension(_track.Path));
        }

        /// <summary>切歌 / 换歌词之后把内容推给桌面歌词。</summary>
        private void RefreshDesktopLyricsContent()
        {
            var window = _desktopLyrics;
            if (window == null) return;

            // 歌词偏移在这里就套上：桌面歌词和侧栏看的必须是同一份时间轴
            window.SetTrack(CurrentTrackTitle(), _track.ShiftedLyrics);
            window.UpdatePosition(
                _engine.HasMedia ? TimeSpan.FromMilliseconds(_engine.Time) : TimeSpan.Zero);

            SyncDesktopLyricsPlayingState();
        }

        /// <summary>把"正在播放吗"推给桌面歌词：控制条第一个按钮据此画播放 / 暂停。</summary>
        private void SyncDesktopLyricsPlayingState()
        {
            var window = _desktopLyrics;
            if (window == null) return;

            window.Playing = _engine.HasMedia && _engine.IsPlaying;
        }

        /// <summary>
        /// 桌面歌词控制条上的按钮被点了。
        /// <para>
        /// 走的就是托盘菜单那四个命令（同一批方法），所以"从哪儿点"不会有两套行为：
        /// 「下一首」带上 <c>userInitiated: true</c>，和托盘一致。
        /// </para>
        /// </summary>
        private void OnDesktopLyricsControlClicked(DesktopLyricsWindow.LyricsControl control)
        {
            switch (control)
            {
                case DesktopLyricsWindow.LyricsControl.PlayPause:
                    TogglePlayPause();
                    break;

                case DesktopLyricsWindow.LyricsControl.Previous:
                    PlayPrevious();
                    break;

                case DesktopLyricsWindow.LyricsControl.Next:
                    PlayNext(true);
                    break;

                case DesktopLyricsWindow.LyricsControl.Stop:
                    StopPlayback();
                    break;
            }

            // 命令下去之后状态可能立刻变了（暂停/继续），别等下一次界面计时
            SyncDesktopLyricsPlayingState();
        }

        /// <summary>退出时记下位置。</summary>
        private void SaveDesktopLyricsBounds()
        {
            var window = _desktopLyrics;
            if (window == null) return;

            _settings.DesktopLyricsX = window.Left;
            _settings.DesktopLyricsY = window.Top;
            _settings.DesktopLyricsPlaced = true;
        }

        private void DisposeDesktopLyricsWindow()
        {
            _desktopLyrics?.Dispose();
            _desktopLyrics = null;

            // 右键菜单是手动挂上去的，Control.Dispose 不会管它。
            _desktopLyricsMenu?.Dispose();
            _desktopLyricsMenu = null;
        }
    }
}
