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
        private readonly Button _deleteButton = new Button();
        private readonly Button _folderButton = new Button();
        private readonly Button _closeButton = new Button();

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
                         + "双击一行就能载入；外面的 m3u / m3u8 也可以直接拖进来收进歌单库。";
            _list.View = View.Details;
            _list.FullRowSelect = true;
            _list.MultiSelect = false;
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

            _detail.AutoSize = true;

            _loadButton.Text = "载入（替换当前列表）";
            _loadButton.Click += (s, e) => LoadSelected();

            _appendButton.Text = "追加到当前列表";
            _appendButton.Click += (s, e) => AppendSelected();

            _newButton.Text = "新建空歌单…";
            _newButton.Click += (s, e) => CreateNew();

            _renameButton.Text = "重命名…";
            _renameButton.Click += (s, e) => RenameSelected();

            _deleteButton.Text = "删除";
            _deleteButton.Click += (s, e) => DeleteSelected();

            _folderButton.Text = "打开歌单文件夹";
            _folderButton.Click += (s, e) => OpenFolder();

            _closeButton.Text = "关闭";
            _closeButton.DialogResult = DialogResult.Cancel;

            Controls.AddRange(new Control[]
            {
                _hint, _list, _detail,
                _loadButton, _appendButton, _newButton, _renameButton, _deleteButton,
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

            _deleteButton.Bounds = new Rectangle(
                _renameButton.Right + gap, secondRow, deleteWidth, buttonHeight);

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

        private bool IsCurrent(string name) =>
            _result.CurrentName != null &&
            string.Equals(name, _result.CurrentName, StringComparison.CurrentCultureIgnoreCase);

        private void OnSelectionChanged()
        {
            _note = null;

            var selected = SelectedPlaylist();

            _loadButton.Enabled = selected != null;
            _appendButton.Enabled = selected != null;
            _renameButton.Enabled = selected != null;
            _deleteButton.Enabled = selected != null;

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

        private void DeleteSelected()
        {
            var selected = SelectedPlaylist();
            if (selected == null) return;

            var answer = MessageBox.Show(
                this,
                $"要删除歌单「{selected.Name}」吗？\n\n"
                + "（只删这个歌单文件，里面的歌曲、视频一个都不会动。）",
                "删除歌单",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (answer != DialogResult.Yes) return;

            if (!PlaylistLibrary.Delete(selected.Name, out var error))
            {
                MessageBox.Show(this, error, "删除歌单失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (IsCurrent(selected.Name)) _result.CurrentName = null;

            _result.Changed = true;
            Reload();

            _note = $"已删除歌单「{selected.Name}」。";
            UpdateDetail();
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

        private void OnDragEnter(object? sender, DragEventArgs e) =>
            e.Effect = HasPlaylistFiles(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;

        private void OnDragDrop(object? sender, DragEventArgs e)
        {
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
