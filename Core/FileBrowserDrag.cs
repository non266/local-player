namespace 播放器.Core
{
    /// <summary>
    /// 从文件页拖到播放列表时额外带的自定义格式。
    /// <para>
    /// 仍然同时放 <c>DataFormats.FileDrop</c>（资源管理器 / 其它程序能用）；
    /// 这个格式只给本程序看，用来区分"文件页拖来的"与"外部拖进来的"——
    /// 前者的文件夹递归要走 32 层 / 200,000 文件的有界扫描，后者保持原行为。
    /// </para>
    /// </summary>
    internal static class FileBrowserDrag
    {
        internal const string Format = "播放器.FileBrowserPaths";
    }
}
