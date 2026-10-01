namespace 播放器.Core
{
    /// <summary>
    /// 分离出去的悬浮窗（侧栏 / 播放列表）吸附在主窗口的哪一条边上。
    /// <para>
    /// 持久化到设置文件时按名字保存，所以改名会影响旧配置的读取。
    /// </para>
    /// </summary>
    public enum WindowSnapEdge
    {
        /// <summary>不吸附，窗口自由摆放。</summary>
        None,

        /// <summary>贴在主窗口左侧。</summary>
        Left,

        /// <summary>贴在主窗口右侧。</summary>
        Right,

        /// <summary>贴在主窗口上方。</summary>
        Top,

        /// <summary>贴在主窗口下方。</summary>
        Bottom
    }
}
