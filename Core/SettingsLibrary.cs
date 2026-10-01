using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace 播放器.Core
{
    /// <summary>方案库里的一份设置方案（磁盘上的一个 json 文件）。</summary>
    public sealed class SavedSettingsProfile
    {
        public SavedSettingsProfile(string name, string filePath, DateTime modifiedUtc, long size, bool readable)
        {
            Name = name;
            FilePath = filePath;
            ModifiedUtc = modifiedUtc;
            Size = size;
            Readable = readable;
        }

        /// <summary>方案名（不含扩展名）。</summary>
        public string Name { get; }

        /// <summary>磁盘上的完整路径。</summary>
        public string FilePath { get; }

        public DateTime ModifiedUtc { get; }

        public long Size { get; }

        /// <summary>这个文件能不能当成设置读出来（被手改坏的文件也要列出来，用户才好删掉它）。</summary>
        public bool Readable { get; }

        /// <summary>最后修改时间（本地时间）。</summary>
        public DateTime ModifiedLocal => ModifiedUtc.ToLocalTime();
    }

    /// <summary>
    /// 设置方案库：把当前这套设置命名存下来，随时切过去。
    /// <para>
    /// 存的就是<b>一份 settings.json</b>（同样的字段、同样的格式），目录固定为
    /// <c>数据目录\设置方案</c>（数据目录见 <see cref="AppSettings.SettingsDirectory"/>）。
    /// 用同一种格式有两个好处：拷给别人就是拷一个文件；出问题用记事本打开就能看。
    /// </para>
    /// <para>
    /// 本类只管磁盘上的那份真相，<b>不持有"当前用的是哪份方案"</b>
    /// （那个记在 <see cref="AppSettings.CurrentSettingsProfileName"/> 里）。
    /// </para>
    /// </summary>
    public static class SettingsLibrary
    {
        /// <summary>方案目录名。</summary>
        public const string FolderName = "设置方案";

        /// <summary>方案文件的扩展名。</summary>
        public const string Extension = ".json";

        /// <summary>方案目录（不保证存在）。</summary>
        public static string Directory => Path.Combine(AppSettings.SettingsDirectory, FolderName);

        /// <summary>确保方案目录存在，返回它的路径。</summary>
        public static string EnsureDirectory()
        {
            var directory = Directory;
            if (!System.IO.Directory.Exists(directory))
                System.IO.Directory.CreateDirectory(directory);

            return directory;
        }

        // ---- 名字 -------------------------------------------------------------

        /// <summary>把用户输入的名字收拾成可用的文件名（规则和歌单名共用一套）。</summary>
        public static bool TryNormalizeName(string? raw, out string name, out string error) =>
            SavedName.TryNormalize(raw, "方案名", out name, out error);

        /// <summary>方案名是否可用（合法且不重名）；不合法时给出理由。</summary>
        public static bool CanUseName(string? raw, out string name, out string error)
        {
            if (!TryNormalizeName(raw, out name, out error)) return false;

            if (ResolvePath(name) != null)
            {
                error = $"已经有一个叫「{name}」的方案了。";
                return false;
            }

            return true;
        }

        // ---- 查询 -------------------------------------------------------------

        /// <summary>列出方案库里的全部方案，按自然顺序排列（"方案2" 在 "方案10" 前面）。</summary>
        public static IReadOnlyList<SavedSettingsProfile> List()
        {
            var found = new List<SavedSettingsProfile>();
            var directory = Directory;

            if (!System.IO.Directory.Exists(directory)) return found;

            IEnumerable<string> files;
            try
            {
                files = System.IO.Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly);
            }
            catch (Exception)
            {
                return found;
            }

            foreach (var file in files)
            {
                if (!string.Equals(Path.GetExtension(file), Extension, StringComparison.OrdinalIgnoreCase))
                    continue;

                FileInfo info;
                try
                {
                    info = new FileInfo(file);
                    if (!info.Exists) continue;
                }
                catch (Exception)
                {
                    continue;
                }

                var name = Path.GetFileNameWithoutExtension(file);
                if (string.IsNullOrWhiteSpace(name)) continue;

                found.Add(new SavedSettingsProfile(
                    name, info.FullName, info.LastWriteTimeUtc, info.Length, LooksReadable(info.FullName)));
            }

            found.Sort((a, b) => SavedName.CompareNatural(a.Name, b.Name));
            return found;
        }

        /// <summary>方案是否存在（名字不区分大小写）。</summary>
        public static bool Exists(string? name) => ResolvePath(name) != null;

        /// <summary>名字对应的现有文件；名字非法或文件不存在时返回 <c>null</c>。</summary>
        public static string? ResolvePath(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;

            var wanted = name.Trim();

            // 和歌单库一样按磁盘上实际的名字走：大小写不敏感的文件系统上，
            // "工作" 和 "工作 " 不该变成两个文件。
            foreach (var saved in List())
            {
                if (string.Equals(saved.Name, wanted, StringComparison.CurrentCultureIgnoreCase))
                    return saved.FilePath;
            }

            return null;
        }

        /// <summary>这个名字该写到哪个文件：已有同名文件就沿用它的大小写，否则按 .json 新建。</summary>
        private static string PathFor(string name) =>
            ResolvePath(name) ?? Path.Combine(EnsureDirectory(), name + Extension);

        /// <summary>文件内容能不能当成设置读出来（只看"解析得动吗"，不关心具体值）。</summary>
        private static bool LooksReadable(string path)
        {
            try
            {
                var json = File.ReadAllText(path);
                return !string.IsNullOrWhiteSpace(json) && AppSettings.TryFromJson(json, out _);
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ---- 增删改 -----------------------------------------------------------

        /// <summary>把一套设置存成方案；同名时<b>覆盖</b>。返回存下来的那份。</summary>
        public static SavedSettingsProfile Save(string name, AppSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            if (!TryNormalizeName(name, out var clean, out var error))
                throw new ArgumentException(error, nameof(name));

            var path = PathFor(clean);

            // AppSettings.SaveTo 走"临时文件 + 改名覆盖"，不会留半截 JSON
            settings.SaveTo(path);

            var info = new FileInfo(path);

            return new SavedSettingsProfile(
                Path.GetFileNameWithoutExtension(path),
                info.FullName,
                info.Exists ? info.LastWriteTimeUtc : DateTime.UtcNow,
                info.Exists ? info.Length : 0,
                readable: true);
        }

        /// <summary>读一份方案；读不出来时如实说明（不返回"一份默认设置"冒充成功）。</summary>
        public static bool TryLoad(string name, out AppSettings settings, out string error)
        {
            settings = new AppSettings();
            error = string.Empty;

            var path = ResolvePath(name);

            if (path == null)
            {
                error = $"找不到方案「{name}」。";
                return false;
            }

            string json;
            try
            {
                json = File.ReadAllText(path);
            }
            catch (Exception ex)
            {
                error = "方案读取失败：" + ex.Message;
                return false;
            }

            if (string.IsNullOrWhiteSpace(json))
            {
                error = $"方案「{Path.GetFileNameWithoutExtension(path)}」是空的。";
                return false;
            }

            if (!AppSettings.TryFromJson(json, out var parsed))
            {
                error = $"方案「{Path.GetFileNameWithoutExtension(path)}」不是一份有效的设置文件"
                        + "（用记事本打开看看是不是被改坏了）。";
                return false;
            }

            settings = parsed;
            return true;
        }

        /// <summary>把方案改名。成功时 <paramref name="newName"/> 是实际用的名字。</summary>
        public static bool Rename(string oldName, string newName, out string newNameOut, out string error)
        {
            newNameOut = string.Empty;
            error = string.Empty;

            var path = ResolvePath(oldName);
            if (path == null)
            {
                error = $"找不到方案「{oldName}」。";
                return false;
            }

            if (!TryNormalizeName(newName, out var clean, out error)) return false;

            var destination = Path.Combine(Path.GetDirectoryName(path) ?? EnsureDirectory(), clean + Extension);

            // 名字一个字都没变：不折腾文件，直接算成功
            if (string.Equals(destination, path, StringComparison.Ordinal))
            {
                newNameOut = Path.GetFileNameWithoutExtension(path);
                return true;
            }

            // 只差大小写时文件系统上其实是同一个文件，要放行
            if (File.Exists(destination) && !string.Equals(destination, path, StringComparison.OrdinalIgnoreCase))
            {
                error = $"已经有一个叫「{clean}」的方案了。";
                return false;
            }

            try
            {
                File.Move(path, destination, overwrite: true);
            }
            catch (Exception ex)
            {
                error = "改名失败：" + ex.Message;
                return false;
            }

            newNameOut = Path.GetFileNameWithoutExtension(destination);
            return true;
        }

        /// <summary>删除一份方案（只删方案文件，settings.json 与别的东西一个都不动）。</summary>
        public static bool Delete(string name, out string error)
        {
            error = string.Empty;

            var path = ResolvePath(name);
            if (path == null)
            {
                error = $"找不到方案「{name}」。";
                return false;
            }

            try
            {
                File.Delete(path);
            }
            catch (Exception ex)
            {
                error = "删除失败：" + ex.Message;
                return false;
            }

            return true;
        }

        /// <summary>
        /// 把一个外部的 json 收进方案库（原文件一个字节都不动）。
        /// <para>
        /// 收的时候<b>读出来重新写一遍</b>：外面的文件可能带着旧版本的字段、或者被人手改成了
        /// 半截格式；重写一遍就只剩当前这一版的字段，坏文件也会在这里被挡住、不会躺进方案库。
        /// </para>
        /// </summary>
        public static bool Import(string sourcePath, bool overwrite, out string name, out string error)
        {
            name = string.Empty;
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                error = "文件不存在。";
                return false;
            }

            string json;
            try
            {
                json = File.ReadAllText(sourcePath);
            }
            catch (Exception ex)
            {
                error = "读不出来：" + ex.Message;
                return false;
            }

            if (!AppSettings.TryFromJson(json, out var imported))
            {
                error = "这个文件不是一份有效的设置文件。";
                return false;
            }

            var raw = Path.GetFileNameWithoutExtension(sourcePath);

            if (!TryNormalizeName(raw, out var clean, out error)) return false;

            var destination = ResolvePath(clean);

            if (destination != null && !overwrite)
            {
                error = $"已经有一个叫「{clean}」的方案了。";
                return false;
            }

            try
            {
                imported.SaveTo(destination ?? Path.Combine(EnsureDirectory(), clean + Extension));
            }
            catch (Exception ex)
            {
                error = "导入失败：" + ex.Message;
                return false;
            }

            name = Path.GetFileNameWithoutExtension(destination ?? clean + Extension);
            return true;
        }

        /// <summary>把一套设置导出到指定文件（覆盖同名文件）。</summary>
        public static bool ExportTo(AppSettings settings, string destinationPath, out string error)
        {
            error = string.Empty;

            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (string.IsNullOrWhiteSpace(destinationPath))
            {
                error = "没有指定要写到哪个文件。";
                return false;
            }

            try
            {
                settings.SaveTo(destinationPath);
                return true;
            }
            catch (Exception ex)
            {
                error = "导出失败：" + ex.Message;
                return false;
            }
        }

        /// <summary>把方案描述成"名字（3.4 KB · 08-05 12:30）"；读不出来的文件如实标出来。</summary>
        public static string Describe(SavedSettingsProfile profile)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));

            var text = new StringBuilder(profile.Name);

            if (!profile.Readable)
            {
                text.Append("（读不出来）");
                return text.ToString();
            }

            text.Append("（");
            text.Append(FormatSize(profile.Size));
            text.Append(" · ");
            text.Append(profile.ModifiedLocal.ToString("MM-dd HH:mm"));
            text.Append('）');

            return text.ToString();
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.#") + " KB";

            return (bytes / (1024.0 * 1024.0)).ToString("0.#") + " MB";
        }
    }
}
