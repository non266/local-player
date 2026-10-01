namespace 播放器.Core
{
    /// <summary>
    /// 桌面歌词的可调范围与默认值。
    /// <para>
    /// 放在这里而不是放进窗口类：<see cref="AppSettings"/> 要用它做默认值与钳制，
    /// 而设置类不该反过来依赖界面类型；两边各写一份迟早会走偏。
    /// </para>
    /// </summary>
    public static class DesktopLyricsDefaults
    {
        public const int MinimumFontSize = 12;
        public const int MaximumFontSize = 96;
        public const int FontSize = 30;
        public const int FontSizeStep = 2;

        public const int MinimumOpacity = 20;
        public const int MaximumOpacity = 100;

        /// <summary>
        /// 默认不做成完全不透明。
        /// <para>
        /// 桌面歌词是盖在别的程序之上的，全不透明时下面那块内容完全被吃掉；
        /// 留一点点透明度，既能看清字，也能看出"这里盖了一层"。
        /// </para>
        /// </summary>
        public const int Opacity = 88;

        public const int OpacityStep = 5;
    }
}
