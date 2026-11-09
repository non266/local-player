using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace 播放器.Core
{
    /// <summary>
    /// 光驱里那张盘是哪一类。
    /// <para>
    /// 判据只用"根目录长什么样"，<b>不递归枚举</b>：这个判断在每次打开「文件 → 打开光盘」
    /// 时都会跑一遍，数据盘上递归扫一遍目录可能要好几百毫秒，菜单会明显卡一下。
    /// "真有可播的媒体文件吗"由 <see cref="OpticalDisc.HasPlayableMedia"/> 在真正打开时再问。
    /// </para>
    /// </summary>
    public enum DiscKind
    {
        /// <summary>没有盘，或者读不出来。</summary>
        None,

        /// <summary>DVD-Video：根目录有 <c>VIDEO_TS</c>。</summary>
        DvdVideo,

        /// <summary>蓝光：根目录有 <c>BDMV</c>。<b>1.11.0 只识别，不支持播放</b>（见 README 的已知限制）。</summary>
        BluRay,

        /// <summary>音频 CD：根目录只有 <c>*.cda</c> 那种。</summary>
        AudioCd,

        /// <summary>普通数据盘（可能一个媒体文件都没有，打开时才说）。</summary>
        Data
    }

    /// <summary>本机的一个光驱。没插盘时 <see cref="IsReady"/> 为假、<see cref="Kind"/> 为 <see cref="DiscKind.None"/>。</summary>
    public sealed class OpticalDiscDrive
    {
        public OpticalDiscDrive(string root, string label, bool isReady, DiscKind kind)
        {
            Root = root ?? string.Empty;
            Label = label ?? string.Empty;
            IsReady = isReady;
            Kind = kind;
        }

        /// <summary>带反斜杠的根目录，例如 <c>L:\</c>。</summary>
        public string Root { get; }

        /// <summary>盘符，例如 <c>L:</c>（libvlc 的 MRL 要的就是这一段）。</summary>
        public string Letter => Root.TrimEnd('\\');

        /// <summary>卷标；没有就是空串。</summary>
        public string Label { get; }

        /// <summary>有没有盘。</summary>
        public bool IsReady { get; }

        public DiscKind Kind { get; }

        /// <summary>菜单上那一行字，例如 <c>DVD 光盘 (L:)</c> 或 <c>L:（没有光盘）</c>。</summary>
        public string KindText => OpticalDisc.Describe(Kind, Letter);
    }

    /// <summary>
    /// 光驱与光盘的判定（纯逻辑，不碰播放器）。
    /// <para>
    /// 为什么单独抽出来：这套判定是"打开光盘"里唯一能稳定自动验证的部分——冒烟用一个临时目录
    /// 就能把四种盘判一遍，不必插一张真的 DVD；真盘那一段用环境变量
    /// <c>播放器_TEST_DISC=L:</c> 另外跑（没有盘就打印原因跳过，照 <c>播放器_TEST_VIDEO</c> 的先例）。
    /// </para>
    /// </summary>
    public static class OpticalDisc
    {
        /// <summary>列出本机的光驱（可能为空）。</summary>
        public static IReadOnlyList<OpticalDiscDrive> Detect()
        {
            var result = new List<OpticalDiscDrive>();

            DriveInfo[] drives;
            try
            {
                drives = DriveInfo.GetDrives();
            }
            catch (Exception)
            {
                // 枚举盘符都失败：当成"没有光驱"，不影响别的功能。
                return result;
            }

            foreach (var drive in drives)
            {
                if (drive.DriveType != DriveType.CDRom) continue;

                var root = drive.RootDirectory.FullName;
                var ready = false;
                string label = string.Empty;

                try
                {
                    ready = drive.IsReady;
                    if (ready) label = drive.VolumeLabel ?? string.Empty;
                }
                catch (Exception)
                {
                    // 拔盘 / 驱动异常：按"没有盘"处理。
                    ready = false;
                }

                var kind = ready ? Classify(root) : DiscKind.None;
                result.Add(new OpticalDiscDrive(root, label, ready, kind));
            }

            return result;
        }

        /// <summary>按根目录的结构判这张盘是哪一类。</summary>
        public static DiscKind Classify(string root) =>
            Classify(root, Directory.Exists, SafeListRoot);

        /// <summary>
        /// 按给定的探针判类型。<paramref name="directoryExists"/> 与 <paramref name="filesInRoot"/>
        /// 由调用方提供，测试就能拿临时目录装出四种盘来。
        /// </summary>
        public static DiscKind Classify(
            string root, Func<string, bool> directoryExists, Func<string, string[]> filesInRoot)
        {
            if (string.IsNullOrWhiteSpace(root)) return DiscKind.None;
            if (directoryExists == null || filesInRoot == null) return DiscKind.None;
            if (!directoryExists(root)) return DiscKind.None;

            if (directoryExists(Path.Combine(root, "VIDEO_TS"))) return DiscKind.DvdVideo;
            if (directoryExists(Path.Combine(root, "BDMV"))) return DiscKind.BluRay;

            try
            {
                if (filesInRoot(root).Any(f => f.EndsWith(".cda", StringComparison.OrdinalIgnoreCase)))
                    return DiscKind.AudioCd;
            }
            catch (Exception)
            {
                // 根目录读不出来：仍然当成数据盘，打开时再去问"有没有媒体文件"。
            }

            return DiscKind.Data;
        }

        /// <summary>这张盘上有没有能播的媒体文件（递归枚举，可能慢；只在真正打开时调）。</summary>
        public static bool HasPlayableMedia(string root)
        {
            if (string.IsNullOrWhiteSpace(root)) return false;

            try
            {
                return MediaFormats.EnumerateMediaFiles(root, recursive: true).Count > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 这一盘该给 libvlc 的 MRL；数据盘（按文件加进列表）返回空串。
        /// <para>
        /// 1.16.0 起 DVD 用 <c>dvd://</c>：停在菜单上，由 <see cref="DvdMenu"/> 负责方向键导航
        /// （真盘实测：菜单能显示、高亮能移动、能进子菜单）。只想直接看正片时用
        /// <see cref="BuildSimpleMrl"/>——那是 1.11.0 的老口径 <c>dvdsimple://</c>。
        /// </para>
        /// <para>
        /// ⚠ 两种 MRL 的标题编号不一样：<c>dvd://</c> 的 title 0 是菜单、正片从 1 开始；
        /// <c>dvdsimple://</c> 的 title 0 就是正片。别把两边的编号混着用。
        /// </para>
        /// </summary>
        public static string BuildMrl(DiscKind kind, string root)
        {
            var letter = LetterOf(root);
            if (letter.Length == 0) return string.Empty;

            switch (kind)
            {
                case DiscKind.DvdVideo: return "dvd:///" + letter;
                case DiscKind.AudioCd: return "cdda:///" + letter;
                case DiscKind.BluRay: return "bluray:///" + letter;
                default: return string.Empty;
            }
        }

        /// <summary>
        /// "直接播放主标题"的 MRL（<c>dvdsimple://</c>）：跳过菜单，也不用菜单导航。
        /// <para>菜单打不开时自动退回的也是这一条（见 <see cref="DvdMenu.ShouldFallBack"/>）。</para>
        /// </summary>
        public static string BuildSimpleMrl(DiscKind kind, string root)
        {
            var letter = LetterOf(root);
            if (letter.Length == 0) return string.Empty;

            return kind == DiscKind.DvdVideo ? "dvdsimple:///" + letter : string.Empty;
        }

        /// <summary>
        /// 从光盘 MRL 里取盘符（<c>dvd:///L:</c> → <c>L:</c>），取不到返回空串。
        /// <para>"回到菜单 / 直接播放主标题"要把当前条目换一种 MRL，得先从它身上认出是哪张盘。</para>
        /// </summary>
        public static string LetterOfMrl(string? location)
        {
            if (string.IsNullOrWhiteSpace(location)) return string.Empty;
            if (!MediaFormats.IsDiscLocation(location)) return string.Empty;

            try
            {
                var uri = new Uri(location);
                var letter = uri.AbsolutePath.Trim('/');
                return LetterOf(letter);
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        /// <summary>菜单 / 播放列表上那一行字。</summary>
        public static string Describe(DiscKind kind, string letter)
        {
            switch (kind)
            {
                case DiscKind.DvdVideo: return "DVD 光盘 (" + letter + ")";
                case DiscKind.AudioCd: return "音频 CD (" + letter + ")";
                case DiscKind.BluRay: return "蓝光光盘 (" + letter + ")";
                case DiscKind.Data: return "数据光盘 (" + letter + ")";
                default: return letter + "（没有光盘）";
            }
        }

        /// <summary>
        /// 从 MRL 反推一个能显示的名字，给 <see cref="PlaylistItem.DisplayName"/> 用
        /// （否则 <c>Path.GetFileName("dvdsimple:///L:")</c> 会得到 <c>L:</c>）。
        /// </summary>
        public static string DescribeLocation(string location)
        {
            if (string.IsNullOrWhiteSpace(location)) return location ?? string.Empty;

            try
            {
                var uri = new Uri(location);
                var letter = uri.AbsolutePath.Trim('/');

                switch (uri.Scheme.ToLowerInvariant())
                {
                    case "dvd":
                    case "dvdsimple":
                        return Describe(DiscKind.DvdVideo, letter);
                    case "cdda":
                        return Describe(DiscKind.AudioCd, letter);
                    case "bluray":
                        return Describe(DiscKind.BluRay, letter);
                    case "vcd":
                    case "vcdx":
                        return "VCD (" + letter + ")";
                    default:
                        return location;
                }
            }
            catch (Exception)
            {
                return location;
            }
        }

        /// <summary>从根目录里取出盘符（<c>L:\</c> → <c>L:</c>）。</summary>
        private static string LetterOf(string root)
        {
            if (string.IsNullOrWhiteSpace(root)) return string.Empty;

            var letter = root.Trim().TrimEnd('\\', '/');

            // 只认 "X:" 这种盘符形式：别把用户目录之类的东西拼成 dvd:///C:\Users…
            return letter.Length == 2 && letter[1] == ':' ? letter : string.Empty;
        }

        private static string[] SafeListRoot(string root)
        {
            try
            {
                return Directory.GetFiles(root);
            }
            catch (Exception)
            {
                return Array.Empty<string>();
            }
        }
    }
}
