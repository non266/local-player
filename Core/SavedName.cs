using System;
using System.Linq;

namespace 播放器.Core
{
    /// <summary>
    /// 「按名字存成一个文件」这类东西共用的名字规则（歌单库、设置方案库……）。
    /// <para>
    /// 两处的需求其实一模一样：非法字符要<b>指名道姓地挡</b>（不是默默换成下划线）、
    /// Windows 保留名（CON / NUL…）不能建、结尾的点和空格要收拾掉、长度要有上限、
    /// 列出来要按自然顺序（"方案2" 在 "方案10" 前面）。
    /// </para>
    /// <para>
    /// 这套规则原来长在 <see cref="PlaylistLibrary"/> 里，只有一处用。做设置方案时第二处要用，
    /// 于是抽到这里——<b>规则只有一份</b>，不会出现"歌单叫 CON 会被挡、方案叫 CON 却存下去了"
    /// 这种两个入口两套规矩的笑话。
    /// </para>
    /// </summary>
    public static class SavedName
    {
        /// <summary>名字长度上限。留足空间给目录路径（Windows 单段文件名上限是 255）。</summary>
        public const int MaximumLength = 80;

        /// <summary>Windows 保留设备名：叫这些名字的文件在资源管理器里根本建不出来。</summary>
        private static readonly string[] ReservedNames =
        {
            "CON", "PRN", "AUX", "NUL", "CLOCK$",
            "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        /// <summary>
        /// 把用户输入的名字收拾成可用的文件名。
        /// <para>
        /// 这里<b>不</b>默默替换非法字符：名字是用户自己起的，"劲歌/慢歌"被他打出来，
        /// 结果存成"劲歌_慢歌"、下次找不着，还不如直接告诉他不能带斜杠。
        /// 只有结尾的点和空格会被悄悄去掉——那是 Windows 自己也会吃掉的东西，
        /// 留着会让"输入的名字"和"磁盘上的名字"不一致。
        /// </para>
        /// <para><paramref name="kind"/> 是提示语里的主语（"歌单名" / "方案名"），只影响文案。</para>
        /// </summary>
        public static bool TryNormalize(string? raw, string kind, out string name, out string error)
        {
            name = string.Empty;
            error = string.Empty;

            var value = (raw ?? string.Empty).Trim();
            value = value.TrimEnd('.', ' ');

            if (value.Length == 0)
            {
                error = $"{kind}不能为空。";
                return false;
            }

            var invalid = System.IO.Path.GetInvalidFileNameChars();
            if (value.IndexOfAny(invalid) >= 0)
            {
                error = $"{kind}里不能包含这些字符：" + DescribeInvalidCharacters(invalid);
                return false;
            }

            if (value.Length > MaximumLength)
            {
                error = $"{kind}最多 {MaximumLength} 个字符（现在 {value.Length} 个）。";
                return false;
            }

            if (IsReserved(value))
            {
                error = $"「{value}」是 Windows 保留名，换一个吧。";
                return false;
            }

            name = value;
            return true;
        }

        /// <summary>把非法字符列出来给用户看（只列能打出来的，控制字符只说个数）。</summary>
        private static string DescribeInvalidCharacters(char[] invalid)
        {
            var printable = invalid.Where(c => c >= ' ').Distinct().Select(c => c.ToString()).ToList();
            var control = invalid.Count(c => c < ' ');

            var text = string.Join(" ", printable);
            if (control > 0) text += (text.Length > 0 ? " " : string.Empty) + $"以及 {control} 个控制字符";

            return text;
        }

        /// <summary>
        /// 名字（去掉扩展名的第一段）是不是 Windows 保留设备名。
        /// <para>"CON.m3u8" 同样建不出来，所以要先掐掉扩展名再比。</para>
        /// </summary>
        public static bool IsReserved(string name)
        {
            var dot = name.IndexOf('.');
            var stem = dot < 0 ? name : name.Substring(0, dot);

            return ReservedNames.Any(r => string.Equals(r, stem, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 自然顺序比较：名字里的数字按<b>数值</b>比，"歌单2" 排在 "歌单10" 前面
        /// （纯字符串比较会得到"歌单10"在前，和人眼的直觉相反）。其余部分按当前区域、不区分大小写。
        /// </summary>
        public static int CompareNatural(string? left, string? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left == null) return -1;
            if (right == null) return 1;

            var i = 0;
            var j = 0;

            while (i < left.Length && j < right.Length)
            {
                if (char.IsDigit(left[i]) && char.IsDigit(right[j]))
                {
                    var startI = i;
                    var startJ = j;

                    while (i < left.Length && char.IsDigit(left[i])) i++;
                    while (j < right.Length && char.IsDigit(right[j])) j++;

                    // 前导零不参与比较（"02" 和 "2" 是同一个数）
                    while (startI < i - 1 && left[startI] == '0') startI++;
                    while (startJ < j - 1 && right[startJ] == '0') startJ++;

                    var lengthI = i - startI;
                    var lengthJ = j - startJ;

                    if (lengthI != lengthJ) return lengthI - lengthJ;

                    for (var offset = 0; offset < lengthI; offset++)
                    {
                        var difference = left[startI + offset] - right[startJ + offset];
                        if (difference != 0) return difference;
                    }

                    continue;
                }

                var comparison = string.Compare(
                    left, i, right, j, 1, StringComparison.CurrentCultureIgnoreCase);

                if (comparison != 0) return comparison;

                i++;
                j++;
            }

            return (left.Length - i) - (right.Length - j);
        }
    }
}
