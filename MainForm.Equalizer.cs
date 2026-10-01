using System.Windows.Forms;
using 播放器.Core;
using 播放器.Ui;

// 「播放」菜单下的均衡器：预置、自定义、启用开关。

namespace 播放器
{
    /// <summary>「播放 → 均衡器」：预置曲线、自定义对话框、启用开关。</summary>
    public partial class MainForm
    {
        private ToolStripMenuItem? _menuEqualizer;

        private ToolStripMenuItem? _menuEqEnabled;

        // =====================================================================
        // 均衡器
        // =====================================================================
        private void ConfigureEqualizerMenu()
        {
            _menuEqualizer = new ToolStripMenuItem("均衡器");

            _menuEqEnabled = new ToolStripMenuItem("启用均衡器") { CheckOnClick = true };
            _menuEqEnabled.Click += (s, e) =>
            {
                _settings.Equalizer.Enabled = _menuEqEnabled.Checked;
                ApplyEqualizer();
                SetStatus(_menuEqEnabled.Checked ? "均衡器已启用" : "均衡器已关闭");
            };

            _menuEqualizer.DropDownItems.Add(_menuEqEnabled);
            _menuEqualizer.DropDownItems.Add(new ToolStripSeparator());

            foreach (var preset in EqualizerCatalog.Presets)
            {
                var item = new ToolStripMenuItem($"{preset.DisplayName}（{preset.Name}）") { Tag = preset };
                item.Click += (s, e) => SelectEqualizerPreset((EqualizerPreset)item.Tag!);
                _menuEqualizer.DropDownItems.Add(item);
            }

            _menuEqualizer.DropDownItems.Add(new ToolStripSeparator());

            var customize = new ToolStripMenuItem("自定义…");
            customize.Click += (s, e) => CustomizeEqualizer();
            _menuEqualizer.DropDownItems.Add(customize);

            var reset = new ToolStripMenuItem("恢复平坦");
            reset.Click += (s, e) =>
            {
                _settings.Equalizer.Reset();
                _settings.Equalizer.Enabled = true;
                ApplyEqualizer();
                SetStatus("均衡器已恢复平坦");
            };
            _menuEqualizer.DropDownItems.Add(reset);

            menuPlayback.DropDownItems.Add(new ToolStripSeparator());
            menuPlayback.DropDownItems.Add(_menuEqualizer);
        }

        private void SelectEqualizerPreset(EqualizerPreset preset)
        {
            _settings.Equalizer.CopyFrom(preset);
            ApplyEqualizer();
            SetStatus("均衡器：" + preset.DisplayName);
        }

        private void CustomizeEqualizer()
        {
            var updated = EqualizerDialog.Show(this, _settings.Equalizer, _palette);
            if (updated == null) return;

            _settings.Equalizer = updated;
            ApplyEqualizer();
            SetStatus(updated.Enabled ? "均衡器设置已应用" : "均衡器已关闭");
        }

        private void ApplyEqualizer()
        {
            _engine.ApplyEqualizer(_settings.Equalizer);
            UpdateEqualizerMenuChecks();
        }

        private void UpdateEqualizerMenuChecks()
        {
            if (_menuEqualizer == null) return;

            var state = _settings.Equalizer;

            if (_menuEqEnabled != null) _menuEqEnabled.Checked = state.Enabled;

            foreach (ToolStripItem item in _menuEqualizer.DropDownItems)
            {
                if (item is ToolStripMenuItem { Tag: EqualizerPreset preset } menuItem)
                    menuItem.Checked = state.Enabled && state.Matches(preset);
            }
        }
    }
}
