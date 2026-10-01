using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

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

        /// <summary>判断路径是否为可打开的目标：本地存在的文件，或网络流地址。</summary>
        public static bool IsOpenable(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            if (IsStreamUri(path)) return true;
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
        /// </summary>
        public static List<string> EnumerateMediaFiles(string folder, bool recursive)
        {
            var result = new List<string>();
            if (!Directory.Exists(folder)) return result;

            try
            {
                var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                foreach (var file in Directory.EnumerateFiles(folder, "*", option))
                {
                    if (IsMediaFile(file)) result.Add(file);
                }
            }
            catch (UnauthorizedAccessException) { /* 忽略无权限的子目录 */ }
            catch (DirectoryNotFoundException) { /* 目录在枚举过程中消失 */ }
            catch (IOException) { /* 忽略个别读取失败 */ }

            result.Sort(StringComparer.CurrentCultureIgnoreCase);
            return result;
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
