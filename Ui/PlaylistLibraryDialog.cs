using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using 播放器.Core;

namespace 播放器.Ui
{
    /// <summary>
    /// 歌单窗口：把歌单库里的<b>全部歌单一次列出来</b>，挑一个载入，
    /// 或者就地改名 / 删除 / 把外面的 m3u 拖进来。
    /// <para>
    /// 菜单里只列得出前二十个，而且菜单里没法"看清楚了再选"。这里是主入口：
    /// 名字、有多少项、什么时候改的，一行看清。
    /// </para>
    /// <para>
    /// 载入动作<b>不在这里执行</b>：窗口只负责选择，退出后由主窗体去替换播放列表
    /// （那要停播、要问"当前列表有未保存的改动"，都是主窗体的活）。
    /// 改名和删除是磁盘上的操作，就地做完。
    /// </para>
    /// </summary>
    internal sealed class PlaylistLibraryDialog : Form
    {
        private const int DesignWidth = 700;

        /// <summary>窗口关掉之后交给主窗体的结果。</summary>
        internal sealed class Selection
        {
            /// <summary>要载入的歌单名；<c>null</c> 表示用户在窗口里没选载入。</summary>
            public string? LoadName { get; set; }

            /// <summary>true = 追加到当前列表，false = 替换。</summary>
            public bool Append { get; set; }

            /// <summary>刚新建的空歌单名；主窗体据此问一句要不要切过去。</summary>
            public string? NewName { get; set; }

            /// <summary>歌单库有没有被改过（新建 / 导入 / 改名 / 删除）。</summary>
            public bool Changed { get; set; }

            /// <summary>窗口关闭时"当前歌单"叫什么；被删掉时为 <c>null</c>。</summary>
            public string? CurrentName { get; set; }
        }

        private readonly ListView _list = new ListView();
        private readonly Label _hint = new Label();
        private readonly Label _detail = new Label();
        private readonly Button _loadButton = new Button();
        private readonly Button _appendButton = new Button();
        private readonly Button _newButton = new Button();
        private readonly Button _renameButton = new Button();
        private readonly Button _batchButton = new Button();
        private readonly Button _deleteButton = new Button();
        private readonly Button _folderButton = new Button();
        private readonly Button _closeButton = new Button();

        /// <summary>正在拖自己的行（调整歌单顺序）——用来和"从外面拖 m3u 进来"区分开。</summary>
        private bool _draggingRow;

        private readonly Selection _result;

        /// <summary>只读入口：窗口关掉之后带出去的选择（给冒烟测试用）。</summary>
        internal Selection Result => _result;

        private readonly Font _currentFont;

        private List<SavedPlaylist> _playlists = new List<SavedPlaylist>();

        /// <summary>上一次操作的说明（导入结果之类），选中别的歌单时清掉。</summary>
        private string? _note;

        internal PlaylistLibraryDialog(string? currentName)
        {
            _result = new Selection { CurrentName = currentName };

            Text = "我的歌单";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            Font = UiFonts.Sidebar;
            DoubleBuffered = true;
            AllowDrop = true;

            _currentFont = new Font(UiFonts.Sidebar, FontStyle.Bold);

            BuildLayout();
            ApplyDpiLayout();
            Reload();
        }

        private float DpiScale => DeviceDpi / 96f;

        private int Scaled(int value) => (int)Math.Round(value * DpiScale);

