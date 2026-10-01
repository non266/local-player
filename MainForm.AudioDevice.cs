using System.Windows.Forms;
using 播放器.Core;

// 「播放」菜单下的音频输出设备：列出 libvlc 报的设备、切换、恢复。

namespace 播放器
{
    /// <summary>
    /// 「播放 → 音频输出设备」：把 libvlc 报出来的设备列进菜单，选中之后切过去。
    /// <para>设备列表要在展开时重建（插拔耳机、装虚拟声卡都会变），所以不缓存。</para>
    /// </summary>
    public partial class MainForm
    {
        private ToolStripMenuItem? _menuAudioDevice;

        // =====================================================================
        // 音频输出设备
        // =====================================================================
        private void ConfigureAudioDeviceMenu()
        {
            _menuAudioDevice = new ToolStripMenuItem("音频输出设备");

            // 设备列表会随插拔变化，所以每次展开时重新枚举
            _menuAudioDevice.DropDownOpening += (s, e) => RebuildAudioDeviceMenu();

            menuPlayback.DropDownItems.Add(_menuAudioDevice);
        }

        private void RebuildAudioDeviceMenu()
        {
            if (_menuAudioDevice == null) return;

            _menuAudioDevice.DropDownItems.Clear();

            var devices = _engine.GetAudioOutputDevices();

            if (devices.Count <= 1)
            {
                _menuAudioDevice.DropDownItems.Add(
                    new ToolStripMenuItem("（没有发现可切换的设备）") { Enabled = false });
                return;
            }

            var selectedAny = false;

            foreach (var device in devices)
            {
                var selected = string.IsNullOrEmpty(_settings.AudioDeviceId)
                    ? device.IsDefault
                    : device.Matches(_settings.AudioDeviceId);

                selectedAny |= selected;

                var item = new ToolStripMenuItem(device.Description) { Checked = selected };
                item.Click += (s, e) => SelectAudioDevice(device);
                _menuAudioDevice.DropDownItems.Add(item);
            }

            // 保存的设备已经不在了（比如耳机拔了）：把"系统默认设备"标上
            if (!selectedAny && _menuAudioDevice.DropDownItems.Count > 0 &&
                _menuAudioDevice.DropDownItems[0] is ToolStripMenuItem first)
                first.Checked = true;
        }

        private void SelectAudioDevice(AudioDeviceOption device)
        {
            if (!_engine.SetAudioOutputDevice(device))
            {
                SetStatus("切换音频输出设备失败");
                return;
            }

            _settings.AudioDeviceId = device.DeviceId ?? string.Empty;
            _settings.AudioDeviceModule = device.Module ?? string.Empty;
            SetStatus("音频输出：" + device.Description);
        }

        /// <summary>启动时恢复上次选定的输出设备。</summary>
        private void RestoreAudioDevice()
        {
            if (string.IsNullOrEmpty(_settings.AudioDeviceId)) return;

            var module = string.IsNullOrEmpty(_settings.AudioDeviceModule)
                ? null
                : _settings.AudioDeviceModule;

            _engine.SetAudioOutputDevice(
                new AudioDeviceOption(_settings.AudioDeviceId, _settings.AudioDeviceId, module));
        }
    }
}
