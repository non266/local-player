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
    internal static class UiFonts
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

        /// <summary>
        /// 导入一个字体文件：复制进字体库并立刻装进进程。
        /// <para>
        /// 失败时要说得清是"文件读不出来"还是"拷不进去"，并且<b>把复制进来的坏文件删掉</b>——
        /// 留着它的话每次启动都会再失败一次。
        /// </para>
        /// </summary>
        public static bool TryImportFontFile(string path, out string[] families, out string? error)
        {
            families = Array.Empty<string>();
            error = null;

            if (!FontLibrary.IsSupported(path))
            {
                error = "只支持 .ttf / .otf / .ttc / .otc 字体文件";
                return false;
            }

            // 扩展名对了不代表内容是字体。先按文件头挡一道：GDI+ 探测坏文件时可能报出
            // 上一个装过的字体族（实测 200 次里 1 次，垃圾文件被认成 Arial），
            // 那样垃圾会被"导入成功"留在字体库里，菜单里还会多一个幽灵字体。
            if (!FontLibrary.LooksLikeFontFile(path))
            {
                error = "这个文件不是字体文件（扩展名像，但文件头对不上）";
                return false;
            }

            string target;

            try
            {
                target = FontLibrary.Copy(path);
            }
            catch (Exception ex)
            {
                error = "复制字体文件失败：" + ex.Message;
                return false;
            }

            var added = AddFont(target);

            if (added.Count == 0)
            {
                FontLibrary.TryDelete(target);
                error = "这个文件里没有能用的字体（可能不是字体文件，或者这个格式 GDI+ 不支持）";
                return false;
            }

            families = added.ToArray();
            return true;
        }

        /// <summary>
        /// 从字体库里移除一个字体<b>文件</b>。
        /// <para>
        /// 按文件而不是按字体族名：同一个族名可能来自多个文件（同一个字体导入两次就会），
        /// 按族名删会有歧义。删掉最后一个提供某族名的文件时，那个族名才会从列表里消失。
        /// </para>
        /// <para>
        /// GDI+ 的 <c>PrivateFontCollection</c> <b>没有"卸载"这一说</b>，
        /// 所以这里做的是"把文件和登记表都抹掉"：菜单立刻不再列它、
        /// 按名字也再也解析不到（退回默认字体）；已经装进 GDI+ 的那份数据要等进程退出。
        /// 对一个"导错了想删掉"的场景来说这就够了。
        /// </para>
        /// </summary>
        public static bool TryRemoveFontFile(string path, out string? error)
        {
            error = null;

            if (string.IsNullOrWhiteSpace(path))
            {
                error = "没有指定要删除的字体文件";
                return false;
            }

            LoadImportedFonts();

            if (!File.Exists(path))
            {
                error = "这个字体文件已经不在了";
                return false;
            }

            var families = FamiliesByFile.TryGetValue(path, out var list)
                ? list.ToArray()
                : Array.Empty<string>();

            // 删得掉就当场删；删不掉（.ttc 那种只能按文件加载的）排到下次启动
            if (!FontLibrary.TryDelete(path)) FontLibrary.MarkForDeletion(path);

            FamiliesByFile.Remove(path);

            foreach (var name in families)
            {
                if (!FilesByFamily.TryGetValue(name, out var files)) continue;

                files.RemoveAll(file => string.Equals(file, path, StringComparison.OrdinalIgnoreCase));

                if (files.Count == 0)
                {
                    FilesByFamily.Remove(name);
                    FileByFamily.Remove(name);
                    ImportedByName.Remove(name);
                }
                else
                {
                    // 还有别的文件提供这个族名：换个文件记着，字体本身留着
                    FileByFamily[name] = files[0];
                }
            }

            return true;
        }

        /// <summary>
        /// 把一个字体文件装进来，返回这个文件里的字体族名。
        /// <para>
        /// <b>优先从内存加载</b>：<c>AddFontFile</c> 会把字体文件占住（实测"导入之后
        /// 用户自己的那一份删不掉"就是这么来的），而内存加载只读一次数据，
        /// 文件不再被锁——用户才能真的把导入的字体删掉。
        /// </para>
        /// <para>
        /// <b>GDI+ 与 GDI 两边都要装</b>：桌面歌词走 GDI+（<c>DrawString</c>），
        /// 侧栏歌词走 <c>TextRenderer</c>（GDI 的 <c>DrawText</c>），
        /// 而 GDI 看不见 <c>PrivateFontCollection</c>——只装一边就会"桌面歌词换了字体、
        /// 侧栏歌词纹丝不动"（实测：GDI 会把导入的字体族名解析成默认字体）。
        /// </para>
        /// <para>
        /// <c>.ttc</c> / <c>.otc</c> 这类字体集合 GDI+ 的内存加载不认（探测出来是 0 个字体族），
        /// 这时退回文件方式：那一份会被占住，删除要等下次启动（见 <see cref="FontLibrary.MarkForDeletion"/>）。
        /// </para>
        /// <para>
        /// 先用一个<b>临时集合</b>问出"这个文件里有哪些字体族"，再装进长期集合。
        /// 不能靠"长期集合里新增了哪些"来判断：同一个文件导入第二次时那一批名字
        /// 早就在集合里了，会被误判成"这个文件里没有能用的字体"。
        /// </para>
        /// <para>重复导入同一个文件是允许的（幂等）：用户可能就是想重新选一次。</para>
        /// </summary>
        private static List<string> AddFont(string path)
        {
            var bytes = TryReadAllBytes(path);

            var handle = bytes is { Length: > 0 } ? GCHandle.Alloc(bytes, GCHandleType.Pinned) : default;

            var pointer = handle.IsAllocated ? handle.AddrOfPinnedObject() : IntPtr.Zero;
            var length = bytes?.Length ?? 0;

            // GDI 那一侧先注册上：它和 GDI+ 是两套字体表，各装各的
            var gdiRegistered = pointer != IntPtr.Zero && RegisterForGdi(pointer, length);

            var fromMemory = pointer == IntPtr.Zero ? new List<string>() : ProbeMemory(pointer, length);

            if (fromMemory.Count > 0)
            {
                try
                {
                    Imported.AddMemoryFont(pointer, length);
                }
                catch (Exception)
                {
                    if (handle.IsAllocated) handle.Free();
                    return new List<string>();
                }

                // GDI+ 拿的是这块内存的地址，数据不能动，句柄得留着
                PinnedFontData.Add(handle);

                Register(path, fromMemory);
                return fromMemory;
            }

            // GDI+ 的内存加载不认这种格式（实测 .ttc / .otc 就是）：退回按文件加载。
            // 文件那一份会被占住（删除排到下次启动），但 GDI 那边已经注册好了，
            // 侧栏歌词照样用得上这个字体。
            var fromFile = ProbeFile(path);

            if (fromFile.Count == 0)
            {
                if (handle.IsAllocated) handle.Free();
                return fromFile;
            }

            try
            {
                Imported.AddFontFile(path);
            }
            catch (Exception)
            {
                // 文件损坏 / 格式不支持：当作没导入，由调用方决定怎么提示
                if (handle.IsAllocated) handle.Free();
                fromFile.Clear();
                return fromFile;
            }

            if (handle.IsAllocated)
            {
                // GDI 还在用这块内存就别释放，否则它随时可能去读一块已经还给 GC 的地址
                if (gdiRegistered) PinnedFontData.Add(handle);
                else handle.Free();
            }

            Register(path, fromFile);
            return fromFile;
        }

        private static byte[]? TryReadAllBytes(string path)
        {
            try
            {
                var bytes = File.ReadAllBytes(path);
                return bytes.Length > 0 ? bytes : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>用临时集合探一下内存加载认不认这个文件，返回文件里的字体族名。</summary>
        private static List<string> ProbeMemory(IntPtr pointer, int length)
        {
            var families = new List<string>();

            try
            {
                // FontFamily 对象在集合释放后就失效了，所以这里只留下名字
                using var probe = new PrivateFontCollection();
                probe.AddMemoryFont(pointer, length);

                foreach (var family in probe.Families)
                {
                    if (!families.Contains(family.Name, StringComparer.OrdinalIgnoreCase))
                        families.Add(family.Name);
                }
            }
            catch (Exception)
            {
                families.Clear();
            }

            return families;
        }

        /// <summary>用临时集合探一下按文件加载能读出哪些字体族。</summary>
        private static List<string> ProbeFile(string path)
        {
            var families = new List<string>();

            try
            {
                using var probe = new PrivateFontCollection();
                probe.AddFontFile(path);

                foreach (var family in probe.Families)
                {
                    if (!families.Contains(family.Name, StringComparer.OrdinalIgnoreCase))
                        families.Add(family.Name);
                }
            }
            catch (Exception)
            {
                families.Clear();
            }

            return families;
        }

        /// <summary>
        /// 把字体注册给 GDI（<c>AddFontMemResourceEx</c>）。
        /// <para>
        /// 这是 GDI 那一侧的对应物：只对<b>本进程</b>可见，不写进系统字体库，
        /// 程序退出就没了（相当于给 GDI 单独开了一份字体表）。
        /// 侧栏歌词、以及任何用 <c>TextRenderer</c> 的地方都靠它才认得导入的字体。
        /// </para>
        /// <para>
        /// 注册失败不算致命：那一处会退回默认字体（只是"侧栏没换字体"），
        /// 所以这里不抛异常，只在返回值里告诉调用方"这块内存还得留着"。
        /// </para>
        /// </summary>
        private static bool RegisterForGdi(IntPtr pointer, int length)
        {
            try
            {
                var resource = AddFontMemResourceEx(pointer, (uint)length, IntPtr.Zero, out _);

                if (resource == IntPtr.Zero) return false;

                GdiFontResources.Add(resource);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>把一个文件里的字体族登记进两张表（族名 → 文件、文件 → 族名）。</summary>
        private static void Register(string path, List<string> families)
        {
            foreach (var family in Imported.Families)
            {
                if (!ImportedByName.ContainsKey(family.Name))
                    ImportedByName[family.Name] = family;
            }

            FamiliesByFile[path] = new List<string>(families);

            foreach (var name in families)
            {
                if (!FilesByFamily.TryGetValue(name, out var files))
                {
                    files = new List<string>();
                    FilesByFamily[name] = files;
                }

                if (!files.Contains(path, StringComparer.OrdinalIgnoreCase))
                    files.Add(path);

                // 界面上按族名找文件时用第一个（同一个族名有多个文件时，它们内容通常是同一个字体）
                if (!FileByFamily.ContainsKey(name)) FileByFamily[name] = path;
            }
        }

        private static void ReleasePinnedFontData()
        {
            foreach (var handle in PinnedFontData)
            {
                if (handle.IsAllocated) handle.Free();
            }

            PinnedFontData.Clear();
        }

        /// <summary>进程退出时把 GDI 的字体资源与钉住的内存一起还回去（顺序：先注销字体，再放内存）。</summary>
        private static void ReleaseNativeFontData()
        {
            foreach (var resource in GdiFontResources)
            {
                try
                {
                    RemoveFontMemResourceEx(resource);
                }
                catch (Exception ex)
                {
// 进程都要结束了，注销失败无所谓
                    AppLog.Swallowed("进程都要结束了，注销失败无所谓", ex);
                }
            }

            GdiFontResources.Clear();
            ReleasePinnedFontData();
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
