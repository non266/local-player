using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace 播放器.Core
{
    /// <summary>一行歌词。</summary>
    public sealed class LyricLine
    {
        public LyricLine(TimeSpan time, string text)
        {
            Time = time;
            Text = text;
        }

        /// <summary>这一行开始显示的时间点。</summary>
        public TimeSpan Time { get; }

        /// <summary>歌词正文。</summary>
        public string Text { get; }

        public override string ToString() => $"[{Time:hh\\:mm\\:ss\\.fff}] {Text}";
    }

    /// <summary>
    /// 一份歌词：可能带时间轴（LRC / SYLT），也可能是纯文本。
    /// <para>
    /// 纯文本歌词整段显示，不做高亮；带时间轴时按 <see cref="LineIndexAt"/> 定位当前行。
    /// </para>
    /// </summary>
    public sealed class LyricsDocument
    {
        /// <summary>时间戳：<c>[mm:ss.xx]</c>、<c>[mm:ss.xxx]</c>、<c>[hh:mm:ss.xx]</c> 都认。</summary>
        private static readonly Regex TimeTag = new(
            @"\[\s*(?:(\d{1,3}):)?(\d{1,2}):(\d{1,2})(?:[.:](\d{1,3}))?\s*\]",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>元数据标签：<c>[ti:歌名]</c>、<c>[offset:+500]</c> 之类。</summary>
        private static readonly Regex MetaTag = new(
            @"^\s*\[([a-zA-Z#]+)\s*:\s*([^\]]*)\]\s*$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>空歌词。</summary>
        public static LyricsDocument Empty { get; } = new(
            Array.Empty<LyricLine>(), string.Empty, TimeSpan.Zero, null, null, null, null, null);

        private readonly LyricLine[] _lines;

        private LyricsDocument(
            LyricLine[] lines,
            string plainText,
            TimeSpan offset,
            string? title,
            string? artist,
            string? album,
            string? createdBy,
            string? source)
        {
            _lines = lines;
            PlainText = plainText;
            Offset = offset;
            Title = title;
            Artist = artist;
            Album = album;
            CreatedBy = createdBy;
            Source = source;
        }

        /// <summary>按时间排序后的歌词行；纯文本歌词时为空。</summary>
        public IReadOnlyList<LyricLine> Lines => _lines;

        /// <summary>不带时间轴的歌词全文；带时间轴时为空。</summary>
        public string PlainText { get; }

        /// <summary>文件里声明的整体时间偏移（<c>[offset:]</c>），已经应用到各行时间上。</summary>
        public TimeSpan Offset { get; }

        /// <summary>LRC 头部信息。</summary>
        public string? Title { get; }

        public string? Artist { get; }

        public string? Album { get; }

        /// <summary>LRC 里的 <c>[by:]</c>，即歌词制作者。</summary>
        public string? CreatedBy { get; }

        /// <summary>歌词来源的说明文字，用于在界面上告诉用户这是哪儿来的。</summary>
        public string? Source { get; }

        /// <summary>是否带时间轴。</summary>
        public bool IsSynchronized => _lines.Length > 0;

        /// <summary>既没有时间轴也没有正文。</summary>
        public bool IsEmpty => _lines.Length == 0 && string.IsNullOrWhiteSpace(PlainText);

        /// <summary>
        /// 解析歌词文本。
        /// <para>
        /// 带时间戳的行按时间收集，不带时间戳的行在完全没有时间戳时当作纯文本歌词。
        /// <c>[offset:]</c> 的语义与常见 LRC 播放器一致：**正值表示歌词提前显示**，
        /// 所以最终时间是「时间戳 − offset」。
        /// </para>
        /// </summary>
        public static LyricsDocument Parse(string? text, string? source = null)
        {
            if (string.IsNullOrWhiteSpace(text)) return Empty;

            var entries = new List<LyricLine>();
            var plain = new List<string>();
            var offset = TimeSpan.Zero;

            string? title = null, artist = null, album = null, createdBy = null;

            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r').Trim();
                if (line.Length == 0)
                {
                    plain.Add(string.Empty);
                    continue;
                }

                // 一行可以挂多个时间戳，例如 [00:01.00][00:05.00]副歌
                var cursor = 0;
                var stamps = new List<TimeSpan>();

                while (true)
                {
                    while (cursor < line.Length && line[cursor] == ' ') cursor++;

                    var match = TimeTag.Match(line, cursor);
                    if (!match.Success || match.Index != cursor) break;

                    stamps.Add(ToTimeSpan(match));
                    cursor = match.Index + match.Length;
                }

                if (stamps.Count > 0)
                {
                    var content = line[cursor..].Trim();
                    foreach (var stamp in stamps) entries.Add(new LyricLine(stamp, content));
                    continue;
                }

                var meta = MetaTag.Match(line);
                if (meta.Success)
                {
                    var key = meta.Groups[1].Value.ToLowerInvariant();
                    var value = meta.Groups[2].Value.Trim();

                    switch (key)
                    {
                        case "offset":
                            if (int.TryParse(
                                    value.TrimStart('+'),
                                    NumberStyles.Integer,
                                    CultureInfo.InvariantCulture,
                                    out var milliseconds))
                                offset = TimeSpan.FromMilliseconds(milliseconds);
                            break;
                        case "ti":
                            title ??= value;
                            break;
                        case "ar":
                            artist ??= value;
                            break;
                        case "al":
                            album ??= value;
                            break;
                        case "by":
                            createdBy ??= value;
                            break;
                    }

                    continue;
                }

                plain.Add(line);
            }

            if (entries.Count == 0)
            {
                var body = string.Join("\n", plain).Trim();
                return body.Length == 0
                    ? Empty
                    : new LyricsDocument(Array.Empty<LyricLine>(), body, offset, title, artist, album, createdBy, source);
            }

            if (offset != TimeSpan.Zero)
            {
                for (var i = 0; i < entries.Count; i++)
                {
                    var shifted = entries[i].Time - offset;
                    entries[i] = new LyricLine(shifted < TimeSpan.Zero ? TimeSpan.Zero : shifted, entries[i].Text);
                }
            }

            // OrderBy 是稳定排序，同一时间戳的多行不会被打乱
            var sorted = entries.OrderBy(e => e.Time).ToArray();

            return new LyricsDocument(sorted, string.Empty, offset, title, artist, album, createdBy, source);
        }

        /// <summary>
        /// 返回一份"整行时间都平移过"的副本，用于歌词与声音对不上时的手动微调。
        /// <para>
        /// 参数与解析 <c>[offset:]</c> 时同号：<b>正值 = 歌词提前</b>（时间戳减掉偏移）。
        /// 提前到 0 之前的时间戳会被压到 0——和解析 <c>[offset:]</c> 时的处理一致：
        /// 让时间变成负数的话，界面上的时间码会显示成 <c>-00:00:00.5</c>，反而更费解。
        /// </para>
        /// <para>纯文本歌词没有时间轴，原样返回。</para>
        /// </summary>
        public LyricsDocument Shifted(TimeSpan offset)
        {
            if (offset == TimeSpan.Zero || _lines.Length == 0) return this;

            var shifted = new LyricLine[_lines.Length];

            for (var i = 0; i < _lines.Length; i++)
            {
                var time = _lines[i].Time - offset;
                if (time < TimeSpan.Zero) time = TimeSpan.Zero;

                shifted[i] = new LyricLine(time, _lines[i].Text);
            }

            return new LyricsDocument(shifted, PlainText, Offset, Title, Artist, Album, CreatedBy, Source);
        }

        /// <summary>
        /// 返回给定播放位置应该高亮的那一行下标；位置在第一行之前时返回 <c>-1</c>。
        /// <para>
        /// <b>同一个时间戳上的多行算"同一句"</b>（双语 LRC 就是两行：原文 + 译文），
        /// 返回的是这一组里的<b>第一行</b>。以前返回的是最后一行，于是译文成了"当前句"、
        /// 原文永远是暗的，而译文那一行也永远显示不出"当前"的样子——
        /// 两行里总有一行轮不到（见 <see cref="CompanionOf"/>）。
        /// </para>
        /// </summary>
        public int LineIndexAt(TimeSpan position)
        {
            if (_lines.Length == 0) return -1;

            var low = 0;
            var high = _lines.Length - 1;
            var result = -1;

            while (low <= high)
            {
                var middle = (low + high) / 2;

                if (_lines[middle].Time <= position)
                {
                    result = middle;
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            // 退回这一组的第一行（同一时间戳的多行是同一句）
            while (result > 0 && _lines[result - 1].Time == _lines[result].Time) result--;

            return result;
        }

        /// <summary>
        /// 第 <paramref name="index"/> 行的"同时间戳下一行"——双语歌词里通常是**译文**；
        /// 没有（下一行是别的时间戳，或已经是最后一行）时返回 <c>null</c>。
        /// </summary>
        public LyricLine? CompanionOf(int index)
        {
            if (index < 0 || index + 1 >= _lines.Length) return null;

            return _lines[index + 1].Time == _lines[index].Time ? _lines[index + 1] : null;
        }

        /// <summary>
        /// 跳过同一时间戳上的所有行之后，"下一句"的下标；没有下一句时返回 <c>-1</c>。
        /// <para>桌面歌词的"第三行"要用它：不能把译文当成"下一句"。</para>
        /// </summary>
        public int NextGroupStart(int index)
        {
            if (index < 0) return -1;

            var time = _lines[index].Time;

            for (var i = index + 1; i < _lines.Length; i++)
            {
                if (_lines[i].Time > time) return i;
            }

            return -1;
        }

        /// <summary>
        /// 解析 <c>[mm:ss.xx]</c> 时间戳。
        /// <para>
        /// 必须显式用 <see cref="CultureInfo.InvariantCulture"/>：默认重载走当前区域设置，
        /// 而正则里的 <c>\d</c> 在 .NET 下会匹配非 ASCII 数字（例如阿拉伯-印度数字），
        /// 同一个歌词文件在不同区域设置下可能解析成功也可能直接抛异常。
        /// </para>
        /// </summary>
        private static TimeSpan ToTimeSpan(Match match)
        {
            var hours = match.Groups[1].Success
                ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)
                : 0;
            var minutes = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            var seconds = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);

            var milliseconds = 0;
            if (match.Groups[4].Success)
            {
                // 1 位是十分之一秒，2 位是百分之一秒，3 位是毫秒
                var fraction = match.Groups[4].Value;
                milliseconds = fraction.Length switch
                {
                    1 => int.Parse(fraction, CultureInfo.InvariantCulture) * 100,
                    2 => int.Parse(fraction, CultureInfo.InvariantCulture) * 10,
                    _ => int.Parse(fraction[..3], CultureInfo.InvariantCulture)
                };
            }

            return new TimeSpan(0, hours, minutes, seconds, milliseconds);
        }
    }

    /// <summary>
    /// 按优先级寻找歌词：同目录 .lrc → 内嵌歌词 → 同目录 .txt。
    /// <para>
    /// 同一批来源里，**带时间轴的优先于纯文本**——毕竟能高亮滚动体验更好；
    /// 同类之间再按上面的顺序取（外挂 .lrc 用户可以自己改，所以排在前面）。
    /// </para>
    /// </summary>
    public static class LyricsLoader
    {
        /// <summary>内嵌歌词在界面上的来源说明。</summary>
        public const string EmbeddedSource = "内嵌歌词";

        /// <summary>为指定媒体文件寻找歌词；找不到时返回 <see cref="LyricsDocument.Empty"/>。</summary>
        public static LyricsDocument Load(string mediaPath, MediaTags? tags)
        {
            var sidecar = TryLoadFile(FindSidecar(mediaPath, ".lrc", tags));
            var embedded = tags is { HasLyrics: true }
                ? LyricsDocument.Parse(tags.Lyrics, EmbeddedSource)
                : null;

            if (sidecar is { IsSynchronized: true }) return sidecar;
            if (embedded is { IsSynchronized: true }) return embedded;
            if (sidecar is { IsEmpty: false }) return sidecar;
            if (embedded is { IsEmpty: false }) return embedded;

            // 最后才看同名的 .txt——有些抓轨资源只附一个纯文本歌词
            return TryLoadFile(FindSidecar(mediaPath, ".txt", tags)) ?? LyricsDocument.Empty;
        }

        private static LyricsDocument? TryLoadFile(string? path)
        {
            if (path == null) return null;

            try
            {
                var text = LegacyText.DecodeFile(File.ReadAllBytes(path));
                var document = LyricsDocument.Parse(text, Path.GetFileName(path));
                return document.IsEmpty ? null : document;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 找同名的外挂歌词：先试与媒体同名的文件，再试 <c>艺术家 - 曲名</c> 命名。
        /// </summary>
        private static string? FindSidecar(string mediaPath, string extension, MediaTags? tags)
        {
            var folder = Path.GetDirectoryName(mediaPath);
            if (string.IsNullOrEmpty(folder)) return null;

            var baseName = Path.GetFileNameWithoutExtension(mediaPath);

            var direct = Path.Combine(folder, baseName + extension);
            if (File.Exists(direct)) return direct;

            if (tags is { Artist.Length: > 0, Title.Length: > 0 })
            {
                var named = Path.Combine(folder, $"{tags.Artist} - {tags.Title}{extension}");
                if (File.Exists(named)) return named;
            }

            return null;
        }
    }
}
