namespace 播放器.Core
{
    /// <summary>
    /// 播放器的高层状态。用于驱动界面按钮的可用性与文字显示。
    /// </summary>
    public enum PlayerState
    {
        /// <summary>尚未打开任何媒体。</summary>
        Idle,

        /// <summary>正在打开/缓冲媒体。</summary>
        Opening,

        /// <summary>正在播放。</summary>
        Playing,

        /// <summary>已暂停。</summary>
        Paused,

        /// <summary>已停止。</summary>
        Stopped,

        /// <summary>当前媒体自然播放结束。</summary>
        Ended,

        /// <summary>播放出错。</summary>
        Error
    }

    /// <summary>
    /// 循环模式。
    /// </summary>
    public enum RepeatMode
    {
        /// <summary>不循环，播完列表即停止。</summary>
        None,

        /// <summary>单曲循环。</summary>
        One,

        /// <summary>列表循环。</summary>
        All
    }
}
