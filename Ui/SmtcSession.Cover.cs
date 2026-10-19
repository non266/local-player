using System;
using System.IO;
using System.Threading.Tasks;
using 播放器.Core;
using Windows.Media;
using Windows.Storage;
using Windows.Storage.Streams;

// 系统媒体控件（SMTC）的封面：内嵌图 / 同目录外挂图 / 没有封面时显式清一次。
// 从 Ui/SmtcSession.cs 按 ARCHITECTURE 17.2 的规矩整块搬过来：成员一个字没动，
// 字段与 WinRT 互操作声明仍留在主文件。

namespace 播放器.Ui
{
    internal sealed partial class SmtcSession
    {
        /// <summary>
        /// 把封面推给系统：内嵌图先落到数据目录里一个临时文件，再走 <see cref="SetCoverFile"/>。
        /// <para><c>null</c> / 空数组表示"这一首没有封面"，会把系统那边挂着的旧封面清掉。</para>
        /// </summary>
        internal void SetCover(byte[]? data, string? mimeType)
        {
            if (_disposed || _controls == null) return;

            if (data is not { Length: > 0 })
            {
                SetCoverFile(null);
                return;
            }

            string path;

            try
            {
                var extension = mimeType switch
                {
                    "image/png" => ".png",
                    "image/gif" => ".gif",
                    "image/bmp" => ".bmp",
                    _ => ".jpg"
                };

                path = Path.Combine(AppSettings.SettingsDirectory, "smtc-cover" + extension);
                File.WriteAllBytes(path, data);
            }
            catch (Exception ex)
            {
                AppLog.Swallowed("把封面落成临时文件失败。", ex);
                return;
            }

            SetCoverFile(path);
        }

        /// <summary>
        /// 直接把<b>磁盘上的一个图片文件</b>当封面喂给系统（同目录的 <c>cover.jpg</c> 这类就走这条）。
        /// <para>
        /// 比 <see cref="SetCover"/> 少一趟读写：文件本来就在盘上，不必再抄进数据目录。
        /// </para>
        /// <para>
        /// 传 <c>null</c>（或文件已经不在）表示没有封面：<b>上一首要是喂过封面就得显式清一次</b>——
        /// 不清的话，切到下一首没有封面的歌时，音量弹窗里还挂着上一首的封面。
        /// 本来就没封面（<see cref="CoverPath"/> 也是空的）则什么都不做，不必白跑一趟 WinRT。
        /// </para>
        /// </summary>
        internal void SetCoverFile(string? path)
        {
            if (_disposed || _controls == null) return;

            if (string.IsNullOrEmpty(path) || !File.Exists(path)) path = null;

            // 本来就没有封面、现在也没有：系统那边不用动（每首歌都白调两次 WinRT 没有意义）
            if (path == null && CoverPath == null) return;

            CoverPath = path;
            CoverSet = false;

            _ = SetCoverAsync(path, ++_coverToken);
        }

        private async Task SetCoverAsync(string? path, int token)
        {
            try
            {
                RandomAccessStreamReference? thumbnail = null;

                if (path != null)
                {
                    var file = await StorageFile.GetFileFromPathAsync(path);
                    thumbnail = RandomAccessStreamReference.CreateFromFile(file);
                }

                // 换歌很快的时候上一首那份可能还在飞：晚到的不许盖掉现在这一首的
                if (_disposed || _controls == null || token != _coverToken) return;

                var updater = _controls.DisplayUpdater;
                updater.Thumbnail = thumbnail;

                // DisplayUpdater 上的改动要靠 Update() 推给系统；只在设封面时不调的话，
                // 系统那边看着像是"封面喂了却没出来"。
                updater.Update();

                CoverSet = thumbnail != null;
            }
            catch (Exception ex)
            {
                AppLog.Swallowed("把封面推给系统媒体控件失败。", ex);
            }
        }
    }
}
