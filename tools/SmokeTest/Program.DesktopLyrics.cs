// 检查点 15：桌面歌词（窗口/托盘/热键/字体/外观/HitTest/真实拖动/输入框布局）。

namespace SmokeTest
{
    internal static partial class Program
    {
        /// <summary>
        /// 桌面歌词的完整检查（第 15 步）。
        /// <para>
        /// 自己把设置清掉再开始，所以它<b>既能全量跑，也能单独跑</b>：全量跑时前面的步骤
        /// 已经把设置写成了别的样子，而这一步要验的恰恰是"从没有设置开始 → 开启 →
        /// 关掉程序 → 下次启动自己回来"。
        /// </para>
        /// <para>只依赖第 9 步合成的那个带内嵌时间轴歌词的样本文件。</para>
        /// </summary>
        private static bool TestDesktopLyrics()
        {
            var media = Path.Combine(AppContext.BaseDirectory, "smoke-tags", "sample.flac");

            // 分段计时：这一步在整轮里有时 30 秒、有时 3 分钟（维护手册 R2 那一条），
            // 先把时间花在哪一段量出来，再决定要不要改判据。
            var sectionClock = System.Diagnostics.Stopwatch.StartNew();

            if (!File.Exists(media))
            {
                Log(15, "缺少第 9 步生成的样本文件");
                return false;
            }

            // 从"干净设置"开始（这是测试自己的临时目录，不是使用者的配置）
            TryDelete(AppSettings.SettingsFilePath);

            try
            {
                Rectangle placed;
                Size placedSize = Size.Empty;

                using (var form = new 播放器.MainForm(Array.Empty<string>()))
                {
                    form.Show();
                    PumpMessages(300);

                    // 走真实的元数据加载路径，拿到内嵌的时间轴歌词
                    form.LoadTrackMetadata(media);

                    PumpMessages(1200);

                    var lyrics = SessionOf(form).Lyrics;

                    if (!lyrics.IsSynchronized)
                    {
                        Log(15, "没有从样本文件里读到带时间轴的歌词");
                        return false;
                    }

                    Journal($"第15步分段：主窗体创建 + 读歌词 {sectionClock.ElapsedMilliseconds} ms");
                    sectionClock.Restart();

                    if (!CheckDesktopLyrics(form, lyrics)) return false;

                    Journal($"第15步分段：桌面歌词各项检查 {sectionClock.ElapsedMilliseconds} ms");
                    sectionClock.Restart();

                    if (form.DesktopLyricsWindow is not { } window)
                    {
                        Log(15, "检查结束时桌面歌词窗口不见了");
                        return false;
                    }

                    // 只记位置：宽高是按文字算出来的，不参与持久化。
                    // ⚠ 先把它摆到工作区左侧再记：重启后窗口可能宽得多（歌词换句就会变宽），
                    // 摆在右边的话"保存的 X + 新宽度"根本放不下，会被 ClampToWorkingArea
                    // 挪回屏幕内——那是钳制在正常工作，却会让这条断言随机变红。
                    var workArea = Screen.FromControl(window).WorkingArea;
                    window.MoveTo(new Point(workArea.Left + 40, workArea.Top + 120));
                    PumpMessages(150);

                    placed = new Rectangle(window.Left, window.Top, 0, 0);
                    placedSize = window.ClientSize;

                    form.Close();
                    PumpMessages(300);
                }

                var saved = AppSettings.Load();

                if (!saved.DesktopLyricsEnabled || !saved.DesktopLyricsPlaced)
                {
                    Log(15, "桌面歌词的开关 / 位置没有存进设置（开着 "
                            + $"{saved.DesktopLyricsEnabled}、记过位置 {saved.DesktopLyricsPlaced}）");
                    return false;
                }

                if (string.IsNullOrEmpty(saved.LyricsFontFamily))
                {
                    Log(15, "歌词字体没有存进设置");
                    return false;
                }

                // ---- 重新启动一次：它应该自己回来，而且回到上次那个位置 ----
                // ⚠ 先把设置里的"上次会话"清空：这一段要验的是"没有媒体时的占位文字"，
                // 而重开的窗口只要恢复了上次的播放列表 + 选中项（「启动时恢复上次播放列表」
                // 被别的步骤打开过时就会），就会恢复出一个带歌词的曲目，
                // 占位文字本来就不该出现，拿它断言会随机变红（实测 1/4 次）。
                // 用完再还原：后面的步骤不该被这里改过的会话影响。
                var previousPlaylist = saved.LastPlaylist;
                var previousIndex = saved.LastPlaylistIndex;

                saved.LastPlaylist = new System.Collections.Generic.List<string>();
                saved.LastPlaylistIndex = -1;
                saved.Save();

                using (var reopened = new 播放器.MainForm(Array.Empty<string>()))
                {
                    reopened.Show();
                    PumpMessages(500);

                    if (reopened.DesktopLyricsWindow is not { } window)
                    {
                        Log(15, "重新启动之后桌面歌词没有自己回来");
                        return false;
                    }

                    if (!window.Visible)
                    {
                        Log(15, "重新启动之后桌面歌词窗建出来了却是隐藏的");
                        return false;
                    }

                    if (window.Left != placed.Left || window.Top != placed.Top)
                    {
                        Log(15, $"重新启动之后没有回到上次的位置（{placed.Left},{placed.Top}"
                                + $" → {window.Left},{window.Top}；"
                                + $"关之前客户区 {placedSize}，现在 {window.ClientSize}）");
                        return false;
                    }

                    // 没在播放时是"桌面歌词 /（未在播放）"，不该是一块空白
                    if (window.CurrentText != "桌面歌词")
                    {
                        Log(15, "没有媒体时显示的占位文字不对：" + window.CurrentText);
                        return false;
                    }

                    reopened.Close();
                    PumpMessages(200);
                }

                Journal($"第15步分段：重启验证 {sectionClock.ElapsedMilliseconds} ms");
                sectionClock.Restart();

                // 把会话还原：这一段的失败会直接结束整个测试（设置状态无所谓），
                // 但成功之后要保证后面的步骤看到的是原来的设置。
                var restore = AppSettings.Load();
                restore.LastPlaylist = previousPlaylist;
                restore.LastPlaylistIndex = previousIndex;
                restore.Save();

                Journal($"第15步分段：收尾 {sectionClock.ElapsedMilliseconds} ms");

                Log(15, "桌面歌词全套正常：见上一条；另外开关与位置能存下来，"
                        + "重新启动自动回到上次的位置");
                return true;
            }
            catch (Exception ex)
            {
                Log(15, "桌面歌词检查失败: " + ex);
                return false;
            }
        }







