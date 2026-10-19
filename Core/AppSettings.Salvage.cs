using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

// 设置文件的"抢救"：语法坏在中间时逐项救回能读的字段，坏的记名字、用默认值。
// 从 Core/AppSettings.cs 按 ARCHITECTURE 17.2 的规矩整块搬过来：成员一个字没动，
// 字段（属性）仍留在主文件。

namespace 播放器.Core
{
    public sealed partial class AppSettings
    {
        /// <summary>
        /// 从一份读不出来的设置里把还能用的项尽量捞出来。
        /// <para>
        /// 做法是<b>逐项试</b>：把每个属性的原始 JSON 片段单独反序列化一遍，
        /// 成功的留在结果里、失败的把名字记下来。用 <see cref="Utf8JsonReader"/> 一项一项读，
        /// 所以<b>语法坏了也能救出前半部分</b>——读到坏的地方就停，并用
        /// <paramref name="restUnreadable"/> 说明"后面的确实没办法了"。
        /// </para>
        /// <para>返回 <c>null</c> 表示一项都没救回来（这时调用方照旧整份退回默认值并备份）。</para>
        /// </summary>
        internal static AppSettings? TrySalvage(
            string json, out int kept, out IReadOnlyList<string> lost, out bool restUnreadable)
        {
            kept = 0;
            restUnreadable = false;

            var lostNames = new List<string>();
            lost = lostNames;

            var bytes = Encoding.UTF8.GetBytes(json);

            var options = new JsonReaderOptions
            {
                // 手改过的文件常常是"多一个逗号"或"加了注释"，这两种先宽容一点
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            };

            var reader = new Utf8JsonReader(bytes, options);
            var recovered = new JsonObject();
            var current = string.Empty;

            try
            {
                if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;

                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndObject) break;

                    if (reader.TokenType != JsonTokenType.PropertyName)
                    {
                        // 结构已经乱了（比如少了个大括号），后面不再猜
                        restUnreadable = true;
                        break;
                    }

                    current = reader.GetString() ?? string.Empty;

                    // 先前进到"值"上：Skip 只跳过<b>当前 token 的子节点</b>，
                    // 停在属性名上它什么也不做（这一步漏了的话取到的片段是空的）
                    if (!reader.Read())
                    {
                        restUnreadable = true;
                        break;
                    }

                    var valueStart = (int)reader.TokenStartIndex;

                    // 取值：Skip 会把整个值（含子对象 / 数组）一起跳过；跳不过去就说明这里是坏的
                    reader.Skip();

                    var raw = Encoding.UTF8.GetString(bytes, valueStart, (int)reader.BytesConsumed - valueStart);

                    if (!KnownPropertyNames.Contains(current)) continue;   // 不认识的字段：正常忽略

                    if (TryRecoverProperty(current, raw) is { } value)
                    {
                        recovered[current] = value;
                        continue;
                    }

                    lostNames.Add(current);
                }
            }
            catch (JsonException)
            {
                // 文件在这一项上坏了：这一项和后面的都没了
                restUnreadable = true;

                if (current.Length > 0 && KnownPropertyNames.Contains(current) && !lostNames.Contains(current))
                    lostNames.Add(current);
            }

            if (recovered.Count == 0) return null;

            if (!TryFromJson(recovered.ToJsonString(), out var result)) return null;

            kept = recovered.Count;
            return result;
        }

        /// <summary>把单个属性的原始 JSON 片段试着重出来；重不出来返回 <c>null</c>。</summary>
        private static JsonNode? TryRecoverProperty(string name, string rawValue)
        {
            // 名字来自文件本身，用 JsonEncodedText 转义一下再拼，免得名字里有引号把片段拼坏。
            // 注意 Encode 出来的**不含**两侧引号，要自己加上。
            var fragment = "{\"" + JsonEncodedText.Encode(name).ToString() + "\":" + rawValue + "}";

            if (!TryFromJson(fragment, out var single)) return null;

            var node = JsonNode.Parse(single.ToJson());

            return node is JsonObject obj && obj.TryGetPropertyValue(name, out var value)
                ? value?.DeepClone()
                : null;
        }
    }
}
