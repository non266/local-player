using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace 播放器.Core
{
    /// <summary>
    /// 集中维护播放器支持的媒体/字幕扩展名与文件对话框过滤器。
    /// </summary>
    public static class MediaFormats
    {
        /// <summary>视频扩展名（小写，含点）。只在 <see cref="BuildPattern"/> 与下面的集合里用。</summary>
        private static readonly string[] VideoExtensions =
        {
            ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".mpg", ".mpeg",
            ".ts", ".m2ts", ".mts", ".rmvb", ".rm", ".3gp", ".3g2", ".vob", ".ogv", ".asf",
            ".divx", ".f4v", ".mxf", ".dv", ".wtv", ".amv"
        };

        /// <summary>音频扩展名（小写，含点）。</summary>
        private static readonly string[] AudioExtensions =
        {
            ".mp3", ".flac", ".wav", ".aac", ".m4a", ".m4b", ".ogg", ".oga", ".opus", ".wma",
            ".ape", ".alac", ".aiff", ".aif", ".mid", ".midi", ".mka", ".ac3", ".dts", ".amr",
            ".tak", ".wv", ".tta", ".spx"
        };

        /// <summary>字幕扩展名（小写，含点）。</summary>
        private static readonly string[] SubtitleExtensions =
        {
            ".srt", ".ass", ".ssa", ".sub", ".idx", ".vtt", ".smi", ".sup", ".txt", ".lrc"
        };

        /// <summary>播放列表扩展名（小写，含点）。</summary>
        private static readonly string[] PlaylistExtensions = { ".m3u", ".m3u8" };

        private static readonly HashSet<string> VideoSet = new HashSet<string>(VideoExtensions, StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> AudioSet = new HashSet<string>(AudioExtensions, StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> SubtitleSet = new HashSet<string>(SubtitleExtensions, StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> PlaylistSet = new HashSet<string>(PlaylistExtensions, StringComparer.OrdinalIgnoreCase);

        /// <summary>所有可播放的媒体扩展名。</summary>
        private static IEnumerable<string> AllMediaExtensions => VideoExtensions.Concat(AudioExtensions);

        /// <summary>判断是否为可播放的媒体文件（按扩展名）。</summary>
        public static bool IsMediaFile(string? path)
        {
            var ext = GetExtension(path);
            return ext.Length > 0 && (VideoSet.Contains(ext) || AudioSet.Contains(ext));
        }

        /// <summary>
        /// 判断是否为视频文件（按扩展名）。
        /// <para>用来把"要画面"的东西和"要歌词"的东西分开：视频不去歌词服务里搜
        /// （电影名搜回来的多半只是一首同名的歌）。</para>
        /// </summary>
        public static bool IsVideoFile(string? path)
        {
            var ext = GetExtension(path);
            return ext.Length > 0 && VideoSet.Contains(ext);
        }

        /// <summary>判断是否为音频文件（按扩展名）。</summary>
        public static bool IsAudioFile(string? path)
        {
            var ext = GetExtension(path);
            return ext.Length > 0 && AudioSet.Contains(ext);
        }

        /// <summary>判断是否为字幕文件（按扩展名）。</summary>
        public static bool IsSubtitleFile(string? path)
        {
            var ext = GetExtension(path);
            return ext.Length > 0 && SubtitleSet.Contains(ext);
        }

        /// <summary>
        /// 判断是否为 m3u / m3u8 播放列表文件（按扩展名）。
        /// <para>
        /// 用来把"列表"和"媒体"分开：把 m3u 当成媒体丢给播放器，只会得到一条"无法播放"
        /// （用户明明是想把里面的条目加进来）。
        /// </para>
        /// </summary>
        public static bool IsPlaylistFile(string? path)
        {
            var ext = GetExtension(path);
            return ext.Length > 0 && PlaylistSet.Contains(ext);
        }

        /// <summary>判断是否为网络流地址。</summary>
        public static bool IsStreamUri(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            if (!Uri.TryCreate(path, UriKind.Absolute, out var uri)) return false;

            switch (uri.Scheme.ToLowerInvariant())
            {
                case "http":
                case "https":
                case "rtsp":
                case "rtmp":
                case "rtmps":
                case "ftp":
                case "mms":
                case "udp":
                case "rtp":
                case "sftp":
                case "smb":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// 给日志用的路径描述（1.21.0）：本地路径原样；网络流只留 <c>scheme://host:port/…</c>。
        /// <para>
        /// 流地址里经常带凭据（<c>http://user:pass@host/…</c>，或者一次性签名参数），
        /// 日志是拿来发给别人排查的，不能把口令一起塞进去。1.20.0 把 LrcAPI 的密钥
        /// 从日志里赶了出去，这条线是同一个规矩：<b>只记"打开了哪个网络流"，不记凭据</b>。
        /// </para>
        /// </summary>
        public static string DescribeForLog(string? path)
        {
            if (!IsStreamUri(path)) return path ?? string.Empty;

            if (!Uri.TryCreate(path, UriKind.Absolute, out var uri)) return "（网络流）";

            var port = uri.IsDefaultPort ? string.Empty : ":" + uri.Port;

            return uri.Scheme + "://" + uri.Host + port + "/…";
        }

        /// <summary>
        /// 判断是否为光盘 / 设备位置（libvlc 的 location 型 MRL）。
        /// <para>
        /// 这些地址<b>不是文件</b>：它们没有扩展名、不参与标签 / 歌词 / 时长扫描、也不该记播放进度——
        /// 但 libvlc 认得它们，而且必须用 <c>libvlc_media_new_location</c> 打开（走 <c>FromPath</c> 会失败）。
        /// </para>
        /// </summary>
        public static bool IsDiscLocation(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            if (!Uri.TryCreate(path, UriKind.Absolute, out var uri)) return false;

            switch (uri.Scheme.ToLowerInvariant())
            {
                case "dvd":
                case "dvdsimple":
                case "cdda":
                case "bluray":
                case "vcd":
                case "vcdx":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// 网络流或光盘：凡是"不是本地文件"的媒体都算。
        /// <para>历史记录、标签、歌词、同目录字幕这些"按文件来"的功能一律跳过它们。</para>
        /// </summary>
        public static bool IsLocationUri(string? path) => IsStreamUri(path) || IsDiscLocation(path);

        /// <summary>
        /// 判断是否为<b>网络位置</b>：UNC 路径（<c>\\server\share</c>）或映射到网络盘的盘符。
        /// <para>
        /// 只看路径本身，<b>一次磁盘都不碰</b>：UNC 前缀直接认，盘符走
        /// <see cref="DriveInfo.DriveType"/>（GetDriveType，不会去连服务器）。
        /// </para>
        /// <para>
        /// 为什么要单独判它：网络盘离线时，一次 <c>File.Exists</c> 要等满 SMB 超时（几十秒），
        /// 而播放那一路是在界面线程上跑的——列表里几百条就能把窗口冻死。判断"在不在"必须
        /// 交给后台（见 <c>PlaybackCoordinator</c> 的网络路径探测）。
        /// </para>
        /// </summary>
        public static bool IsNetworkPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;

            // UNC：\\server\share 与 //server/share（Win32 路径两种都认）
            if (path.StartsWith(@"\\", StringComparison.Ordinal) ||
                path.StartsWith("//", StringComparison.Ordinal))
            {
                return true;
            }

            // 盘符：C:\... / Z:/...
            if (path.Length < 2 || path[1] != ':' || !char.IsLetter(path[0])) return false;

            try
            {
                return new DriveInfo(char.ToUpperInvariant(path[0]) + ":\\").DriveType == DriveType.Network;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
        }

        /// <summary>判断路径是否为可打开的目标：本地存在的文件、网络流地址，或光盘位置。</summary>
        /// <remarks>
        /// 网络位置（UNC / 网络盘）这里<b>只按"可能打开"返回 true，不同步查磁盘</b>：
        /// 真正在不在由播放前的后台探测决定（离线的条目要留在列表里，不能悄悄丢掉）。
        /// </remarks>
        public static bool IsOpenable(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            if (IsLocationUri(path)) return true;
            if (IsNetworkPath(path)) return true;
            return File.Exists(path);
        }

        /// <summary>"打开文件"对话框的过滤器。</summary>
        public static string OpenFileDialogFilter => string.Concat(
            "媒体文件|", BuildPattern(AllMediaExtensions), "|",
            "视频文件|", BuildPattern(VideoExtensions), "|",
            "音频文件|", BuildPattern(AudioExtensions), "|",
            "所有文件|*.*");

        /// <summary>"加载字幕"对话框的过滤器。</summary>
        public static string SubtitleDialogFilter => string.Concat(
            "字幕文件|", BuildPattern(SubtitleExtensions), "|",
            "所有文件|*.*");

        /// <summary>m3u 播放列表对话框（另存为 / 打开）的过滤器。</summary>
        public static string PlaylistDialogFilter =>
            "播放列表 (*.m3u;*.m3u8)|*.m3u;*.m3u8|所有文件|*.*";

        /// <summary>
        /// 递归/非递归枚举目录下的媒体文件，返回按文件名排序的结果。
        /// <para>
        /// 真正的遍历在 <see cref="MediaScanner"/>（1.17.0）：这里给"无限"参数，
        /// 行为与以前一致（顺序、异常口径、排序都不变）。网络那一路走
        /// <see cref="MediaScanner.Walk"/> 的有限参数，本方法保持给本地目录用。
        /// </para>
        /// </summary>
        public static List<string> EnumerateMediaFiles(string folder, bool recursive)
        {
            var limits = MediaScanLimits.Unlimited();

            // 旧的 recursive=false 是"只看这一层"：根目录算第 0 层，不许往下。
            if (!recursive) limits.MaxDepth = 0;

            return MediaScanner.Walk(folder, limits, CancellationToken.None, null).Files;
        }

        private static string GetExtension(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            try
            {
                return Path.GetExtension(path) ?? string.Empty;
            }
            catch (ArgumentException)
            {
                return string.Empty;
            }
        }

        private static string BuildPattern(IEnumerable<string> extensions)
        {
            return string.Join(";", extensions.Select(e => "*" + e));
        }
    }
}
