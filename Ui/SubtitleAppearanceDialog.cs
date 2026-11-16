using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using 播放器.Core;

namespace 播放器.Ui
{
    /// <summary>
    /// 字幕外观对话框：字号 / 颜色 / 描边 / 背景（1.23.0）。
    /// <para>
    /// 为什么是"保存后下次启动生效"而不是边拖边改：libvlc 3 的字幕外观参数是
    /// <c>--freetype-*</c>，只有内核级才认（实测媒体级写法一个都不生效），换外观就得换内核。
    /// 内核在本程序里是按旋转值缓存、跟着播放器走的（M8 的教训），会话内反复新建内核会越攒越多，
    /// 所以这里只改设置，真正生效留到下一次启动（对话框与状态栏都写明）。
    /// </para>
    /// <para>
    /// 预览是<b>近似</b>的：用 GDI+ 按比例画一行示例，真实效果由 libvlc 的 freetype 渲染器画。
    /// 另外 ASS / SSA 字幕走 libass、按字幕文件自己的样式画，不受这里影响。
    /// </para>
    /// </summary>
    internal sealed class SubtitleAppearanceDialog : Form
    {
        private const int DesignWidth = 476;
        private const int RowHeight = 42;
        private const int ComboLeft = 72;
        private const int ComboWidth = 190;

        private readonly ComboBox _sizeBox = new ComboBox();
        private readonly ComboBox _colorBox = new ComboBox();
        private readonly ComboBox _outlineBox = new ComboBox();
        private readonly ComboBox _backgroundBox = new ComboBox();
        private readonly Panel _preview = new Panel();
        private readonly Label _hint = new Label();
        private readonly Button _resetButton = new Button();
        private readonly Button _okButton = new Button();
        private readonly Button _cancelButton = new Button();

        private readonly List<(Control Control, Rectangle DesignBounds)> _layout =
            new List<(Control, Rectangle)>();

        private Font? _previewFont;
        private float _previewFontSize;

        internal SubtitleAppearance Appearance { get; private set; } = SubtitleAppearance.Default;

        private SubtitleAppearanceDialog(SubtitleAppearance current)
        {
            Text = "字幕外观";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            Font = UiFonts.Sidebar;
            DoubleBuffered = true;

            BuildLayout();
            ApplyDpiLayout();
            LoadFrom(current);
        }

        /// <summary>打开对话框；确定时带回新的外观。</summary>
        internal static bool Show(
            IWin32Window owner, ThemePalette palette, SubtitleAppearance current, out SubtitleAppearance result)
        {
            using var dialog = new SubtitleAppearanceDialog(current);

            dialog.ApplyPalette(palette);

            var accepted = dialog.ShowDialog(owner) == DialogResult.OK;

            result = accepted ? dialog.Appearance : current;
            return accepted;
        }

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

            ClientSize = new Size(Scaled(DesignWidth), Math.Max(Scaled(200), bottom + Scaled(16)));
        }

        private void BuildLayout()
        {
            var top = 16;

            BuildRow(top, "字号", _sizeBox);
            top += RowHeight;

            BuildRow(top, "颜色", _colorBox);
            top += RowHeight;

            BuildRow(top, "描边", _outlineBox);
            top += RowHeight;

            BuildRow(top, "背景", _backgroundBox);
            top += RowHeight + 2;

            _preview.BorderStyle = BorderStyle.FixedSingle;
            _preview.Paint += (s, e) => DrawPreview(e.Graphics);
            Place(_preview, 14, top, DesignWidth - 28, 92);

            top += 92 + 6;

            _hint.AutoSize = false;
            _hint.Text = "预览是近似效果；ASS / SSA 字幕由字幕文件自己的样式决定，不受这里影响。"
                         + "外观参数只有建内核时能设，保存后下次启动程序生效。";
            Place(_hint, 14, top, DesignWidth - 28, 34);

            top += 34 + 10;

            _resetButton.Text = "恢复默认";
            _resetButton.Click += (s, e) => ResetToDefaults();
            Place(_resetButton, 14, top, 92, 30);

            _okButton.Text = "确定";
            _okButton.DialogResult = DialogResult.OK;
            _okButton.Click += (s, e) => Collect();
            Place(_okButton, 286, top, 82, 30);

            _cancelButton.Text = "取消";
            _cancelButton.DialogResult = DialogResult.Cancel;
            Place(_cancelButton, 374, top, 82, 30);

            AcceptButton = _okButton;
            CancelButton = _cancelButton;
        }

        private void BuildRow(int top, string caption, ComboBox box)
        {
            var name = new Label
            {
                Text = caption,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoSize = false
            };

            Place(name, 14, top + 9, 52, 20);

            box.DropDownStyle = ComboBoxStyle.DropDownList;
            box.FlatStyle = FlatStyle.Flat;
            box.SelectedIndexChanged += (s, e) =>
            {
                RebuildPreviewFont();
                _preview.Invalidate();
            };

            Place(box, ComboLeft, top, ComboWidth, 28);
        }

        // ---- 取值 -------------------------------------------------------------

        private void LoadFrom(SubtitleAppearance appearance)
        {
            foreach (var option in SubtitleAppearance.Sizes) _sizeBox.Items.Add(option.Label);
            foreach (var option in SubtitleAppearance.Colors) _colorBox.Items.Add(option.Label);
            foreach (var option in SubtitleAppearance.Outlines) _outlineBox.Items.Add(option.Label);
            foreach (var option in SubtitleAppearance.Backgrounds) _backgroundBox.Items.Add(option.Label);

            _sizeBox.SelectedIndex = IndexOf(SubtitleAppearance.Sizes, appearance.Size, option => option.Id);
            _colorBox.SelectedIndex = IndexOf(SubtitleAppearance.Colors, appearance.Color, option => option.Id);
            _outlineBox.SelectedIndex = IndexOf(SubtitleAppearance.Outlines, appearance.Outline, option => option.Value);
            _backgroundBox.SelectedIndex = IndexOf(SubtitleAppearance.Backgrounds, appearance.Background, option => option.Value);

            RebuildPreviewFont();
        }

