using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

// 网络位置的批量解析：把"拖进来的一批网络路径"分成目录（递归展开）、单个文件、
// 网络 m3u（读里面的条目）三类。所有 I/O 都是阻塞的，调用方负责丢到线程池。

namespace 播放器.Core
{
    /// <summary>
    /// 把一批<b>网络位置</b>（UNC / 网络盘上的路径）解析成可以加进播放列表的条目。
    /// <para>
    /// 为什么单独一层：网络位置的存在性检查会等满 SMB 超时（1.13.0 实测一次
    /// <c>File.Exists</c> 阻塞 68 秒，1.17.0 复测不可达地址 82～93 秒），而"这是不是一个目录"
    /// 只有问过磁盘才知道。这一层只做判定与枚举，<b>不碰任何控件</b>，由界面把它丢到线程池上跑
    /// （和 <see cref="PlaybackCoordinator"/> 的 <c>PathProbe</c> 同一套做法）。
    /// </para>
    /// <para>
    /// 三条规则都按"先看路径、再碰磁盘"的顺序：
    /// ① 扩展名是 m3u / m3u8 → 读条目，它自己不是媒体；
    /// ② 扩展名是媒体 / 字幕 → 直接按文件收下，<b>不做目录探测</b>
    /// （否则离线的网络文件也要白等一次超时）；
    /// ③ 扩展名不认识 → 才问"是不是目录"，是就递归枚举，不是就按一个条目收下
    /// （可能离线，也可能是没有扩展名的文件，播放前还会再探测一次）。
    /// </para>
    /// <para>
    /// 1.17.0：目录枚举换成 <see cref="MediaScanner.Walk"/>，带<b>深度 / 条数上限、取消、进度</b>。
    /// 取消时已经找到的条目照常返回（<see cref="NetworkPathImportResult.Cancelled"/>），
    /// 后面的路径一条都不再碰；触到上限时 <see cref="NetworkPathImportResult.Truncated"/> 为真，
    /// 宿主必须把"只加了一部分"说出来。
    /// </para>
    /// </summary>
    internal sealed class NetworkPathImport
    {
        /// <summary>
        /// "这个网络路径是不是目录"的探测方式。默认是 <see cref="Directory.Exists"/>；
        /// 测试可以替换掉，避免真的去连不可达的地址。
        /// </summary>
        internal Func<string, bool> IsDirectory { get; set; } = Directory.Exists;

        /// <summary>
        /// 枚举一个目录里的媒体文件。默认走 <see cref="MediaScanner.Walk"/>（有界、可取消）；
        /// 测试可以替换掉，避免真的去连不可达的地址。
        /// </summary>
        internal Func<string, MediaScanLimits, CancellationToken, Action<string>?, MediaScanResult> WalkFolder
        {
            get;
            set;
        } = (folder, limits, token, onFile) => MediaScanner.Walk(folder, limits, token, onFile);

        /// <summary>递归枚举的上限。<b>只对网络路径生效</b>（本地目录沿用无限的那条路）。</summary>
        internal MediaScanLimits Limits { get; set; } = new MediaScanLimits();

        /// <summary>读一个 m3u / m3u8（默认走 <see cref="PlaylistFile.Load"/>）。测试可以替换。</summary>
        internal Func<string, List<string>> ReadPlaylist { get; set; } = PlaylistFile.Load;

        /// <summary>
        /// 解析一批网络路径。<b>阻塞调用</b>：只做磁盘 I/O，不抛异常、不碰控件。
        /// </summary>
        /// <param name="token">取消：<b>当前这一条</b>的枚举会就地停，后面的路径不再处理。</param>
        /// <param name="onFound">每收下一条（枚举到的文件 / 直给的文件 / m3u 的条目）调一次，做进度用。</param>
        internal NetworkPathImportResult Run(
            IEnumerable<string> paths,
            CancellationToken token = default,
            Action<string>? onFound = null)
        {
            var result = new NetworkPathImportResult();
            if (paths == null) return result;

            foreach (var raw in paths)
            {
                if (token.IsCancellationRequested)
                {
                    result.Cancelled = true;
                    break;
                }

                if (string.IsNullOrWhiteSpace(raw)) continue;

                var path = raw.Trim('"', ' ');
                if (path.Length == 0) continue;

                try
                {
                    Import(path, result, token, onFound);
                }
                catch (Exception ex)
                {
                    // 单条失败不影响整批：照样按"一个文件"收下，至少不丢用户的路径
                    AppLog.Swallowed("网络位置解析失败，已按单个文件收下：" + path, ex);
                    result.Items.Add(path);
                    result.Failures.Add(path);
                }

                if (result.Cancelled) break;
            }

            return result;
        }

