using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using 播放器.Core;

// 布局：建控件、按 DPI 摆一遍、上色。

namespace 播放器.Ui
{
    internal sealed partial class PlaylistLibraryDialog
    {
        // ---- 布局 -------------------------------------------------------------

        private void BuildLayout()
        {
            _hint.AutoSize = true;
            _hint.Text = "歌单存在数据目录的「播放列表」文件夹里，一个歌单一个 m3u8 文件。"
                         + "双击一行就能载入；按住 Ctrl / Shift 可以多选，多选之后「删除」会一次删掉这几个、"
                         + "「批量重命名…」会按这里的顺序把它们改成「基名 1」「基名 2」…。"
                         + "拖动一行可以调整歌单顺序（顺序记在同一个文件夹的「" + PlaylistLibrary.OrderFileName
                         + "」里，删掉它就回到按名字排）。"
                         + "外面的 m3u / m3u8 也可以直接拖进来收进歌单库。";
            _list.View = View.Details;
            _list.FullRowSelect = true;

            // 多选只为"批量删除"：载入 / 追加 / 重命名 都是针对一份歌单的动作，
            // 多选时它们会点不动（见 OnSelectionChanged）
            _list.MultiSelect = true;
            _list.HideSelection = false;
            _list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            _list.ShowItemToolTips = true;
            _list.UseCompatibleStateImageBehavior = false;
            _list.BorderStyle = BorderStyle.FixedSingle;
            _list.AllowDrop = true;
            _list.Columns.Add("歌单", 250, HorizontalAlignment.Left);
            _list.Columns.Add("曲目", 70, HorizontalAlignment.Right);
            _list.Columns.Add("最后修改", 150, HorizontalAlignment.Left);

            _list.SelectedIndexChanged += (s, e) => OnSelectionChanged();
            _list.DoubleClick += (s, e) => LoadSelected();
            _list.DragEnter += OnDragEnter;
            _list.DragDrop += OnDragDrop;

            // 拖动自己的一行 = 调整歌单顺序（和"从外面拖文件进来"是两条路，见 _draggingRow）
            _list.ItemDrag += OnRowDrag;
            _list.DragOver += OnRowsDragOver;
            _list.DragDrop += OnRowsDragDrop;
            _list.DragLeave += (s, e) => _list.InsertionMark.Index = -1;

            _detail.AutoSize = true;

            _loadButton.Text = "载入（替换当前列表）";
            _loadButton.Click += (s, e) => LoadSelected();

            _appendButton.Text = "追加到当前列表";
            _appendButton.Click += (s, e) => AppendSelected();

            _newButton.Text = "新建空歌单…";
            _newButton.Click += (s, e) => CreateNew();

            _renameButton.Text = "重命名…";
            _renameButton.Click += (s, e) => RenameSelected();

            _batchButton.Text = "批量重命名…";
            _batchButton.Click += (s, e) => BatchRename(null);

            _deleteButton.Text = "删除";
            _deleteButton.Click += (s, e) => DeleteSelected(confirm: true);

            _folderButton.Text = "打开歌单文件夹";
            _folderButton.Click += (s, e) => OpenFolder();

            _closeButton.Text = "关闭";
            _closeButton.DialogResult = DialogResult.Cancel;

            Controls.AddRange(new Control[]
            {
                _hint, _list, _detail,
                _loadButton, _appendButton, _newButton, _renameButton, _batchButton, _deleteButton,
                _folderButton, _closeButton
            });

            AcceptButton = _loadButton;
            CancelButton = _closeButton;

            // 拖到窗口空白处也要能导入
            DragEnter += OnDragEnter;
            DragDrop += OnDragDrop;
        }

        /// <summary>
        /// 按测量结果摆一遍：提示文字可能是一行也可能是三行（DPI 越大行数越多），
        /// 列表要接在它的真实高度下面，不能按"固定 40 像素"估。
        /// <para>
        /// 按钮排两行：一行是"载入 / 追加"这类主操作加关闭，另一行是新建 / 改名 / 删除 / 文件夹。
        /// 七个按钮挤一行会超出窗口（125% 缩放下尤其明显）。
        /// </para>
        /// </summary>
        private void ApplyDpiLayout()
        {
            var padding = Scaled(14);
            var gap = Scaled(8);
            var buttonHeight = Scaled(30);
            var width = Scaled(DesignWidth);

            _hint.Location = new Point(padding, Scaled(12));
            _hint.MaximumSize = new Size(width - padding * 2, 0);

            var hintHeight = Math.Max(Scaled(30), _hint.PreferredSize.Height);
            var listTop = _hint.Top + hintHeight + Scaled(10);

            var listWidth = width - padding * 2;

            _list.Bounds = new Rectangle(padding, listTop, listWidth, Scaled(240));
            _list.Columns[0].Width = Math.Max(Scaled(120), listWidth - Scaled(70) - Scaled(150) - Scaled(24));
            _list.Columns[1].Width = Scaled(70);
            _list.Columns[2].Width = Scaled(150);

            // 明细那一行里有完整路径，必须限宽让它折行：
            // AutoSize 的 Label 不限宽就会一路撑到 1000 多像素、直接超出窗口（实测踩到过）。
            _detail.MaximumSize = new Size(listWidth, 0);
            _detail.Location = new Point(padding, _list.Bottom + gap);

            var firstRow = Math.Max(_list.Bottom + Scaled(30), _detail.Bottom + gap + Scaled(6));
            var secondRow = firstRow + buttonHeight + gap;

            var loadWidth = Scaled(160);
            var appendWidth = Scaled(140);
            var closeWidth = Scaled(90);
            var newWidth = Scaled(120);
            var renameWidth = Scaled(90);
            var batchWidth = Scaled(130);
            var deleteWidth = Scaled(76);
            var folderWidth = Scaled(140);

            _loadButton.Bounds = new Rectangle(padding, firstRow, loadWidth, buttonHeight);

            _appendButton.Bounds = new Rectangle(
                _loadButton.Right + gap, firstRow, appendWidth, buttonHeight);

            _closeButton.Bounds = new Rectangle(
                width - padding - closeWidth, firstRow, closeWidth, buttonHeight);

            _newButton.Bounds = new Rectangle(padding, secondRow, newWidth, buttonHeight);

            _renameButton.Bounds = new Rectangle(
                _newButton.Right + gap, secondRow, renameWidth, buttonHeight);

            _batchButton.Bounds = new Rectangle(
                _renameButton.Right + gap, secondRow, batchWidth, buttonHeight);

            _deleteButton.Bounds = new Rectangle(
                _batchButton.Right + gap, secondRow, deleteWidth, buttonHeight);

            _folderButton.Bounds = new Rectangle(
                _deleteButton.Right + gap, secondRow, folderWidth, buttonHeight);

            ClientSize = new Size(width, secondRow + buttonHeight + Scaled(14));
        }
    }
}
