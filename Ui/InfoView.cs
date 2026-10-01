using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace 播放器.Ui
{
    /// <summary>
    /// 「媒体信息」页：两列（项目 / 内容）的只读清单。
    /// <para>
    /// 直接复用 <see cref="ListView"/> 的详细视图并把表头藏掉——滚动条、文本选择、
    /// 深色模式这些都和播放列表走同一套既有逻辑，没必要再写一个自绘列表控件。
    /// </para>
    /// </summary>
    internal sealed class InfoView : ListView
    {
        private readonly ColumnHeader _nameColumn = new ColumnHeader { Text = "项目", Width = 96 };
        private readonly ColumnHeader _valueColumn = new ColumnHeader { Text = "内容", Width = 200 };
        private bool _adjustingColumns;

        public InfoView()
        {
            View = View.Details;
            HeaderStyle = ColumnHeaderStyle.None;
            FullRowSelect = true;
            MultiSelect = false;
            HideSelection = false;
            Scrollable = true;
            ShowGroups = false;
            UseCompatibleStateImageBehavior = false;
            Font = UiFonts.Sidebar;

            // 侧栏注定不宽，编解码器名称这类长内容放不下，靠悬停提示补全。
            ShowItemToolTips = true;

            Columns.Add(_nameColumn);
            Columns.Add(_valueColumn);
        }

        /// <summary>替换整个清单。</summary>
        public void SetRows(IReadOnlyList<(string Name, string Value)> rows)
        {
            BeginUpdate();

            try
            {
                Items.Clear();

                foreach (var (name, value) in rows)
                {
                    var item = new ListViewItem(name);
                    item.SubItems.Add(value);
                    Items.Add(item);
                }
            }
            finally
            {
                EndUpdate();
            }

            AdjustColumns();
        }

        public void ApplyPalette(ThemePalette palette)
        {
            BackColor = palette.ListBack;
            ForeColor = palette.ListFore;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            AdjustColumns();
        }

        /// <summary>项目列约占三成，其余留给内容列。</summary>
        private void AdjustColumns()
        {
            if (_adjustingColumns || Columns.Count < 2) return;

            _adjustingColumns = true;

            try
            {
                var width = ClientSize.Width;
                if (width <= 0) return;

                var name = Math.Clamp((int)(width * 0.32), 84, 120);
                _nameColumn.Width = name;
                _valueColumn.Width = Math.Max(120, width - name - 4);
            }
            finally
            {
                _adjustingColumns = false;
            }
        }
    }
}
