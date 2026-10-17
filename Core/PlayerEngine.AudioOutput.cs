using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using LibVLCSharp.Shared;
using LibVLCSharp.Shared.Structures;

// 均衡器与音频输出设备。

namespace 播放器.Core
{
    public sealed partial class PlayerEngine
    {
        // ---- 均衡器 -----------------------------------------------------------

        /// <summary>
        /// 应用均衡器设置；未启用（或传 <c>null</c>）时关闭均衡器。
        /// <para>
        /// libvlc 会把均衡器保留在播放器上，之后打开的媒体会自动套用，
        /// 所以只需要在设置变化时调用一次，不必每首歌都重设。
        /// </para>
        /// </summary>
        /// <returns>是否成功。</returns>
        public bool ApplyEqualizer(EqualizerState? state)
        {
            _equalizerState = state;
            return ApplyEqualizerTo(_mediaPlayer, state);
        }

        /// <summary>
        /// 把均衡器设置铺到指定的播放器上（换内核之后要重新铺一遍——
        /// 均衡器是挂在播放器上的，不是内核上的）。
        /// </summary>
        private static bool ApplyEqualizerTo(MediaPlayer player, EqualizerState? state)
        {
            try
            {
                if (state == null || !state.Enabled)
                {
                    player.UnsetEqualizer();
                    return true;
                }

                var amps = state.NormalizedAmps();

                // 无参构造 = 全零的均衡器，再逐段写入用户保存的曲线
                using var equalizer = new Equalizer();
                var bands = Math.Min((int)equalizer.BandCount, amps.Length);

                equalizer.SetPreamp(state.Preamp);
                for (var band = 0; band < bands; band++) equalizer.SetAmp(amps[band], (uint)band);

                player.SetEqualizer(equalizer);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ---- 音频输出设备 -----------------------------------------------------

        /// <summary>
        /// 列出可选的音频输出设备，第一项固定是"系统默认设备"。
        /// <para>
        /// 正在播放时优先用播放器自己的设备表（最准）；没在播放就退回向
        /// libvlc 询问输出模块的设备表——否则暂停状态下列表会是空的。
        /// </para>
        /// </summary>
        public IReadOnlyList<AudioDeviceOption> GetAudioOutputDevices()
        {
            var options = new List<AudioDeviceOption> { new(null, "系统默认设备") };
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var current = _mediaPlayer.AudioOutputDeviceEnum;

                if (current is { Length: > 0 })
                {
                    foreach (var device in current)
                    {
                        if (string.IsNullOrEmpty(device.DeviceIdentifier)) continue;
                        if (!seen.Add(device.DeviceIdentifier)) continue;

                        options.Add(new AudioDeviceOption(device.DeviceIdentifier, device.Description));
                    }

                    return options;
                }

                var module = ChooseAudioOutputModule();
                if (module == null) return options;

                foreach (var device in _libVlc.AudioOutputDevices(module))
                {
                    if (string.IsNullOrEmpty(device.DeviceIdentifier)) continue;
                    if (!seen.Add(device.DeviceIdentifier)) continue;

                    options.Add(new AudioDeviceOption(device.DeviceIdentifier, device.Description, module));
                }
            }
            catch (Exception ex)
            {
// 枚举失败就只给"系统默认设备"
                AppLog.Swallowed("枚举失败就只给'系统默认设备'", ex);
            }

            return options;
        }

        /// <summary>切换音频输出设备；下一首（或重新播放）生效。</summary>
        public bool SetAudioOutputDevice(AudioDeviceOption? option)
        {
            if (option == null) return false;

            // libvlc 推荐模块传 NULL；不同 LibVLCSharp 版本对 NULL 的容忍度不一样，
            // 所以依次尝试几种组合，哪种不抛异常就用哪种。
            var attempts = new List<(string? Module, string? Device)>
            {
                (option.Module, option.DeviceId)
            };

            if (option.Module != null) attempts.Add((null, option.DeviceId));
            attempts.Add(("mmdevice", option.DeviceId));

            foreach (var (module, device) in attempts)
            {
                try
                {
                    _mediaPlayer.SetOutputDevice(module!, device!);
                    return true;
                }
                catch (Exception ex)
                {
// 换下一种组合
                    AppLog.Swallowed("换下一种组合", ex);
                }
            }

            return false;
        }

        private string? ChooseAudioOutputModule()
        {
            var outputs = _libVlc.AudioOutputs;
            if (outputs == null || outputs.Length == 0) return null;

            // Windows 上默认是 mmdevice；找不到就用第一个可用的
            foreach (var output in outputs)
                if (string.Equals(output.Name, "mmdevice", StringComparison.OrdinalIgnoreCase)) return output.Name;

            return outputs[0].Name;
        }
    }
}
