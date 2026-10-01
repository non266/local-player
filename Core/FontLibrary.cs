using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace 播放器.Core
{
    /// <summary>
    /// 用户导入的字体文件库：统一放在数据目录下的 <c>fonts</c> 子目录里。
    /// <para>
    /// 之所以要复制一份而不是直接引用用户选的那个文件，有两个原因：
    /// <list type="number">
    ///   <item>GDI+ 的 <c>PrivateFontCollection.AddFontFile</c> 会<b>占用</b>字体文件，
    ///   直接引用会让用户删不掉、也移动不了自己的字体；</item>
    ///   <item>导入必须跨重启有效——设置文件里只存着字体族名，
    ///   下次启动得能在同一个地方把它重新装回来。</item>
    /// </list>
    /// </para>
    /// </summary>
    public static class FontLibrary
    {
        /// <summary>认得的字体文件扩展名。</summary>
        private static readonly string[] SupportedExtensions = { ".ttf", ".otf", ".ttc", ".otc" };

        /// <summary>字体库目录。</summary>
        public static string DirectoryPath => Path.Combine(AppSettings.SettingsDirectory, "fonts");

        /// <summary>这个文件看起来是不是字体文件（只看扩展名，内容对不对要真装了才知道）。</summary>
        public static bool IsSupported(string path) =>
            !string.IsNullOrWhiteSpace(path) &&
            SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 文件头像不像一个 sfnt 字体（<c>.ttf / .otf / .ttc / .otc</c> 都是这一族）。
        /// <para>
        /// 光靠 GDI+ 的"装进去看有没有字体族"不足以判断：<b>实测 GDI+ 会在探测一个坏文件时
        /// 报出进程里上一个装过的字体族</b>（200 次里撞到 1 次，垃圾文件报成了 Arial）。
        /// 后果是垃圾文件被当成"导入成功"留在字体库里，菜单里还多出一个幽灵字体。
        /// 这里先按文件头挡一道，确定性的部分不交给 GDI+。
        /// </para>
        /// <para>
        /// 认四种魔数：<c>0x00010000</c>（TrueType）、<c>OTTO</c>（CFF / OpenType）、
        /// <c>true</c>（老式 Apple TrueType）、<c>ttcf</c>（字体集合）。
        /// 读不出来（文件不存在 / 没权限）时返回 <c>false</c>，交给调用方按"不是字体"处理。
        /// </para>
        /// </summary>
        public static bool LooksLikeFontFile(string path)
        {
            try
            {
                using var stream = File.OpenRead(path);
                Span<byte> header = stackalloc byte[4];

                if (stream.Read(header) != header.Length) return false;

                var magic = (uint)((header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3]);

                return magic is 0x00010000 or 0x4F54544F or 0x74727565 or 0x74746366;
            }
            catch (Exception ex)
            {
                AppLog.Swallowed("读不出文件头就当它不是字体（调用方会给用户一句说明）", ex);
                return false;
            }
        }

        /// <summary>已经导入的字体文件（按文件名排序）；目录不存在时返回空列表。</summary>
        public static IReadOnlyList<string> ListFiles()
        {
            try
            {
                var directory = DirectoryPath;
                if (!Directory.Exists(directory)) return Array.Empty<string>();

                return Directory
                    .EnumerateFiles(directory)
                    .Where(IsSupported)
                    .OrderBy(Path.GetFileName, StringComparer.CurrentCulture)
                    .ToArray();
            }
            catch (Exception)
            {
                return Array.Empty<string>();
            }
        }

        /// <summary>
        /// 把字体文件复制进库，返回复制后的完整路径。
        /// <para>
        /// 用"临时文件 + 改名覆盖"写进去：中途失败不会在库里留下一个半截文件，
        /// 而半截字体文件每次启动都会被重新读一遍、每次都失败。
        /// </para>
        /// </summary>
        public static string Copy(string sourcePath)
        {
            if (!File.Exists(sourcePath)) throw new FileNotFoundException("找不到字体文件", sourcePath);

            var directory = DirectoryPath;
            Directory.CreateDirectory(directory);

            var target = MakeUniquePath(directory, Path.GetFileName(sourcePath));
            var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";

            try
            {
                File.Copy(sourcePath, temporary, overwrite: true);
                File.Move(temporary, target, overwrite: true);
            }
            catch (Exception)
            {
                try
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
                catch (Exception ex)
                {
// 删不掉就算了，反正是 .tmp，下次不会再被读
                    AppLog.Swallowed("删不掉就算了，反正是 .tmp，下次不会再被读", ex);
                }

                throw;
            }

            return target;
        }

        /// <summary>删掉一个已经导入的字体文件。文件正被 GDI+ 占用时会失败——这是预期行为。</summary>
        public static bool TryDelete(string path)
        {
            try
            {
                File.Delete(path);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// "这次没删掉"的字体文件名单。
        /// <para>
        /// 正常情况下导入的字体是<b>从内存装进 GDI+</b> 的（见 <c>UiFonts</c>），文件不会被占住，
        /// 删起来立刻就成。但 <c>.ttc</c> / <c>.otc</c> 这类字体集合 GDI+ 只能按文件加载，
        /// 那种文件在进程退出前删不掉——于是记在这里，下次启动、还没加载字体之前再删一次。
        /// </para>
        /// </summary>
        private static string PendingDeletionPath => Path.Combine(DirectoryPath, "pending-delete.txt");

        /// <summary>把一个删不掉的文件排进"下次启动再删"的队列。</summary>
        public static void MarkForDeletion(string path)
        {
            try
            {
                Directory.CreateDirectory(DirectoryPath);

                var lines = ReadPendingDeletions();

                if (!lines.Contains(path, StringComparer.OrdinalIgnoreCase))
                    lines.Add(path);

                File.WriteAllLines(PendingDeletionPath, lines);
            }
            catch (Exception ex)
            {
// 排不上队就只能让用户手动删——至少这次的删除操作本身是成功的
                AppLog.Swallowed("排不上队就只能让用户手动删——至少这次的删除操作本身是成功的", ex);
            }
        }

        /// <summary>
        /// 清一遍"下次启动再删"的队列。
        /// <para>必须在<b>加载字体之前</b>调用：文件一旦被 GDI+ 装进去就又要等到下次了。</para>
        /// </summary>
        public static void FlushPendingDeletions()
        {
            var lines = ReadPendingDeletions();
            if (lines.Count == 0) return;

            var remaining = new List<string>();

            foreach (var path in lines)
            {
                if (TryDelete(path)) continue;
                if (File.Exists(path)) remaining.Add(path);
            }

            try
            {
                if (remaining.Count == 0)
                {
                    if (File.Exists(PendingDeletionPath)) File.Delete(PendingDeletionPath);
                }
                else
                {
                    File.WriteAllLines(PendingDeletionPath, remaining);
                }
            }
            catch (Exception ex)
            {
// 队列本身写不回去也不是致命问题：下次还会再试一遍
                AppLog.Swallowed("队列本身写不回去也不是致命问题：下次还会再试一遍", ex);
            }
        }

        private static List<string> ReadPendingDeletions()
        {
            try
            {
                var path = PendingDeletionPath;
                if (!File.Exists(path)) return new List<string>();

                return File.ReadAllLines(path)
                    .Select(line => line.Trim())
                    .Where(line => line.Length > 0)
                    .ToList();
            }
            catch (Exception)
            {
                return new List<string>();
            }
        }

        /// <summary>
        /// 同名文件已经有了就加一个序号。
        /// <para>同一个字体文件重复导入要覆盖（名字一样、内容一样），
        /// 但两个不同文件恰好同名时不能互相盖掉。</para>
        /// </summary>
        private static string MakeUniquePath(string directory, string fileName)
        {
            var target = Path.Combine(directory, fileName);
            if (!File.Exists(target)) return target;

            var name = Path.GetFileNameWithoutExtension(fileName);
            var extension = Path.GetExtension(fileName);

            for (var index = 2; index < 1000; index++)
            {
                var candidate = Path.Combine(directory, $"{name} ({index}){extension}");
                if (!File.Exists(candidate)) return candidate;
            }

            return Path.Combine(directory, $"{name} ({Guid.NewGuid():N}){extension}");
        }
    }
}