        private void Import(
            string path,
            NetworkPathImportResult result,
            CancellationToken token,
            Action<string>? onFound)
        {
            // ① m3u / m3u8：读它的条目。它自己绝不能变成列表里的一条媒体。
            if (MediaFormats.IsPlaylistFile(path))
            {
                try
                {
                    var entries = ReadPlaylist(path) ?? new List<string>();

                    if (entries.Count == 0)
                    {
                        result.EmptyPlaylists.Add(path);
                    }
                    else
                    {
                        result.FromPlaylists.AddRange(entries);

                        // 条目也是"找到的东西"：进度要跟着涨，不能等整批结束才动一下。
                        foreach (var entry in entries) onFound?.Invoke(entry);
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Swallowed("读取网络播放列表失败（可能离线或没有权限）：" + path, ex);
                    result.UnreadablePlaylists.Add(path);
                }

                return;
            }

            // ② 扩展名已经说明它是文件：不做目录探测。离线时这一步能省掉一次 SMB 超时。
            if (MediaFormats.IsMediaFile(path) || MediaFormats.IsSubtitleFile(path))
            {
                result.Items.Add(path);
                onFound?.Invoke(path);
                return;
            }

            // ③ 扩展名不认识：可能是目录，也可能是一个没有扩展名的文件（或离线路径）。
            if (token.IsCancellationRequested)
            {
                result.Cancelled = true;
                return;
            }

            bool isDirectory;

            try
            {
                isDirectory = IsDirectory(path);
            }
            catch (Exception ex)
            {
                AppLog.Swallowed("判断网络位置是不是目录时出错，已按单个文件收下：" + path, ex);
                result.Items.Add(path);
                result.Failures.Add(path);
                return;
            }

            if (!isDirectory)
            {
                // 离线时 Directory.Exists 会等满超时再返回 false，和"真的不是目录"分不开。
                // 照样收下一条：播放前的后台探测还会再试一次，条目不会丢。
                result.Items.Add(path);
                result.Unreachable.Add(path);
                onFound?.Invoke(path);
                return;
            }

            result.Folders.Add(path);

            // 目录探到了、枚举却失败（权限 / 中途掉线）：这条路径本身已经记进 Folders，
            // 不再补一条"按文件收下"——它确实是目录，加一条只会让用户看到一条打不开的目录。
            // 单层失败由 MediaScanner 自己跳过并记日志。
            var scan = WalkFolder(path, Limits, token, onFound);

            if (scan.Files.Count > 0) result.Items.AddRange(scan.Files);
            if (scan.Truncated) result.Truncated = true;
            if (scan.Cancelled) result.Cancelled = true;
        }
    }

    /// <summary><see cref="NetworkPathImport.Run"/> 的结果，按用途分桶（顺序与输入顺序一致）。</summary>
    internal sealed class NetworkPathImportResult
    {
        /// <summary>要加进播放列表的路径：目录里找到的媒体 + 直接给出的网络文件。</summary>
        internal List<string> Items { get; } = new();

        /// <summary>真的被当成目录扫过的路径（决定状态栏怎么说）。</summary>
        internal List<string> Folders { get; } = new();

        /// <summary>网络 m3u 里的条目（按「追加」语义进列表，不去重）。</summary>
        internal List<string> FromPlaylists { get; } = new();

        /// <summary>读得出来、但一条可用条目都没有的 m3u。</summary>
        internal List<string> EmptyPlaylists { get; } = new();

        /// <summary>读不出来的 m3u（离线 / 没有权限）。</summary>
        internal List<string> UnreadablePlaylists { get; } = new();

        /// <summary>没当成目录、扩展名又不认识的路径（多半是离线目录）——它们也在 <see cref="Items"/> 里。</summary>
        internal List<string> Unreachable { get; } = new();

        /// <summary>解析时抛过异常的路径（它们也在 <see cref="Items"/> 里）。</summary>
        internal List<string> Failures { get; } = new();

        /// <summary>被取消了：<see cref="Items"/> 里是取消之前已经找到的那些。</summary>
        internal bool Cancelled { get; set; }

        /// <summary>触到了深度 / 条数上限：结果不完整，宿主必须把这件事说出来。</summary>
        internal bool Truncated { get; set; }
    }
}
