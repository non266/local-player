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

// 网络这一层：带鉴权的 GET、重试、错误描述与下载图片。

namespace 播放器.Core
{
    public static partial class LrcApi
    {
        /// <summary>歌词来源文字（显示在侧栏「媒体信息」里，用户能看出这是网上取的）。</summary>
        private static string SourceText(string? detail) =>
            string.IsNullOrWhiteSpace(detail) ? "LrcApi 在线歌词" : "LrcApi 在线歌词 · " + detail.Trim();

        private static string SafeDetail(LyricHit hit)
        {
            var parts = new[] { hit.Title, hit.Artist }
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToArray();

            return parts.Length == 0 ? string.Empty : string.Join(" - ", parts);
        }

        // =====================================================================
        // 网络
        // =====================================================================

        /// <summary>
        /// 发一次 GET。鉴权策略是<b>先不带、被挑战了再带</b>：
        /// <list type="number">
        ///   <item>默认<b>不带</b> <c>Authorization</c>——很多实例的歌词接口压根不需要鉴权，
        ///   无脑带上只会在服务端日志里刷一片"未授权请求"；</item>
        ///   <item>收到 401/403 时带上重试一次，成功就记住"这台服务要带"；</item>
        ///   <item>带了反而被 5xx 时（服务端鉴权实现有 bug）去掉鉴权再试，并记下这台服务。</item>
        /// </list>
        /// <para>
        /// 鉴权头始终<b>逐请求</b>加、且只发给用户自己配的那台服务（见 <see cref="ShouldSendToken"/>）：
        /// 封面图常挂在第三方主机上，密钥不能跟着跑过去。
        /// </para>
        /// </summary>
        private static async Task<HttpResponseMessage> SendAsync(
            string url, Endpoint endpoint, CancellationToken cancellationToken)
        {
            var key = StateKey(endpoint);
            var canAuthenticate = ShouldSendToken(endpoint, url) && !IsTokenToxic(key);
            var sendToken = canAuthenticate && IsAuthNeeded(key);

            var response = await SendOnceAsync(url, sendToken ? endpoint.Token : null, cancellationToken)
                .ConfigureAwait(true);

            if (sendToken && (int)response.StatusCode >= 500)
            {
                var without = await SendOnceAsync(url, null, cancellationToken).ConfigureAwait(true);

                // ⚠ 只有"去掉鉴权就好了"才能断定是鉴权实现的问题。
                // 去掉鉴权也一样不行（比如这个接口自己就 500），那是接口本身坏了，
                // 不能因此把密钥丢掉——那会连累同一台服务上别的正常接口。实测踩过：
                // 批量搜索 500 被判成"鉴权有毒"，结果封面接口也不带头了、全都 401。
                if (without.IsSuccessStatusCode)
                {
                    response.Dispose();
                    MarkTokenToxic(key);

                    return without;
                }

                without.Dispose();

                return response;
            }

            if (!sendToken && canAuthenticate && IsAuthChallenge(response.StatusCode))
            {
                response.Dispose();

                var retry = await SendOnceAsync(url, endpoint.Token, cancellationToken).ConfigureAwait(true);

                // 带上之后不再被挑战，说明这个服务确实需要鉴权（下次直接带）
                if (!IsAuthChallenge(retry.StatusCode)) MarkAuthNeeded(key);

                return retry;
            }

            return response;
        }

        private static bool IsAuthChallenge(HttpStatusCode status) =>
            status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

        private static async Task<HttpResponseMessage> SendOnceAsync(
            string url, string? token, CancellationToken cancellationToken)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);

            if (!string.IsNullOrEmpty(token))
                request.Headers.TryAddWithoutValidation("Authorization", token);

            try
            {
                return await Client
                    .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                    .ConfigureAwait(true);
            }
            finally
            {
                request.Dispose();
            }
        }

        private static async Task<(string? Text, HttpStatusCode Status, string? Error)> GetStringAsync(
            string url, Endpoint endpoint, CancellationToken cancellationToken)
        {
            using var response = await SendAsync(url, endpoint, cancellationToken).ConfigureAwait(true);

            if (!response.IsSuccessStatusCode)
                return (null, response.StatusCode, await DescribeErrorAsync(response, cancellationToken).ConfigureAwait(true));

            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(true);

            return (text, response.StatusCode, null);
        }

        private static async Task<(byte[]? Data, HttpStatusCode Status, string? Error)> GetBytesAsync(
            string url, Endpoint endpoint, CancellationToken cancellationToken)
        {
            using var response = await SendAsync(url, endpoint, cancellationToken).ConfigureAwait(true);

            if (!response.IsSuccessStatusCode)
                return (null, response.StatusCode, await DescribeErrorAsync(response, cancellationToken).ConfigureAwait(true));

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(true);

            return (bytes, response.StatusCode, null);
        }

        /// <summary>
        /// 服务端出错时说一句人话。<b>5xx 要把响应体也读出来</b>——
        /// 自建实例崩掉时会直接吐一段 Python traceback，
        /// 那一行 <c>TypeError: ...</c> 比"HTTP 500"有用得多。
        /// </summary>
        private static async Task<string?> DescribeErrorAsync(
            HttpResponseMessage response, CancellationToken cancellationToken)
        {
            var status = (int)response.StatusCode;

            if (status < 500) return null;

            try
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(true);

                foreach (var line in body.Split('\n'))
                {
                    var text = line.Trim();

                    if (text.Length == 0) continue;
                    if (text.StartsWith("<", StringComparison.Ordinal)) break;   // HTML 错误页，没必要贴出来

                    if (text.Length > 200) text = text[..200] + "…";

                    return $"{status}（{text}）";
                }
            }
            catch (Exception ex)
            {
// 读不出响应体就只报状态码
                AppLog.Swallowed("读不出响应体就只报状态码", ex);
            }

            return status.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 下一张图，<b>失败不算致命</b>：返回 <c>null</c> 并把原因记进 <paramref name="note"/>。
        /// <para>
        /// 封面可能有好几个候选地址（搜索结果里的、封面接口给的），
        /// 其中一个下不下来（DNS、TLS、对方 403）不该让整条链断掉——换下一个继续试才是对的。
        /// </para>
        /// </summary>
        private static async Task<byte[]?> TryGetImageAsync(
            string url,
            Endpoint endpoint,
            CancellationToken cancellationToken,
            Action<string> note,
            Action<HttpStatusCode> checkAuth)
        {
            try
            {
                var (data, status, error) = await GetBytesAsync(url, endpoint, cancellationToken).ConfigureAwait(true);

                checkAuth(status);

                if (error != null) note(error);

                return data;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                note("封面地址拿到了但下载失败：" + ex.Message);
                return null;
            }
        }

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = Timeout };

            try
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
                client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
            }
            catch (Exception ex)
            {
// 头设置失败不影响使用
                AppLog.Swallowed("头设置失败不影响使用", ex);
            }

            return client;
        }
    }
}
