using System;
using System.Collections.Generic;
using System.IO;

namespace 播放器.Core
{
    /// <summary>
    /// 文件页缩略图的「封面字节」来源：内嵌图优先，其次同目录外挂图。
    /// <para>
    /// 这里只做 I/O，<b>不解码、不碰界面、不判断网络</b>：解码在 <c>Ui/CoverThumbnails</c> 里，
    /// 网络策略在视图侧（只有一处规则）。任何异常都吞掉并返回 <c>null</c>——
    /// 一个坏文件绝不能拖住整页缩略图。
    /// </para>
    /// <para>
    /// 同目录外挂图按目录缓存一份：300 个文件都要找同一张 <c>cover.jpg</c>，
    /// 每个文件各枚举一次目录是白白的几百次 I/O。
    /// </para>
    /// </summary>
    internal class CoverArtLoader
    {
        /// <summary>单张封面的字节上限；与 <see cref="TagReader.MaxCoverBytes"/> 同量级（12 MB）。</summary>
        internal const int MaxCoverBytes = 12 * 1024 * 1024;

        /// <summary>按目录记住的"外挂封面在哪"最多记几个目录，防长期浏览把字典撑大。</summary>
        private const int MaxFolders = 64;

        private readonly Dictionary<string, string?> _sidecarByFolder =
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 取这个文件的封面字节：内嵌图（ID3 / FLAC / MP4 / Ogg / APE / ASF 都走
        /// <see cref="TagReader"/>）→ 同目录外挂图；都没有返回 <c>null</c>。
        /// </summary>
        internal virtual byte[]? Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;

            try
            {
                var tags = TagReader.Read(path);

                if (tags.CoverArt is { Length: > 0 } art && art.Length <= MaxCoverBytes)
                    return art;

                var sidecar = SidecarOf(path);
                if (sidecar == null) return null;

                var info = new FileInfo(sidecar);

                if (!info.Exists || info.Length <= 0 || info.Length > MaxCoverBytes)
                    return null;

                return File.ReadAllBytes(sidecar);
            }
            catch (Exception ex)
            {
                AppLog.Swallowed("文件页缩略图读取封面失败：" + path, ex);
                return null;
            }
        }

        private string? SidecarOf(string path)
        {
            string? folder;

            try
            {
                folder = Path.GetDirectoryName(path);
            }
            catch (Exception)
            {
                // 路径本身不合法（带非法字符）：当作没有外挂封面
                return null;
            }

            if (string.IsNullOrEmpty(folder)) return null;

            if (_sidecarByFolder.TryGetValue(folder, out var cached)) return cached;

            var found = SidecarCover.FindInFolder(folder);

            // 满了就整份清掉重记：长期浏览不该让字典无限长，缓存价值也不值得做 LRU。
            if (_sidecarByFolder.Count >= MaxFolders) _sidecarByFolder.Clear();

            _sidecarByFolder[folder] = found;
            return found;
        }
    }
}
