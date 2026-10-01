using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using 播放器.Core;

namespace 播放器.Ui
{
    /// <summary>
    /// 载入设置方案时问一句：<b>这一份里的哪些设置要拿过来</b>。
    /// <para>
    /// 一份方案里什么都有（音量、主题、桌面歌词、均衡器……），但用户想要的常常只是其中一块：
    /// "把这套外观搬过来，音量别动"。所以默认全勾上，想挑就一个一个取消，
    /// 每一行还写着<b>这一组和当前有几项不一样</b>——点之前就知道有没有区别，不会白点一下。
    /// </para>
    /// <para>
    /// 这里只负责"选哪几组"，真正的合并与应用在主窗体那边（那要和界面、引擎打交道）。
    /// </para>
    /// </summary>
    internal sealed class SettingsLoadDialog : Form
    {
        private const int DesignWidth = 660;

        private readonly ListView _list = new ListView();
        private readonly Label _prompt = new Label();
        private readonly Label _hint = new Label();
        private readonly Button _selectAll = new Button();
        private readonly Button _selectNone = new Button();
        private readonly Button _load = new Button();
        private readonly Button _cancel = new Button();

        private readonly AppSettings _current;
        private readonly AppSettings _incoming;

        internal SettingsLoadDialog(AppSettings current, AppSettings incoming, string profileName)
        {
            _current = current ?? throw new ArgumentNullException(nameof(current));
            _incoming = incoming ?? throw new ArgumentNullException(nameof(incoming));

            Text = "载入设置方案";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            Font = UiFonts.Sidebar;
            DoubleBuffered = true;

            _prompt.Text = $"要载入「{profileName}」里的哪些设置？\n"
                           + "没勾的那些保持现在这样。方案文件本身不会被这次载入改动。";
            _prompt.AutoSize = true;

            _list.View = View.Details;
            _list.CheckBoxes = true;
            _list.FullRowSelect = true;
            _list.MultiSelect = false;
            _list.HideSelection = false;
            _list.HeaderStyle = ColumnHeaderStyle.Nonclickable;

            _list.Columns.Add("设置组", 150);
            _list.Columns.Add("包含", 330);
            _list.Columns.Add("与当前不同", 90);

            BuildRows();

            _hint.Text = "退出时（以及切换方案前）这一套会自动写回方案文件。";
            _hint.AutoSize = true;

            _selectAll.Text = "全选";
            _selectNone.Text = "全不选";
            _load.Text = "载入";
            _load.DialogResult = DialogResult.OK;
            _cancel.Text = "取消";
            _cancel.DialogResult = DialogResult.Cancel;

            _selectAll.Click += (s, e) => SetAllChecked(true);
            _selectNone.Click += (s, e) => SetAllChecked(false);
            _list.ItemChecked += (s, e) => UpdateLoadButton();

            Controls.AddRange(new Control[] { _prompt, _list, _hint, _selectAll, _selectNone, _load, _cancel });

            AcceptButton = _load;
            CancelButton = _cancel;

            ApplyLayout();
            UpdateLoadButton();
        }

        private float DpiScale => DeviceDpi / 96f;

        private int Scaled(int value) => (int)Math.Round(value * DpiScale);

        /// <summary>勾选框里选中的那几组（按界面顺序）。</summary>
        internal IReadOnlyList<SettingsSection> SelectedSections =>
            _list.CheckedIndices.Cast<int>()
                .Where(index => index >= 0 && index < SettingsProfile.All.Count)
                .Select(index => SettingsProfile.All[index])
                .ToList();

        /// <summary>只读入口：现在能不能点「载入」（一组都没勾就不行）。给冒烟测试用。</summary>
        internal bool CanLoad => _load.Enabled;

        /// <summary>把某一组勾上 / 取消（给冒烟测试用，免得去合成鼠标点击）。</summary>
        internal void SetChecked(SettingsSection section, bool value)
        {
            var index = SettingsProfile.All.ToList().IndexOf(section);

            if (index >= 0 && index < _list.Items.Count)
                _list.Items[index].Checked = value;
        }

