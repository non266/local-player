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

// 端点、鉴权与来源的状态（按「地址 + 密钥」记）+ 地址与内容的纯函数。

namespace 播放器.Core
{
    public static partial class LrcApi
    {
        /// <summary>取一次"带鉴权被服务端 500、已改成不带鉴权重试"的提示（取完即清）。</summary>
        public static bool ConsumeTokenDroppedNotice()
        {
            if (!_tokenDroppedNotice) return false;

            _tokenDroppedNotice = false;
            return true;
        }

        private static bool IsTokenToxic(string key)
        {
            lock (StateLock) return TokenToxicKeys.Contains(key);
        }

        private static void MarkTokenToxic(string key)
        {
            lock (StateLock)
            {
                TokenToxicKeys.Add(key);
                AuthNeededKeys.Remove(key);
            }

            _tokenDroppedNotice = true;
        }

        private static bool IsAuthNeeded(string key)
        {
            lock (StateLock) return AuthNeededKeys.Contains(key);
        }

        private static void MarkAuthNeeded(string key)
        {
            lock (StateLock)
            {
                if (!TokenToxicKeys.Contains(key)) AuthNeededKeys.Add(key);
            }
        }

        /// <summary>这个来源（某条歌词路径 / 某个封面接口）是不是已经确认不可用了。</summary>
        internal static bool IsSourceDead(Endpoint endpoint, string source)
        {
            lock (StateLock)
            {
                return DeadSources.TryGetValue(StateKey(endpoint), out var dead) && dead.Contains(source);
            }
        }

        private static void MarkSourceDead(Endpoint endpoint, string source)
        {
            lock (StateLock)
            {
                var key = StateKey(endpoint);

                if (!DeadSources.TryGetValue(key, out var dead))
                {
                    dead = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    DeadSources[key] = dead;
                }

                dead.Add(source);
            }
        }

        /// <summary>
        /// 接口级失败（说明这个来源本身不可用，而不是这次没搜到）：
        /// 鉴权被拒、方法不允许、已下线、服务端报错。<b>404 不算</b>。
        /// </summary>
        private static bool IsSourceUnusable(HttpStatusCode status) =>
            status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                or HttpStatusCode.MethodNotAllowed or HttpStatusCode.Gone
                || (int)status >= 500;

        // =====================================================================
        // 地址与内容判定（纯函数，冒烟测试直接单测这几条）
        // =====================================================================

        /// <summary>
        /// 拼一个请求地址。地址为空时返回空串（调用方会给出"还没填地址"的提示）。
        /// <para>少了协议头就补 <c>https://</c>；参数为空白的一律不带上。</para>
        /// </summary>
        public static string BuildUrl(string? baseUrl, string path, params (string Name, string? Value)[] query)
        {
            var root = NormalizeBaseUrl(baseUrl);
            if (root.Length == 0) return string.Empty;

            var relative = path.StartsWith('/') ? path : "/" + path;

            var builder = new StringBuilder(root).Append(relative);
            var first = true;

            foreach (var (name, value) in query)
            {
                if (string.IsNullOrWhiteSpace(value)) continue;

                builder.Append(first ? '?' : '&');
                first = false;

                builder.Append(name).Append('=').Append(Uri.EscapeDataString(value.Trim()));
            }

            return builder.ToString();
        }

        /// <summary>
        /// 把用户填的服务地址收拾成可用的形式；没填就返回空串。
        /// <para>这里<b>故意没有默认值</b>：地址由用户自己填，程序不会私自把歌词查询发到某个第三方服务上。</para>
        /// </summary>
        public static string NormalizeBaseUrl(string? baseUrl)
        {
            var text = (baseUrl ?? string.Empty).Trim();

            if (text.Length == 0) return string.Empty;

            if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;

            return text.TrimEnd('/');
        }

