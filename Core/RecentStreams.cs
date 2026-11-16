using System;
using System.Collections.Generic;
using System.Linq;

// 最近打开过的网络串流地址（1.23.0 F8）。
//
// 为什么单独一个纯逻辑类：菜单、设置、落盘加密三处都要用它，而"最近 10 条、去重、
// 最新在前、只收网络流"这几条规则本身与界面无关——放在这里就能直接断言。
//
// ⚠ 地址里常带凭据（http://user:pass@host/…）或一次性签名参数（?token=…）：
//   · 落盘一律走 DPAPI 密文（见 SecretProtector.ProtectStream / AppSettings.RecentStreamsStored）；
//   · 菜单与日志只显示 Describe() 的短标签（去掉 userinfo / query / fragment），
//     完整地址只在真的去播的时候用。

namespace 播放器.Core
{
    /// <summary>最近串流地址的纯逻辑：去重、MRU、上限、短标签。</summary>
    public static class RecentStreams
    {
        /// <summary>最多记多少条（菜单里的加速键只到 &9，第 10 条没有键）。</summary>
        public const int MaxEntries = 10;

        /// <summary>
        /// 把 <paramref name="values"/> 归一：只留网络流、去重（按完全相同的字符串）、
        /// 保持原顺序、最多 <see cref="MaxEntries"/> 条。
        /// </summary>
        public static List<string> Normalize(IEnumerable<string>? values)
        {
            var result = new List<string>();

            if (values == null) return result;

            foreach (var value in values)
            {
                var text = (value ?? string.Empty).Trim();

                if (text.Length == 0 || !MediaFormats.IsStreamUri(text)) continue;

                if (result.Contains(text, StringComparer.Ordinal)) continue;

                result.Add(text);

                if (result.Count >= MaxEntries) break;
            }

            return result;
        }

        /// <summary>把 <paramref name="url"/> 放到最前面（不收本地路径）；重复的移到最前，不新增。</summary>
        public static List<string> Push(IEnumerable<string>? current, string? url)
        {
            var values = new List<string>();

            if (!string.IsNullOrWhiteSpace(url)) values.Add(url);

            if (current != null) values.AddRange(current);

            return Normalize(values);
        }

        /// <summary>
        /// 菜单里显示的短标签：<c>scheme://host[:port]/…/最后一段</c>。
        /// 去掉 userinfo（账号口令）与 query / fragment（token 常在那里）。
        /// </summary>
        public static string Describe(string url)
        {
            var text = (url ?? string.Empty).Trim();

            if (text.Length == 0) return "（空地址）";

            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return Truncate(text);

            var port = uri.IsDefaultPort ? string.Empty : ":" + uri.Port;
            var label = uri.Scheme + "://" + uri.Host + port;

            var tail = uri.Segments.LastOrDefault()?.Trim('/');

            label += tail is { Length: > 0 } ? "/…/" + tail : "/…";

            return Truncate(label);
        }

        private static string Truncate(string text) => text.Length <= 60 ? text : text[..57] + "…";
    }
}
