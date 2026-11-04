using System;
using System.Windows.Forms;
using 播放器.Core;

namespace 播放器
{
    /// <summary>
    /// 「文件 → 打开光盘」（1.11.0 的 F13）。
    /// <para>
    /// 菜单在每次下拉时按<b>本机现在的光驱</b>重建：光驱是 USB 的、可以随时插拔，盘也可以随时换；
    /// 启动时查一次就冻结的话，插上光驱要重启程序才认得。
    /// </para>
    /// <para>
    /// 分流规则见 <see cref="OpticalDisc"/>：DVD-Video 用 <c>dvdsimple://</c> 直接放主标题
    /// （不做菜单导航），音频 CD 用 <c>cdda://</c>，数据盘按"打开文件夹"那条路加进播放列表。
    /// 蓝光只识别、不支持。
    /// </para>
    /// </summary>
    public partial class MainForm : Form
    {
        /// <summary>测试用：光驱菜单（子项在每次下拉时重建）。</summary>
        internal ToolStripMenuItem DiscMenu => menuFileOpenDisc;

        private void ConfigureDiscMenu()
        {
            menuFileOpenDisc.DropDownOpening += (s, e) => RebuildDiscMenu();

            // 先建一次：菜单没展开过就点不到子项，但测试与"菜单文字对不对"要看它。
            RebuildDiscMenu();
        }

        /// <summary>按本机光驱重建「打开光盘」的子菜单。</summary>
        internal void RebuildDiscMenu()
        {
            ClearMenuItems(menuFileOpenDisc.DropDownItems);

            var drives = OpticalDisc.Detect();

            if (drives.Count == 0)
            {
                menuFileOpenDisc.Enabled = false;
                var none = new ToolStripMenuItem("没有检测到光驱") { Enabled = false };
                menuFileOpenDisc.DropDownItems.Add(none);
                return;
            }

            menuFileOpenDisc.Enabled = true;

            foreach (var drive in drives)
            {
                var item = new ToolStripMenuItem(DescribeDrive(drive))
                {
                    Tag = drive,
                    Enabled = drive.IsReady && drive.Kind != DiscKind.None && drive.Kind != DiscKind.BluRay
                };

                item.Click += (s, e) =>
                {
                    if (item.Tag is OpticalDiscDrive selected) OpenOpticalDisc(selected);
                };

                menuFileOpenDisc.DropDownItems.Add(item);
            }
        }

        /// <summary>菜单上那一行字：类型 + 盘符，卷标有就带上。</summary>
        private static string DescribeDrive(OpticalDiscDrive drive)
        {
            var text = drive.IsReady ? drive.KindText : drive.Letter + "（没有光盘）";

            if (drive.IsReady && drive.Kind == DiscKind.BluRay)
                text += "（暂不支持）";
            else if (drive.IsReady && drive.Label.Length > 0)
                text += "  " + drive.Label;

            return text;
        }

        /// <summary>用户点了某个光驱。</summary>
        private void OpenOpticalDisc(OpticalDiscDrive drive)
        {
            if (drive == null) return;

            if (!drive.IsReady)
            {
                SetStatus(drive.Letter + " 里没有光盘（或者盘读不出来）");
                return;
            }

            switch (drive.Kind)
            {
                case DiscKind.DvdVideo:
                case DiscKind.AudioCd:
                    PlayLocation(OpticalDisc.BuildMrl(drive.Kind, drive.Root), drive.KindText);
                    break;

                case DiscKind.BluRay:
                    // 插件在包里，但加密蓝光需要 AACS 密钥库，本机也没有蓝光设备：
                    // 没在真机上验过的路不写进发行版，这里如实说一句。
                    SetStatus("蓝光光盘暂不支持（缺 AACS 密钥库，也没在真机上验证过）");
                    break;

                case DiscKind.Data:
                    if (!OpticalDisc.HasPlayableMedia(drive.Root))
                    {
                        SetStatus("这张光盘里没有可播放的媒体文件：" + drive.Letter);
                        return;
                    }

                    SetStatus("正在扫描数据光盘 " + drive.Letter + "…");
                    AddPathsWithFolders(new[] { drive.Root }, AddPlayback.Always);
                    break;

                default:
                    SetStatus("没认出 " + drive.Letter + " 里是什么盘（不是 DVD 视频 / 音频 CD，也没有媒体文件）");
                    break;
            }
        }

        /// <summary>
        /// 把一个 location 型 MRL（光盘）加进播放列表并开播。
        /// <para>
        /// 加进列表而不是"只放不进列表"：这样上一首 / 下一首 / 循环 / 停止都和别的条目一样，
        /// 也能像网络流那样存进 m3u（<see cref="MediaFormats.IsLocationUri"/> 那一套守卫负责
        /// 不让它进历史、标签、歌词）。
        /// </para>
        /// </summary>
        internal bool PlayLocation(string mrl, string title)
        {
            if (string.IsNullOrWhiteSpace(mrl))
            {
                SetStatus("这一项没有可播放的地址");
                return false;
            }

            var index = _playlist.IndexOfPath(mrl);

            if (index < 0)
            {
                var added = _playlist.AddRangeDistinct(new[] { mrl });
                if (added.Count == 0)
                {
                    SetStatus("没能把 " + title + " 加进播放列表");
                    return false;
                }

                index = added[0];
            }

            SetStatus("正在打开" + title + "…");

            // 明确点开的那一项：失败就停下说清楚，不顺手去播列表里别的东西。
            PlayIndex(index, keepCurrent: false, allowFallback: false);
            return true;
        }
    }
}
