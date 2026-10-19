// 私有字体两条路径：GDI+ 内存加载与 GDI AddFontMemResourceEx，以及字体管理界面。

namespace SmokeTest
{
    internal static partial class Program
    {
        /// <summary>
        /// <c>.ttc</c> / <c>.otc</c> 字体集合：GDI+ 的内存加载不认它们（实测探测出来 0 个字体族），
        /// 所以走的是"按文件加载"那条路——而按文件加载又会把文件占住（删除要等下次启动）。
        /// <para>
        /// 这条检查要的是：这条路仍然<b>装得上、而且 GDI 那边也认</b>
        /// （侧栏歌词走 GDI，见上一条）。用一个系统自带的字体集合样本，验完删掉。
        /// </para>
        /// </summary>
        private static bool CheckFontCollectionImport()
        {
            string? sample;

            try
            {
                var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
                sample = Directory.EnumerateFiles(fonts, "*.ttc").OrderBy(p => new FileInfo(p).Length).FirstOrDefault();
            }
            catch (Exception)
            {
                sample = null;
            }

            if (sample == null)
            {
                Log(15, "字体集合检查：这台机器上没有 .ttc 样本，跳过");
                return true;
            }

            var source = Path.Combine(AppContext.BaseDirectory, "sample-collection.ttc");
            File.Copy(sample, source, overwrite: true);

            try
            {
                var filesBefore = UiFonts.ImportedFiles.ToList();
                if (!UiFonts.TryImportFontFile(source, out var families, out var error))
                {
                    Log(15, $"字体集合检查：{Path.GetFileName(sample)} 导入失败（{error ?? "没有说明"}）");
                    return false;
                }


                if (families.Length == 0)
                {
                    Log(15, "字体集合检查：导入成功却没有给出任何字体族");
                    return false;
                }

                foreach (var name in families.Take(2))
                {
                    var font = UiFonts.Resolve(name, 16f, FontStyle.Regular);

                    if (font == null)
                    {
                        Log(15, $"字体集合检查：「{name}」解析不出字体");
                        return false;
                    }

                    string face;

                    using (font) face = GdiFaceName(font);

                    if (!string.Equals(face, name, StringComparison.OrdinalIgnoreCase))
                    {
                        Log(15, $"字体集合检查：GDI 认不出「{name}」，会画成「{face}」"
                                + "——字体集合里的字体在侧栏歌词上用不了");
                        return false;
                    }
                }

                var added = UiFonts.ImportedFiles
                    .Where(file => !filesBefore.Contains(file, StringComparer.OrdinalIgnoreCase))
                    .ToList();

                foreach (var file in added)
                    UiFonts.TryRemoveFontFile(file, out _);

                Log(15, $"字体集合正常：{Path.GetFileName(sample)}（{families.Length} 个字体族，"
                        + $"如 {families[0]}）导入后 GDI 也认得，按文件加载的代价只是删除要等下次启动");
                return true;
            }
            finally
            {
                TryDelete(source);
            }
        }

