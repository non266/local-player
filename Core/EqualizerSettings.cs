using System;
using System.Collections.Generic;
using System.Globalization;
using LibVLCSharp.Shared;

namespace 播放器.Core
{
    /// <summary>一套均衡器预置：10 个频段的增益加上前置放大。</summary>
    public sealed class EqualizerPreset
    {
        public EqualizerPreset(string name, string displayName, float preamp, float[] amps)
        {
            Name = name;
            DisplayName = displayName;
            Preamp = preamp;
            Amps = amps;
        }

        /// <summary>VLC 里的原始预置名，例如 <c>"Rock"</c>。</summary>
        public string Name { get; }

        /// <summary>界面显示名，例如 <c>"摇滚"</c>。</summary>
        public string DisplayName { get; }

        /// <summary>前置放大（dB）。</summary>
        public float Preamp { get; }

        /// <summary>各频段增益（dB）。</summary>
        public float[] Amps { get; }

        public override string ToString() => DisplayName;
    }

    /// <summary>
    /// 均衡器的当前状态，随设置一起保存。
    /// <para>
    /// 保存的是频段数值而不是"用哪个预置"，这样即使用的是自定义曲线，
    /// 或者以后 VLC 改了预置表，用户的听感也不会莫名其妙变掉。
    /// </para>
    /// </summary>
    public sealed class EqualizerState
    {
        /// <summary>是否启用均衡器。</summary>
        public bool Enabled { get; set; }

        /// <summary>当前沿用（或最后选用）的预置名，仅用于界面显示勾选项。</summary>
        public string PresetName { get; set; } = EqualizerCatalog.FlatPresetName;

        /// <summary>前置放大（dB）。</summary>
        public float Preamp { get; set; }

        /// <summary>各频段增益（dB）。</summary>
        public float[] Amps { get; set; } = new float[EqualizerCatalog.BandCount];

        /// <summary>复制一套预置到当前状态。</summary>
        public void CopyFrom(EqualizerPreset preset)
        {
            PresetName = preset.Name;
            Preamp = preset.Preamp;
            Amps = (float[])preset.Amps.Clone();
            Enabled = true;
        }

        /// <summary>把所有频段和前置放大归零。</summary>
        public void Reset()
        {
            PresetName = EqualizerCatalog.FlatPresetName;
            Preamp = 0f;
            Amps = new float[EqualizerCatalog.BandCount];
        }

        /// <summary>把频段数组规整到 libvlc 实际支持的段数（多截少补）。</summary>
        public float[] NormalizedAmps()
        {
            var count = EqualizerCatalog.BandCount;
            var result = new float[count];

            if (Amps != null)
                for (var i = 0; i < count && i < Amps.Length; i++) result[i] = Amps[i];

            return result;
        }

        /// <summary>当前数值是否与某套预置完全一致（用于界面判断"自定义"）。</summary>
        public bool Matches(EqualizerPreset preset)
        {
            var amps = NormalizedAmps();
            if (Math.Abs(preset.Preamp - Preamp) > 0.01f) return false;

            for (var i = 0; i < amps.Length; i++)
                if (i >= preset.Amps.Length || Math.Abs(preset.Amps[i] - amps[i]) > 0.01f) return false;

            return true;
        }

        public EqualizerState Clone() => new()
        {
            Enabled = Enabled,
            PresetName = PresetName,
            Preamp = Preamp,
            Amps = NormalizedAmps()
        };
    }

    /// <summary>
    /// 均衡器预置表，数值在首次使用时从 libvlc 现读。
    /// <para>
    /// 全部预置都来自用户机器上的 VLC（<c>libvlc_audio_equalizer_new_from_preset</c>），
    /// 不是写死的近似值；万一读不到，就只剩「平坦」一套，手动调频段仍然可用。
    /// </para>
    /// </summary>
    public static class EqualizerCatalog
    {
        /// <summary>libvlc 的均衡器固定是 10 段。</summary>
        public const int BandCount = 10;

        /// <summary>平坦预置的名字（VLC 里就叫 Flat）。</summary>
        public const string FlatPresetName = "Flat";

        /// <summary>
        /// 读不到频段信息时的兜底频率，与 libvlc 的 10 段等程均衡器一致
        /// （31.25 Hz 起，每段翻倍）。
        /// </summary>
        private static readonly float[] FallbackFrequencies =
            { 31.25f, 62.5f, 125f, 250f, 500f, 1000f, 2000f, 4000f, 8000f, 16000f };

