using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using 播放器.Core;

namespace 播放器.Ui
{
    /// <summary>
    /// 新增界面部件的字体。
    /// <para>
    /// 只在侧栏、歌词和新建对话框上使用，<b>不动主窗体的字体</b>——
    /// 主窗体的布局是在 125% DPI 下按默认字号调好的，换字体整体都会走形。
    /// </para>
    /// </summary>
    internal static partial class UiFonts
    {
        /// <summary>默认字体的候选（按优先级）：优先中文字形完整的，最后退回系统消息字体。</summary>
        private static readonly string[] DefaultFamilies =
        {
            "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI"
        };

        private static Font? _sidebar;
        private static string[]? _installedFamilies;

        /// <summary>用户导入的字体（不在系统字体库里，只能用这个集合里的 FontFamily 来建字体）。</summary>
        private static readonly PrivateFontCollection Imported = new PrivateFontCollection();

        /// <summary>已导入的字体族名 → FontFamily。字典是为了避免每次都遍历一遍集合。</summary>
        private static readonly Dictionary<string, FontFamily> ImportedByName =
            new Dictionary<string, FontFamily>(StringComparer.OrdinalIgnoreCase);

        /// <summary>字体族名 → 它在字体库里的文件（管理对话框要按族名找文件）。</summary>
        private static readonly Dictionary<string, string> FileByFamily =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 字体族名 → 提供它的那些文件（可能有多个）。
        /// <para>
        /// 同一族名来自多个文件是<b>常态</b>：同一个字体导入两次、或者同一族的两个字重
        /// 分别从不同文件导进来，都会出现。所以删除必须按<b>文件</b>来，
        /// 只有当一个族名最后一个文件也没了，这个字体才算真的没了。
        /// </para>
        /// </summary>
        private static readonly Dictionary<string, List<string>> FilesByFamily =
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>字体库文件 → 这个文件里有哪些字体族。</summary>
        private static readonly Dictionary<string, List<string>> FamiliesByFile =
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 用内存方式装进 GDI+ 的字体数据，必须一直钉住。
        /// <para>
        /// <c>AddMemoryFont</c> 收的是一块非托管内存的地址；这块内存是托管数组钉住来的，
        /// 数组只要被 GC 搬走、或者句柄被释放，字体数据就成了野指针。
        /// 所以句柄留在这个列表里，进程退出时统一释放（每个字体几百 KB ~ 几 MB）。
        /// </para>
        /// </summary>
        private static readonly List<GCHandle> PinnedFontData = new List<GCHandle>();

        /// <summary>
        /// 注册给 <b>GDI</b> 的字体资源句柄（<c>AddFontMemResourceEx</c> 的返回值）。
        /// <para>
        /// 与上面的 GDI+ 私有集合是<b>两套独立的东西</b>，两边都要装：
        /// 桌面歌词用 GDI+（<c>DrawString</c>），侧栏歌词用 <c>TextRenderer</c>，
        /// 而那是 GDI 的 <c>DrawText</c>——它看不到 <c>PrivateFontCollection</c>，
        /// 名字查不到就静默换成默认字体。
        /// </para>
        /// </summary>
        private static readonly List<IntPtr> GdiFontResources = new List<IntPtr>();

        private static bool _importedLoaded;

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr AddFontMemResourceEx(IntPtr fileView, uint size, IntPtr reserved, out uint count);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool RemoveFontMemResourceEx(IntPtr handle);

        static UiFonts()
        {
            AppDomain.CurrentDomain.ProcessExit += (s, e) => ReleaseNativeFontData();
        }

        /// <summary>侧栏正文字体。</summary>
        public static Font Sidebar => _sidebar ??= Resolve(10f, FontStyle.Regular);

        /// <summary>按优先级挑一个存在的中文字体。</summary>
        public static Font Resolve(float size, FontStyle style) => Resolve(null, size, style);

        /// <summary>
        /// 指定字体族时优先用它，用不了再退回默认候选链。
        /// <para>
        /// <paramref name="family"/> 为空、不存在、或者装了个坏字体时都必须能正常出字——
        /// 字体名来自设置文件，用户可以手改，也可能在别的机器上根本没装。
        /// </para>
        /// </summary>
        public static Font Resolve(string? family, float size, FontStyle style)
        {
            // 导入的字体要先装回来，否则用户重启之后会发现"导入的字体没了"
            LoadImportedFonts();

            if (!string.IsNullOrWhiteSpace(family))
            {
                // 有些字体族没有粗体，GDI+ 会直接抛 ArgumentException。
                // 这时宁可不要粗体也要保住用户选的字体族——换成别的字体更让人困惑。
                var chosen = TryCreate(family.Trim(), size, style)
                             ?? TryCreate(family.Trim(), size, FontStyle.Regular);

                if (chosen != null) return chosen;
            }

            foreach (var name in DefaultFamilies)
            {
                var font = TryCreate(name, size, style);
                if (font != null) return font;
            }

            return new Font(SystemFonts.MessageBoxFont ?? Control.DefaultFont, style);
        }

        /// <summary>
        /// 建一个指定字体族的字体；族不存在时返回 <c>null</c>。
        /// <para>
        /// GDI+ 在字体族不存在时<b>不会抛异常</b>，而是静默退回默认字体，
        /// 所以必须回头核对 <c>FontFamily.Name</c> 才能确认真的用上了。
        /// </para>
        /// </summary>
        private static Font? TryCreate(string family, float size, FontStyle style)
        {
            // 导入的字体优先：它们不在系统字体库里，new Font(名字, ...) 是找不到的，
            // 必须用集合里的 FontFamily 来建。
            if (ImportedByName.TryGetValue(family, out var imported))
            {
                try
                {
                    return new Font(imported, size, style);
                }
                catch (Exception ex)
                {
// 这个字体族没有这个字重（例如没有粗体）：交给调用方退回 Regular
                    AppLog.Swallowed("这个字体族没有这个字重（例如没有粗体）：交给调用方退回 Regular", ex);
                }
            }

            try
            {
                var font = new Font(family, size, style);

                if (string.Equals(font.FontFamily.Name, family, StringComparison.OrdinalIgnoreCase))
                    return font;

                font.Dispose();
            }
            catch (Exception ex)
            {
// 参数非法或字体损坏：交给调用方换下一个候选
                AppLog.Swallowed("参数非法或字体损坏：交给调用方换下一个候选", ex);
            }

            return null;
        }

        private static string[] LoadInstalledFamilies()
        {
            try
            {
                // FontFamily 对象在集合释放后就失效了，所以这里只留下名字
                using var collection = new InstalledFontCollection();

                return collection.Families
                    .Select(f => f.Name)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => name, StringComparer.CurrentCulture)
                    .ToArray();
            }
            catch (Exception)
            {
                return Array.Empty<string>();
            }
        }

        /// <summary>
        /// 系统上已安装的字体族（按名称排序）。
        /// <para>
        /// 界面暂时用不到它——歌词字体只列用户自己导入的那些（见 README）。
        /// 留在这里是为了以后想加"选系统字体"时不用重新写一遍枚举逻辑；
        /// 它是惰性的，没人用就不会去枚举。
        /// </para>
        /// </summary>
        public static IReadOnlyList<string> InstalledFamilies => _installedFamilies ??= LoadInstalledFamilies();
    }
}
