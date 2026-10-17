using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using 播放器.Core;

// 导入与卸载：内存加载、给 GDI 注册、探测字体族、进程退出时还回原生资源。

namespace 播放器.Ui
{
    internal static partial class UiFonts
    {
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
    }
}
