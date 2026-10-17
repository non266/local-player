using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

// JSON 解析与候选打分（纯函数，冒烟测试直接单测这几条）。

namespace 播放器.Core
{
    public static partial class LrcApi
    {
        /// <summary>把 <c>/lyrics/advance</c> 的 JSON 解析成候选列表；解析不了就返回空列表。</summary>
        public static IReadOnlyList<LyricHit> ParseLyricsJson(string? json)
        {
            var hits = new List<LyricHit>();

            if (string.IsNullOrWhiteSpace(json)) return hits;

            try
            {
                using var document = JsonDocument.Parse(json);

                if (document.RootElement.ValueKind != JsonValueKind.Array) return hits;

                foreach (var element in document.RootElement.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.Object) continue;

                    // 封面字段：搜索结果里叫 cover，封面接口的响应里叫 img，两个都认
                    var cover = ReadString(element, "cover") ?? ReadString(element, "img");

                    hits.Add(new LyricHit(
                        ReadString(element, "id"),
                        ReadString(element, "title"),
                        ReadString(element, "artist"),
                        ReadString(element, "lyrics"),
                        cover));
                }
            }
            catch (JsonException)
            {
                // 对面返回的不是 JSON（例如一段 HTML 错误页）：当作没有候选
                return new List<LyricHit>();
            }

            return hits;
        }

        /// <summary>从封面接口的 JSON 里取出 <c>img</c>（图片地址）；取不到返回 <c>null</c>。</summary>
        public static string? ParseCoverJson(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;

            try
            {
                using var document = JsonDocument.Parse(json);

                if (document.RootElement.ValueKind != JsonValueKind.Object) return null;

                var image = ReadString(document.RootElement, "img");

                return string.IsNullOrWhiteSpace(image) ? null : image.Trim();
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// 从候选里挑一条最像的。
        /// <para>
        /// 打分而不是"取第一条"：批量搜索会把各种翻唱、现场版一起返回，歌手对不上的那条经常排在前面。
        /// 带时间轴的优先——没有时间轴的歌词在桌面歌词上只能退化成纯文本，等于半个废品。
        /// </para>
        /// </summary>
        public static LyricHit? PickBest(IReadOnlyList<LyricHit>? hits, string? title, string? artist)
        {
            if (hits == null || hits.Count == 0) return null;

            LyricHit? best = null;
            var bestScore = int.MinValue;

            foreach (var hit in hits)
            {
                if (string.IsNullOrWhiteSpace(hit.Lyrics)) continue;

                var score = (hit.IsSynchronized ? 40 : 0)
                            + Similarity(hit.Title, title) * 2
                            + Similarity(hit.Artist, artist);

                if (score <= bestScore) continue;

                bestScore = score;
                best = hit;
            }

            return best;
        }

        /// <summary>
        /// 0~20 的粗相似度：完全一样 20、去掉了空格标点之后一样 18、互相包含 12。
        /// <para>不追求精确：接口返回的标题经常带 <c>（Live）</c>、<c>- Remastered</c> 之类的尾巴。</para>
        /// </summary>
        private static int Similarity(string? left, string? right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return 0;

            var a = left.Trim();
            var b = right.Trim();

            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return 20;

            if (Normalize(a) == Normalize(b)) return 18;

            if (a.Contains(b, StringComparison.OrdinalIgnoreCase) ||
                b.Contains(a, StringComparison.OrdinalIgnoreCase))
            {
                return 12;
            }

            return 0;
        }

        /// <summary>去掉空格与常见标点、统一大小写，用来比较"看起来一样"的标题。</summary>
        private static string Normalize(string text)
        {
            var builder = new StringBuilder(text.Length);

            foreach (var ch in text)
            {
                if (char.IsWhiteSpace(ch) || char.IsPunctuation(ch) || char.IsSymbol(ch)) continue;

                builder.Append(char.ToLowerInvariant(ch));
            }

            return builder.ToString();
        }

        /// <summary>小工具：从 JSON 对象里读一个字符串字段（不存在 / 类型不对都返回 <c>null</c>）。</summary>
        private static string? ReadString(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out var value)) return null;

            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.ToString(),
                _ => null
            };
        }
    }
}
