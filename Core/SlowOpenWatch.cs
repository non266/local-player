using System;

namespace 播放器.Core
{
    /// <summary>
    /// "网络流打开得慢"的判定（1.21.0）：只回答"现在该不该说一句"，不碰界面也不联网。
    /// <para>
    /// 为什么要有这一条：1.19.0 量到不可达的 http / rtsp 流会<b>一直停在 <c>Opening</c></b>
    /// （60 秒都没有错误事件，libvlc 自己的网络超时更长）——界面不冻，但"连不上"这件事
    /// 程序不会主动说，用户只看到「正在打开…」。
    /// </para>
    /// <para>
    /// 刻意<b>只提示、不自动停</b>：流慢不等于流死，慢而能播的流被停掉比多等一下更糟。
    /// 停止是用户自己的决定（<c>停止</c> 在 <c>Opening</c> 上也点得动）。
    /// </para>
    /// <para>
    /// 状态机很小：不是 <see cref="PlayerState.Opening"/>、或路径不是网络流 / 网络位置 → 复位；
    /// 一次打开只返回一次 <c>true</c>；下一次打开重新计时（所以第二条死流还会提示）。
    /// 判据写成纯逻辑是为了能<b>直接断言</b>——真等 12 秒再验，既慢又受机器快慢影响。
    /// </para>
    /// </summary>
    public sealed class SlowOpenWatch
    {
        /// <summary>默认阈值：12 秒。放成一个常量，改它只改这一处。</summary>
        public const int DefaultThresholdMilliseconds = 12_000;

        /// <summary>多久没播起来才提示（冒烟会把它调小，产品默认 12 秒）。</summary>
        public int ThresholdMilliseconds { get; set; } = DefaultThresholdMilliseconds;

        /// <summary>这一次打开已经提示过了。</summary>
        public bool Hinted { get; private set; }

        /// <summary>这一次打开从哪一拍开始等；<c>-1</c> 表示没在等。</summary>
        public long StartedMilliseconds { get; private set; } = -1;

        /// <summary>正在等一次打开（起点已记下）。</summary>
        public bool Watching => StartedMilliseconds >= 0;

        /// <summary>复位：下一次 <see cref="Tick"/> 会重新记起点。</summary>
        public void Reset()
        {
            StartedMilliseconds = -1;
            Hinted = false;
        }

        /// <summary>
        /// 界面计时器每一拍调一次。返回 <c>true</c> 表示"就是这一拍该提示了"，一次打开最多一次。
        /// </summary>
        /// <param name="nowMilliseconds">单调时钟（<c>Environment.TickCount64</c>）。</param>
        /// <param name="state">引擎当前状态。</param>
        /// <param name="path">当前媒体路径 / 地址。</param>
        public bool Tick(long nowMilliseconds, PlayerState state, string? path)
        {
            if (state != PlayerState.Opening || !Applies(path))
            {
                Reset();
                return false;
            }

            if (StartedMilliseconds < 0)
            {
                StartedMilliseconds = nowMilliseconds;
                return false;
            }

            if (Hinted) return false;

            if (nowMilliseconds - StartedMilliseconds < ThresholdMilliseconds) return false;

            Hinted = true;
            return true;
        }

        /// <summary>
        /// 只对网络流（http / rtsp / rtmp …）与网络位置（UNC / 网络盘）有意义：
        /// 本地文件打开慢是磁盘 / 杀毒软件的事，说"网络慢"只会误导。
        /// </summary>
        public static bool Applies(string? path) =>
            MediaFormats.IsStreamUri(path) || MediaFormats.IsNetworkPath(path);
    }
}
