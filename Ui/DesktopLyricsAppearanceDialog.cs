using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using 播放器.Core;

namespace 播放器.Ui
{
    /// <summary>
    /// 桌面歌词的外观调节对话框：字号 + 文字浓度。
    /// <para>
    /// 为什么要单独开一个对话框：这两个值原来只能在右键菜单里一步一步点——
    /// 字号 12~96、每次 2 磅（从 30 调到 60 要按 15 次），浓度 20~100、每次 5%（16 次），
    /// 而且菜单上看不到当前是多少。滑块一次拖到位，并且<b>边拖边改真的歌词窗口</b>，
    /// 不用"点一下、看一眼、再点一下"。
    /// </para>
    /// <para>
    /// 拖动时是即时生效的（调用方通过 <c>onChanged</c> 回调把值推给歌词窗口），
    /// 所以"取消"由调用方把原值写回去，而不是这个对话框自己回滚。
    /// </para>
    /// </summary>
    internal sealed class DesktopLyricsAppearanceDialog : Form
    {
        private const int DesignWidth = 460;
        private const int RowHeight = 44;
        private const int SliderLeft = 70;
        private const int SliderWidth = 290;
        private const int ValueLeft = 368;

        private readonly Action<int, int> _onChanged;
        private readonly string? _fontFamily;
        private readonly Color _textColor;

        private readonly FlatSlider _sizeSlider = new FlatSlider();
        private readonly FlatSlider _opacitySlider = new FlatSlider();
        private readonly Label _sizeValue = new Label();
        private readonly Label _opacityValue = new Label();
        private readonly Panel _preview = new Panel();

        private readonly Button _resetButton = new Button();
        private readonly Button _okButton = new Button();
        private readonly Button _cancelButton = new Button();

        private readonly List<(Control Control, Rectangle DesignBounds)> _layout =
            new List<(Control, Rectangle)>();

        private Font? _previewFont;
        private float _previewFontSize;
        private bool _loading;

        internal DesktopLyricsAppearanceDialog(
            int fontSize, int opacity, string? fontFamily, Color textColor, Action<int, int> onChanged)
        {
            _onChanged = onChanged;
            _fontFamily = fontFamily;
            _textColor = textColor;

            Text = "桌面歌词外观";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            Font = UiFonts.Sidebar;
            DoubleBuffered = true;

            BuildLayout();
            ApplyDpiLayout();

            _loading = true;
            _sizeSlider.Value = fontSize;
            _opacitySlider.Value = opacity;
            _loading = false;

            UpdateValueLabels();
        }

        private float DpiScale => DeviceDpi / 96f;

        private int Scaled(int value) => (int)Math.Round(value * DpiScale);

        /// <summary>
        /// 打开外观对话框。确定返回 <c>true</c>；取消时调用方负责把原值写回去
        /// （拖动过程中已经实时改过歌词窗口了）。
        /// </summary>
        public static bool Show(
            IWin32Window owner,
            ThemePalette palette,
            int fontSize,
            int opacity,
            string? fontFamily,
            Color textColor,
            Action<int, int> onChanged)
        {
            using var dialog = new DesktopLyricsAppearanceDialog(
                fontSize, opacity, fontFamily, textColor, onChanged);

            dialog.ApplyPalette(palette);

            return dialog.ShowDialog(owner) == DialogResult.OK;
        }

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

            ClientSize = new Size(Scaled(DesignWidth), Math.Max(Scaled(120), bottom + Scaled(16)));
        }

        private void BuildLayout()
        {
            var top = 16;

            BuildRow(top, "字号", _sizeSlider, _sizeValue,
                DesktopLyricsDefaults.MinimumFontSize,
                DesktopLyricsDefaults.MaximumFontSize,
                DesktopLyricsDefaults.FontSizeStep);

            top += RowHeight;

            BuildRow(top, "浓度", _opacitySlider, _opacityValue,
                DesktopLyricsDefaults.MinimumOpacity,
                DesktopLyricsDefaults.MaximumOpacity,
                DesktopLyricsDefaults.OpacityStep);

            top += RowHeight + 4;

            _preview.BorderStyle = BorderStyle.FixedSingle;
            _preview.Paint += (s, e) => DrawPreview(e.Graphics);
            Place(_preview, 14, top, DesignWidth - 28, 64);

            top += 64 + 14;

            _resetButton.Text = "恢复默认";
            _resetButton.Click += (s, e) => ResetToDefaults();
            Place(_resetButton, 14, top, 92, 30);

            _okButton.Text = "确定";
            _okButton.DialogResult = DialogResult.OK;
            Place(_okButton, 272, top, 82, 30);

            _cancelButton.Text = "取消";
            _cancelButton.DialogResult = DialogResult.Cancel;
            Place(_cancelButton, 360, top, 82, 30);

            AcceptButton = _okButton;
            CancelButton = _cancelButton;
        }

