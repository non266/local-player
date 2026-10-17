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

// 对外的数据形状：Endpoint / LyricHit / Result<T>（公开嵌套类型，签名一个字没动）。

namespace 播放器.Core
{
    public static partial class LrcApi
    {
        // =====================================================================
        // 数据
        // =====================================================================

        /// <summary>一个 LrcAPI 服务端点：地址 + 可选的鉴权密钥。</summary>
        public readonly struct Endpoint
        {
            public Endpoint(string? baseUrl, string? token)
            {
                BaseUrl = NormalizeBaseUrl(baseUrl);
                Token = NormalizeToken(token);
            }

            /// <summary>服务地址（已补协议头、去掉尾部斜杠）；空表示还没配。</summary>
            public string BaseUrl { get; }

            /// <summary>鉴权密钥；没填就是 <c>null</c>。</summary>
            public string? Token { get; }

            public bool IsConfigured => BaseUrl.Length > 0;

            public bool HasToken => !string.IsNullOrEmpty(Token);
        }

        /// <summary>批量搜索结果里的一条候选。</summary>
        public sealed class LyricHit
        {
            public LyricHit(string? id, string? title, string? artist, string? lyrics, string? coverUrl = null)
            {
                Id = id ?? string.Empty;
                Title = title ?? string.Empty;
                Artist = artist ?? string.Empty;
                Lyrics = lyrics ?? string.Empty;
                CoverUrl = string.IsNullOrWhiteSpace(coverUrl) ? null : coverUrl.Trim();
            }

            public string Id { get; }
            public string Title { get; }
            public string Artist { get; }
            public string Lyrics { get; }

            /// <summary>
            /// 这条结果附带的封面地址（字段名是 <c>cover</c>）。
            /// <para>
            /// ⚠ 实测：<b>封面优先从这里拿</b>。自建实例上封面接口经常是坏的
            /// （新版那个 302 到一个拼错的官方地址、旧版那个要鉴权甚至一律 401），
            /// 而搜索结果里的这个地址指向各家平台的图片，往往是唯一稳的一条路。
            /// </para>
            /// </summary>
            public string? CoverUrl { get; }

            /// <summary>这条候选是否带时间轴（<c>[mm:ss.xx]</c>）。</summary>
            public bool IsSynchronized => Lyrics.Contains('[') && Lyrics.Contains(']');
        }

        /// <summary>一次查找的结果：拿到的东西 + 一句给人看的话。</summary>
        public sealed class Result<T> where T : class
        {
            private Result(T? value, string message)
            {
                Value = value;
                Message = message;
            }

            public T? Value { get; }

            public string Message { get; }

            public bool Ok => Value != null;

            public static Result<T> Success(T value, string message) => new Result<T>(value, message);

            public static Result<T> Fail(string message) => new Result<T>(null, message);
        }
    }
}
