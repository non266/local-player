using System;
using System.Drawing;
using System.Windows.Forms;

namespace 播放器.Ui
{
    /// <summary>
    /// 侧栏顶部的标签条：三个等宽标签加一个折叠按钮，全部自绘以跟随主题。
    /// <para>
    /// 没用 <see cref="TabControl"/> 是因为它的标签外观由系统绘制，
    /// 深色主题下要么白得刺眼，要么得整套 owner-draw 重写；
    /// 这里只有三个标签，直接画反而更省事、样式也统一。
    /// </para>
    /// </summary>
    internal sealed class SidebarTabs : Control
    {
        private static readonly string[] Titles = { "歌词", "封面", "信息", "视频" };

        /// <summary>折叠按钮占的宽度（未按 DPI 缩放）。</summary>
        private const int CollapseWidth = 28;

        /// <summary>标签条高度（未按 DPI 缩放）。</summary>
        private const int BarHeight = 30;

        private int _selectedIndex;
        private int _hoverIndex = -1;

        private Color _accentColor = Color.FromArgb(0x4C, 0xC2, 0xFF);
        private Color _foreColor = Color.White;
        private Color _mutedColor = Color.FromArgb(0xA0, 0xA0, 0xA8);
        private Color _borderColor = Color.FromArgb(0x40, 0x40, 0x48);

        public SidebarTabs()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.UserPaint
                     | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.ResizeRedraw, true);

            // 焦点与 Tab 键都打开：这是切换侧栏页面的唯二入口（另一个是鼠标点击），
            // 以前 Selectable/TabStop 都是 false，键盘和读屏软件完全够不着它。
            SetStyle(ControlStyles.Selectable, true);
            TabStop = true;
            Font = UiFonts.Sidebar;