        private void BuildRow(
            int top, string caption, FlatSlider slider, Label valueLabel, int minimum, int maximum, int step)
        {
            var name = new Label
            {
                Text = caption,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoSize = false
            };

            Place(name, 14, top + 9, 52, 20);

            slider.Minimum = minimum;
            slider.Maximum = maximum;

            // 这个对话框是模态的，主窗体的方向键快捷键照顾不到它：
            // 不打开键盘的话就只能用鼠标拖。
            slider.KeyboardEnabled = true;
            slider.KeyboardStep = step;
            slider.AccessibleName = caption;
            slider.AccessibleDescription = $"范围 {minimum} 到 {maximum}，用方向键调整";
            slider.Scroll += (s, e) => OnSliderChanged();

            Place(slider, SliderLeft, top, SliderWidth, 30);

            valueLabel.TextAlign = ContentAlignment.MiddleRight;
            valueLabel.AutoSize = false;
            Place(valueLabel, ValueLeft, top + 9, 78, 20);
        }

        // ---- 取值 -------------------------------------------------------------

        internal void OnSliderChanged()
        {
            UpdateValueLabels();

            if (_loading) return;

            RebuildPreviewFont();
            _preview.Invalidate();

            // 实时推给真正的歌词窗口：这块预览板只是参考，屏幕上的那个才是本体
            _onChanged(_sizeSlider.Value, _opacitySlider.Value);
        }

        private void ResetToDefaults()
        {
            _loading = true;

            _sizeSlider.Value = DesktopLyricsDefaults.FontSize;
            _opacitySlider.Value = DesktopLyricsDefaults.Opacity;

            _loading = false;

            OnSliderChanged();
        }

        private void UpdateValueLabels()
        {
            _sizeValue.Text = _sizeSlider.Value + " 磅";
            _opacityValue.Text = _opacitySlider.Value + "%";
        }

        // ---- 预览 -------------------------------------------------------------

        /// <summary>
        /// 预览用的字体。
        /// <para>
        /// 缓存而不是每次绘制都 <c>new Font</c>：那是 GDI 对象，每帧漏一个句柄
        /// （见 README「自绘控件不要在绘制里 new Font」）。
        /// </para>
        /// <para>
        /// 字号不直接用设置值：把它设成 96 磅的话，预览板会被一个字撑爆。
        /// 这里按比例压到一个能放进板的尺寸，看得出字形和相对大小就够了。
        /// </para>
        /// </summary>
        private Font RebuildPreviewFont()
        {
            var size = Math.Clamp(_sizeSlider.Value * 0.55f, 10f, 34f);

            if (_previewFont != null && Math.Abs(_previewFontSize - size) < 0.01f) return _previewFont;

            _previewFont?.Dispose();
            _previewFont = UiFonts.Resolve(_fontFamily, size, FontStyle.Bold);
            _previewFontSize = size;

            return _previewFont;
        }

        private void DrawPreview(Graphics g)
        {
            var font = RebuildPreviewFont();

            g.Clear(_preview.BackColor);

            var alpha = (int)Math.Round(255 * _opacitySlider.Value / 100.0);
            var color = Color.FromArgb(Math.Clamp(alpha, 8, 255), _textColor);

            var text = "桌面歌词示例 Desktop Lyrics 123";
            var box = new RectangleF(8, 0, Math.Max(1, _preview.ClientSize.Width - 16), _preview.ClientSize.Height);

            using var format = new StringFormat(StringFormat.GenericTypographic)
            {
                Alignment = StringAlignment.Near,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap
            };

            using var outline = new SolidBrush(Color.FromArgb(190, 0, 0, 0));
            using var brush = new SolidBrush(color);

            // 描边：和真正的歌词窗口一样，不然浅色的字在浅色背景上看不出来
            for (var dx = -1; dx <= 1; dx++)
            {
                for (var dy = -1; dy <= 1; dy++)
                {
                    if (dx == 0 && dy == 0) continue;

                    g.DrawString(text, font, outline, new RectangleF(box.X + dx, box.Y + dy, box.Width, box.Height), format);
                }
            }

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
                    case FlatSlider slider:
                        slider.BackColor = palette.WindowBack;
                        slider.TrackColor = palette.TrackBack;
                        slider.FillColor = palette.Accent;
                        slider.ThumbColor = palette.Accent;
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
