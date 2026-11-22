using System;
using System.IO;

namespace 播放器.Core
{
    /// <summary>数据目录是从哪儿定下来的（1.29.0）。</summary>
    public enum DataDirectorySource
    {
        /// <summary>环境变量 <c>播放器_DATA_DIR</c>（最高优先，冒烟测试靠它隔离）。</summary>
        Environment,

        /// <summary>程序旁边有 <c>portable.ini</c>，数据放在 <c>&lt;程序目录&gt;\data</c>。</summary>
        Portable,

        /// <summary>默认位置 <c>%AppData%\播放器</c>。</summary>
        AppData,

        /// <summary>有 <c>portable.ini</c> 但程序目录写不进去，退回 <c>%AppData%\播放器</c>。</summary>
        PortableFallback
    }

    /// <summary>判定结果：用哪个目录、为什么、要不要在界面上说一句。</summary>
    public sealed class DataDirectoryChoice
    {
        public DataDirectoryChoice(string directory, DataDirectorySource source, string? notice)
        {
            Directory = directory;
            Source = source;
            Notice = notice;
        }

        public string Directory { get; }

        public DataDirectorySource Source { get; }

        /// <summary>要显示一次的话（便携模式 / 退回 AppData）；没有就是 <c>null</c>。</summary>
        public string? Notice { get; }
    }

    /// <summary>
    /// 数据目录的判定（1.29.0）：环境变量 → <c>portable.ini</c> → <c>%AppData%\播放器</c>。
    /// <para>
    /// 1.0 起数据目录就能用环境变量改（README / 参考手册写着"可以做绿色版"），但那要用户会设环境变量。
    /// 1.29.0 补的是"程序旁边放一个 <c>portable.ini</c> 就切过去"：解压到 U 盘、把 marker 放进同一个
    /// 目录，设置 / 历史 / 歌单 / 字体 / 缩略图缓存 / 在线缓存就全跟着程序走。
    /// </para>
    /// <para>
    /// <b>不预置 marker</b>：便携 zip 里刻意不放它。老用户把新 zip 解开覆盖旧目录时，数据仍留在
    /// <c>%AppData%</c>，不会"凭空搬家"；想要便携就自己放一个（README 写了怎么放）。
    /// </para>
    /// <para>
    /// 判定为便携之后会真的建一次目录：建不出来（只读介质 / 权限）就<b>退回</b> <c>%AppData%</c>
    /// 并在状态栏说一句，绝不静默地"设置存哪儿了都不知道"。
    /// </para>
    /// </summary>
    public static class DataDirectory
    {
        /// <summary>便携模式的标记文件名（放在 <c>播放器.exe</c> 旁边）。</summary>
        public const string PortableMarkerFileName = "portable.ini";

        /// <summary>便携模式的数据子目录名（<c>&lt;程序目录&gt;\data</c>）。</summary>
        public const string PortableFolderName = "data";

        /// <summary>默认数据目录名（<c>%AppData%\播放器</c>）。</summary>
        private const string AppFolderName = "播放器";

        private static readonly object Gate = new object();
        private static DataDirectoryChoice? _current;

        /// <summary>
        /// 纯判定（不碰静态状态，测试可以直接喂路径）。
        /// <paramref name="ensureWritable"/> 传 <c>null</c> 表示"不试写"（默认按可写算）。
        /// </summary>
        public static DataDirectoryChoice Resolve(
            string? exeDirectory,
            string? appDataDirectory,
            string? environmentOverride,
            Func<string, bool>? ensureWritable = null)
        {
            if (!string.IsNullOrWhiteSpace(environmentOverride))
                return new DataDirectoryChoice(environmentOverride!, DataDirectorySource.Environment, null);

            var appData = Path.Combine(
                string.IsNullOrWhiteSpace(appDataDirectory)
                    ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
                    : appDataDirectory!,
                AppFolderName);

            var exe = (exeDirectory ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (exe.Length > 0 && File.Exists(Path.Combine(exe, PortableMarkerFileName)))
            {
                var portable = Path.Combine(exe, PortableFolderName);

                // 同名目录不算 marker（File.Exists 只认文件），这里再显式排一次，免得"建了个目录"也被当成开关
                if (Directory.Exists(Path.Combine(exe, PortableMarkerFileName)))
                    return new DataDirectoryChoice(appData, DataDirectorySource.AppData, null);

                var writable = ensureWritable == null || ensureWritable(portable);

                return writable
                    ? new DataDirectoryChoice(
                        portable,
                        DataDirectorySource.Portable,
                        "便携模式：数据目录在程序旁边（" + portable + "）")
                    : new DataDirectoryChoice(
                        appData,
                        DataDirectorySource.PortableFallback,
                        "便携模式：程序目录写不进去，数据退回 " + appData);
            }

            return new DataDirectoryChoice(appData, DataDirectorySource.AppData, null);
        }

        /// <summary>
        /// 本进程实际用的那个（惰性算一次：同一进程里数据目录必须稳定）。
        /// <para>测试要换环境只能开新进程，或者用 <see cref="ResetForTest"/>。</para>
        /// </summary>
        public static DataDirectoryChoice Current
        {
            get
            {
                lock (Gate)
                {
                    return _current ??= Resolve(
                        AppContext.BaseDirectory,
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        Environment.GetEnvironmentVariable("播放器_DATA_DIR"),
                        EnsureWritable);
                }
            }
        }

        /// <summary>测试用：清掉缓存，下一次 <see cref="Current"/> 重新判定。</summary>
        internal static void ResetForTest()
        {
            lock (Gate) _current = null;
        }

        /// <summary>真的建一次目录：只读介质 / 权限不足时返回 <c>false</c>（由调用方决定退回哪儿）。</summary>
        private static bool EnsureWritable(string directory)
        {
            try
            {
                System.IO.Directory.CreateDirectory(directory);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