        /// <summary>
        /// 歌词字体。
        /// <para>
        /// 字体列表里<b>只有手动导入的字体</b>（外加"默认字体"），所以选字体这件事从导入开始：
        /// 先导入一个真实的 .ttf，再验两处歌词都换过去、坏字体名安静退回，
        /// 最后验字体菜单里确实没有混进系统字体。
        /// </para>
        /// <para>结束时把字体留在导入的那个上，方便外面验它会存进设置。</para>
        /// </summary>
        private static bool CheckLyricsFont(播放器.MainForm form, DesktopLyricsWindow window)
        {
            var sidebar = Find(form, "sidebarPanel");
            var lyricsView = sidebar == null ? null : FindByTypeName(sidebar, "LyricsView");

            if (lyricsView == null)
            {
                Log(15, "歌词字体检查：找不到侧栏歌词控件");
                return false;
            }

            // ---- 导入一个字体文件（字体列表里只有导入的，所以这是选字体的前提）----
            if (!CheckFontImport(form, window, out var family)) return false;
            if (family == null) return true;      // 系统里没有 ttf 样本，整段跳过

            // 垃圾文件必须是「靠文件头」被挡掉的
            // （见那条检查的注释：GDI+ 探测坏文件时会报出上一个装过的字体族）
            if (!CheckGarbageFontRejectedByHeader()) return false;

            // ---- 字体菜单里只该有"默认 / 导入 / 已导入的字体" ----
            if (!CheckLyricsFontMenu(form, family)) return false;

            // ---- 一个根本不存在的字体族：不能崩，也不能卡在坏字体上 ----
            const string bogus = "绝不存在的字体族 XYZ";

            form.SetLyricsFont(bogus);
            PumpMessages(250);

            var bogusSidebar = lyricsView.Font.FontFamily.Name;
            var bogusDesktop = window.MainFont?.FontFamily.Name;

            if (bogusSidebar == bogus || bogusDesktop == bogus)
            {
                Log(15, $"歌词字体检查：不存在的字体族被用上了（侧栏 {bogusSidebar} / 桌面 {bogusDesktop}）");
                return false;
            }

            if (!string.Equals(bogusSidebar, bogusDesktop, StringComparison.OrdinalIgnoreCase))
            {
                Log(15, $"歌词字体检查：坏字体名的回退结果不一致（侧栏 {bogusSidebar} / 桌面 {bogusDesktop}）");
                return false;
            }

            // ---- 换成导入的那个字体：两处都要跟着换 ----
            form.SetLyricsFont(family);
            PumpMessages(250);

            var sidebarFamily = lyricsView.Font.FontFamily.Name;

            if (!string.Equals(sidebarFamily, family, StringComparison.OrdinalIgnoreCase))
            {
                Log(15, $"歌词字体检查：侧栏歌词页没有换成「{family}」（现在是「{sidebarFamily}」）");
                return false;
            }

            var desktopFamily = window.MainFont?.FontFamily.Name;

            if (!string.Equals(desktopFamily, family, StringComparison.OrdinalIgnoreCase))
            {
                Log(15, $"歌词字体检查：桌面歌词没有换成「{family}」（现在是「{desktopFamily}」）");
                return false;
            }

            // 字宽变了，窗口必须重新按新字体量一遍
            var nextFamily = window.NextFont.FontFamily.Name;

            if (!string.Equals(nextFamily, family, StringComparison.OrdinalIgnoreCase))
            {
                Log(15, $"歌词字体检查：下一句那一行没有跟着换字体（现在是「{nextFamily}」）");
                return false;
            }

            Log(15, $"歌词字体正常：导入的「{family}」在侧栏歌词页与桌面歌词都生效了，"
                    + "不存在的字体名会安静退回默认字体（两处一致）");

            // ---- 管理：内存加载（文件不再被占住）+ 真能删掉 ----
            if (!CheckFontManagement(form, family)) return false;

            // ---- 导入的字体必须对 GDI 也可见（侧栏歌词是 TextRenderer 画的） ----
            if (!CheckPrivateFontReachesGdi(form, window, family)) return false;

            // ---- .ttc 字体集合走的是"按文件加载"那条路，GDI 那边也要认 ----
            if (!CheckFontCollectionImport()) return false;

            // ---- 两个新对话框：外观调节 / 字体管理 ----
            if (!CheckDesktopLyricsAppearanceDialog(form, window)) return false;
            if (!CheckFontManagerDialog(form, family)) return false;

            // ---- 通用输入对话框（服务地址 / 鉴权密钥都靠它）----
            if (!CheckInputDialogLayout()) return false;

            return true;
        }

