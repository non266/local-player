using System;
using System.Windows.Forms;
using 播放器.Core;

namespace 播放器
{
    /// <summary>
    /// 网络流 / 网络位置打开得慢时的等待提示（1.21.0）。
    /// <para>
    /// 判定与状态机在 <see cref="SlowOpenWatch"/>（纯逻辑，能直接断言）；这里只做三件事：
    /// 把引擎状态喂给它、说一句状态栏、往诊断日志里留一行"打开超过 X 秒"。
    /// </para>
    /// <para>
    /// ⚠ 日志里<b>不写地址</b>：网络流地址经常自带凭据（<c>http://user:pass@host/…</c>），
    /// 1.20.0 刚把 LrcAPI 的密钥从日志里赶出去，不能再从这条路上带回来。
    /// 状态栏那句也只有"慢 / 没响应"，没有地址。
    /// </para>
    /// </summary>
    public partial class MainForm : Form
    {
        /// <summary>流（http / rtsp …）的措辞。</summary>
        private const string SlowStreamMessage = "这个网络流还没有播放起来（网络慢或对方没响应；按「停止」放弃）";

        /// <summary>网络位置（UNC / 网络盘）的措辞：同一个意思，换个更准确的说法。</summary>
        private const string SlowNetworkPathMessage = "这个网络位置还没有播放起来（网络慢或对方没响应；按「停止」放弃）";

        private readonly SlowOpenWatch _slowOpenWatch = new SlowOpenWatch();

        /// <summary>冒烟用：把阈值调小，不必真等 12 秒（产品默认值在 <see cref="SlowOpenWatch"/> 里）。</summary>
        internal void SetSlowOpenHintForTest(int milliseconds) =>
            _slowOpenWatch.ThresholdMilliseconds = milliseconds;

        /// <summary>这一次打开已经提示过了（冒烟核对"跳过"是否发生）。</summary>
        internal bool SlowOpenHinted => _slowOpenWatch.Hinted;

        /// <summary>界面计时器每一拍调一次：到点说一句，且只说一次。</summary>
        private void CheckSlowOpen()
        {
            if (!_slowOpenWatch.Tick(Environment.TickCount64, _engine.State, _engine.CurrentPath)) return;

            var path = _engine.CurrentPath;

            SetStatus(MediaFormats.IsStreamUri(path) ? SlowStreamMessage : SlowNetworkPathMessage);

            AppLog.Info(
                "网络上的媒体打开超过 " + (_slowOpenWatch.ThresholdMilliseconds / 1000) + " 秒仍未播放"
                + "（仍在试，不自动停止）");
        }

        /// <summary>提示过之后真的播起来了：把"到底等了多久"留给下次调阈值用（同样不含地址）。</summary>
        private void LogSlowOpenRecovered()
        {
            if (!_slowOpenWatch.Hinted) return;

            var elapsed = Environment.TickCount64 - _slowOpenWatch.StartedMilliseconds;

            AppLog.Info("网络上的媒体在提示之后播起来了（打开共用约 " + Math.Max(0, elapsed / 1000) + " 秒）");
        }
    }
}