            AccessibleName = "侧栏页面";
            AccessibleDescription = "用左右方向键切换页面，Enter 选中，Ctrl+Enter 折叠侧栏";
            AccessibleRole = AccessibleRole.PageTabList;
        }

        /// <summary>选中了某个标签。</summary>
        public event EventHandler<int>? TabSelected;

        /// <summary>点了折叠按钮。</summary>
        public event EventHandler? CollapseRequested;

        /// <summary>当前标签下标。</summary>
        public int SelectedIndex
        {
            get => _selectedIndex;
            set
            {
                var clamped = Math.Clamp(value, 0, Titles.Length - 1);
                if (_selectedIndex == clamped) return;

                _selectedIndex = clamped;
                Invalidate();
            }
        }

        public void ApplyPalette(ThemePalette palette)
        {
            BackColor = palette.PanelBack;
            _accentColor = palette.Accent;
            _foreColor = palette.ListFore;

            // 未选中的标签是"正常的次要文字"，不是禁用项，所以用 HintFore 而不是
            // MenuDisabledFore（后者在浅色主题下画在 #F0F0F0 上只有约 2.3:1，太淡）。
            _mutedColor = palette.HintFore;
            _borderColor = palette.MenuBorder;
            Invalidate();
        }

        private float DpiScale => DeviceDpi / 96f;

        private int ScaledCollapseWidth => (int)Math.Round(CollapseWidth * DpiScale);

        private int TabWidth
        {
            get
            {
                var available = Math.Max(1, Width - ScaledCollapseWidth);
                return Math.Max(1, available / Titles.Length);
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Height = (int)Math.Round(BarHeight * DpiScale);
        }

        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);
            Height = (int)Math.Round(BarHeight * DpiScale);
            Invalidate();
        }

        // ---- 绘制 -------------------------------------------------------------

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);

            var tabWidth = TabWidth;

            for (var i = 0; i < Titles.Length; i++)
            {
                var rect = new Rectangle(i * tabWidth, 0, tabWidth, Height);
                var selected = i == _selectedIndex;
                var color = selected ? _accentColor : i == _hoverIndex ? _foreColor : _mutedColor;

                TextRenderer.DrawText(
                    g, Titles[i], Font, rect, color,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

                if (!selected) continue;

                // 选中标签下面的小横条
                var inset = (int)Math.Round(10 * DpiScale);
                var thickness = Math.Max(2, (int)Math.Round(2 * DpiScale));
                var bar = new RectangleF(
                    rect.X + inset,
                    Height - thickness - 1,
                    Math.Max(2, rect.Width - inset * 2),
                    thickness);

                using var brush = new SolidBrush(_accentColor);
                using var path = GdiHelpers.RoundedRect(bar, thickness / 2f);
                g.FillPath(brush, path);
            }

            // 右侧折叠按钮
            var collapse = new Rectangle(Width - ScaledCollapseWidth, 0, ScaledCollapseWidth, Height);
            var collapseColor = _hoverIndex == Titles.Length ? _foreColor : _mutedColor;

            TextRenderer.DrawText(
                g, "»", Font, collapse, collapseColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            // 与内容区之间的分隔线
            using (var pen = new Pen(_borderColor))
                g.DrawLine(pen, 0, Height - 1, Width, Height - 1);

            // 焦点框：键盘操作时至少要看得见焦点在哪
            if (Focused)
            {
                var focus = new Rectangle(1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 3));
                using var pen = new Pen(_accentColor) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot };
                g.DrawRectangle(pen, focus);
            }
        }

        // ---- 键盘 -------------------------------------------------------------

        /// <summary>方向键在本控件内部处理，不要被窗体拿去当"快进/快退"。</summary>
        protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) switch
        {
            Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End => true,
            _ => base.IsInputKey(keyData)
        };

        protected override void OnKeyDown(KeyEventArgs e)
        {
            switch (e.KeyCode)
            {
                case Keys.Left:
                case Keys.Up:
                    SelectRelative(-1);
                    e.Handled = true;
                    break;

                case Keys.Right:
                case Keys.Down:
                    SelectRelative(1);
                    e.Handled = true;
                    break;

                case Keys.Home:
                    SelectTab(0);
                    e.Handled = true;
                    break;

                case Keys.End:
                    SelectTab(Titles.Length - 1);
                    e.Handled = true;
                    break;

                case Keys.Enter:
                    // 注意不要接空格：空格在整个程序里是"播放/暂停"，
                    // 侧栏拿到焦点时也应当保持这个习惯，所以只认 Enter。
                    // 折叠按钮落在最后一位，用 Ctrl+Enter 触发，避免和切换页面抢键。
                    if ((e.Modifiers & Keys.Control) != 0)
                        CollapseRequested?.Invoke(this, EventArgs.Empty);
                    else
                        SelectTab(_selectedIndex);

                    e.Handled = true;
                    break;
            }

            base.OnKeyDown(e);
        }

        protected override void OnEnter(EventArgs e)
        {
            base.OnEnter(e);
            Invalidate();
        }

        protected override void OnLeave(EventArgs e)
        {
            base.OnLeave(e);
            Invalidate();
        }

        /// <summary>把选中项移动 <paramref name="delta"/> 个位置（不绕回，到边界就停）。</summary>
        private void SelectRelative(int delta)
        {
            var target = Math.Clamp(_selectedIndex + delta, 0, Titles.Length - 1);
            if (target != _selectedIndex) SelectTab(target);
            else Invalidate();
        }

        private void SelectTab(int index)
        {
            index = Math.Clamp(index, 0, Titles.Length - 1);

            if (index == _selectedIndex)
            {
                Invalidate();
                return;
            }

            // 和鼠标点击走同一条路径，主窗体负责实际切换与持久化。
            TabSelected?.Invoke(this, index);
        }

        // ---- 交互 -------------------------------------------------------------

        /// <summary>命中测试：返回标签下标；点在折叠按钮上时返回 -1 并置 <paramref name="onCollapse"/>。</summary>
        private int HitTest(Point point, out bool onCollapse)
        {
            onCollapse = point.X >= Width - ScaledCollapseWidth;
            if (onCollapse) return -1;

            return Math.Clamp(point.X / TabWidth, 0, Titles.Length - 1);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var tab = HitTest(e.Location, out var onCollapse);

            // 折叠按钮用 Titles.Length 当作它的"下标"，方便统一处理悬停状态
            var index = onCollapse ? Titles.Length : tab;

            if (index != _hoverIndex)
            {
                _hoverIndex = index;
                Cursor = index >= 0 ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }

            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (_hoverIndex != -1)
            {
                _hoverIndex = -1;
                Cursor = Cursors.Default;
                Invalidate();
            }

            base.OnMouseLeave(e);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                var index = HitTest(e.Location, out var onCollapse);

                if (onCollapse)
                    CollapseRequested?.Invoke(this, EventArgs.Empty);
                else if (index != _selectedIndex)
                    TabSelected?.Invoke(this, index);
            }

            base.OnMouseClick(e);
        }
    }
}