        private static int IndexOf<T>(IReadOnlyList<T> options, string id, Func<T, string> read)
            where T : struct
        {
            for (var i = 0; i < options.Count; i++)
            {
                if (string.Equals(read(options[i]), id, StringComparison.Ordinal)) return i;
            }

            return 0;
        }

        private static int IndexOf<T>(IReadOnlyList<T> options, int value, Func<T, int> read)
            where T : struct
        {
            for (var i = 0; i < options.Count; i++)
            {
                if (read(options[i]) == value) return i;
            }

            return 0;
        }

        private SubtitleAppearance Current()
        {
            var sizeIndex = Math.Max(0, _sizeBox.SelectedIndex);
            var colorIndex = Math.Max(0, _colorBox.SelectedIndex);
            var outlineIndex = Math.Max(0, _outlineBox.SelectedIndex);
            var backgroundIndex = Math.Max(0, _backgroundBox.SelectedIndex);

            return SubtitleAppearance.Normalize(
                SubtitleAppearance.Sizes[sizeIndex].Id,
                SubtitleAppearance.Colors[colorIndex].Id,
                SubtitleAppearance.Outlines[outlineIndex].Value,
                SubtitleAppearance.Backgrounds[backgroundIndex].Value);
        }

        private void Collect()
        {
            Appearance = Current();
        }

        private void ResetToDefaults()
        {
            _sizeBox.SelectedIndex = 0;
            _colorBox.SelectedIndex = 0;
            _outlineBox.SelectedIndex = 0;
            _backgroundBox.SelectedIndex = 0;

            RebuildPreviewFont();
            _preview.Invalidate();
        }

        // ---- 预览 -------------------------------------------------------------

        private Color PreviewColor()
        {
            var index = Math.Max(0, _colorBox.SelectedIndex);
            var value = SubtitleAppearance.Colors[index].Value;

            return value == 0 ? Color.White : Color.FromArgb((value >> 16) & 0xFF, (value >> 8) & 0xFF, value & 0xFF);
        }

        private int PreviewOutline()
        {
            var value = SubtitleAppearance.Outlines[Math.Max(0, _outlineBox.SelectedIndex)].Value;
            return value < 0 ? 2 : value;
        }

        private int PreviewBackgroundOpacity()
        {
            var value = SubtitleAppearance.Backgrounds[Math.Max(0, _backgroundBox.SelectedIndex)].Value;
            return value < 0 ? 0 : value;
        }

        /// <summary>
        /// 预览字号：libvlc 的 <c>--freetype-rel-fontsize</c> 实测是"画面高度 ÷ 这个值"，
        /// 所以这里也按"360 / 值"折成预览板上的磅数（默认 rel=0 用 16 磅）。
        /// 只是示例，不是 libvlc 的真实算法。
        /// </summary>
        private Font RebuildPreviewFont()
        {
            var rel = SubtitleAppearance.Sizes[Math.Max(0, _sizeBox.SelectedIndex)].RelFontSize;
            var points = rel <= 0 ? 16f : Math.Clamp(360f / rel, 9f, 32f);

            if (_previewFont != null && Math.Abs(_previewFontSize - points) < 0.01f) return _previewFont;

            _previewFont?.Dispose();
            _previewFont = UiFonts.Resolve(null, points, FontStyle.Bold);
            _previewFontSize = points;

            return _previewFont;
        }

        private void DrawPreview(Graphics g)
        {
            var font = RebuildPreviewFont();

            g.Clear(_preview.BackColor);

            var text = "字幕示例 Subtitle Sample 123";
            var box = new RectangleF(8, 0, Math.Max(1, _preview.ClientSize.Width - 16), _preview.ClientSize.Height);

            using var format = new StringFormat(StringFormat.GenericTypographic)
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap
            };

            var background = PreviewBackgroundOpacity();

            if (background > 0)
            {
                using var back = new SolidBrush(Color.FromArgb(Math.Min(200, background), 0, 0, 0));
                g.FillRectangle(back, new RectangleF(2, _preview.ClientSize.Height * 0.5f - font.Height * 0.75f,
                    _preview.ClientSize.Width - 4, font.Height * 1.5f));
            }

            var outline = PreviewOutline();

            using var outlineBrush = new SolidBrush(Color.FromArgb(200, 0, 0, 0));

            for (var dx = -outline; dx <= outline; dx++)
            {
                for (var dy = -outline; dy <= outline; dy++)
                {
                    if (dx == 0 && dy == 0) continue;

                    g.DrawString(text, font, outlineBrush,
                        new RectangleF(box.X + dx, box.Y + dy, box.Width, box.Height), format);
                }
            }

            using var brush = new SolidBrush(PreviewColor());
            g.DrawString(text, font, brush, box, format);
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
                    case ComboBox box:
                        box.BackColor = palette.MenuBack;
                        box.ForeColor = palette.MenuFore;
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

                    case Panel panel:
                        panel.BackColor = palette.ListBack;
                        break;
                }
            }

            _preview.Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _previewFont?.Dispose();
                _previewFont = null;
            }

            base.Dispose(disposing);
        }
    }
}
