// 字体 —— 导入链路与系统字体查找（导入一份真字体、再找一个系统字体当对照）。
//
// 从 Program.Fonts.cs 按 ARCHITECTURE 17.2 的规矩整块搬过来：成员一个字没动。

namespace SmokeTest
{
    internal static partial class Program
    {
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
    }
}