        /// <summary>
        /// 填的服务地址看着像不像一个能用的地址。
        /// <para>
        /// 只做最基本的形状检查，但有一点必须挡住：<c>Uri.TryCreate</c> 会把
        /// "https://这不是地址" 也认成一个合法的（国际化域名）绝对地址，
        /// 所以主机名要求是 ASCII 的字母数字 / 点 / 短横线 / 端口那一套。
        /// </para>
        /// </summary>
        public static bool LooksLikeBaseUrl(string? baseUrl)
        {
            var text = NormalizeBaseUrl(baseUrl);

            if (text.Length == 0 || text.Contains(' ') || text.Contains('\t')) return false;

            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return false;

            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;

            if (uri.Host.Length == 0) return false;

            foreach (var ch in uri.Host)
            {
                var ok = char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or ':' or '[' or ']';

                if (!ok) return false;
            }

            return true;
        }

        /// <summary>密钥去掉首尾空白；空串当作没填。</summary>
        public static string? NormalizeToken(string? token)
        {
            var text = (token ?? string.Empty).Trim();

            return text.Length == 0 ? null : text;
        }

        /// <summary>
        /// 鉴权密钥只发给<b>用户自己配的那个服务</b>，绝不发给第三方。
        /// <para>
        /// 这一点很重要：搜索结果里的封面图往往挂在 <c>p*.music.126.net</c> 这类<b>外部主机</b>上，
        /// 图省事把 Authorization 一股脑带上，就等于把用户的密钥交给了第三方。
        /// </para>
        /// </summary>
        public static bool ShouldSendToken(Endpoint endpoint, string? url)
        {
            if (!endpoint.HasToken || string.IsNullOrWhiteSpace(url)) return false;

            if (!Uri.TryCreate(url, UriKind.Absolute, out var target)) return false;
            if (!Uri.TryCreate(endpoint.BaseUrl, UriKind.Absolute, out var server)) return false;

            return target.Port == server.Port &&
                   string.Equals(target.Host, server.Host, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 401 时该怎么说。
        /// <para>
        /// 关键区别：<b>没填密钥</b>当然是叫他去填；<b>已经填了还是 401</b> 就不能再喊"去填密钥"——
        /// 那说明这个接口不认我们的密钥（或者密钥不对、或者它只认网页登录的 Cookie）。
        /// </para>
        /// </summary>
        private static string DescribeAuth(Endpoint endpoint) =>
            endpoint.HasToken
                ? "服务端拒绝了鉴权（HTTP 401）：密钥没被接受——检查密钥是否正确，"
                  + "或者这个接口不认 Authorization 头（有的实例只认网页登录的 Cookie）"
                : AuthRequiredMessage;

        /// <summary>响应看着像不像 LRC 歌词（有字，且不是一段 HTML）。</summary>
        public static bool LooksLikeLyrics(string? text) =>
            !string.IsNullOrWhiteSpace(text) &&
            !text.TrimStart().StartsWith("<", StringComparison.Ordinal);

        /// <summary>字节流看着像不像图片（封面接口有可能重定向到一张错误页）。</summary>
        public static bool LooksLikeImage(byte[]? data)
        {
            if (data is not { Length: > 8 }) return false;

            if (data[0] == 0xFF && data[1] == 0xD8) return true;                                      // JPEG
            if (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47) return true; // PNG
            if (data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46) return true;                   // GIF
            if (data[0] == 0x42 && data[1] == 0x4D) return true;                                      // BMP

            return data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46 &&
                   data[8] == 0x57 && data[9] == 0x45 && data[10] == 0x42 && data[11] == 0x50;        // WEBP
        }

        /// <summary>取图片扩展名对应的 MIME（下载回来的封面要交给 GDI+ 解码）。</summary>
        public static string ImageMimeType(byte[]? data)
        {
            if (data is not { Length: > 4 }) return "image/jpeg";

            if (data[0] == 0x89 && data[1] == 0x50) return "image/png";
            if (data[0] == 0x47 && data[1] == 0x49) return "image/gif";
            if (data[0] == 0x42 && data[1] == 0x4D) return "image/bmp";

            if (data[0] == 0x52 && data[1] == 0x49 && data.Length > 11 && data[8] == 0x57)
                return "image/webp";

            return "image/jpeg";
        }
    }
}
