using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;

namespace 播放器.Core
{
    /// <summary>文件浏览器里一行的种类（只用来选图标 / 判断能不能播放）。</summary>
    internal enum FileBrowserKind
    {
        Folder,
        Drive,
        Video,
        Audio,
        Subtitle,
        Playlist,
        Other
    }

    /// <summary>文件列表里的一条。目录与磁盘也用它，只是 <see cref="Size"/> 无意义。</summary>
    internal sealed class FileBrowserEntry
    {
        private readonly string? _typeText;

        internal FileBrowserEntry(
            string name,
            string fullPath,
            FileBrowserKind kind,
            long size,
            DateTime modified,
            string? typeText = null)
        {
            Name = name;
            FullPath = fullPath;
            Kind = kind;
            Size = size;
            Modified = modified;
            _typeText = typeText;
        }

        internal string Name { get; }

        internal string FullPath { get; }

        internal FileBrowserKind Kind { get; }

        internal bool IsDirectory => Kind is FileBrowserKind.Folder or FileBrowserKind.Drive;

        internal long Size { get; }

        internal DateTime Modified { get; }

        internal string TypeText => _typeText ?? Kind switch
        {
            FileBrowserKind.Folder => "文件夹",
            FileBrowserKind.Drive => "驱动器",
            FileBrowserKind.Video => "视频",
            FileBrowserKind.Audio => "音频",
            FileBrowserKind.Subtitle => "字幕",
            FileBrowserKind.Playlist => "播放列表",
            _ => "文件"
        };

        /// <summary>「大小」列。目录显示"文件夹"，磁盘显示盘的类型，与资源管理器的感觉一致。</summary>
        internal string SizeText => Kind switch
        {
            FileBrowserKind.Folder => "文件夹",
            FileBrowserKind.Drive => TypeText,
            _ => FileBrowserService.FormatSize(Size)
        };

