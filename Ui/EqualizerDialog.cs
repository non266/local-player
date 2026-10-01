using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using 播放器.Core;

namespace 播放器.Ui
{
    /// <summary>
    /// 均衡器调节对话框：前置放大 + 10 个频段的增益。
    /// <para>
    /// 用的是自绘的 <see cref="FlatSlider"/>，所以轨道、圆点都会跟着主题走，
    /// 不会在深色主题下冒出白色的系统控件。
    /// </para>
    /// <para>
    /// 数值精度上做了妥协：滑块是整数 dB，而 VLC 的预置曲线带小数。
    /// 处理办法是——<b>用户没碰过的频段保留预置的原始小数</b>，
    /// 只有真的拖动过的那一段才写成整数，这样"选个预置听"和"自己微调"
    /// 两种用法都不会被无谓地改掉音色。
    /// </para>
    /// </summary>
    internal sealed class EqualizerDialog : Form
    {
        private const int MinimumGain = -20;
        private const int MaximumGain = 20;

        private const int RowHeight = 36;
        private const int SliderLeft = 70;
        private const int SliderWidth = 300;
        private const int ValueLeft = 376;

        /// <summary>
        /// 设计基准宽度。上面的坐标都是 96 DPI 下的像素，
        /// 实际使用时要乘 <see cref="DpiScale"/>——本对话框是全项目控件最密的，
        /// 不缩放的话 150% 显示下右侧的数值和按钮会直接被挤到客户区外面。
        /// </summary>
        private const int DesignWidth = 460;

        private readonly EqualizerState _state;
        private readonly FlatSlider _preampSlider = new FlatSlider();
        private readonly Label _preampValue = new Label();
        private readonly FlatSlider[] _bandSliders = new FlatSlider[EqualizerCatalog.BandCount];
        private readonly Label[] _bandValues = new Label[EqualizerCatalog.BandCount];

        private readonly CheckBox _enabledBox = new CheckBox();
        private readonly Label _presetLabel = new Label();
        private readonly Button _presetButton = new Button();
        private readonly Button _resetButton = new Button();
        private readonly Button _okButton = new Button();
        private readonly Button _cancelButton = new Button();
        private readonly ContextMenuStrip _presetMenu = new ContextMenuStrip();

        private bool _loading;

        internal EqualizerDialog(EqualizerState current, ThemePalette palette)
        {
            _state = current.Clone();

            // ⚠ 上面那两个数组只是"壳"：`new FlatSlider[n]` 得到的是 n 个 null。
            // 必须在这里真的把元素 new 出来，否则 BuildLayout 会把 null 传给 BuildRow，
            // 一点"均衡器"就抛 NullReferenceException（对话框根本打不开）。
            for (var band = 0; band < _bandSliders.Length; band++)
            {
                _bandSliders[band] = new FlatSlider();
                _bandValues[band] = new Label();
            }

            Text = "均衡器";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            Font = UiFonts.Sidebar;
            DoubleBuffered = true;

            BuildLayout();
            ApplyDpiLayout();
            ApplyPalette(palette);
            LoadFromState();
        }

        private float DpiScale => DeviceDpi / 96f;

        /// <summary>把设计像素换算成当前 DPI 下的像素。</summary>
        private int Scaled(int value) => (int)Math.Round(value * DpiScale);

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);

