using System.Collections.Generic;

namespace 播放器.Core
{
    /// <summary>
    /// 一个媒体该带的 libvlc 选项（缓存 + 音量均衡）。
    /// <para>
    /// <b>为什么单独抽一个纯函数</b>：这些选项只在<b>创建媒体那一刻</b>拼一次，是"媒体级"的——
    /// 播放中改设置不会生效，必须重载媒体。判据要是能直接断言这串选项，
    /// 就不必为了"看看滤镜挂上没有"去起播放器、更不必靠耳朵听。
    /// </para>
    /// </summary>
    public static class MediaOptions
    {
        /// <summary>网络流的缓存（毫秒）。</summary>
        public const int NetworkCachingMilliseconds = 1000;

        /// <summary>本地文件的缓存（毫秒）。</summary>
        public const int FileCachingMilliseconds = 300;

        /// <summary>音量均衡用的音频滤镜名（libvlc 自带的 <c>normvol</c>）。</summary>
        public const string NormalizeAudioFilter = "normvol";

        /// <summary>拼一个媒体要带的全部选项。</summary>
        /// <param name="isStream">网络流（用网络缓存）还是本地文件（用文件缓存）。</param>
        /// <param name="normalizeVolume">要不要挂音量均衡。</param>
        public static IReadOnlyList<string> For(bool isStream, bool normalizeVolume)
        {
            var options = new List<string>
            {
                isStream
                    ? $":network-caching={NetworkCachingMilliseconds}"
                    : $":file-caching={FileCachingMilliseconds}"
            };

            if (normalizeVolume)
                options.Add(":audio-filter=" + NormalizeAudioFilter);

            return options;
        }
    }
}
