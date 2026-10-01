// 检查点 10：均衡器与引擎新增接口，以及均衡器对话框布局。

namespace SmokeTest
{
    internal static partial class Program
    {
        // -----------------------------------------------------------------
        // 10) 均衡器与引擎新增接口
        // -----------------------------------------------------------------

        private static bool TestEqualizerAndEngine()
        {
            EqualizerCatalog.EnsureLoaded();

            if (!EqualizerCatalog.LoadedFromLibVlc)
            {
                Log(10, "没能从 libvlc 读到均衡器预置（只剩兜底的「平坦」）");
                return false;
            }

            if (EqualizerCatalog.Presets.Count < 5)
            {
                Log(10, $"均衡器预置太少：{EqualizerCatalog.Presets.Count} 套");
                return false;
            }

            var frequencies = EqualizerCatalog.BandFrequencies;
            if (frequencies.Count != EqualizerCatalog.BandCount)
            {
                Log(10, $"频段数不正确：{frequencies.Count}（期望 {EqualizerCatalog.BandCount}）");
                return false;
            }

            for (var i = 1; i < frequencies.Count; i++)
            {
                if (frequencies[i] <= frequencies[i - 1])
                {
                    Log(10, $"频段频率不是递增的：{frequencies[i - 1]} → {frequencies[i]}");
                    return false;
                }
            }

            // 预置曲线必须是 libvlc 的真实数值，不能是"全 0 的空壳"
            var rock = EqualizerCatalog.Find("Rock");
            if (rock == null || rock.Amps.Length != EqualizerCatalog.BandCount)
            {
                Log(10, "找不到 Rock 预置，或者它的频段数不对");
                return false;
            }

            if (rock.Amps.All(a => Math.Abs(a) < 0.01f))
            {
                Log(10, "Rock 预置的曲线全是 0，说明读到的不是真实预置值");
                return false;
            }

            var flat = EqualizerCatalog.Find("Flat");
            if (flat == null || flat.Amps.Any(a => Math.Abs(a) > 0.01f))
            {
                Log(10, "Flat 预置不应该是非零曲线");
                return false;
            }

            // 状态行为
            var state = new EqualizerState();
            state.CopyFrom(rock);

            if (!state.Enabled || !state.Matches(rock))
            {
                Log(10, "CopyFrom 之后的状态与预置不一致");
                return false;
            }

            state.Amps[0] = rock.Amps[0] + 5f;
            if (state.Matches(rock))
            {
                Log(10, "改动过频段之后仍然被认为匹配原预置");
                return false;
            }

            if (new EqualizerState { Amps = new[] { 1f, 2f, 3f } }.NormalizedAmps().Length != EqualizerCatalog.BandCount)
            {
                Log(10, "频段数组长度不一致时没有补齐到 10 段");
                return false;
            }

            if (EqualizerCatalog.DisplayName("Rock") != "摇滚")
            {
                Log(10, $"预置中文名不正确：{EqualizerCatalog.DisplayName("Rock")}");
                return false;
            }

            // 频段标签要各不相同且非空；libvlc 的 10 段是 31.25Hz 起的等程划分
            var labels = Enumerable.Range(0, EqualizerCatalog.BandCount)
                .Select(EqualizerCatalog.BandLabel)
                .ToList();

            if (labels.Any(string.IsNullOrEmpty) || labels.Distinct().Count() != labels.Count)
            {
                Log(10, "频段标签有空的或者重复的：" + string.Join("/", labels));
                return false;
            }

            if (labels[^1] != "16K" || frequencies[0] >= 100f)
            {
                Log(10, $"频段范围不像 10 段均衡器：{frequencies[0]} ~ {frequencies[^1]}"
                        + $"（标签 {labels[0]} ~ {labels[^1]}）");
                return false;
            }

            if (!CheckEqualizerDialogLayout())
            {
                return false;
            }

            // 引擎接口（此时还没有媒体，属于"设置先存下、播放时生效"的路径）
            using (var owner = new Form())
            using (var engine = new PlayerEngine(owner))
            {
                if (!engine.ApplyEqualizer(state))
                {
                    Log(10, "ApplyEqualizer 失败");
                    return false;
                }

                if (!engine.ApplyEqualizer(null))
                {
                    Log(10, "关闭均衡器失败");
                    return false;
                }

                var devices = engine.GetAudioOutputDevices();
                if (devices.Count == 0 || !devices[0].IsDefault)
                {
                    Log(10, $"音频输出设备列表的第一项不是「系统默认设备」（共 {devices.Count} 项）");
                    return false;
                }

                if (!engine.SetAudioOutputDevice(devices[0]))
                {
                    Log(10, "切换回系统默认音频设备失败");
                    return false;
                }

                var chapters = engine.GetChapters();
                if (chapters.Count != 0)
                {
                    Log(10, $"没有媒体时 GetChapters 返回了 {chapters.Count} 项");
                    return false;
                }

                if (engine.ChapterCount != 0)
                {
                    Log(10, $"没有媒体时 ChapterCount 应为 0，实际是 {engine.ChapterCount}（libvlc 的 -1 不该漏出来）");
                    return false;
                }

                if (engine.GetTracks().Count != 0)
                {
                    Log(10, "没有媒体时不应该报告任何轨道");
                    return false;
                }

                Log(10, $"均衡器预置 {EqualizerCatalog.Presets.Count} 套（取自 libvlc），"
                        + $"频段 {EqualizerCatalog.BandLabel(0)}~{EqualizerCatalog.BandLabel(EqualizerCatalog.BandCount - 1)}，"
                        + $"可选音频设备 {devices.Count} 个");
            }

            return true;
        }