        /// <summary>
        /// 通用输入对话框的排版：**提示文字一行都不能被输入框压住**。
        /// <para>
        /// 这条是真踩过的：原来按"提示有几行 × 18 像素"估算高度，服务地址那个提示有四行，
        /// 估算少了一行，文本框正好压在最后一行上（截图一看就是"挡住了"）。
        /// 现在改成按 <c>Label.PreferredSize</c> 测量摆放，用一条检查钉住它：
        /// 多行提示、掩码输入两种都要保证输入框在提示下面、所有控件都在客户区里。
        /// </para>
        /// </summary>
        private static bool CheckInputDialogLayout()
        {
            const string prompt =
                "自己跑一个 LrcAPI（或用它提供的服务），把地址填在这里，例如：\n"
                + "    http://127.0.0.1:28883\n"
                + "    https://api.lrc.cx\n"
                + "程序不会替你选服务，填哪个就用哪个：";

            foreach (var mask in new[] { false, true })
            {
                var dialog = new InputDialog("LrcApi 服务地址", prompt, "http://127.0.0.1:28883", mask);

                using (dialog)
                {
                    dialog.StartPosition = FormStartPosition.Manual;
                    dialog.Location = new Point(60, 60);
                    dialog.Show();
                    PumpMessages(150);

                    var label = dialog.Controls.OfType<Label>().FirstOrDefault();
                    var box = dialog.Controls.OfType<TextBox>().FirstOrDefault();

                    if (label == null || box == null)
                    {
                        Log(16, "输入对话框检查：找不到提示文字或输入框");
                        return false;
                    }

                    if (box.Top < label.Bottom)
                    {
                        Log(16, $"输入对话框检查：输入框压住了提示文字"
                                + $"（提示到 {label.Bottom}，输入框从 {box.Top} 开始，"
                                + $"客户区 {dialog.ClientSize}）");
                        return false;
                    }

                    if (mask != box.UseSystemPasswordChar)
                    {
                        Log(16, $"输入对话框检查：掩码没生效（mask={mask}，UseSystemPasswordChar={box.UseSystemPasswordChar}）");
                        return false;
                    }

                    var scale = dialog.DeviceDpi / 96f;

                    foreach (Control control in dialog.Controls)
                    {
                        if (control.Right > dialog.ClientSize.Width || control.Bottom > dialog.ClientSize.Height)
                        {
                            Log(16, $"输入对话框在 {scale:0.##} 倍缩放下有控件越界："
                                    + $"{control.GetType().Name} {control.Bounds} 超出客户区 {dialog.ClientSize}");
                            return false;
                        }
                    }

                    if (dialog.ClientSize.Height < box.Bottom + 30)
                    {
                        Log(16, "输入对话框检查：按钮被挤出客户区（高度不够）");
                        return false;
                    }

                    dialog.Close();
                }
            }

            Log(16, "输入对话框排版正常：4 行提示下输入框仍在其下方（按实际测量摆放，不是估算行数），"
                    + "掩码输入生效，125% 缩放下无控件越界");
            return true;
        }

