using System;
using System.Security.Cryptography;
using System.Text;

// 凭据的落盘保护（1.20.0）。
//
// 只有一件事：把 settings.json 里的 LrcApi 鉴权密钥从"明文"换成"用当前 Windows 用户
// 的 DPAPI 加密过的密文"。为什么值得做：那个文件本来就不加密，密钥明文躺在磁盘上；
// 备份、同步盘、发截图时它都会跟着走。DPAPI 的当前用户作用域不需要管理员、也不需要
// 额外依赖（ProtectedData 随 WindowsDesktop 共享框架自带）。
//
// 代价写在 README 里：密文只在这台机器、这个 Windows 用户下解得开；换机器 / 换用户
// 会解不出来——那时按"没配密钥"处理（服务端要鉴权就明说），而不是整个程序崩掉。
// 另外"导出设置到文件"是**故意**写成明文的：那份文件的目的就是拿到别的机器上用。

namespace 播放器.Core
{
    /// <summary>
    /// 字符串的 DPAPI 保护。密文带 <c>dpapi:</c> 前缀，明文没有前缀——
    /// 这样老设置文件（明文）读进来仍然可用，下次保存时自动变成密文。
    /// </summary>
    public static class SecretProtector
    {
        private const string Prefix = "dpapi:";

        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("播放器.LrcApiToken.v1");

        private static bool _warnedProtect;
        private static bool _warnedUnprotect;

        /// <summary>冒烟测试用：模拟"这台机器上 DPAPI 不可用"。默认 false。</summary>
        internal static bool FailForTest;

        public static bool IsProtected(string? value) =>
            value != null && value.StartsWith(Prefix, StringComparison.Ordinal);

        /// <summary>
        /// 加密。空串原样返回；已经是密文的不再套一层。
        /// <para>加密失败时**返回明文并记一次日志**：宁可退回"和以前一样"，也不让用户
        /// 填好的密钥因为一次 DPAPI 故障就悄悄消失。</para>
        /// </summary>
        public static string Protect(string? plain)
        {
            var text = plain ?? string.Empty;

            if (text.Length == 0) return string.Empty;
            if (IsProtected(text)) return text;

            if (FailForTest)
            {
                if (!_warnedProtect)
                {
                    _warnedProtect = true;
                    AppLog.Warn("LrcApi 密钥：这台机器上加密不可用（测试模拟），按明文保存");
                }

                return text;
            }

            try
            {
                var bytes = ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(text), Entropy, DataProtectionScope.CurrentUser);

                return Prefix + Convert.ToBase64String(bytes);
            }
            catch (Exception ex)
            {
                if (!_warnedProtect)
                {
                    _warnedProtect = true;
                    AppLog.Warn("LrcApi 密钥：加密不了（" + ex.GetType().Name + "），这次按明文保存", ex);
                }

                return text;
            }
        }

        /// <summary>
        /// 解密。<b>没有前缀的当明文原样返回</b>（老文件）；带前缀但解不开的返回空串 +
        /// 一次日志——那是别的用户 / 别的机器存下的，留着也解不开，界面上要按"未配置"处理。
        /// </summary>
        public static string Unprotect(string? stored)
        {
            var text = stored ?? string.Empty;

            if (!IsProtected(text)) return text;

            if (FailForTest)
            {
                if (!_warnedUnprotect)
                {
                    _warnedUnprotect = true;
                    AppLog.Warn("LrcApi 密钥：密文读不出来（测试模拟），按未配置处理");
                }

                return string.Empty;
            }

            try
            {
                var raw = Convert.FromBase64String(text.Substring(Prefix.Length));
                var bytes = ProtectedData.Unprotect(raw, Entropy, DataProtectionScope.CurrentUser);

                return Encoding.UTF8.GetString(bytes);
            }
            catch (Exception)
            {
                if (!_warnedUnprotect)
                {
                    _warnedUnprotect = true;
                    AppLog.Warn("LrcApi 密钥：密文读不出来（可能是别的 Windows 用户 / 别的机器存下的），"
                                + "已按未配置处理——要用的服务请重新填一次密钥");
                }

                return string.Empty;
            }
        }
    }
}