        /// <summary>
        /// 均衡器对话框必须真的能打开，并且所有控件都落在客户区之内。
        /// <para>
        /// 两个都是真出过问题的点：
        /// <list type="bullet">
        ///   <item>频段滑块/数值标签两个数组只在字段里 `new FlatSlider[n]`，
        ///     元素从来没被实例化，于是 BuildRow 收到 null —— 一点「均衡器」
        ///     就抛 NullReferenceException，对话框完全打不开。</item>
        ///   <item>对话框里所有坐标都是写死的 96 DPI 像素，125% / 150% 显示下
        ///     字体变大而位置不变，右侧数值和底部按钮会被挤出客户区。</item>
        /// </list>
        /// 所以这里实例化真实对话框、用真实 DPI 打开，再逐个控件比对边界。
        /// </para>
        /// </summary>
        private static bool CheckEqualizerDialogLayout()
        {
            var dialog = new EqualizerDialog(new EqualizerState(), Themes.Light);

            using (dialog)
            {
                dialog.StartPosition = FormStartPosition.Manual;
                dialog.Location = new Point(40, 40);
                dialog.Show();
                PumpMessages(150);

                var sliderCount = 0;
                foreach (Control control in dialog.Controls)
                {
                    if (control.GetType().Name == "FlatSlider") sliderCount++;
                }

                if (sliderCount != EqualizerCatalog.BandCount + 1)
                {
                    Log(10, $"均衡器滑块数不对：{sliderCount}（期望 {EqualizerCatalog.BandCount + 1}）");
                    return false;
                }

                var scale = dialog.DeviceDpi / 96f;
                var worstRight = 0;
                var worstBottom = 0;

                foreach (Control control in dialog.Controls)
                {
                    worstRight = Math.Max(worstRight, control.Right);
                    worstBottom = Math.Max(worstBottom, control.Bottom);

                    if (control.Right > dialog.ClientSize.Width || control.Bottom > dialog.ClientSize.Height)
                    {
                        Log(10, $"均衡器对话框在 {scale:0.##} 倍缩放下有控件越界："
                                + $"{control.GetType().Name} {control.Bounds} 超出客户区 {dialog.ClientSize}");
                        return false;
                    }
                }

                var deviceDpi = dialog.DeviceDpi;
                var clientSize = dialog.ClientSize;

                dialog.Close();

                Log(10, $"均衡器对话框正常打开：{sliderCount} 条滑块，"
                        + $"DPI {deviceDpi}（{scale:0.##} 倍）客户区 {clientSize.Width}x{clientSize.Height}，"
                        + $"控件最大右边界 {worstRight} / 下边界 {worstBottom}，无越界");
            }

            return true;
        }
    }
}