        /// <summary>悬停提示：完整路径 + 类型 / 大小 / 修改时间。</summary>
        internal string ToolTip
        {
            get
            {
                var meta = TypeText;

                if (!IsDirectory) meta += " · " + FileBrowserService.FormatSize(Size);

                if (Modified != DateTime.MinValue)
                    meta += " · " + Modified.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);

                return FullPath + Environment.NewLine + meta;
            }
        }
    }

    /// <summary>一次目录读取的结果。取消 / 出错时也可能带着已经读到的条目。</summary>
    internal sealed class FileBrowserReadResult
    {
        internal List<FileBrowserEntry> Entries { get; } = new List<FileBrowserEntry>();

        /// <summary>给用户看的错误；正常时为 <c>null</c>。</summary>
        internal string? Error { get; set; }

        /// <summary>条目超过上限：结果不完整。</summary>
        internal bool Truncated { get; set; }

        /// <summary>被取消：结果不完整，调用方应当丢弃。</summary>
        internal bool Cancelled { get; set; }

        /// <summary>这一批是"此电脑"的磁盘列表（列头与排序口径不同）。</summary>
        internal bool IsRoots { get; set; }
    }

    /// <summary>
    /// 目录读取：阻塞调用、无界面依赖。<b>调用方负责丢到线程池</b>，这里只做 I/O 与分类。
    /// <para>
    /// 单目录最多 <see cref="MaxEntries"/> 条；只读<b>当前一层</b>，递归是"添加整个文件夹"的事
    /// （走 <see cref="MediaScanner"/> 的有界遍历）。隐藏 / 系统项默认不显示，
    /// 打开"显示所有文件"才一起列出来。
    /// </para>
    /// <para>
    /// 磁盘列表刻意<b>不读卷标 / 不碰 IsReady</b>：离线的网络盘上这两下都可能等满 SMB 超时，
    /// 而"此电脑"是用户最常打开的一页。用 <see cref="DriveInfo.DriveType"/> 判断类型即可。
    /// </para>
    /// </summary>
    internal class FileBrowserService
    {
        /// <summary>单目录最多显示多少条。超过就截断并说明，避免一次往 ListView 里灌进十万行。</summary>
        internal int MaxEntries { get; set; } = 20_000;

        /// <summary>读一个目录（<paramref name="path"/> 为空 = 此电脑）。</summary>
        internal virtual FileBrowserReadResult ReadDirectory(
            string? path, bool showAllFiles, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(path)) return ListRoots(token);

            var result = new FileBrowserReadResult();

            if (token.IsCancellationRequested)
            {
                result.Cancelled = true;
                return result;
            }

            FileSystemInfo[] entries;

            try
            {
                if (File.Exists(path))
                {
                    result.Error = "这是一个文件，不是文件夹";
                    return result;
                }

                var directory = new DirectoryInfo(path);

                if (!directory.Exists)
                {
                    result.Error = "文件夹不存在（可能已拔出或离线）";
                    return result;
                }

                entries = directory.GetFileSystemInfos();
            }
            catch (UnauthorizedAccessException)
            {
                result.Error = "没有权限读取这个文件夹";
                return result;
            }
            catch (DirectoryNotFoundException)
            {
                result.Error = "文件夹不存在（可能已拔出或离线）";
                return result;
            }
            catch (PathTooLongException)
            {
                result.Error = "路径太长了";
                return result;
            }
            catch (IOException)
            {
                result.Error = "读取失败（驱动器可能未就绪或已断开）";
                return result;
            }
            catch (ArgumentException)
            {
                result.Error = "路径里有不能用的字符";
                return result;
            }
            catch (Exception ex)
            {
                AppLog.Swallowed("文件浏览器读取目录失败：" + path, ex);
                result.Error = "读取失败，详情见日志";
                return result;
            }

            var maximum = Math.Max(1, MaxEntries);

            foreach (var info in entries)
            {
                if (token.IsCancellationRequested)
                {
                    result.Cancelled = true;
                    break;
                }

                if (result.Entries.Count >= maximum)
                {
                    result.Truncated = true;
                    break;
                }

                var entry = CreateEntry(info, showAllFiles);
                if (entry != null) result.Entries.Add(entry);
            }

            return result;
        }

        /// <summary>磁盘列表；排序在调用方做（此电脑只有名称可排）。</summary>
        internal virtual FileBrowserReadResult ListRoots(CancellationToken token)
        {
            var result = new FileBrowserReadResult { IsRoots = true };

            DriveInfo[] drives;

            try
            {
                drives = DriveInfo.GetDrives();
            }
            catch (Exception ex)
            {
                AppLog.Swallowed("文件浏览器读取磁盘列表失败", ex);
                result.Error = "读取磁盘列表失败，详情见日志";
                return result;
            }

            foreach (var drive in drives)
            {
                if (token.IsCancellationRequested)
                {
                    result.Cancelled = true;
                    break;
                }

                try
                {
                    if (drive.DriveType == DriveType.NoRootDirectory) continue;

                    // 不读 VolumeLabel / IsReady：离线的网络盘会在这里卡满一次 SMB 超时。
                    var typeText = DescribeDriveType(drive.DriveType);
                    result.Entries.Add(new FileBrowserEntry(
                        drive.Name, drive.Name, FileBrowserKind.Drive, 0, DateTime.MinValue, typeText));
                }
                catch (Exception ex)
                {
                    // 单个驱动器坏了不影响其它盘。
                    AppLog.Swallowed("文件浏览器跳过一个读不出来的驱动器", ex);
                }
            }

            if (result.Entries.Count == 0 && result.Error == null && !result.Cancelled)
                result.Error = "没有找到可用的磁盘";

            return result;
        }

        /// <summary>按当前口径排序（文件夹永远在前）。排序只动内存，不再读磁盘。</summary>
        internal static void Sort(List<FileBrowserEntry> entries, FileSortField field, bool descending)
        {
            entries.Sort((left, right) =>
            {
                if (left.IsDirectory != right.IsDirectory)
                    return left.IsDirectory ? -1 : 1;

                var result = field switch
                {
                    FileSortField.Size => left.Size.CompareTo(right.Size),
                    FileSortField.Modified => left.Modified.CompareTo(right.Modified),
                    FileSortField.Type => string.Compare(
                        Path.GetExtension(left.FullPath),
                        Path.GetExtension(right.FullPath),
                        StringComparison.OrdinalIgnoreCase),
                    _ => CompareName(left, right)
                };

                if (descending) result = -result;

                return result != 0 ? result : CompareName(left, right);
            });
        }

        private static int CompareName(FileBrowserEntry left, FileBrowserEntry right) =>
            string.Compare(left.Name, right.Name, StringComparison.CurrentCultureIgnoreCase);

        private static FileBrowserEntry? CreateEntry(FileSystemInfo info, bool showAllFiles)
        {
            FileAttributes attributes;

            try
            {
                attributes = info.Attributes;
            }
            catch (Exception)
            {
                // 断掉的符号链接 / 正在拔出的 U 盘：跳过这一条。
                return null;
            }

            if (!showAllFiles && (attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                return null;

            var isDirectory = (attributes & FileAttributes.Directory) != 0;

            var modified = DateTime.MinValue;

            try
            {
                modified = info.LastWriteTime;
            }
            catch (Exception)
            {
                // 读不到时间不影响列出这一项。
            }

            if (isDirectory)
            {
                return new FileBrowserEntry(
                    info.Name, info.FullName, FileBrowserKind.Folder, 0, modified);
            }

            var kind = KindOf(info.FullName);

            if (!showAllFiles && kind == FileBrowserKind.Other) return null;

            long size = 0;

            if (info is FileInfo file)
            {
                try
                {
                    size = file.Length;
                }
                catch (Exception)
                {
                    // 读不到大小就按 0 显示，仍然让它出现在列表里。
                }
            }

            return new FileBrowserEntry(info.Name, info.FullName, kind, size, modified);
        }

        private static FileBrowserKind KindOf(string path)
        {
            if (MediaFormats.IsVideoFile(path)) return FileBrowserKind.Video;
            if (MediaFormats.IsAudioFile(path)) return FileBrowserKind.Audio;
            if (MediaFormats.IsPlaylistFile(path)) return FileBrowserKind.Playlist;
            if (MediaFormats.IsSubtitleFile(path)) return FileBrowserKind.Subtitle;

            return FileBrowserKind.Other;
        }

        private static string DescribeDriveType(DriveType type) => type switch
        {
            DriveType.Fixed => "本地磁盘",
            DriveType.Removable => "可移动磁盘",
            DriveType.Network => "网络驱动器",
            DriveType.CDRom => "CD 驱动器",
            DriveType.Ram => "RAM 磁盘",
            _ => "驱动器"
        };

        /// <summary>文件大小：不到 1 KB 说字节数，避免出现"0 KB"。</summary>
        internal static string FormatSize(long bytes)
        {
            if (bytes < 0) bytes = 0;

            if (bytes < 1024) return bytes.ToString(CultureInfo.CurrentCulture) + " 字节";
            if (bytes < 1024L * 1024) return (bytes / 1024.0).ToString("0.#", CultureInfo.CurrentCulture) + " KB";
            if (bytes < 1024L * 1024 * 1024)
                return (bytes / 1024.0 / 1024.0).ToString("0.#", CultureInfo.CurrentCulture) + " MB";

            return (bytes / 1024.0 / 1024.0 / 1024.0).ToString("0.##", CultureInfo.CurrentCulture) + " GB";
        }
    }
}