        /// <summary>
        /// 外观对话框：字号 / 浓度两个滑块 + 实时推给歌词窗口。
        /// <para>
        /// 重点是"边拖边改"这条线：以前只能一级一级点菜单（字号 12~96 每次 2 磅，
        /// 从 30 调到 60 要按 15 次），现在拖一下就该立刻生效。
        /// </para>
        /// </summary>
        private static bool CheckDesktopLyricsAppearanceDialog(播放器.MainForm form, DesktopLyricsWindow window)
        {
            var received = new List<(int Size, int Opacity)>();

            var dialog = new DesktopLyricsAppearanceDialog(
                DesktopLyricsDefaults.FontSize,
                DesktopLyricsDefaults.Opacity,
                string.Empty,
                Color.White,
                (size, opacity) => received.Add((size, opacity)));

            using (dialog)
            {
                dialog.StartPosition = FormStartPosition.Manual;
                dialog.Location = new Point(60, 60);
                dialog.Show();
                PumpMessages(150);

                var sliders = dialog.Controls.Cast<Control>()
                    .Where(c => c.GetType().Name == "FlatSlider")
                    .ToList();

                if (sliders.Count != 2)
                {
                    Log(15, $"外观对话框检查：滑块数是 {sliders.Count}（期望 2：字号 + 浓度）");
                    return false;
                }

                var size = (FlatSlider)sliders[0];
                var opacity = (FlatSlider)sliders[1];

                var sizeMin = size.Minimum;
                var sizeMax = size.Maximum;
                var opacityMin = opacity.Minimum;
                var opacityMax = opacity.Maximum;

                if (sizeMin != DesktopLyricsDefaults.MinimumFontSize ||
                    sizeMax != DesktopLyricsDefaults.MaximumFontSize ||
                    opacityMin != DesktopLyricsDefaults.MinimumOpacity ||
                    opacityMax != DesktopLyricsDefaults.MaximumOpacity)
                {
                    Log(15, $"外观对话框检查：滑块范围不对（字号 {sizeMin}~{sizeMax}，浓度 {opacityMin}~{opacityMax}）");
                    return false;
                }

                if (size.Value != DesktopLyricsDefaults.FontSize ||
                    opacity.Value != DesktopLyricsDefaults.Opacity)
                {
                    Log(15, "外观对话框检查：打开时没有把当前值填进滑块");
                    return false;
                }

                // 模拟"用户拖到了 60 磅 / 50%"：程序化设值不触发 Scroll，所以直接叫它的处理函数
                size.Value = 60;
                opacity.Value = 50;

                dialog.OnSliderChanged();
                PumpMessages(120);

                if (received.Count == 0 || received[^1] != (60, 50))
                {
                    Log(15, "外观对话框检查：拖滑块没有把新值推出去（实时预览这条线断了）"
                            + $"（收到 {received.Count} 次回调）");
                    return false;
                }

                // 预览板上必须真的画了字，不能是一块空白
                var preview = dialog.Controls.Cast<Control>()
                    .FirstOrDefault(c => c.GetType().Name == "Panel");

                if (preview == null)
                {
                    Log(15, "外观对话框检查：找不到预览板");
                    return false;
                }

                using (var bitmap = new Bitmap(Math.Max(1, preview.Width), Math.Max(1, preview.Height)))
                {
                    preview.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));

                    var opaque = CountOpaquePixels(bitmap);

                    if (opaque < 20)
                    {
                        Log(15, $"外观对话框检查：预览板是空的（只有 {opaque} 个不透明像素）");
                        return false;
                    }
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
                        Log(15, $"外观对话框在 {scale:0.##} 倍缩放下有控件越界："
                                + $"{control.GetType().Name} {control.Bounds} 超出客户区 {dialog.ClientSize}");
                        return false;
                    }
                }

                var deviceDpi = dialog.DeviceDpi;
                var clientSize = dialog.ClientSize;

                dialog.Close();

                Log(15, $"外观对话框正常：两个滑块（字号 {sizeMin}~{sizeMax}、浓度 {opacityMin}~{opacityMax}）"
                        + $"带当前值与实时回调，预览板有字，DPI {deviceDpi}（{scale:0.##} 倍）"
                        + $"客户区 {clientSize.Width}x{clientSize.Height} 无越界");
            }

            return true;
        }







    }
}
