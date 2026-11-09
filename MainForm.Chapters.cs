using System;
using System.Windows.Forms;
using LibVLCSharp.Shared;
using 播放器.Core;

// 「播放」菜单下的章节跳转：从当前媒体的章节里选一个跳过去。

namespace 播放器
{
    /// <summary>「播放 → 章节」：当前媒体的章节列表，点一个跳过去。</summary>
    public partial class MainForm
    {
        private ToolStripMenuItem? _menuChapter;

        // =====================================================================
        // 章节
        // =====================================================================
        private void ConfigureChapterMenu()
        {
            _menuChapter = new ToolStripMenuItem("章节");
            menuPlayback.DropDownItems.Add(_menuChapter);

            // 每次下拉都重建：换标题 / 换盘之后章数不一样，冻结一次就会点错。
            _menuChapter.DropDownOpening += (s, e) => RebuildChapterMenu();

            RebuildChapterMenu();
        }

        private void RebuildChapterMenu()
        {
            if (_menuChapter == null) return;

            _menuChapter.Text = DescribeChapterMenuCaption();
            _menuChapter.DropDownItems.Clear();

            var chapters = _engine.HasMedia
                ? _engine.GetChapters()
                : Array.Empty<LibVLCSharp.Shared.Structures.ChapterDescription>();

            var hasChapters = chapters.Count > 0;
            var current = hasChapters ? _engine.CurrentChapter : -1;

            var previous = new ToolStripMenuItem("上一章") { Enabled = hasChapters };
            previous.Click += (s, e) => { _engine.PreviousChapter(); RebuildChapterMenu(); };

            var next = new ToolStripMenuItem("下一章") { Enabled = hasChapters };
            next.Click += (s, e) => { _engine.NextChapter(); RebuildChapterMenu(); };

            _menuChapter.DropDownItems.Add(previous);
            _menuChapter.DropDownItems.Add(next);
            _menuChapter.DropDownItems.Add(new ToolStripSeparator());

            if (!hasChapters)
            {
                _menuChapter.DropDownItems.Add(
                    new ToolStripMenuItem("（该文件没有章节）") { Enabled = false });
                return;
            }

            for (var i = 0; i < chapters.Count; i++)
            {
                var chapter = chapters[i];
                var index = i;

                var stamp = TimeFormatter.FormatWithHours(TimeSpan.FromMilliseconds(chapter.TimeOffset));
                var label = string.IsNullOrWhiteSpace(chapter.Name)
                    ? $"{i + 1}. {stamp}"
                    : $"{i + 1}. {chapter.Name}";

                var item = new ToolStripMenuItem(label)
                {
                    Checked = i == current,
                    ToolTipText = stamp
                };

                item.Click += (s, e) => { _engine.SetChapter(index); RebuildChapterMenu(); };
                _menuChapter.DropDownItems.Add(item);
            }
        }

        /// <summary>
        /// 「章节」这一项的字：DVD 正片里带上"第几章 / 共几章"（菜单上不带——菜单 title 的章数是
        /// 菜单自己那几格，和正片无关，写出来反而误导）。
        /// </summary>
        private string DescribeChapterMenuCaption()
        {
            if (!DvdMenu.IsMenuMrl(_engine.CurrentPath)) return "章节";

            var title = _engine.Title;
            var chapter = _engine.CurrentChapter;
            var count = _engine.ChapterCount;

            if (title <= 0 || chapter < 0 || count <= 0) return "章节";

            return "章节（第 " + (chapter + 1) + " 章 / 共 " + count + " 章）";
        }
    }
}
