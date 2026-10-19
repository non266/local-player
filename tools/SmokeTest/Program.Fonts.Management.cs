// 字体 —— 管理（侧栏"字体管理"入口：导入 / 卸载 / 列表随设置变）。
//
// 从 Program.Fonts.cs 按 ARCHITECTURE 17.2 的规矩整块搬过来：成员一个字没动。

namespace SmokeTest
{
    internal static partial class Program
    {
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
    }
}