        /// <summary>
        /// 打开歌单窗口。返回的一律是当前状态（不是 <c>null</c>）：
        /// 用户可能在窗口里改了名就关掉，主窗体的"当前歌单"必须跟着更新。
        /// </summary>
        public static Selection Show(IWin32Window owner, ThemePalette palette, string? currentName)
        {
            using var dialog = new PlaylistLibraryDialog(currentName);

            dialog.ApplyPalette(palette);
            dialog.ShowDialog(owner);

            return dialog._result;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyDpiLayout();
        }

        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);
            ApplyDpiLayout();
        }

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

        // ---- 列表 -------------------------------------------------------------

        private void Reload(string? selectName = null)
        {
            _playlists = PlaylistLibrary.List().ToList();

            var previous = selectName ?? SelectedPlaylist()?.Name ?? _result.CurrentName;

            _list.BeginUpdate();

            try
            {
                _list.Items.Clear();

                foreach (var playlist in _playlists)
                {
                    var isCurrent = IsCurrent(playlist.Name);

                    // 当前歌单加"●"并加粗：一眼知道现在列表对应的是哪一份
                    var row = new ListViewItem(isCurrent ? "● " + playlist.Name : playlist.Name)
                    {
                        Tag = playlist,
                        ToolTipText = playlist.FilePath,
                        Font = isCurrent ? _currentFont : UiFonts.Sidebar
                    };

                    row.SubItems.Add(playlist.TrackCount < 0 ? "?" : playlist.TrackCount.ToString());
                    row.SubItems.Add(playlist.ModifiedLocal.ToString("yyyy-MM-dd HH:mm"));

                    _list.Items.Add(row);
                }
            }
            finally
            {
                _list.EndUpdate();
            }

            _note = null;

            var wanted = -1;

            if (previous != null)
            {
                for (var i = 0; i < _playlists.Count; i++)
                {
                    if (!string.Equals(_playlists[i].Name, previous, StringComparison.CurrentCultureIgnoreCase))
                        continue;

                    wanted = i;
                    break;
                }
            }

            if (wanted < 0 && _list.Items.Count > 0) wanted = 0;

            if (wanted >= 0) _list.Items[wanted].Selected = true;

            OnSelectionChanged();
        }

        private SavedPlaylist? SelectedPlaylist() =>
            _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as SavedPlaylist : null;

        /// <summary>选中的那几份（按列表顺序）；多选时只有"删除"用得上。</summary>
        private List<SavedPlaylist> SelectedPlaylists()
        {
            var selected = new List<SavedPlaylist>();

            foreach (ListViewItem row in _list.SelectedItems)
            {
                if (row.Tag is SavedPlaylist playlist) selected.Add(playlist);
            }

            return selected;
        }

        private bool IsCurrent(string name) =>
            _result.CurrentName != null &&
            string.Equals(name, _result.CurrentName, StringComparison.CurrentCultureIgnoreCase);

        private void OnSelectionChanged()
        {
            _note = null;

            var count = _list.SelectedItems.Count;

            // 载入 / 追加 / 重命名 针对"一份歌单"：选了两份以上就点不动（点哪一个都不明确）
            _loadButton.Enabled = count == 1;
            _appendButton.Enabled = count == 1;
            _renameButton.Enabled = count == 1;

            // 批量重命名要的就是"选了好几份"：一份直接点「重命名…」就够了
            _batchButton.Enabled = count > 1;

            // 删除支持多选：选中几个就删几个，按钮上直接写出个数
            _deleteButton.Enabled = count > 0;
            _deleteButton.Text = count > 1 ? $"删除选中的 {count} 个" : "删除";

            UpdateDetail();
        }

        private void UpdateDetail()
        {
            if (_note != null)
            {
                _detail.Text = _note;
                return;
            }

            var selected = SelectedPlaylist();

            if (selected == null)
            {
                _detail.Text = _playlists.Count == 0
                    ? "歌单库里还没有歌单。先回主窗口保存一个，或者把 m3u 文件拖进来。"
                    : "歌单文件夹：" + PlaylistLibrary.Directory;
                return;
            }

            var count = selected.TrackCount < 0 ? "读不出条目数" : $"{selected.TrackCount} 项";

            _detail.Text = $"{selected.Name} · {count} · {selected.FilePath}"
                           + (IsCurrent(selected.Name) ? "　（当前列表对应的歌单）" : string.Empty);
        }

        // ---- 操作 -------------------------------------------------------------

        internal void LoadSelected()
        {
            var selected = SelectedPlaylist();
            if (selected == null) return;

            _result.LoadName = selected.Name;
            _result.Append = false;
            _result.NewName = null;      // 选了别的歌单，之前新建的那份就不切过去了

            DialogResult = DialogResult.OK;
            Close();
        }

        private void AppendSelected()
        {
            var selected = SelectedPlaylist();
            if (selected == null) return;

            _result.LoadName = selected.Name;
            _result.Append = true;
            _result.NewName = null;

            DialogResult = DialogResult.OK;
            Close();
        }

        /// <summary>
        /// 新建一个空歌单。文件在这里就建好（这本来就是"新建"该做的事），
        /// 至于要不要把当前播放列表切过去，等窗口关掉之后由主窗体问过用户再决定——
        /// 清空当前列表会打断正在放的东西，不该由一个"管理歌单"的窗口替用户决定。
        /// </summary>
        private void CreateNew()
        {
            var raw = InputDialog.Show(this, "新建歌单", "新歌单的名字：", SuggestName());
            if (raw == null) return;

            if (!PlaylistLibrary.TryNormalizeName(raw, out var name, out var error))
            {
                MessageBox.Show(this, error, "歌单名不可用", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (PlaylistLibrary.Exists(name))
            {
                MessageBox.Show(
                    this,
                    $"已经有一个叫「{name}」的歌单了。",
                    "新建歌单失败",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);

                return;
            }

            try
            {
                PlaylistLibrary.Create(name);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "新建歌单失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _result.NewName = name;
            _result.Changed = true;

            Reload(name);

            _note = $"已新建空歌单「{name}」；关掉本窗口后会问你要不要切过去。";
            UpdateDetail();
        }

        /// <summary>新歌单的默认名：新歌单 / 新歌单 2 / …（避开已经用掉的名字）。</summary>
        private static string SuggestName()
        {
            const string stem = "新歌单";

            var candidate = stem;

            for (var suffix = 2; suffix <= 99 && PlaylistLibrary.Exists(candidate); suffix++)
                candidate = $"{stem} {suffix}";

            return candidate;
        }

        private void RenameSelected()
        {
            var selected = SelectedPlaylist();
            if (selected == null) return;

            var raw = InputDialog.Show(this, "重命名歌单", "新的名字：", selected.Name);
            if (raw == null) return;

            if (!PlaylistLibrary.Rename(selected.Name, raw, out var actual, out var error))
            {
                MessageBox.Show(this, error, "重命名歌单失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (IsCurrent(selected.Name)) _result.CurrentName = actual;

            _result.Changed = true;
            Reload(actual);

            _note = $"「{selected.Name}」已改名为「{actual}」。";
            UpdateDetail();
        }

        /// <summary>
        /// 删除选中的歌单（支持多选）：<b>一次确认，逐个删</b>，删不掉的说清是哪一个、为什么。
        /// <para>返回真的删掉了几个。<paramref name="confirm"/> 为 false 时不弹确认框（给冒烟测试用）。</para>
        /// </summary>
        internal int DeleteSelected(bool confirm)
        {
            var selected = SelectedPlaylists();
            if (selected.Count == 0) return 0;

            if (confirm)
            {
                var what = selected.Count == 1
                    ? $"要删除歌单「{selected[0].Name}」吗？"
                    : $"要删除这 {selected.Count} 个歌单吗？\n\n"
                      + string.Join("\n", selected.Select(playlist => "· " + playlist.Name));

                var answer = MessageBox.Show(
                    this,
                    what + "\n\n（只删这些歌单文件，里面的歌曲、视频一个都不会动。）",
                    "删除歌单",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                if (answer != DialogResult.Yes) return 0;
            }

            var deleted = new List<string>();
            var failures = new List<string>();

            foreach (var playlist in selected)
            {
                if (PlaylistLibrary.Delete(playlist.Name, out var error)) deleted.Add(playlist.Name);
                else failures.Add($"「{playlist.Name}」：{error}");
            }

            if (deleted.Count == 0)
            {
                MessageBox.Show(this, string.Join("\n", failures), "删除歌单失败",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);

                return 0;
            }

            // 删掉的正好是"当前歌单"时，主窗体的关联要跟着断开
            if (deleted.Any(IsCurrent)) _result.CurrentName = null;

            _result.Changed = true;
            Reload();

            _note = deleted.Count == 1
                ? $"已删除歌单「{deleted[0]}」。"
                : $"已删除 {deleted.Count} 个歌单：{string.Join("、", deleted)}。";

            if (failures.Count > 0)
            {
                _note += $" 另有 {failures.Count} 个没删掉：{string.Join("；", failures)}";

                MessageBox.Show(this, string.Join("\n", failures), "部分歌单没删掉",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            UpdateDetail();
            return deleted.Count;
        }

        private void OpenFolder()
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "explorer.exe", $"\"{PlaylistLibrary.EnsureDirectory()}\"")
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "打开歌单文件夹失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // ---- 拖动换位（调整歌单顺序） -------------------------------------------

        private void OnRowDrag(object? sender, ItemDragEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;

            // 和主列表一样只拖"一行"：多选时拖哪一行都不明确（多选是给删除 / 批量重命名用的）
            if (_list.SelectedItems.Count != 1) return;
            if (e.Item is not ListViewItem row) return;

            _draggingRow = true;

            try
            {
                _list.DoDragDrop(row, DragDropEffects.Move);
            }
            finally
            {
                _draggingRow = false;
                _list.InsertionMark.Index = -1;
            }
        }

        private void OnRowsDragOver(object? sender, DragEventArgs e)
        {
            if (!_draggingRow) return;      // 从外面拖文件进来时走导入那条路

            var point = _list.PointToClient(new Point(e.X, e.Y));
            var target = _list.InsertionMark.NearestIndex(point);

            if (target > -1)
            {
                var bounds = _list.GetItemRect(target);

                _list.InsertionMark.AppearsAfterItem = point.Y > bounds.Top + bounds.Height / 2;
                _list.InsertionMark.Index = target;
            }

            e.Effect = DragDropEffects.Move;
        }

        private void OnRowsDragDrop(object? sender, DragEventArgs e)
        {
            if (!_draggingRow) return;      // 同上

            var insertAt = _list.InsertionMark.Index;
            var after = _list.InsertionMark.AppearsAfterItem;
            _list.InsertionMark.Index = -1;

            if (insertAt < 0 || _list.SelectedIndices.Count != 1) return;

            MoveSelectedTo(TargetIndex(_list.SelectedIndices[0], insertAt, after));
        }

        /// <summary>
        /// 拖放的"插到哪儿"换成"挪完之后的最终下标"。
        /// <para>
        /// 插到自己后面时要减一：把自己从列表里摘掉之后，后面的下标会整体前移一位。
        /// 主列表的拖动换位是同一个算术（那里写在 <c>OnPlaylistReorderDrop</c> 里）；
        /// 这里抽成纯函数是为了能直接断言——差一位这种错在界面上很难看出来。
        /// </para>
        /// </summary>
        internal static int TargetIndex(int from, int insertAt, bool after)
        {
            var target = after ? insertAt + 1 : insertAt;

            if (target > from) target--;

            return Math.Max(0, target);
        }

        /// <summary>
        /// 把选中的那一行挪到第 <paramref name="targetIndex"/> 位，<b>顺序立即写进顺序文件</b>
        /// （这个窗口的语义就是"直接动文件"，拖完不落盘等于白拖）。
        /// </summary>
        internal bool MoveSelectedTo(int targetIndex) => MoveSelectedTo(targetIndex, interactive: true);

        /// <summary>
        /// 同上；<paramref name="interactive"/> 为 false 时失败也不弹框（理由写进窗口里那行字），
        /// 供冒烟测试驱动——模态框会把测试挂死。
        /// </summary>
        internal bool MoveSelectedTo(int targetIndex, bool interactive)
        {
            var selected = SelectedPlaylist();
            if (selected == null) return false;

            if (!PlaylistLibrary.Move(selected.Name, targetIndex, out var error))
            {
                if (interactive)
                    MessageBox.Show(this, error, "调整歌单顺序失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);

                _note = error;
                UpdateDetail();
                return false;
            }

            _result.Changed = true;
            Reload(selected.Name);

            _note = $"已把「{selected.Name}」挪到第 {targetIndex + 1} 位"
                    + $"（顺序记在 {PlaylistLibrary.OrderFileName} 里，删掉它就回到按名字排）。";

            UpdateDetail();
            return true;
        }

        // ---- 批量重命名 ---------------------------------------------------------

        /// <summary>
        /// 批量重命名选中的歌单：按<b>列表显示顺序</b>依次改成「基名 1」「基名 2」…
        /// <para>
        /// <b>先把所有新名字验一遍，再动文件</b>：任何一个不合法、或者撞上别的歌单，
        /// 就一个文件都不动——批量操作改到一半最难受（手上几个改了一半的名字，
        /// 还得自己去分辨哪几个是新的）。
        /// </para>
        /// <para><paramref name="rawBaseName"/> 为 <c>null</c> 时弹输入框（冒烟测试直接传基名）。</para>
        /// </summary>
        /// <returns>真的改了几个。</returns>
        internal int BatchRename(string? rawBaseName) => BatchRename(rawBaseName, interactive: true);

        /// <summary>
        /// 同上；<paramref name="interactive"/> 为 false 时<b>一个对话框都不弹</b>
        /// （理由写进窗口下方那行字里），供冒烟测试驱动——模态框会把测试挂死。
        /// </summary>
        internal int BatchRename(string? rawBaseName, bool interactive)
        {
            var selected = SelectedPlaylists();

            // 被挡住时统一走这里：该弹框的弹框，不弹框时也要把理由留在窗口里
            int Reject(string message, string title)
            {
                if (interactive)
                {
                    MessageBox.Show(this, message, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }

                _note = message.Replace(Environment.NewLine, " ");
                UpdateDetail();
                return 0;
            }

            if (selected.Count < 2)
            {
                if (interactive && rawBaseName == null)
                {
                    MessageBox.Show(
                        this,
                        "批量重命名要先选中两份以上；只改一份直接点「重命名…」就行。",
                        "批量重命名歌单",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }

                return 0;
            }

            var baseName = rawBaseName;

            if (baseName == null)
            {
                baseName = InputDialog.Show(
                    this,
                    "批量重命名歌单",
                    $"新名字的基名（这 {selected.Count} 份会按这里的顺序变成「基名 1」「基名 2」…）：",
                    "歌单");

                if (baseName == null) return 0;
            }

            // ---- ① 先把全部目标名字算出来并验一遍 ----
            var plans = new List<(string Old, string New)>();

            for (var i = 0; i < selected.Count; i++)
            {
                if (!PlaylistLibrary.TryNormalizeName($"{baseName} {i + 1}", out var clean, out var error))
                    return Reject(error, "批量重命名歌单失败");

                plans.Add((selected[i].Name, clean));
            }

            foreach (var plan in plans)
            {
                // 自己的名字没变（"基名 1"正好就是它现在叫的）：放行，不用动文件
                if (string.Equals(plan.Old, plan.New, StringComparison.CurrentCultureIgnoreCase)) continue;

                if (PlaylistLibrary.Exists(plan.New))
                {
                    return Reject(
                        $"已经有一个叫「{plan.New}」的歌单了，这次一个文件都没动。"
                        + Environment.NewLine + Environment.NewLine
                        + "换个基名，或者先把那份改名 / 删掉再试。",
                        "批量重命名歌单失败");
                }
            }

            var duplicate = plans
                .GroupBy(plan => plan.New, StringComparer.CurrentCultureIgnoreCase)
                .FirstOrDefault(group => group.Count() > 1);

            if (duplicate != null)
                return Reject($"算出来的名字里有重复的「{duplicate.Key}」，这次一个文件都没动。", "批量重命名歌单失败");

            // ---- ② 验完了才动文件 ----
            var renamed = new List<string>();
            var failures = new List<string>();

            foreach (var plan in plans)
            {
                if (string.Equals(plan.Old, plan.New, StringComparison.CurrentCultureIgnoreCase))
                {
                    renamed.Add(plan.New);
                    continue;
                }

                if (!PlaylistLibrary.Rename(plan.Old, plan.New, out var actual, out var error))
                {
                    failures.Add($"「{plan.Old}」：{error}");
                    continue;
                }

                if (IsCurrent(plan.Old)) _result.CurrentName = actual;

                renamed.Add(actual);
            }

            if (renamed.Count == 0)
                return Reject(string.Join(Environment.NewLine, failures), "批量重命名歌单失败");

            _result.Changed = true;
            Reload(renamed[renamed.Count - 1]);

            _note = $"已重命名 {renamed.Count} 个：{string.Join(" / ", renamed)}。";

            if (failures.Count > 0)
            {
                _note += $" 另有 {failures.Count} 个没改成：{string.Join("；", failures)}";

                if (interactive)
                {
                    MessageBox.Show(
                        this,
                        string.Join(Environment.NewLine, failures),
                        "部分歌单没改成",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
            }

            UpdateDetail();
            return renamed.Count;
        }

        // ---- 拖入 m3u 导入 -----------------------------------------------------

        private static bool HasPlaylistFiles(IDataObject? data)
        {
            if (data?.GetDataPresent(DataFormats.FileDrop) != true) return false;

            if (data.GetData(DataFormats.FileDrop) is not string[] files) return false;

            return files.Any(IsPlaylistFile);
        }

        private static bool IsPlaylistFile(string path)
        {
            var extension = Path.GetExtension(path);

            return string.Equals(extension, ".m3u", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(extension, ".m3u8", StringComparison.OrdinalIgnoreCase);
        }

        private void OnDragEnter(object? sender, DragEventArgs e)
        {
            // 拖自己的一行（调整顺序）不是"拖文件进来"，别把它判成 None
            if (_draggingRow || e.Data?.GetDataPresent(typeof(ListViewItem)) == true)
            {
                e.Effect = DragDropEffects.Move;
                return;
            }

            e.Effect = HasPlaylistFiles(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
        }

        private void OnDragDrop(object? sender, DragEventArgs e)
        {
            if (_draggingRow || e.Data?.GetDataPresent(typeof(ListViewItem)) == true) return;

            if (e.Data?.GetData(DataFormats.FileDrop) is not string[] files) return;

            var imported = new List<string>();
            var problems = new List<string>();

            foreach (var file in files.Where(IsPlaylistFile))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var overwrite = false;

                if (PlaylistLibrary.Exists(name))
                {
                    var answer = MessageBox.Show(
                        this,
                        $"歌单库里已经有一个叫「{name}」的歌单了。\n\n用拖进来的这个文件覆盖它吗？",
                        "导入歌单",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question);

                    if (answer != DialogResult.Yes)
                    {
                        problems.Add($"{Path.GetFileName(file)}：已跳过（同名歌单保留原样）");
                        continue;
                    }

                    overwrite = true;
                }

                if (PlaylistLibrary.Import(file, overwrite, out var importedName, out var error))
                    imported.Add(importedName);
                else
                    problems.Add($"{Path.GetFileName(file)}：{error}");
            }

            if (problems.Count > 0)
            {
                MessageBox.Show(
                    this,
                    string.Join(Environment.NewLine, problems),
                    "导入歌单",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }

            if (imported.Count == 0) return;

            _result.Changed = true;
            Reload(imported[imported.Count - 1]);

            _note = $"已导入 {imported.Count} 个歌单：{string.Join(" / ", imported)}";
            UpdateDetail();
        }

        // ---- 主题 -------------------------------------------------------------

        private void ApplyPalette(ThemePalette palette)
        {
            BackColor = palette.WindowBack;
            ForeColor = palette.ListFore;

            _list.BackColor = palette.ListBack;
            _list.ForeColor = palette.ListFore;

            foreach (Control control in Controls)
            {
                switch (control)
                {
                    case Button button:
                        button.FlatStyle = FlatStyle.Flat;
                        button.BackColor = palette.MenuBack;
                        button.ForeColor = palette.MenuFore;
                        button.FlatAppearance.BorderColor = palette.MenuBorder;
                        break;

                    case Label label:
                        label.ForeColor = palette.ListFore;
                        break;
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _currentFont.Dispose();

            base.Dispose(disposing);
        }
    }
}
