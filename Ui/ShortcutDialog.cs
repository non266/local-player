using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace 播放器.Ui
{
    /// <summary>
    /// 「快捷键设置」对话框（1.29.0）：列出命令目录里的每一条，改主键 / 加次键 / 清除 / 恢复默认。
    /// <para>
    /// 为什么现在才有：1.12.0 把菜单 / 快捷键 / 托盘 / 任务栏 / 卡片都收进了一份命令目录，
    /// "改一个键要改六处"的问题解决了；但那张表还是写死的。1.29.0 在目录外面加了一层覆盖
    /// （<see cref="ShortcutBinding"/>），这个对话框就是那层覆盖的编辑界面。
    /// </para>
    /// <para>
    /// 边界：只覆盖命令目录里的条目；<c>F1</c> / <c>Delete</c> / <c>Esc</c> /
    /// <c>Enter</c> / 文件页与 DVD 菜单那些固定键不能改（见 <see cref="ShortcutBinding.Reserved"/>）。
    /// 逐帧 / 音画字幕延迟 / 歌词偏移从 1.30.0 起已进命令目录。
    /// </para>
    /// </summary>
    internal sealed class ShortcutDialog : Form
    {
        private const int DesignWidth = 560;
        private const int ListTop = 16;
        private const int ListHeight = 330;
        private const int ListWidth = 352;
        private const int ButtonLeft = 384;
        private const int ButtonWidth = 162;

        private readonly ListView _list = new ListView();
        private readonly Button _setPrimary = new Button();
        private readonly Button _setSecondary = new Button();
        private readonly Button _clear = new Button();
        private readonly Button _resetOne = new Button();
        private readonly Button _resetAll = new Button();
        private readonly Label _hint = new Label();
        private readonly Button _okButton = new Button();
        private readonly Button _cancelButton = new Button();

        private readonly List<(Control Control, Rectangle DesignBounds)> _layout =
            new List<(Control, Rectangle)>();

        /// <summary>正在等用户按新键：0 = 主键，1 = 次键，-1 = 没在等。</summary>
        private int _captureSlot = -1;

        private readonly Dictionary<PlayerCommandId, Keys[]> _working = ShortcutBinding.EffectiveMap();

        private ShortcutDialog()
        {
            Text = "快捷键设置";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            Font = UiFonts.Sidebar;
            DoubleBuffered = true;
            KeyPreview = true;

            BuildLayout();
            ApplyDpiLayout();
            FillList();
        }

        /// <summary>打开对话框；确定时带回要写进设置的条目（只含与默认不同的项）。</summary>
        internal static bool Show(IWin32Window owner, ThemePalette palette, out List<string> entries)
        {
            using var dialog = new ShortcutDialog();

            dialog.ApplyPalette(palette);

            if (dialog.ShowDialog(owner) != DialogResult.OK)
            {
                entries = new List<string>();
                return false;
            }

            entries = ShortcutBinding.BuildEntries(dialog._working);
            return true;
        }

        /// <summary>测试用：造一个不显示的对话框（真打开会卡在 ShowDialog 上）。</summary>
        internal static ShortcutDialog CreateForTest() => new ShortcutDialog();

        /// <summary>测试用：当前编辑中的按键表。</summary>
        internal IReadOnlyDictionary<PlayerCommandId, Keys[]> Working => _working;

        /// <summary>测试用：提示那一行现在写着什么（冲突 / 保留键的理由都在这里）。</summary>
        internal string HintText => _hint.Text;

        /// <summary>测试用：列表里有多少行（应等于命令目录的条数）。</summary>
        internal int RowCount => _list.Items.Count;

        /// <summary>测试用：某个命令那一行显示的快捷键串。</summary>
        internal string DisplayFor(PlayerCommandId id)
        {
            var item = FindItem(id);
            return item?.SubItems.Count > 1 ? item.SubItems[1].Text : string.Empty;
        }

        /// <summary>测试用：直接改一条命令的键（等价于用户按键改动）。</summary>
        internal void SetKeysForTest(PlayerCommandId id, params Keys[] keys)
        {
            _working[id] = keys ?? Array.Empty<Keys>();
            RefreshRow(id);
            UpdateHint();
        }

        /// <summary>测试用：点「确定」——走与按钮完全一样的校验路径。</summary>
        internal bool AcceptForTest() => CollectAndClose();

        /// <summary>测试用：只校验、不关窗（"点确定被挡住"这件事要能在不关窗的情况下断言）。</summary>
        internal List<string> ValidateForTest() => ShortcutBinding.Validate(_working);

        /// <summary>测试用：点「全部恢复默认」。</summary>
        internal void ResetAllForTest() => ResetAll();

        private float DpiScale => DeviceDpi / 96f;

        private int Scaled(int value) => (int)Math.Round(value * DpiScale);

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyDpiLayout();
        }

        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);
            ApplyDpiLayout();
        }

        // ---- 布局 -------------------------------------------------------------

        private void Place(Control control, int x, int y, int width, int height)
        {
            var bounds = control.AutoSize ? new Rectangle(x, y, 0, 0) : new Rectangle(x, y, width, height);

            _layout.Add((control, bounds));
            control.Bounds = bounds;
            Controls.Add(control);
        }

        private void ApplyDpiLayout()
        {
            var bottom = 0;

            foreach (var (control, design) in _layout)
            {
                var x = (int)Math.Round(design.X * DpiScale);
                var y = (int)Math.Round(design.Y * DpiScale);

                if (control.AutoSize)
                {
                    control.Location = new Point(x, y);
                }
                else
                {
                    control.Bounds = new Rectangle(
                        x, y,
                        Math.Max(1, (int)Math.Round(design.Width * DpiScale)),
                        Math.Max(1, (int)Math.Round(design.Height * DpiScale)));
                }

                bottom = Math.Max(bottom, control.Bottom);
            }

            ClientSize = new Size(Scaled(DesignWidth), Math.Max(Scaled(240), bottom + Scaled(14)));
        }

        private void BuildLayout()
        {
            _list.View = View.Details;
            _list.FullRowSelect = true;
            _list.MultiSelect = false;
            _list.HideSelection = false;
            _list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            _list.ShowGroups = true;
            _list.Columns.Add("命令", Scaled(210));
            _list.Columns.Add("快捷键", Scaled(120));
            _list.SelectedIndexChanged += (s, e) => UpdateHint();
            Place(_list, 14, ListTop, ListWidth, ListHeight);

            _setPrimary.Text = "改主键…";
            _setPrimary.Click += (s, e) => BeginCapture(0);
            Place(_setPrimary, ButtonLeft, ListTop, ButtonWidth, 30);

            _setSecondary.Text = "加次键…";
            _setSecondary.Click += (s, e) => BeginCapture(1);
            Place(_setSecondary, ButtonLeft, ListTop + 38, ButtonWidth, 30);

            _clear.Text = "清除本项";
            _clear.Click += (s, e) => ClearSelected();
            Place(_clear, ButtonLeft, ListTop + 76, ButtonWidth, 30);

            _resetOne.Text = "恢复本项默认";
            _resetOne.Click += (s, e) => ResetSelected();
            Place(_resetOne, ButtonLeft, ListTop + 122, ButtonWidth, 30);

            _resetAll.Text = "全部恢复默认";
            _resetAll.Click += (s, e) => ResetAll();
            Place(_resetAll, ButtonLeft, ListTop + 160, ButtonWidth, 30);

            _hint.AutoSize = false;
            _hint.TextAlign = ContentAlignment.TopLeft;
            Place(_hint, 14, ListTop + ListHeight + 10, DesignWidth - 28, 42);

            _okButton.Text = "确定";
            _okButton.DialogResult = DialogResult.None;
            _okButton.Click += (s, e) => CollectAndClose();
            Place(_okButton, 386, ListTop + ListHeight + 58, 82, 30);

            _cancelButton.Text = "取消";
            _cancelButton.DialogResult = DialogResult.Cancel;
            Place(_cancelButton, 476, ListTop + ListHeight + 58, 82, 30);

            CancelButton = _cancelButton;
            UpdateHint();
        }

        // ---- 列表 -------------------------------------------------------------

        private void FillList()
        {
            _list.BeginUpdate();

            try
            {
                _list.Items.Clear();
                _list.Groups.Clear();

                var groups = new Dictionary<string, ListViewGroup>(StringComparer.Ordinal);

                foreach (var definition in PlayerCommandCatalog.Defaults)
                {
                    if (!groups.TryGetValue(definition.Category, out var group))
                    {
                        group = new ListViewGroup(definition.Category);
                        groups[definition.Category] = group;
                        _list.Groups.Add(group);
                    }

                    var item = new ListViewItem(definition.HelpText) { Tag = definition.Id, Group = group };
                    item.SubItems.Add(DisplayOf(definition.Id));
                    _list.Items.Add(item);
                }
            }
            finally
            {
                _list.EndUpdate();
            }

            if (_list.Items.Count > 0) _list.Items[0].Selected = true;
        }

        private string DisplayOf(PlayerCommandId id)
        {
            var keys = _working.TryGetValue(id, out var current) ? current : Array.Empty<Keys>();

            return keys.Length == 0
                ? "（没有键）"
                : string.Join(" / ", keys.Select(ShortcutKeyText.Format));
        }

        private ListViewItem? FindItem(PlayerCommandId id)
        {
            foreach (ListViewItem item in _list.Items)
            {
                if (item.Tag is PlayerCommandId value && value == id) return item;
            }

            return null;
        }

        private void RefreshRow(PlayerCommandId id)
        {
            var item = FindItem(id);
            if (item == null) return;

            if (item.SubItems.Count > 1) item.SubItems[1].Text = DisplayOf(id);
        }

        private PlayerCommandId? SelectedId =>
            _list.SelectedItems.Count > 0 && _list.SelectedItems[0].Tag is PlayerCommandId id ? id : null;

        // ---- 编辑 -------------------------------------------------------------

        private void BeginCapture(int slot)
        {
            if (SelectedId == null)
            {
                _hint.Text = "先在左边选一条命令。";
                return;
            }

            _captureSlot = slot;
            _hint.Text = "按下新的快捷键…（Esc 取消）";
        }

        private void ClearSelected()
        {
            if (SelectedId is not { } id) return;

            _working[id] = Array.Empty<Keys>();
            RefreshRow(id);
            UpdateHint();
        }

        private void ResetSelected()
        {
            if (SelectedId is not { } id) return;

            _working[id] = PlayerCommandCatalog.Defaults
                .First(definition => definition.Id == id)
                .DefaultShortcuts
                .ToArray();

            RefreshRow(id);
            UpdateHint();
        }

        private void ResetAll()
        {
            foreach (var definition in PlayerCommandCatalog.Defaults)
                _working[definition.Id] = definition.DefaultShortcuts.ToArray();

            foreach (var definition in PlayerCommandCatalog.Defaults)
                RefreshRow(definition.Id);

            UpdateHint();
        }

        /// <summary>捕获按键：只在"等新键"的时候接管，其余照常交给对话框。</summary>
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (_captureSlot < 0) return base.ProcessCmdKey(ref msg, keyData);

            if (keyData == Keys.Escape)
            {
                _captureSlot = -1;
                UpdateHint();
                return true;
            }

            if (ShortcutKeyText.IsBareModifier(keyData))
                return true;

            if (_captureSlot == 0 && keyData is Keys.Enter or Keys.Tab)
                return true;

            if (SelectedId is not { } id) return true;

            var reserved = ShortcutBinding.Reserved.FirstOrDefault(item => item.Key == keyData);

            if (reserved.Key != Keys.None)
            {
                _captureSlot = -1;
                _hint.Text = $"「{ShortcutKeyText.Format(keyData)}」是固定快捷键（{reserved.Reason}），换一个。";
                return true;
            }

            var keys = _working.TryGetValue(id, out var current) ? current.ToList() : new List<Keys>();

            if (_captureSlot == 0)
            {
                keys.Clear();
                keys.Add(keyData);
            }
            else if (keys.Count >= 2)
            {
                keys[1] = keyData;
            }
            else if (!keys.Contains(keyData))
            {
                keys.Add(keyData);
            }

            _working[id] = keys.ToArray();
            _captureSlot = -1;
            RefreshRow(id);
            UpdateHint();
            return true;
        }

        private void UpdateHint()
        {
            var id = SelectedId;

            if (id == null)
            {
                _hint.Text = "先在左边选一条命令。";
                return;
            }

            var definition = PlayerCommandCatalog.Defaults.First(item => item.Id == id.Value);
            var keys = _working.TryGetValue(id.Value, out var current) ? current : Array.Empty<Keys>();

            var hint = keys.Length == 0
                ? $"「{definition.HelpText}」现在没有键。"
                : $"「{definition.HelpText}」= {string.Join(" / ", keys.Select(ShortcutKeyText.Format))}。";

            hint += "固定键不能改：F1 / Delete / Esc / Enter / Tab / 文件页与光盘菜单那些键；"
                    + "逐帧 / 延迟 / 歌词偏移已经可以改。";

            _hint.Text = hint;
        }

        /// <summary>「确定」：先校验，通过了才关窗（不通过就留在对话框里说原因）。</summary>
        private bool CollectAndClose()
        {
            var errors = ShortcutBinding.Validate(_working);

            if (errors.Count > 0)
            {
                _hint.Text = errors[0] + (errors.Count > 1 ? $"（另外还有 {errors.Count - 1} 处冲突）" : string.Empty);
                return false;
            }

            DialogResult = DialogResult.OK;
            Close();
            return true;
        }

        // ---- 主题 -------------------------------------------------------------

        private void ApplyPalette(ThemePalette palette)
        {
            BackColor = palette.WindowBack;
            ForeColor = palette.ListFore;

            foreach (Control control in Controls)
            {
                switch (control)
                {
                    case ListView list:
                        list.BackColor = palette.ListBack;
                        list.ForeColor = palette.ListFore;
                        break;

                    case Button button:
                        button.FlatStyle = FlatStyle.Flat;
                        button.BackColor = palette.MenuBack;
                        button.ForeColor = palette.MenuFore;
                        button.FlatAppearance.BorderColor = palette.MenuBorder;
                        break;

                    case Label label:
                        label.ForeColor = palette.ListFore;
                        break;
                }
            }
        }
    }
}