        /// <summary>全选 / 全不选。</summary>
        internal void SetAllChecked(bool value)
        {
            foreach (ListViewItem item in _list.Items)
                item.Checked = value;

            UpdateLoadButton();
        }

        /// <summary>点「载入」：一组都没勾就什么也不做（按钮那时本来就是灰的）。</summary>
        internal void LoadSelected()
        {
            if (!CanLoad) return;

            DialogResult = DialogResult.OK;
            Close();
        }

        /// <summary>
        /// 弹出这个窗口；用户取消时返回 <c>null</c>，否则返回他勾中的那几组。
        /// </summary>
        public static IReadOnlyList<SettingsSection>? Show(
            IWin32Window owner, ThemePalette palette, AppSettings current, AppSettings incoming, string profileName)
        {
            using var dialog = new SettingsLoadDialog(current, incoming, profileName);

            dialog.ApplyPalette(palette);

            return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.SelectedSections : null;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);

            // 句柄就绪之后 DeviceDpi 才是真实值，这时再按真实 DPI 摆一次
            ApplyLayout();
        }

        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);
            ApplyLayout();
        }

        /// <summary>每一组一行：名字、包含什么、和当前有几项不一样。</summary>
        private void BuildRows()
        {
            foreach (var section in SettingsProfile.All)
            {
                var differences = SettingsProfile.CountDifferences(_current, _incoming, section);

                var row = new ListViewItem(SettingsProfile.Name(section))
                {
                    Checked = true,
                    ToolTipText = SettingsProfile.Hint(section)
                };

                row.SubItems.Add(SettingsProfile.Hint(section));
                row.SubItems.Add(differences == 0 ? "没有区别" : $"{differences} 项");

                _list.Items.Add(row);
            }
        }

        private void UpdateLoadButton() => _load.Enabled = _list.CheckedIndices.Count > 0;

        /// <summary>按测量结果摆：提示 → 清单 → 说明 → 按钮，每段接在上一段的真实高度下面。</summary>
        private void ApplyLayout()
        {
            var padding = Scaled(14);
            var gap = Scaled(10);
            var buttonWidth = Scaled(84);
            var buttonHeight = Scaled(30);

            var width = Math.Max(Scaled(420), Scaled(DesignWidth));
            var inner = width - padding * 2;

            _prompt.Location = new Point(padding, padding);
            _prompt.MaximumSize = new Size(inner, 0);

            var listTop = _prompt.Bottom + gap;

            // 行高按 DPI 放大之后再乘行数：宁可高一点（留一条空白），不要把最后一行挤掉
            _list.Bounds = new Rectangle(
                padding, listTop, inner, Scaled(26) * (SettingsProfile.All.Count + 1));

            _hint.Location = new Point(padding, _list.Bottom + gap);
            _hint.MaximumSize = new Size(inner, 0);

            var buttonTop = _hint.Bottom + gap;

            _cancel.Bounds = new Rectangle(width - padding - buttonWidth, buttonTop, buttonWidth, buttonHeight);
            _load.Bounds = new Rectangle(_cancel.Left - Scaled(8) - buttonWidth, buttonTop, buttonWidth, buttonHeight);
            _selectNone.Bounds = new Rectangle(padding, buttonTop, buttonWidth, buttonHeight);
            _selectAll.Bounds = new Rectangle(padding + buttonWidth + Scaled(8), buttonTop, buttonWidth, buttonHeight);

            ClientSize = new Size(width, buttonTop + buttonHeight + padding);
        }

        private void ApplyPalette(ThemePalette palette)
        {
            BackColor = palette.WindowBack;
            ForeColor = palette.ListFore;

            _list.BackColor = palette.ListBack;
            _list.ForeColor = palette.ListFore;

            foreach (Control control in Controls)
            {
                switch (control)
                {
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
