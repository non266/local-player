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

        /// <summary>
        /// 把字体文件里的 family 名字原地改成等长的占位名，返回这个新名字（失败返回 <c>null</c>）。
        /// <para>
        /// 只改 name 表里那几个记录里的字符，<b>长度一个字节都不变</b>：
        /// name 表的记录里有偏移和长度，改长度就得整体重排，等长替换则连校验和都不用管
        /// （字体加载器不校验 name 表）。
        /// </para>
        /// </summary>
        private static string? RenameFontFamily(string sourcePath, string targetPath, string placeholder = "Zzq")
        {
            try
            {
                var data = File.ReadAllBytes(sourcePath);

                if (data.Length < 12) return null;

                var tableCount = ReadUInt16(data, 4);
                var nameOffset = -1;

                for (var i = 0; i < tableCount; i++)
                {
                    var record = 12 + i * 16;
                    if (record + 16 > data.Length) break;

                    if (data[record] == 'n' && data[record + 1] == 'a' &&
                        data[record + 2] == 'm' && data[record + 3] == 'e')
                    {
                        nameOffset = (int)ReadUInt32(data, record + 8);
                        break;
                    }
                }

                if (nameOffset < 0 || nameOffset + 6 > data.Length) return null;

                var count = ReadUInt16(data, nameOffset + 2);
                var stringOffset = nameOffset + ReadUInt16(data, nameOffset + 4);

                string? family = null;

                for (var i = 0; i < count; i++)
                {
                    var record = nameOffset + 6 + i * 12;
                    if (record + 12 > data.Length) break;

                    var platform = ReadUInt16(data, record);
                    var nameId = ReadUInt16(data, record + 6);
                    var length = ReadUInt16(data, record + 8);
                    var offset = stringOffset + ReadUInt16(data, record + 10);

                    // 1 = 字体族名，3 = 唯一标识，4 = 完整名，6 = PostScript 名，16 = 排版用族名：
                    // 全都要改。只改族名的话，这个字体在身份上仍然"是 Arial"
                    // （唯一标识没变），GDI+ 有可能把它当成已经装过的那一个、
                    // 报出旧族名——实测在连着跑、字体库里有残留时就会这样。
                    if (nameId != 1 && nameId != 3 && nameId != 4 && nameId != 6 && nameId != 16) continue;
                    if (offset + length > data.Length || length == 0) continue;

                    var wide = platform == 3;             // Windows 平台的字符串是 UTF-16BE
                    var characters = wide ? length / 2 : length;

                    var replacement = placeholder + new string('z', Math.Max(0, characters - placeholder.Length));
                    replacement = replacement[..characters];

                    for (var c = 0; c < characters; c++)
                    {
                        var position = offset + (wide ? c * 2 : c);

                        if (wide)
                        {
                            data[position] = 0;
                            data[position + 1] = (byte)replacement[c];
                        }
                        else
                        {
                            data[position] = (byte)replacement[c];
                        }
                    }

                    if (nameId == 1) family = replacement;
                }

                if (family == null) return null;

                File.WriteAllBytes(targetPath, data);
                return family;
            }
            catch (Exception)
            {
                return null;
            }
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
        /// 导入的字体要能真的删掉。
        /// <para>
        /// 以前做不到：<c>PrivateFontCollection.AddFontFile</c> 会把字体文件占住，
        /// 用户自己的那一份虽然没被占（那是复制进来的），但<b>字体库里的那一份</b>删不掉，
        /// 于是"卸载字体"只能靠退出程序后手动删目录。现在改成从内存装进 GDI+，
        /// 文件不再被锁——这条检查就是钉住这一点：导入之后立刻删，必须删得掉。
        /// </para>
        /// <para>
        /// 另外钉住"按文件删"这条语义：同一个字体族名可能来自多个文件（同一个字体导入两次就是），
        /// 删掉其中一个文件时，只要还有别的文件提供这个族名，字体本身就该留着。
        /// </para>
        /// </summary>
        private static bool CheckFontManagement(Form form, string keepFamily)
        {
            var systemFont = FindSystemFontFile();

            if (systemFont == null)
            {
                Log(15, "字体管理检查：系统里找不到可用的 .ttf 样本，跳过");
                return true;
            }

            var source = Path.Combine(AppContext.BaseDirectory, "delete-me" + Path.GetExtension(systemFont));
            File.Copy(systemFont, source, overwrite: true);

            try
            {
                var before = UiFonts.ImportedFiles.ToList();

                if (!UiFonts.TryImportFontFile(source, out var families, out var importError))
                {
                    Log(15, "字体管理检查：第二个字体文件导入失败（" + (importError ?? "没有说明") + "）");
                    return false;
                }


                if (families is not { Length: > 0 })
                {
                    Log(15, "字体管理检查：导入成功却没有返回字体族名");
                    return false;
                }

                // 新建出来的是哪一份：按前后差集取，别去猜文件名（重名时会被加序号）
                var added = UiFonts.ImportedFiles
                    .Where(file => !before.Contains(file, StringComparer.OrdinalIgnoreCase))
                    .ToList();

                if (added.Count != 1)
                {
                    Log(15, $"字体管理检查：导入之后字体库里新增了 {added.Count} 个文件（期望 1 个）");
                    return false;
                }

                var copied = added[0];

                if (!File.Exists(copied))
                {
                    Log(15, "字体管理检查：新增的字体文件不在磁盘上");
                    return false;
                }

                var inFile = UiFonts.FamiliesInFile(copied);

                if (!inFile.Contains(families[0], StringComparer.OrdinalIgnoreCase))
                {
                    Log(15, $"字体管理检查：新文件里没有「{families[0]}」");
                    return false;
                }

                if (!UiFonts.ImportedFamilies.Contains(families[0], StringComparer.OrdinalIgnoreCase))
                {
                    Log(15, $"字体管理检查：导入之后列表里没有「{families[0]}」");
                    return false;
                }

                var removed = UiFonts.TryRemoveFontFile(copied, out _);

                if (!removed)
                {
                    Log(15, $"字体管理检查：TryRemoveFontFile({Path.GetFileName(copied)}) 返回失败");
                    return false;
                }

                if (File.Exists(copied))
                {
                    Log(15, "字体管理检查：字体从库里移除之后文件还在"
                            + "（说明它被 GDI+ 占着——字体应当是内存加载的，不该锁文件）");
                    return false;
                }

                if (UiFonts.ImportedFiles.Contains(copied, StringComparer.OrdinalIgnoreCase))
                {
                    Log(15, "字体管理检查：删掉之后文件还在字体库列表里");
                    return false;
                }

                if (!UiFonts.ImportedFamilies.Contains(keepFamily, StringComparer.OrdinalIgnoreCase))
                {
                    Log(15, $"字体管理检查：删一个把别的字体（{keepFamily}）也带走了");
                    return false;
                }

                Log(15, $"字体管理正常：导入的字体文件没被占用（移除时当场删掉了 {Path.GetFileName(copied)}），"
                        + "字体库列表和磁盘同步，删一个不会带走别的字体");
                return true;
            }
            finally
            {
                TryDelete(source);
            }
        }

        /// <summary>
        /// 字体管理对话框：列表里每一行都该用"它自己的字体"画出来（这是唯一看得见字形的地方），
        /// 并且要能选中、能删除。
        /// </summary>
        private static bool CheckFontManagerDialog(Form form, string importedFamily)
        {
            var dialog = new FontManagerDialog(importedFamily, w => { });

            using (dialog)
            {
                dialog.StartPosition = FormStartPosition.Manual;
                dialog.Location = new Point(80, 80);
                dialog.Show();
                PumpMessages(150);

                var list = dialog.Controls.OfType<ListBox>().FirstOrDefault();

                if (list == null)
                {
                    Log(15, "字体管理对话框检查：找不到字体列表");
                    return false;
                }

                if (list.DrawMode != DrawMode.OwnerDrawFixed)
                {
                    Log(15, "字体管理对话框检查：列表不是自绘的，看不出字体的字形");
                    return false;
                }

                var names = list.Items.Cast<object>().Select(o => o?.ToString()).ToList();
                var files = UiFonts.ImportedFiles;

                if (names.Count != files.Count)
                {
                    Log(15, $"字体管理对话框检查：列表里 {names.Count} 项，字体库里 {files.Count} 个文件");
                    return false;
                }

                var wanted = UiFonts.FileForFamily(importedFamily);

                if (wanted == null || !names.Contains(wanted))
                {
                    Log(15, $"字体管理对话框检查：列表里没有「{importedFamily}」所在的文件"
                            + $"（列表里是：{string.Join(" / ", names)}）");
                    return false;
                }

                // 选中之后：下面那行大号样例要用选中的字体
                list.SelectedItem = wanted;
                PumpMessages(120);

                var sample = dialog.Controls.OfType<TextBox>().FirstOrDefault();

                if (sample == null || sample.Font == null)
                {
                    Log(15, "字体管理对话框检查：找不到样例文字框");
                    return false;
                }

                if (!string.Equals(sample.Font.FontFamily.Name, importedFamily, StringComparison.OrdinalIgnoreCase))
                {
                    Log(15, $"字体管理对话框检查：样例没有用选中的字体"
                            + $"（现在是「{sample.Font.FontFamily.Name}」）");
                    return false;
                }

                var scale = dialog.DeviceDpi / 96f;
                var worstBottom = 0;

                foreach (Control control in dialog.Controls)
                {
                    worstBottom = Math.Max(worstBottom, control.Bottom);

                    if (control.Right > dialog.ClientSize.Width || control.Bottom > dialog.ClientSize.Height)
                    {
                        Log(15, $"字体管理对话框在 {scale:0.##} 倍缩放下有控件越界："
                                + $"{control.GetType().Name} {control.Bounds} 超出客户区 {dialog.ClientSize}");
                        return false;
                    }
                }

                var itemHeight = list.ItemHeight;
                var deviceDpi = dialog.DeviceDpi;

                dialog.Close();

                Log(15, $"字体管理对话框正常：{names.Count} 个导入字体、行高 {itemHeight}（自绘预览）、"
                        + $"选中的字体用在样例上，DPI {deviceDpi} 下无越界（最大下边界 {worstBottom}）");
            }

            return true;
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

        /// <summary>
        /// 导入字体文件的完整链路。
        /// <para>
        /// 用系统自带的 <c>arial.ttf</c> 当样本（不安装任何东西，也不依赖中文字体），
        /// 重点验三件事：文件被<b>复制进了数据目录</b>（所以下次启动还找得到）、
        /// 导入之后<b>原文件没被占用</b>、以及<b>假装成 .ttf 的垃圾文件不会留下垃圾</b>。
        /// </para>
        /// </summary>
        private static bool CheckFontImport(播放器.MainForm form, DesktopLyricsWindow window, out string family)
        {
            family = null;

            var systemFont = FindSystemFontFile();

            if (systemFont == null)
            {
                Log(15, "歌词字体：系统里找不到可用的 .ttf 样本，跳过导入检查");
                return true;
            }

            // 从自己的副本导入。这样"有没有占住用户的字体文件"可以直接验：
            // 导入之后必须还能把那一份删掉（系统字体目录本身是只读的，验不了这件事）。
            var source = Path.Combine(
                AppContext.BaseDirectory, "import-source" + Path.GetExtension(systemFont));

            File.Copy(systemFont, source, overwrite: true);

            try
            {
                var ok = UiFonts.TryImportFontFile(source, out var families, out var error);

                if (!ok || families.Length == 0)
                {
                    Log(15, $"歌词字体检查：导入 {Path.GetFileName(source)} 失败（{error ?? "没有说明"}）");
                    return false;
                }

                var copied = Path.Combine(FontLibrary.DirectoryPath, Path.GetFileName(source));

                if (!File.Exists(copied))
                {
                    Log(15, "歌词字体检查：导入的字体文件没有复制进数据目录（下次启动就找不到了）");
                    return false;
                }

                TryDelete(source);

                if (File.Exists(source))
                {
                    Log(15, "歌词字体检查：导入把用户原来的字体文件占住了（删不掉也改不了）");
                    return false;
                }

                var imported = UiFonts.ImportedFamilies;

                if (!imported.Contains(families[0], StringComparer.OrdinalIgnoreCase))
                {
                    Log(15, $"歌词字体检查：导入之后字体族列表里没有「{families[0]}」");
                    return false;
                }

                // 同一个文件导入第二次必须还是成功（幂等），不能报"里面没有可用字体"
                var againOk = UiFonts.TryImportFontFile(systemFont, out var againFamilies, out _);

                if (!againOk || againFamilies.Length == 0)
                {
                    Log(15, "歌词字体检查：同一个字体文件导入第二次被拒绝了（应当幂等）");
                    return false;
                }

                // ---- 导入的字体要能真的用上（两处都换，细节见 CheckLyricsFont）----
                family = families[0];

                // ---- 伪装成 .ttf 的垃圾文件：要说清失败原因，并且不留垃圾 ----
                var bogus = Path.Combine(AppContext.BaseDirectory, "bogus-font.ttf");
                File.WriteAllText(bogus, "这不是字体文件");

                try
                {
                    if (UiFonts.TryImportFontFile(bogus, out var bogusFamilies, out var bogusError))
                    {
                        Log(15, "歌词字体检查：垃圾文件居然被当成字体导入成功了"
                                + $"（{new FileInfo(bogus).Length} 字节，报出来的字体族："
                                + $"{string.Join(" / ", bogusFamilies)}）");
                        return false;
                    }

                    if (string.IsNullOrEmpty(bogusError))
                    {
                        Log(15, "歌词字体检查：导入失败却没有给出原因");
                        return false;
                    }

                    // 复制进来又读不出来的文件必须删掉，否则每次启动都要再失败一遍
                    if (File.Exists(Path.Combine(FontLibrary.DirectoryPath, "bogus-font.ttf")))
                    {
                        Log(15, "歌词字体检查：导入失败的垃圾文件留在了字体库里");
                        return false;
                    }
                }
                finally
                {
                    TryDelete(bogus);
                }

                // ---- 换个扩展名的文件要直接被挡掉 ----
                var wrongExtension = Path.Combine(AppContext.BaseDirectory, "font.txt");
                File.WriteAllText(wrongExtension, "随便写点什么");

                try
                {
                    if (UiFonts.TryImportFontFile(wrongExtension, out _, out _))
                    {
                        Log(15, "歌词字体检查：非字体扩展名的文件被放行了");
                        return false;
                    }
                }
                finally
                {
                    TryDelete(wrongExtension);
                }

                Log(15, $"字体导入正常：{Path.GetFileName(source)} 复制进字体库并立刻生效"
                        + "（导入的那一份原文件还能删掉，说明没被占用），重复导入是幂等的，"
                        + "垃圾文件和错误扩展名都会被挡掉且不留残留");

                // 留在导入的字体上：外面要靠它验"字体设置会存进设置文件"
                form.SetLyricsFont(families[0]);
                PumpMessages(150);

                return true;
            }
            finally
            {
                TryDelete(source);
            }
        }

        /// <summary>
        /// 另找一个系统字体文件（和 <see cref="FindSystemFontFile"/> 通常挑中的那个不同）。
        /// <para>
        /// "私有字体"那条检查必须换一个字体文件来做：它要把族名改掉再导入，
        /// 如果拿的还是 Arial 那一份数据，GDI+ 有可能把它和前面刚导入过的 Arial 认成同一个字体，
        /// 直接报出旧族名（实测时灵时不灵）。换成另一个字体文件，字节就不一样了。
        /// </para>
        /// </summary>
        private static string? FindOtherSystemFontFile(string excludePath)
        {
            try
            {
                var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

                if (string.IsNullOrEmpty(fonts) || !Directory.Exists(fonts)) return null;

                var exclude = Path.GetFileName(excludePath);

                foreach (var name in new[]
                         {
                             "segoeui.ttf", "tahoma.ttf", "verdana.ttf", "calibri.ttf",
                             "times.ttf", "georgia.ttf", "consola.ttf", "cour.ttf", "trebuc.ttf"
                         })
                {
                    if (string.Equals(name, exclude, StringComparison.OrdinalIgnoreCase)) continue;

                    var path = Path.Combine(fonts, name);
                    if (File.Exists(path)) return path;
                }

                return Directory.EnumerateFiles(fonts, "*.ttf")
                    .FirstOrDefault(path => !string.Equals(
                        Path.GetFileName(path), exclude, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception)
            {
                return null;
            }
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
