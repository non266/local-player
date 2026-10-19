// 字体 —— 私有字体样本：造样本、改名、取 GDI 真名（这一组是"字体到底有没有被 GDI 认出来"的地基）。
//
// 从 Program.Fonts.cs 按 ARCHITECTURE 17.2 的规矩整块搬过来：成员一个字没动。

namespace SmokeTest
{
    internal static partial class Program
    {
        /// <summary>
        /// 用某一个系统字体样本跑一遍这条检查。
        /// <para><paramref name="skip"/> 非空表示这一条这台机器上验不了（跳过）；
        /// <paramref name="retry"/> 为 true 表示"夹具撞车"，换个样本再来。</para>
        /// </summary>
        private static bool TryPrivateFontSample(
            Form form, Form window, string keepFamily, string systemFont, out bool retry, out string? skip)
        {
            retry = false;
            skip = null;

            // 文件名带上随机后缀：不留任何可能被上一次运行剩下的字体库文件顶掉的机会
            var renamed = Path.Combine(
                AppContext.BaseDirectory, "private-only-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".ttf");

            var alias = RenameFontFamily(systemFont, renamed);

            if (alias == null)
            {
                Log(15, "私有字体检查：改造字体文件的 family 名失败，跳过");
                return true;
            }

            try
            {
                if (UiFonts.InstalledFamilies.Contains(alias, StringComparer.OrdinalIgnoreCase))
                {
                    Log(15, $"私有字体检查：占位名「{alias}」居然是系统已安装字体，这台的样本不合适，跳过");
                    return true;
                }

                var filesBefore = UiFonts.ImportedFiles.ToList();

                if (!UiFonts.TryImportFontFile(renamed, out var families, out var importError))
                {
                    Log(15, "私有字体检查：改造后的字体文件导入失败（" + (importError ?? "没有说明") + "）");
                    return false;
                }


                var added = UiFonts.ImportedFiles
                    .Where(file => !filesBefore.Contains(file, StringComparer.OrdinalIgnoreCase))
                    .ToList();

                if (families is not { Length: > 0 } || !families.Contains(alias, StringComparer.OrdinalIgnoreCase))
                {
                    string inFile;

                    try
                    {
                        using var probe = new System.Drawing.Text.PrivateFontCollection();
                        probe.AddFontFile(renamed);
                        inFile = string.Join(" / ", probe.Families.Select(f => f.Name));
                    }
                    catch (Exception ex)
                    {
                        inFile = "读不出来（" + ex.Message + "）";
                    }

                    // "报出来的却是本次已经导入过的那个字体族" = GDI+ 把两个字体认成了同一个，
                    // 这是夹具撞车（换样本重试）；报出别的族名说明真的没装进去，必须失败。
                    if (IsKnownFixtureCollision(families))
                    {
                        Log(15, $"私有字体检查：样本 {Path.GetFileName(systemFont)} 被 GDI+ 认成了本次已导入的"
                                + $"「{string.Join(" / ", families ?? Array.Empty<string>())}」，换一个样本再试");
                        retry = true;
                        return false;
                    }

                    Log(15, $"私有字体检查：导入之后字体族名里没有「{alias}」"
                            + $"（返回的是 {string.Join(" / ", families ?? Array.Empty<string>())}，"
                            + $"而文件里的族名是 {inFile}）");
                    return false;
                }

                // ---- 1) GDI 认不认得这个字体族（侧栏歌词正是按名字走 GDI 的）----
                var font = UiFonts.Resolve(alias, 20f, FontStyle.Regular);

                if (font == null)
                {
                    Log(15, "私有字体检查：UiFonts.Resolve 没有给出字体");
                    return false;
                }

                string face;

                using (font)
                {
                    face = GdiFaceName(font);
                }

                if (!string.Equals(face, alias, StringComparison.OrdinalIgnoreCase))
                {
                    // 两种完全不同的情况，必须分开：
                    //  1) 这个族名压根没进应用的字体表 —— GDI+ 把"改造过的字体"并进了本次已装过的
                    //     那个族（改名字改不干净，GDI+ 靠字体内部标识认人）。这是夹具撞车，换样本。
                    //  2) 族名<b>在表里</b>，GDI 却把字画成别的字体 —— 这才是那条真 bug：
                    //     GDI+ 认得、GDI 不认得，于是"桌面歌词换了字体、侧栏歌词纹丝不动"。
                    var aliasKnown = UiFonts.ImportedFamilies
                        .Contains(alias, StringComparer.OrdinalIgnoreCase);

                    if (!aliasKnown)
                    {
                        Log(15, $"私有字体检查：样本 {Path.GetFileName(systemFont)} 被 GDI+ 并进了已装过的字体族"
                                + $"（应用字体表里没有「{alias}」），换一个样本再试");
                        retry = true;
                        return false;
                    }

                    Log(15, $"私有字体检查：GDI 认不出导入的字体族「{alias}」，"
                            + $"它会把字画成「{face}」——侧栏歌词（TextRenderer）就是这么画不对的");
                    return false;
                }

                // ---- 2) 端到端：侧栏歌词换字体之后，画出来的像素必须真的变 ----
                var sidebar = Find(form, "sidebarPanel");
                var lyricsView = sidebar == null ? null : FindByTypeName(sidebar, "LyricsView");

                if (lyricsView == null)
                {
                    Log(15, "私有字体检查：找不到侧栏歌词控件");
                    return false;
                }

                // 换一段拉丁文字的歌词：中文字形在英文字体里本来就会逐字回退，看不出区别
                var view = (LyricsView)lyricsView;
                var previousDocument = view.Document;

                view.SetDocument(LyricsDocument.Parse("[00:01.00]Sidebar Font Sample\n[00:04.00]Second Line 123"));

                PumpMessages(200);

                ((播放器.MainForm)form).SetLyricsFont(string.Empty);
                PumpMessages(250);

                var before = RenderControl(lyricsView);
                var beforeFace = GdiFaceName(lyricsView.Font);

                ((播放器.MainForm)form).SetLyricsFont(alias);
                PumpMessages(250);

                var after = RenderControl(lyricsView);
                var afterFace = GdiFaceName(lyricsView.Font);

                double difference = 0;

                if (before != null && after != null) difference = MeanPixelDifference(before, after);

                before?.Dispose();
                after?.Dispose();

                // 收尾：把字体和歌词都还原，别影响后面的检查；导入的这一份也删掉，
                // 免得它留在字体库里影响下一次运行
                ((播放器.MainForm)form).SetLyricsFont(keepFamily);
                view.SetDocument(previousDocument);
                PumpMessages(200);

                foreach (var file in added)
                    UiFonts.TryRemoveFontFile(file, out _);

                if (difference < 0.5)
                {
                    Log(15, $"私有字体检查：换成导入的「{alias}」之后侧栏歌词画出来几乎没变"
                            + $"（像素差 {difference:0.00}）——字体没落到侧栏上");
                    return false;
                }

                Log(15, $"私有字体检查正常：系统里不存在的字体族「{alias}」导入后 GDI 也认得"
                        + $"（TextRenderer 实际用的字面：默认 {beforeFace} → 换字体后 {afterFace}），"
                        + $"侧栏歌词重绘像素差 {difference:0.00}");
                return true;
            }
            finally
            {
                TryDelete(renamed);
            }
        }
    }
}
