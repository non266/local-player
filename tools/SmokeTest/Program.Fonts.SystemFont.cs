// 字体 —— 系统字体查找（找一个系统字体的文件当对照，以及样本之间的撞名检查）。
//
// 从 Program.Fonts.cs 按 ARCHITECTURE 17.2 的规矩整块搬过来：成员一个字没动。

namespace SmokeTest
{
    internal static partial class Program
    {
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
    }
}
