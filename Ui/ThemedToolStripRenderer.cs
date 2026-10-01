using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace 播放器.Ui
{
    /// <summary>
    /// 把 <see cref="ProfessionalColorTable"/> 接到主题调色板上。
    /// 只要覆盖到的属性够全，菜单、下拉菜单、工具栏、状态栏的底色就不会再漏出系统色。
    /// </summary>
    internal sealed class ThemedColorTable : ProfessionalColorTable
    {
        private readonly ThemePalette _palette;

        public ThemedColorTable(ThemePalette palette)
        {
            _palette = palette;
            UseSystemColors = false;
        }

        // ---- 工具栏主体 ----

        public override Color ToolStripGradientBegin => _palette.MenuBack;
        public override Color ToolStripGradientMiddle => _palette.MenuBack;
        public override Color ToolStripGradientEnd => _palette.MenuBack;
        public override Color ToolStripBorder => _palette.MenuBorder;
        public override Color ToolStripDropDownBackground => _palette.MenuBack;
        public override Color ToolStripContentPanelGradientBegin => _palette.MenuBack;
        public override Color ToolStripContentPanelGradientEnd => _palette.MenuBack;

        // ---- 菜单栏 ----

        public override Color MenuStripGradientBegin => _palette.MenuBack;
        public override Color MenuStripGradientEnd => _palette.MenuBack;
        public override Color MenuBorder => _palette.MenuBorder;
        public override Color MenuItemBorder => _palette.MenuHoverBack;
        public override Color MenuItemSelected => _palette.MenuHoverBack;
        public override Color MenuItemSelectedGradientBegin => _palette.MenuHoverBack;
        public override Color MenuItemSelectedGradientEnd => _palette.MenuHoverBack;
        public override Color MenuItemPressedGradientBegin => _palette.MenuHoverBack;
        public override Color MenuItemPressedGradientEnd => _palette.MenuHoverBack;
        public override Color MenuItemPressedGradientMiddle => _palette.MenuHoverBack;

        // ---- 工具栏按钮 ----

        public override Color ButtonSelectedBorder => _palette.MenuHoverBack;
        public override Color ButtonSelectedHighlight => _palette.MenuHoverBack;
        public override Color ButtonSelectedHighlightBorder => _palette.MenuHoverBack;
        public override Color ButtonSelectedGradientBegin => _palette.MenuHoverBack;
        public override Color ButtonSelectedGradientMiddle => _palette.MenuHoverBack;
        public override Color ButtonSelectedGradientEnd => _palette.MenuHoverBack;
        public override Color ButtonPressedBorder => _palette.MenuHoverBack;
        public override Color ButtonPressedHighlight => _palette.MenuHoverBack;
        public override Color ButtonPressedHighlightBorder => _palette.MenuHoverBack;
        public override Color ButtonPressedGradientBegin => _palette.MenuHoverBack;
        public override Color ButtonPressedGradientMiddle => _palette.MenuHoverBack;
        public override Color ButtonPressedGradientEnd => _palette.MenuHoverBack;
        public override Color ButtonCheckedHighlight => _palette.MenuHoverBack;
        public override Color ButtonCheckedHighlightBorder => _palette.MenuHoverBack;
        public override Color ButtonCheckedGradientBegin => _palette.MenuHoverBack;
        public override Color ButtonCheckedGradientMiddle => _palette.MenuHoverBack;
        public override Color ButtonCheckedGradientEnd => _palette.MenuHoverBack;

        // ---- 下拉菜单的图标边距与勾选 ----

        public override Color ImageMarginGradientBegin => _palette.MenuBack;
        public override Color ImageMarginGradientMiddle => _palette.MenuBack;
        public override Color ImageMarginGradientEnd => _palette.MenuBack;
        public override Color ImageMarginRevealedGradientBegin => _palette.MenuBack;
        public override Color ImageMarginRevealedGradientMiddle => _palette.MenuBack;
        public override Color ImageMarginRevealedGradientEnd => _palette.MenuBack;
        public override Color CheckBackground => _palette.MenuBack;
        public override Color CheckSelectedBackground => _palette.MenuHoverBack;
        public override Color CheckPressedBackground => _palette.MenuHoverBack;

        // ---- 状态栏 / 分隔线 / 溢出按钮 / 拖拽柄 ----

        public override Color StatusStripGradientBegin => _palette.MenuBack;
        public override Color StatusStripGradientEnd => _palette.MenuBack;
        public override Color SeparatorDark => _palette.MenuSeparator;
        public override Color SeparatorLight => _palette.MenuSeparator;
        public override Color GripDark => _palette.MenuSeparator;
        public override Color GripLight => _palette.MenuBack;
        public override Color OverflowButtonGradientBegin => _palette.MenuBack;
        public override Color OverflowButtonGradientMiddle => _palette.MenuBack;
        public override Color OverflowButtonGradientEnd => _palette.MenuBack;
        public override Color RaftingContainerGradientBegin => _palette.MenuBack;
        public override Color RaftingContainerGradientEnd => _palette.MenuBack;
    }

    /// <summary>
    /// 基于主题调色板绘制菜单 / 工具栏 / 状态栏。
    /// 除了配色表，还要接管文字、箭头、勾选、分隔线和悬停背景，
    /// 否则这些细节仍然会用系统自带的颜色。
    /// </summary>
    internal sealed class ThemedToolStripRenderer : ToolStripProfessionalRenderer
    {
        private readonly ThemePalette _palette;

        public ThemedToolStripRenderer(ThemePalette palette)
            : base(new ThemedColorTable(palette))
        {
            _palette = palette;
            RoundedEdges = false;
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using (var brush = new SolidBrush(_palette.MenuBack))
            {
                e.Graphics.FillRectangle(brush, e.AffectedBounds);
            }
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            var strip = e.ToolStrip;

            // 下拉菜单四周描边；菜单栏 / 工具栏只画一条分隔线；状态栏画在顶部。
            using (var pen = new Pen(_palette.MenuBorder))
            {
                if (strip is ToolStripDropDown)
                {
                    var bounds = new Rectangle(0, 0, strip.Width - 1, strip.Height - 1);
                    e.Graphics.DrawRectangle(pen, bounds);
                }
                else if (strip is StatusStrip)
                {
                    e.Graphics.DrawLine(pen, 0, 0, strip.Width, 0);
                }
                else if (strip is MenuStrip || strip is ToolStrip)
                {
                    e.Graphics.DrawLine(pen, 0, strip.Height - 1, strip.Width, strip.Height - 1);
                }
            }
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            var item = e.Item;

            e.TextColor = !item.Enabled
                ? _palette.MenuDisabledFore
                : item.Selected || item.Pressed ? _palette.MenuHoverFore : _palette.MenuFore;

            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            // e.Item 在 .NET 8 的标注里是可空的：为 null 时按可用处理即可。
            e.ArrowColor = e.Item?.Enabled != false ? _palette.MenuFore : _palette.MenuDisabledFore;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            var item = e.Item;
            if (!item.Selected && !item.Pressed) return;

            using (var brush = new SolidBrush(_palette.MenuHoverBack))
            {
                e.Graphics.FillRectangle(brush, new Rectangle(Point.Empty, item.Size));
            }
        }

        protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e)
        {
            var item = e.Item;
            var isChecked = item is ToolStripButton { Checked: true };

            if (!item.Selected && !item.Pressed && !isChecked) return;

            var bounds = new Rectangle(Point.Empty, item.Size);
            var rect = new RectangleF(bounds.X + 0.5f, bounds.Y + 0.5f, bounds.Width - 1f, bounds.Height - 1f);

            using (var brush = new SolidBrush(_palette.MenuHoverBack))
            using (var path = GdiHelpers.RoundedRect(rect, 3f))
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.FillPath(brush, path);
            }
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var bounds = e.ImageRectangle;
            if (bounds.Width <= 2 || bounds.Height <= 2) return;

            using (var pen = new Pen(_palette.Accent, 2f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;

                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.DrawLines(pen, new[]
                {
                    new PointF(bounds.Left + 2f, bounds.Top + bounds.Height * 0.55f),
                    new PointF(bounds.Left + bounds.Width * 0.42f, bounds.Bottom - 3f),
                    new PointF(bounds.Right - 2f, bounds.Top + 3f)
                });
            }
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            var bounds = e.Item.ContentRectangle;

            using (var pen = new Pen(_palette.MenuSeparator))
            {
                if (e.Vertical)
                {
                    var x = bounds.Left + bounds.Width / 2;
                    e.Graphics.DrawLine(pen, x, bounds.Top + 3, x, bounds.Bottom - 3);
                }
                else
                {
                    var y = bounds.Top + bounds.Height / 2;
                    e.Graphics.DrawLine(pen, bounds.Left + 3, y, bounds.Right - 3, y);
                }
            }
        }

        protected override void OnRenderStatusStripSizingGrip(ToolStripRenderEventArgs e)
        {
            // 状态栏右下角的拖拽纹理由系统绘制，深色主题下会显得突兀，直接不画。
        }
    }
}
