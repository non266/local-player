// 字体 —— 改名（把一份样本字体的族名换掉，用来验"认的是内容不是文件名"）。
//
// 从 Program.Fonts.cs 按 ARCHITECTURE 17.2 的规矩整块搬过来：成员一个字没动。

namespace SmokeTest
{
    internal static partial class Program
    {
        /// <summary>
        /// 把字体文件里的 family 名字原地改成等长的占位名，返回这个新名字（失败返回 <c>null</c>）。
        /// <para>
        /// 只改 name 表里那几个记录里的字符，<b>长度一个字节都不变</b>：
        /// name 表的记录里有偏移和长度，改长度就得整体重排，等长替换则连校验和都不用管
        /// （字体加载器不校验 name 表）。
        /// </para>
        /// </summary>
        private static string? RenameFontFamily(string sourcePath, string targetPath, string placeholder = "Zzq")
        {
            try
            {
                var data = File.ReadAllBytes(sourcePath);

                if (data.Length < 12) return null;

                var tableCount = ReadUInt16(data, 4);
                var nameOffset = -1;

                for (var i = 0; i < tableCount; i++)
                {
                    var record = 12 + i * 16;
                    if (record + 16 > data.Length) break;

                    if (data[record] == 'n' && data[record + 1] == 'a' &&
                        data[record + 2] == 'm' && data[record + 3] == 'e')
                    {
                        nameOffset = (int)ReadUInt32(data, record + 8);
                        break;
                    }
                }

                if (nameOffset < 0 || nameOffset + 6 > data.Length) return null;

                var count = ReadUInt16(data, nameOffset + 2);
                var stringOffset = nameOffset + ReadUInt16(data, nameOffset + 4);

                string? family = null;

                for (var i = 0; i < count; i++)
                {
                    var record = nameOffset + 6 + i * 12;
                    if (record + 12 > data.Length) break;

                    var platform = ReadUInt16(data, record);
                    var nameId = ReadUInt16(data, record + 6);
                    var length = ReadUInt16(data, record + 8);
                    var offset = stringOffset + ReadUInt16(data, record + 10);

                    // 1 = 字体族名，3 = 唯一标识，4 = 完整名，6 = PostScript 名，16 = 排版用族名：
                    // 全都要改。只改族名的话，这个字体在身份上仍然"是 Arial"
                    // （唯一标识没变），GDI+ 有可能把它当成已经装过的那一个、
                    // 报出旧族名——实测在连着跑、字体库里有残留时就会这样。
                    if (nameId != 1 && nameId != 3 && nameId != 4 && nameId != 6 && nameId != 16) continue;
                    if (offset + length > data.Length || length == 0) continue;

                    var wide = platform == 3;             // Windows 平台的字符串是 UTF-16BE
                    var characters = wide ? length / 2 : length;

                    var replacement = placeholder + new string('z', Math.Max(0, characters - placeholder.Length));
                    replacement = replacement[..characters];

                    for (var c = 0; c < characters; c++)
                    {
                        var position = offset + (wide ? c * 2 : c);

                        if (wide)
                        {
                            data[position] = 0;
                            data[position + 1] = (byte)replacement[c];
                        }
                        else
                        {
                            data[position] = (byte)replacement[c];
                        }
                    }

                    if (nameId == 1) family = replacement;
                }

                if (family == null) return null;

                File.WriteAllBytes(targetPath, data);
                return family;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
