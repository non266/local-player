using System;
using System.Windows.Forms;
using 播放器.Core;

namespace 播放器
{
    /// <summary>
    /// 「播放 → 光盘」与画面右键菜单里的「光盘菜单」（1.16.0）。
    /// <para>
    /// 为什么要有两个入口：菜单栏在<b>全屏</b>时是收起来的，而 DVD 的菜单恰恰最常在"全屏看正片"
    /// 的时候想回去；那时唯一能弹出来的就是画面右键菜单（视频区被 libvlc 的子窗口盖着，
    /// 见 <see cref="MainForm.VideoMenu"/> 的说明）。
    /// </para>
    /// <para>
    /// 子菜单的内容每次下拉时按<b>当前引擎的标题 / 章节数</b>重建：不同盘的标题数不一样，
    /// 同一个盘换了标题之后章数也不一样，冻结一次就会点错。
    /// </para>
    /// </summary>
    public partial class MainForm
    {
        private ToolStripMenuItem? _menuDiscPlayback;

        /// <summary>测试用：「播放 → 光盘」。</summary>
        internal ToolStripMenuItem? MenuDiscPlayback => _menuDiscPlayback;

        private void ConfigureDiscPlaybackMenu()
        {
            // 一开始是隐藏的：没有光盘时「播放」菜单里不该多出一项点了没反应的。
            _menuDiscPlayback = new ToolStripMenuItem("光盘")
            {
                Name = "menuDiscPlayback",
                Visible = false
            };

            _menuDiscPlayback.DropDownOpening += (s, e) => RebuildDiscPlaybackMenu();
            menuPlayback.DropDownItems.Add(_menuDiscPlayback);

            RebuildDiscPlaybackMenu();
        }

        /// <summary>按当前这一份媒体重建「光盘」子菜单（不是光盘就整项藏起来）。</summary>
        internal void RebuildDiscPlaybackMenu()
        {
            if (_menuDiscPlayback == null) return;

            var mrl = _engine.HasMedia ? _engine.CurrentPath : null;
            var letter = OpticalDisc.LetterOfMrl(mrl);

            ClearMenuItems(_menuDiscPlayback.DropDownItems);

            _menuDiscPlayback.Visible = letter.Length > 0;
            if (letter.Length == 0) return;

            var menuMode = IsDiscMenuOnScreen;
            var titleCount = _engine.TitleCount;
            var currentTitle = _engine.Title;

            _menuDiscPlayback.DropDownItems.Add(BuildDiscTitleMenu(mrl, titleCount, currentTitle));

            _menuDiscPlayback.DropDownItems.Add(new ToolStripSeparator());

            var back = new ToolStripMenuItem("回到菜单")
            {
                Name = "menuDiscBack",
                Enabled = !menuMode
            };
            back.Click += (s, e) => ReturnToDiscMenu();
            _menuDiscPlayback.DropDownItems.Add(back);

            var main = new ToolStripMenuItem("直接播放主标题")
            {
                Name = "menuDiscMainTitle",
                Enabled = DvdMenu.IsMenuMrl(mrl)
            };
            main.Click += (s, e) => PlayMainTitleOfCurrentDisc();
            _menuDiscPlayback.DropDownItems.Add(main);
        }

        /// <summary>「标题」子菜单：只对 <c>dvd://</c> 有意义（它的 title 0 是菜单，正片从 1 开始）。</summary>
        private ToolStripMenuItem BuildDiscTitleMenu(string? mrl, int titleCount, int currentTitle)
        {
            var titles = new ToolStripMenuItem("标题")
            {
                Name = "menuDiscTitles",
                Enabled = DvdMenu.IsMenuMrl(mrl) && titleCount > 1
            };

            if (!DvdMenu.IsMenuMrl(mrl) || titleCount <= 1)
            {
                titles.DropDownItems.Add(new ToolStripMenuItem("（这张盘没有标题列表）") { Enabled = false });
                return titles;
            }

            for (var i = 1; i < titleCount; i++)
            {
                var title = i;
                var item = new ToolStripMenuItem(
                    DvdMenu.DescribeTitle(title, _engine.ChapterCountForTitle(title)))
                {
                    Checked = title == currentTitle
                };

                item.Click += (s, e) => SwitchDiscTitle(title);
                titles.DropDownItems.Add(item);
            }

            return titles;
        }

        // =====================================================================
        // 画面右键菜单里的「光盘菜单」
        // =====================================================================

        /// <summary>
        /// 填「光盘菜单」子菜单：方向键那五项 + 回到菜单 + 直接播放主标题。
        /// <para>
        /// 方向键五项只在菜单真的在屏上时可点——菜单不在屏上时按下去什么都不会发生，
        /// 可点的假按钮比不可点更糟。
        /// </para>
        /// </summary>
        internal void FillDiscMenuItems(ToolStripItemCollection items)
        {
            ClearMenuItems(items);

            var mrl = _engine.HasMedia ? _engine.CurrentPath : null;
            if (OpticalDisc.LetterOfMrl(mrl).Length == 0)
            {
                items.Add(new ToolStripMenuItem("（当前不是光盘）") { Enabled = false });
                return;
            }

            var menuMode = IsDiscMenuOnScreen;

            items.Add(BuildNavigateItem("菜单：上", DvdMenuAction.Up, menuMode));
            items.Add(BuildNavigateItem("菜单：下", DvdMenuAction.Down, menuMode));
            items.Add(BuildNavigateItem("菜单：左", DvdMenuAction.Left, menuMode));
            items.Add(BuildNavigateItem("菜单：右", DvdMenuAction.Right, menuMode));
            items.Add(BuildNavigateItem("菜单：确定", DvdMenuAction.Activate, menuMode));

            items.Add(new ToolStripSeparator());

            var back = new ToolStripMenuItem("回到光盘菜单") { Enabled = !menuMode };
            back.Click += (s, e) => ReturnToDiscMenu();
            items.Add(back);

            var main = new ToolStripMenuItem("直接播放主标题") { Enabled = DvdMenu.IsMenuMrl(mrl) };
            main.Click += (s, e) => PlayMainTitleOfCurrentDisc();
            items.Add(main);
        }

        private ToolStripMenuItem BuildNavigateItem(string text, DvdMenuAction action, bool enabled)
        {
            var item = new ToolStripMenuItem(text) { Enabled = enabled };
            item.Click += (s, e) => NavigateDiscMenu(action);
            return item;
        }
    }
}