            // 句柄就绪之后 DeviceDpi 才是真实值，这里按真实 DPI 再摆一次。
            ApplyDpiLayout();
        }

        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);
            ApplyDpiLayout();
        }

        /// <summary>弹出均衡器对话框；取消时返回 <c>null</c>。</summary>
        public static EqualizerState? Show(IWin32Window owner, EqualizerState current, ThemePalette palette)
        {
            using var dialog = new EqualizerDialog(current, palette);
            return dialog.ShowDialog(owner) == DialogResult.OK ? dialog._state : null;
        }

        // ---- 布局 -------------------------------------------------------------

        /// <summary>每个控件的设计坐标（96 DPI 基准），实际位置由 <see cref="ApplyDpiLayout"/> 按缩放设置。</summary>
        private readonly List<(Control Control, Rectangle DesignBounds)> _layout =
            new List<(Control, Rectangle)>();

        /// <summary>登记一个控件并给出它在 96 DPI 下的位置与大小。</summary>
        private void Place(Control control, int x, int y, int width, int height)
        {
            // AutoSize 的控件（勾选框、文字标签）宽高由字体自己决定，这里只管位置。
            var bounds = control.AutoSize ? new Rectangle(x, y, 0, 0) : new Rectangle(x, y, width, height);

            _layout.Add((control, bounds));
            control.Bounds = bounds;
            Controls.Add(control);
        }

        /// <summary>
        /// 按当前 DPI 摆放所有控件，并把客户区撑到刚好装下。
        /// <para>
        /// 以前这里所有坐标都是写死的 96 DPI 像素：125% / 150% 显示下右侧的 dB 数值和底部
        /// 按钮会被挤出客户区（有的就完全看不见了）。同一个项目里 <c>VideoToolsView</c>
        /// 是有 DPI 缩放的，这个对话框漏了。
        /// </para>
        /// </summary>
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
            const int headerTop = 14;

            _enabledBox.Text = "启用均衡器";
            _enabledBox.AutoSize = true;
            _enabledBox.CheckedChanged += (s, e) =>
            {
                if (_loading) return;
                _state.Enabled = _enabledBox.Checked;
            };
            Place(_enabledBox, 14, headerTop, 0, 0);

            _presetLabel.AutoSize = true;
            _presetLabel.Text = "预置";
            Place(_presetLabel, 150, headerTop + 3, 0, 0);

            _presetButton.Text = "选择预置 ▾";
            _presetButton.Click += (s, e) => ShowPresetMenu();
            Place(_presetButton, 186, headerTop - 2, 140, 28);

            _resetButton.Text = "全部归零";
            _resetButton.Click += (s, e) => ResetAll();
            Place(_resetButton, 334, headerTop - 2, 112, 28);

            var top = 54;

            BuildRow(top, "前置", _preampSlider, _preampValue, value =>
            {
                if (_loading) return;
                _state.Preamp = value;
                UpdateValueLabel(_preampValue, _state.Preamp);
                _state.PresetName = string.Empty;       // 手动调过就不再算某个预置
                _presetButton.Text = "自定义 ▾";
            });

            top += RowHeight;

            for (var band = 0; band < EqualizerCatalog.BandCount; band++)
            {
                var index = band;

                BuildRow(
                    top,
                    EqualizerCatalog.BandLabel(band),
                    _bandSliders[band],
                    _bandValues[band],
                    value =>
                    {
                        if (_loading) return;

                        _state.Amps[index] = value;
                        UpdateValueLabel(_bandValues[index], _state.Amps[index]);
                        _state.PresetName = string.Empty;
                        _presetButton.Text = "自定义 ▾";
                    });

                top += RowHeight;
            }

            _okButton.Text = "确定";
            _okButton.DialogResult = DialogResult.OK;
            Place(_okButton, 272, top + 10, 82, 30);

            _cancelButton.Text = "取消";
            _cancelButton.DialogResult = DialogResult.Cancel;
            Place(_cancelButton, 360, top + 10, 82, 30);

            AcceptButton = _okButton;
            CancelButton = _cancelButton;

            BuildPresetMenu();
        }

        private void BuildRow(int top, string caption, FlatSlider slider, Label valueLabel, Action<int> onChanged)
        {
            var name = new Label
            {
                Text = caption,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoSize = false
            };

            Place(name, 14, top + 8, 52, 20);

            slider.Minimum = MinimumGain;
            slider.Maximum = MaximumGain;
            slider.ShowHoverTip = true;
            slider.HoverTipFormatter = value => FormatGain(value);

            // 这个对话框是模态的，主窗体的方向键快捷键照顾不到它，
            // 不打开键盘的话 11 条滑块就只能用鼠标拖。
            slider.KeyboardEnabled = true;
            slider.KeyboardStep = 1;
            slider.AccessibleName = caption + " 增益";
            slider.AccessibleDescription = $"范围 {MinimumGain} 到 {MaximumGain} dB，用方向键调整";

            // 只认用户操作（Scroll），程序化初始化不算
            slider.Scroll += (s, e) => onChanged(slider.Value);

            Place(slider, SliderLeft, top, SliderWidth, RowHeight - 6);

            valueLabel.TextAlign = ContentAlignment.MiddleRight;
            valueLabel.AutoSize = false;

            Place(valueLabel, ValueLeft, top + 8, 70, 20);
        }

        private void BuildPresetMenu()
        {
            _presetMenu.ShowImageMargin = false;
            _presetMenu.ShowCheckMargin = true;

            foreach (var preset in EqualizerCatalog.Presets)
            {
                var item = new ToolStripMenuItem($"{preset.DisplayName}（{preset.Name}）");
                var captured = preset;
                item.Click += (s, e) => ApplyPreset(captured);
                _presetMenu.Items.Add(item);
            }
        }

        // ---- 取值 -------------------------------------------------------------

        private void LoadFromState()
        {
            _loading = true;

            try
            {
                _enabledBox.Checked = _state.Enabled;

                var amps = _state.NormalizedAmps();

                for (var band = 0; band < _bandSliders.Length; band++)
                {
                    _bandSliders[band].Value = (int)Math.Round(amps[band]);
                    UpdateValueLabel(_bandValues[band], amps[band]);
                }

                _preampSlider.Value = (int)Math.Round(_state.Preamp);
                UpdateValueLabel(_preampValue, _state.Preamp);

                UpdatePresetButtonText();
            }
            finally
            {
                _loading = false;
            }
        }

        private void ApplyPreset(EqualizerPreset preset)
        {
            _state.CopyFrom(preset);
            _state.Enabled = true;

            LoadFromState();
        }

        private void ResetAll()
        {
            _state.Reset();
            _state.Enabled = _enabledBox.Checked;

            LoadFromState();
        }

        private void UpdatePresetButtonText()
        {
            var preset = EqualizerCatalog.Find(_state.PresetName);

            _presetButton.Text = preset != null
                ? $"{preset.DisplayName} ▾"
                : "自定义 ▾";
        }

        private static void UpdateValueLabel(Label label, float value) => label.Text = FormatGain(value);

        private static string FormatGain(float value)
        {
            var rounded = MathF.Round(value, 1);
            var sign = rounded > 0 ? "+" : string.Empty;
            return $"{sign}{rounded:0.#} dB";
        }

        private void ShowPresetMenu() =>
            _presetMenu.Show(_presetButton, new Point(0, _presetButton.Height));

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

                    case CheckBox box:
                        box.ForeColor = palette.ListFore;
                        box.FlatStyle = FlatStyle.Flat;
                        break;
                }
            }

            _presetMenu.Renderer = new ThemedToolStripRenderer(palette);
            _presetMenu.BackColor = palette.MenuBack;
            _presetMenu.ForeColor = palette.MenuFore;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _presetMenu.Dispose();
            base.Dispose(disposing);
        }
    }
}