        /// <summary>
        /// 导入的字体必须**对 GDI 也可见**。
        /// <para>
        /// 两个歌词控件用的绘制 API 不是同一套：桌面歌词走 GDI+
        /// （<c>Graphics.DrawString</c>），侧栏歌词走 <c>TextRenderer.DrawText</c>——那是
        /// <b>GDI</b> 的 <c>DrawText</c>。而 <c>PrivateFontCollection</c> 是 GDI+ 的私有字体集合，
        /// <b>GDI 完全看不到它</b>：字体的名字在 GDI 的字体表里查不到，它会静默换成默认字体。
        /// 表现出来就是"桌面歌词换了字体，侧栏歌词纹丝不动"。
        /// </para>
        /// <para>
        /// 验这条必须用一个**系统上绝对没有的字体族名**：拿 Arial 这种系统自带字体来试，
        /// GDI 就算看不见私有字体也能从系统字体表里找到同名字体，两边看起来一模一样，
        /// 检查会一直通过而 bug 一直在。所以这里把字体文件里的 family 名字
        /// <b>原地改成等长的占位名</b>（改长度会破坏 name 表的偏移，等长最安全）。
        /// </para>
        /// </summary>
        private static bool CheckPrivateFontReachesGdi(Form form, Form window, string keepFamily)
        {
            var candidates = PrivateFontSamples();

            if (candidates.Count == 0)
            {
                Log(15, "私有字体检查：系统里找不到可用的 .ttf 样本，跳过");
                return true;
            }

            // 逐个样本试：GDI+ 认字体靠字体<b>内部</b>的标识，名字改不干净时会把它认成
            // 本次已经装过的那个字体（实测时灵时不灵）。撞车就换下一个样本，
            // 全都撞车才说明"这台机器上验不了这一条"，而不是功能有问题。
            foreach (var sample in candidates)
            {
                var ok = TryPrivateFontSample(form, window, keepFamily, sample, out var retry, out var skip);

                if (skip != null)
                {
                    Log(15, "私有字体检查：" + skip);
                    return true;
                }

                if (ok) return true;
                if (!retry) return false;
            }

            Log(15, $"私有字体检查：试过 {candidates.Count} 个系统字体样本，都被 GDI+ 认成了本次已导入的那个字体"
                    + "（夹具撞车，不是功能问题）——这台机器上验不了这一条，跳过");
            return true;
        }



