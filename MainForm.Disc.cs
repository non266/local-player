using System;
using System.Windows.Forms;
using 播放器.Core;
using 播放器.Ui;

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

        /// <summary>
        /// 已经"菜单打不开、退回主标题"过的盘符（同一张盘只退一次；换一张盘会重新允许）。
        /// </summary>
        private string _discFallbackLetter = string.Empty;

        /// <summary>上次写进状态栏的光盘位置（"菜单" / "标题 2 / 12 · 第 3 章"），只在变化时刷。</summary>
        private string _discStatusText = string.Empty;

        /// <summary>
        /// 最近一次"菜单打不开、已改为直接播放主标题"的说明（空串 = 没发生过）。
        /// <para>
        /// 单独记一份的原因：退回主标题之后紧接着还会再失败一次，状态栏会被"已停止"盖掉；
        /// 光看状态栏根本抓不住那句话。<b>冒烟断言读的是这一份</b>。
        /// </para>
        /// </summary>
        private string _discFallbackNotice = string.Empty;

        /// <summary>测试用：最近一次菜单回退的说明。</summary>
        internal string DiscFallbackNotice => _discFallbackNotice;

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

        // =====================================================================
        // DVD 菜单 / 标题 / 章节（1.16.0）
        // =====================================================================

        /// <summary>测试用：现在是不是停在 DVD 菜单上。</summary>
        internal bool IsDiscMenuOnScreen =>
            _engine.HasMedia && DvdMenu.IsOnScreen(_engine.CurrentPath, _engine.Title, _engine.TitleCount);

        /// <summary>测试用：这个盘符是不是已经退过一次主标题了。</summary>
        internal bool HasFallenBackFromDiscMenu(string letter) =>
            string.Equals(_discFallbackLetter, letter, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 菜单在屏上时，方向键 / 回车交给 libvlc 的菜单导航；返回"这一下被吃掉了"。
        /// <para>
        /// 平时这四个方向键是快退 / 快进 / 音量，所以这里必须**先判**菜单在不在屏上：
        /// 不在就返回 <c>false</c>，让命令层照旧处理。
        /// </para>
        /// </summary>
        internal bool TryNavigateDiscMenu(Keys keyData)
        {
            if (!IsDiscMenuOnScreen) return false;

            var action = DvdMenuInput.For(keyData);
            if (action == null) return false;

            _engine.Navigate(action.Value);
            return true;
        }

        /// <summary>画面右键菜单里的那几项（参数是动作，调用方已经判过"是不是菜单"）。</summary>
        internal void NavigateDiscMenu(DvdMenuAction action)
        {
            if (!_engine.HasMedia) return;
            _engine.Navigate(action);
        }

        /// <summary>切到某个标题（只对 <c>dvd://</c> 有意义；编号 1 是第一个正片）。</summary>
        private void SwitchDiscTitle(int title)
        {
            if (!_engine.HasMedia) return;

            _engine.Title = title;
            SetStatus("已切到标题 " + title);
        }

        /// <summary>
        /// 把当前这一条就地换成另一种 MRL（<c>dvd://</c> ↔ <c>dvdsimple://</c>）再播一次。
        /// <para>
        /// 不新增条目：列表里只该有一行「DVD 光盘 (L:)」，否则上一首 / 下一首会在同一个盘上停两次。
        /// </para>
        /// </summary>
        private bool SwitchDiscMrl(string mrl, string status)
        {
            if (string.IsNullOrWhiteSpace(mrl)) return false;

            var index = _playlist.CurrentIndex;
            if (index < 0) return false;

            _playlist.SetPath(index, mrl);
            PlayIndex(index, keepCurrent: true, allowFallback: false);
            SetStatus(status);
            return true;
        }

        /// <summary>「直接播放主标题」：跳过菜单，用 <c>dvdsimple://</c> 从正片开始。</summary>
        private void PlayMainTitleOfCurrentDisc()
        {
            var letter = OpticalDisc.LetterOfMrl(_engine.CurrentPath);
            if (letter.Length == 0) return;

            SwitchDiscMrl(OpticalDisc.BuildSimpleMrl(DiscKind.DvdVideo, letter),
                "直接播放主标题（想回菜单用「播放 → 光盘 → 回到菜单」）");
        }

        /// <summary>「回到菜单」：已经在菜单上就回菜单根；在主标题里就先换回 <c>dvd://</c>。</summary>
        private void ReturnToDiscMenu()
        {
            var letter = OpticalDisc.LetterOfMrl(_engine.CurrentPath);
            if (letter.Length == 0) return;

            if (DvdMenu.IsMenuMrl(_engine.CurrentPath))
            {
                // 菜单里也可能停在"章节选择"那一层：popup 回上一级，再设 title 0 回根。
                _engine.Navigate(DvdMenuAction.Popup);
                _engine.Title = 0;
                SetStatus("回到光盘菜单");
                return;
            }

            SwitchDiscMrl(OpticalDisc.BuildMrl(DiscKind.DvdVideo, letter), "正在打开光盘菜单…");
        }

        /// <summary>
        /// 菜单打不开（错误事件到了）：把这个条目就地换成主标题再放一次，**同一张盘只退一次**。
        /// <para>
        /// 只处理"当前播的就是这个盘、错误来自 <c>dvd://</c>"这一种；别的错误照旧走
        /// <see cref="OnEngineError"/> 的标记与自动跳过。
        /// </para>
        /// </summary>
        private bool TryFallBackDiscMenu(string message)
        {
            var mrl = _engine.CurrentPath;
            var letter = OpticalDisc.LetterOfMrl(mrl);
            if (letter.Length == 0) return false;

            var alreadyTried = HasFallenBackFromDiscMenu(letter);
            if (!DvdMenu.ShouldFallBack(mrl, alreadyTried, error: true)) return false;

            var index = _playlist.CurrentIndex;
            if (index < 0 || _playlist.Current == null ||
                !string.Equals(_playlist.Current.FilePath, mrl, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            _discFallbackLetter = letter;
            _playlist.SetPath(index, OpticalDisc.BuildSimpleMrl(DiscKind.DvdVideo, letter));

            AppLog.Info("光盘菜单打不开（" + message + "），改为直接播放主标题：" + letter);
            _discFallbackNotice = "这张盘的菜单打不开，已改为直接播放主标题";
            SetStatus(_discFallbackNotice);

            PlayIndex(index, keepCurrent: true, allowFallback: false);
            return true;
        }

        /// <summary>停在菜单上时，把"方向键现在归谁"说一句（进入菜单时一次）。</summary>
        private void AnnounceDiscMenuIfNeeded()
        {
            if (!IsDiscMenuOnScreen) return;

            SetStatus("光盘菜单：方向键移动，回车确认（右键菜单里也有这几项）");
        }

        /// <summary>状态栏上的"菜单 / 标题 N / M · 第 K 章"，只在变化时写一次。</summary>
        private void UpdateDiscStatus()
        {
            var path = _engine.CurrentPath;

            if (!_engine.HasMedia || !MediaFormats.IsDiscLocation(path))
            {
                _discStatusText = string.Empty;
                return;
            }

            var text = DvdMenu.DescribePosition(path, _engine.Title, _engine.TitleCount, _engine.CurrentChapter);
            if (string.Equals(text, _discStatusText, StringComparison.Ordinal)) return;

            _discStatusText = text;
            if (text.Length == 0) return;

            SetStatus(text == "菜单"
                ? "光盘菜单：方向键移动，回车确认（右键菜单里也有这几项）"
                : "正在播放：" + NowPlayingDisplayName() + "（" + text + "）");
        }
    }
}