        /// <summary>预置名的中文对照；没收录的仍显示 VLC 原名。</summary>
        private static readonly Dictionary<string, string> ChineseNames = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Flat"] = "平坦",
            ["Classical"] = "古典",
            ["Club"] = "俱乐部",
            ["Dance"] = "舞曲",
            ["Full bass"] = "重低音",
            ["Full bass and treble"] = "低音与高音",
            ["Full treble"] = "高音",
            ["Headphones"] = "耳机",
            ["Large Hall"] = "大厅",
            ["Live"] = "现场",
            ["Party"] = "派对",
            ["Pop"] = "流行",
            ["Reggae"] = "雷鬼",
            ["Rock"] = "摇滚",
            ["Ska"] = "斯卡",
            ["Soft"] = "柔和",
            ["Soft rock"] = "轻摇滚",
            ["Techno"] = "电子"
        };

        private static readonly object Gate = new object();
        private static IReadOnlyList<EqualizerPreset>? _presets;
        private static IReadOnlyList<float>? _frequencies;

        /// <summary>所有可用预置。</summary>
        public static IReadOnlyList<EqualizerPreset> Presets
        {
            get
            {
                EnsureLoaded();
                return _presets!;
            }
        }

        /// <summary>各频段中心频率（Hz）。</summary>
        public static IReadOnlyList<float> BandFrequencies
        {
            get
            {
                EnsureLoaded();
                return _frequencies!;
            }
        }

        /// <summary>预置是否真的从 libvlc 读到了（否则说明只有兜底的「平坦」）。</summary>
        public static bool LoadedFromLibVlc { get; private set; }

        /// <summary>确保预置表已经读取；应该在 libvlc 初始化之后调用。</summary>
        public static void EnsureLoaded()
        {
            if (_presets != null) return;

            lock (Gate)
            {
                if (_presets != null) return;
                Load();
            }
        }

        /// <summary>按 VLC 原名找预置，找不到返回 <c>null</c>。</summary>
        public static EqualizerPreset? Find(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;

            foreach (var preset in Presets)
                if (string.Equals(preset.Name, name, StringComparison.OrdinalIgnoreCase)) return preset;

            return null;
        }

        /// <summary>取「平坦」预置，它一定存在。</summary>
        public static EqualizerPreset Flat =>
            Find(FlatPresetName) ?? new EqualizerPreset(FlatPresetName, "平坦", 0f, new float[BandCount]);

        /// <summary>把 VLC 预置名翻成中文；没有对照时原样返回。</summary>
        public static string DisplayName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "自定义";
            return ChineseNames.TryGetValue(name, out var chinese) ? chinese : name;
        }

        /// <summary>频段标签，例如 <c>31</c> / <c>500</c> / <c>16K</c>。</summary>
        public static string BandLabel(int index)
        {
            var frequencies = BandFrequencies;
            if (index < 0 || index >= frequencies.Count) return string.Empty;

            var hz = frequencies[index];

            // 频率标签属于界面文字，不该跟着区域设置变：
            // 默认格式化在小数点用逗号的区域会得到 "1,5K"。
            if (hz < 1000f) return ((int)hz).ToString(CultureInfo.InvariantCulture);

            var thousands = hz / 1000f;
            return Math.Abs(thousands - MathF.Round(thousands)) < 0.01f
                ? ((int)thousands).ToString(CultureInfo.InvariantCulture) + "K"
                : thousands.ToString("0.#", CultureInfo.InvariantCulture) + "K";
        }

        private static void Load()
        {
            var frequencies = new List<float>();
            var presets = new List<EqualizerPreset>();

            try
            {
                using var probe = new Equalizer();

                for (uint band = 0; band < probe.BandCount; band++)
                    frequencies.Add(probe.BandFrequency(band));

                // Equalizer(uint) 的参数是「预置序号」而不是频段数——它内部就是
                // libvlc_audio_equalizer_new_from_preset()，所以下面读到的曲线是
                // libvlc 的真实数值，不是写死的近似值。
                for (uint index = 0; index < probe.PresetCount; index++)
                {
                    var name = probe.PresetName(index);
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    using var preset = new Equalizer(index);

                    var bands = (int)preset.BandCount;
                    var amps = new float[bands];
                    for (var band = 0; band < bands; band++) amps[band] = preset.Amp((uint)band);

                    presets.Add(new EqualizerPreset(name, DisplayName(name), preset.Preamp, amps));
                }

                LoadedFromLibVlc = presets.Count > 0;
            }
            catch (Exception ex)
            {
// libvlc 还没初始化、或者版本太老：降级处理
                AppLog.Swallowed("libvlc 还没初始化、或者版本太老：降级处理", ex);
            }

            if (frequencies.Count == 0)
                frequencies.AddRange(FallbackFrequencies);

            if (presets.Count == 0)
                presets.Add(new EqualizerPreset(FlatPresetName, "平坦", 0f, new float[BandCount]));

            _frequencies = frequencies;
            _presets = presets;
        }
    }
}
