using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using 播放器.Core;

// 字体库的登记表：族名 / 文件 / 用户导入列表，以及启动时把导入过的字体装回来。

namespace 播放器.Ui
{
    internal static partial class UiFonts
    {
        /// <summary>用户导入的字体族名（已排序）。</summary>
        public static IReadOnlyList<string> ImportedFamilies =>
            ImportedByName.Keys.OrderBy(name => name, StringComparer.CurrentCulture).ToArray();

        /// <summary>这个字体族是从哪个文件导入的；不是导入进来的就返回 <c>null</c>。</summary>
        public static string? FileForFamily(string? family)
        {
            if (string.IsNullOrWhiteSpace(family)) return null;

            LoadImportedFonts();

            return FileByFamily.TryGetValue(family.Trim(), out var path) ? path : null;
        }

        /// <summary>这个文件里有哪些字体族（管理对话框删除前要提示"会一起移除几个"）。</summary>
        public static IReadOnlyList<string> FamiliesInFile(string path)
        {
            LoadImportedFonts();

            return FamiliesByFile.TryGetValue(path, out var names)
                ? names.ToArray()
                : Array.Empty<string>();
        }

        /// <summary>字体库里的文件（管理对话框一行一个文件）。</summary>
        public static IReadOnlyList<string> ImportedFiles
        {
            get
            {
                LoadImportedFonts();
                return FontLibrary.ListFiles();
            }
        }

        /// <summary>
        /// 把数据目录里已经导入过的字体全部装回来。
        /// <para>
        /// 每次解析字体族之前都会调一次（有标记位，只有第一次真的去读目录）：
        /// 设置文件里只存了字体族名，启动时得先把文件装回来，
        /// 否则用户会发现"导入的字体重启就没了"。
        /// </para>
        /// </summary>
        public static void LoadImportedFonts()
        {
            if (_importedLoaded) return;

            _importedLoaded = true;

            // 上一轮没删掉的（.ttc 这类只能按文件加载的）趁现在还没加载，先删掉
            FontLibrary.FlushPendingDeletions();

            foreach (var file in FontLibrary.ListFiles())
                AddFont(file);
        }
    }
}