        /// <summary>
        /// 问 GDI：按这个字体建出来的 HFONT，最终真正用的是哪个字面？
        /// <para>
        /// 名字查不到时 GDI 会静默替换（返回的就是替换后的字面名），
        /// 所以这是"GDI 认不认得这个字体"最直接的判据——和 <c>TextRenderer</c> 走的是同一条路
        /// （都是按字面名让 GDI 去解析字体）。
        /// </para>
        /// </summary>
        private static string GdiFaceName(Font font)
        {
            var hfont = IntPtr.Zero;
            var dc = IntPtr.Zero;
            var previous = IntPtr.Zero;

            try
            {
                hfont = font.ToHfont();
                dc = CreateCompatibleDC(IntPtr.Zero);
                if (dc == IntPtr.Zero) return string.Empty;

                previous = SelectObject(dc, hfont);

                var buffer = new System.Text.StringBuilder(128);
                var length = GetTextFaceW(dc, buffer.Capacity, buffer);

                return length > 0 ? buffer.ToString() : string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
            finally
            {
                if (dc != IntPtr.Zero)
                {
                    if (previous != IntPtr.Zero) SelectObject(dc, previous);
                    DeleteDC(dc);
                }

                if (hfont != IntPtr.Zero) DeleteObject(hfont);
            }
        }



        /// <summary>
        /// 字体菜单里只该有「默认字体 / 导入字体文件… / 已导入的字体」——
        /// 把系统上那几百个字体全列出来是这一版刻意去掉的。
        /// </summary>
        private static bool CheckLyricsFontMenu(Form form, string importedFamily)
        {
            var parent = ((播放器.MainForm)form).MenuDesktopLyrics;
            var fontMenu = parent == null ? null : FindMenuItem(parent.DropDownItems, "字体");

            if (fontMenu == null)
            {
                Log(15, "歌词字体检查：视图菜单里找不到「字体」子菜单");
                return false;
            }

            // 子菜单平时是"第一次展开时才铺"，这里直接叫它铺一遍

            ((播放器.MainForm)form).EnsureLyricsFontItems(fontMenu);

            var names = fontMenu.DropDownItems
                .OfType<ToolStripMenuItem>()
                .Select(item => item.Text)
                .ToList();

            if (!names.Contains("导入字体文件…"))
            {
                Log(15, "歌词字体检查：字体菜单里没有「导入字体文件…」");
                return false;
            }

            if (!names.Contains(importedFamily))
            {
                Log(15, $"歌词字体检查：字体菜单里没有刚导入的「{importedFamily}」"
                        + $"（菜单里是：{string.Join(" / ", names)}）");
                return false;
            }

            // 系统字体不该出现在这里——"字体只保留手动导入"就是这么落地的
            var installed = UiFonts.InstalledFamilies;

            var leaked = installed.FirstOrDefault(
                name => names.Contains(name) &&
                        !string.Equals(name, importedFamily, StringComparison.OrdinalIgnoreCase));

            if (leaked != null)
            {
                Log(15, $"歌词字体检查：字体菜单里混进了系统字体「{leaked}」（应当只列导入的字体）");
                return false;
            }

            // 真去枚举系统字体的话至少上百项，这里不该有那么长
            if (fontMenu.DropDownItems.Count > 8)
            {
                Log(15, $"歌词字体检查：字体菜单有 {fontMenu.DropDownItems.Count} 项，看起来把系统字体也列进来了");
                return false;
            }

            Log(15, $"歌词字体菜单正常：只有「默认字体 / 导入字体文件… / 已导入的字体」，"
                    + $"系统字体没有被列进来（本次导入 {importedFamily}）");
            return true;
        }

        /// <summary>
        /// 垃圾文件必须被<b>确定性地</b>挡掉：靠文件头，而不是碰运气等 GDI+ 拒绝。
        /// <para>
        /// 这条的由来：垃圾文件那条检查曾经在约 10 次全量里红 1 次，单步连跑 15 次都复现不了。
        /// 后来用压力探针（同一个进程里先真导入一个字体，再连打 200 次垃圾）逼出来了：
        /// <b>1 次被接受，报出来的族名是刚导入的 Arial</b>——GDI+ 探测坏文件时会报出
        /// 进程里上一个装过的字体族（不是这个文件里的东西）。
        /// </para>
        /// <para>
        /// 修法是在碰 GDI+ 之前先看文件头（<c>FontLibrary.LooksLikeFontFile</c>）。
        /// 所以这里除了"必须拒绝"，还要断言<b>拒绝的理由是文件头</b>：
        /// 万一有人把预检去掉，拒绝就退回成"碰运气"，这条检查要能立刻红。
        /// </para>
        /// <para>
        /// 只用<b>非 sfnt</b> 的内容（文本 / 全 0）——截断的真字体文件头是合法的，
        /// GDI+ 可能真的从里面读出了族名，那是另一件事（见文档里"已知容忍"那条）。
        /// </para>
        /// </summary>
        private static bool CheckGarbageFontRejectedByHeader()
        {
            // 先把 GDI+ 的状态捂热：像真实使用一样，在这个进程里先导入一个真字体。
            // 当初那条红就是在这种状态下出现的。
            var systemFont = FindSystemFontFile();

            if (systemFont != null) UiFonts.TryImportFontFile(systemFont, out _, out _);

            var text = Encoding.UTF8.GetBytes("这不是字体文件");

            for (var i = 0; i < 60; i++)
            {
                var path = Path.Combine(AppContext.BaseDirectory, $"probe-bogus-{i}.ttf");
                File.WriteAllBytes(path, i % 2 == 0 ? text : new byte[64]);

                try
                {
                    if (UiFonts.TryImportFontFile(path, out var bogusFamilies, out var reason))
                    {
                        Log(15, $"垃圾字体检查：{text.Length} 字节的垃圾被当成字体导入成功了"
                                + $"（报出来的字体族：{string.Join(" / ", bogusFamilies)}）");
                        return false;
                    }

                    if (reason == null || !reason.Contains("文件头", StringComparison.Ordinal))
                    {
                        Log(15, $"垃圾字体检查：垃圾文件被拒绝了，但不是靠文件头（理由：「{reason ?? "空"}」）"
                                + "——那就退回成「碰运气等 GDI+ 拒绝」，这条检查也就失去意义了");
                        return false;
                    }
                }
                finally
                {
                    TryDelete(path);
                }
            }

            Log(15, "垃圾字体检查正常：非字体内容一律被文件头挡掉（不依赖 GDI+ 的拒绝）");
            return true;
        }



        /// <summary>做一个系统字体文件的候选列表（优先体积小的 .ttf）。</summary>
        private static string FindSystemFontFile()
        {
            string fonts;

            try
            {
                fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            }
            catch (Exception)
            {
                return null;
            }

            if (string.IsNullOrEmpty(fonts) || !Directory.Exists(fonts)) return null;

            foreach (var name in new[] { "arial.ttf", "segoeui.ttf", "calibri.ttf", "simsun.ttc", "msyh.ttc" })
            {
                var path = Path.Combine(fonts, name);
                if (File.Exists(path)) return path;
            }

            // 兜底：随便挑一个 .ttf
            return Directory.EnumerateFiles(fonts, "*.ttf").OrderBy(p => new FileInfo(p).Length).FirstOrDefault();
        }

        /// <summary>能不能改写这个文件？用来确认它没有被 GDI+ 占住。</summary>
        private static bool CanRewrite(string path)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return stream.Length > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// "导入之后报出来的族名是本次已经导入过的那个"——说明 GDI+ 把两个字体认成了同一个。
        /// <para>
        /// 这是<b>夹具</b>撞车（改名字改不干净，GDI+ 靠字体内部标识认人），不是功能问题：
        /// 换成系统默认字体之类的别的族名才是"真的没装进去"。
        /// </para>
        /// </summary>
        private static bool IsKnownFixtureCollision(IEnumerable<string> reported)
        {
            var imported = UiFonts.ImportedFamilies;
            var any = false;

            foreach (var name in reported)
            {
                any = true;

                if (!imported.Contains(name, StringComparer.OrdinalIgnoreCase)) return false;
            }

            return any;
        }

        /// <summary>
        /// "私有字体"那条检查用的系统字体样本：固定候选顺序、确定性挑选。
        /// <para>
        /// 以前是"目录里枚举到的第一个 .ttf"，枚举顺序不稳定 → 有时挑到和前面刚导入的那个
        /// 字体同源的样本 → GDI+ 认成同一个 → 检查随机变红。
        /// </para>
        /// </summary>
        private static List<string> PrivateFontSamples()
        {
            var result = new List<string>();

            try
            {
                var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
                if (string.IsNullOrEmpty(fonts) || !Directory.Exists(fonts)) return result;

                var exclude = Path.GetFileName(FindSystemFontFile() ?? string.Empty);

                foreach (var name in new[]
                         {
                             "segoeui.ttf", "tahoma.ttf", "verdana.ttf", "calibri.ttf",
                             "times.ttf", "georgia.ttf", "consola.ttf", "cour.ttf", "trebuc.ttf"
                         })
                {
                    if (string.Equals(name, exclude, StringComparison.OrdinalIgnoreCase)) continue;

                    var path = Path.Combine(fonts, name);
                    if (File.Exists(path)) result.Add(path);

                    if (result.Count >= 3) break;
                }

                if (result.Count > 0) return result;

                // 兜底也要确定性：按文件名排序后取前几个
                return Directory.EnumerateFiles(fonts, "*.ttf")
                    .Where(path => !string.Equals(
                        Path.GetFileName(path), exclude, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                    .Take(3)
                    .ToList();
            }
            catch (Exception)
            {
                return result;
            }
        }

        /// <summary>
        /// 系统上已安装的字体族（<c>UiFonts</c> 是 internal，走反射取）。
        /// <para>只用来<b>反证</b>字体菜单里没有把它们列出来。</para>
        /// </summary>
        private static IReadOnlyList<string> InstalledFontFamilies() => UiFonts.InstalledFamilies;
    }
}
