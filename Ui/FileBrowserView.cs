using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using 播放器.Core;

namespace 播放器.Ui
{
    /// <summary>
    /// 侧栏「文件」页：工具栏 + 地址栏 + 文件列表 + 状态行。
    /// <para>
    /// 这里<b>不认识播放器</b>：播放 / 添加 / 下一首都只是事件，由 <c>MainForm.FileBrowser</c> 接。
    /// 也不在界面线程上读磁盘：真正的目录读取在 <see cref="FileBrowserService"/> 上，
    /// 由本类的后台任务调用（见 <c>FileBrowserView.Behavior.cs</c>）。
    /// </para>
    /// </summary>
    internal sealed partial class FileBrowserView : Panel
    {
        private readonly Button _backButton = new Button();
        private readonly Button _forwardButton = new Button();
        private readonly Button _upButton = new Button();
        private readonly Button _refreshButton = new Button();
        private readonly Button _positionButton = new Button();

        private readonly Panel _toolbar = new Panel();
        private readonly Panel _filterBar = new Panel();
        private readonly TextBox _address = new TextBox();
        private readonly TextBox _search = new TextBox();
        private readonly CheckBox _recursiveSearch = new CheckBox();
        private readonly ListView _list = new ListView();
        private readonly ColumnHeader _nameColumn = new ColumnHeader { Text = "名称" };
        private readonly ColumnHeader _sizeColumn = new ColumnHeader { Text = "大小" };
        private readonly ColumnHeader _folderColumn = new ColumnHeader { Text = "位置" };
        private readonly Label _status = new Label();

        private readonly ContextMenuStrip _contextMenu = new ContextMenuStrip();
        private readonly ContextMenuStrip _positionMenu = new ContextMenuStrip();

        private ToolStripMenuItem _playItem = null!;
        private ToolStripMenuItem _addItem = null!;
        private ToolStripMenuItem _queueItem = null!;
        private ToolStripMenuItem _revealItem = null!;
        private ToolStripMenuItem _copyItem = null!;
        private ToolStripMenuItem _refreshItem = null!;

        private FileBrowserService _service = new FileBrowserService();
        private FileBrowserState _state = new FileBrowserState();
        private List<FileBrowserEntry> _entries = new List<FileBrowserEntry>();
        private List<FileBrowserEntry> _visibleEntries = new List<FileBrowserEntry>();
        private FileIconSet? _icons;

        private string _query = string.Empty;
        private FileBrowserTypeFilter _typeFilter = FileBrowserTypeFilter.All;
        private bool _truncated;
        private bool _suppressFilterEvents;
        private bool _suppressScopeEvents;
        private string? _pendingSelectPath;

        private FileBrowserSearch _searchService = new FileBrowserSearch();
        private List<FileBrowserEntry> _searchResults = new List<FileBrowserEntry>();
        private bool _searchActive;
        private bool _searching;
        private string _searchQuery = string.Empty;
        private int _searchFolders;
        private int _searchEntries;
        private int _searchFound;
        private int _searchUnreadable;
        private bool _searchTruncated;
        private CancellationTokenSource? _searchCts;
        private int _searchVersion;

        private CancellationTokenSource? _loadCts;
        private int _loadVersion;
        private bool _loading;
        private bool _loadedOnce;
        private bool _statusIsError;

        private ThemePalette _palette = Themes.Light;
        private bool _nativeDark;

        private readonly WheelForwardFilter _wheelFilter;

        internal FileBrowserView()
        {
            Font = UiFonts.Sidebar;
            BackColor = _palette.PanelBack;
            ForeColor = _palette.ListFore;

            SetStyle(ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.UserPaint
                     | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.ResizeRedraw, true);

            BuildToolbar();
            BuildFilterBar();
            BuildList();
            BuildStatus();
            BuildTree();

            // 先加 Fill 再加 Top / Bottom：WinForms 的停靠顺序是反的，
            // 这样列表占剩下的区域，工具栏 / 搜索行与状态行不会被它盖住。
            // 搜索行放在工具栏之前加：后加的工具栏先占顶部，搜索行落在它下面。
            // 树加在列表之后、搜索行之前：它落在两行工具栏下面（像资源管理器）。
            Controls.Add(_list);
            Controls.Add(_tree);
            Controls.Add(_filterBar);
            Controls.Add(_toolbar);
            Controls.Add(_status);

            BuildContextMenu();
            BuildPositionMenu();

            // 1.29.0：分离成独立窗口之后，焦点可能在地址栏 / 搜索框上（那两个控件不吃滚轮），
            // 于是"鼠标在列表上滚"什么都不会发生。侧栏那条路由 SidebarPanel 的消息过滤器转发，
            // 这里补的是视图自己的那条路（悬浮窗 / 以后别处直接用这个视图）。
            _wheelFilter = new WheelForwardFilter(this);
            Application.AddMessageFilter(_wheelFilter);

            AccessibleName = "文件浏览器";
            AccessibleDescription = "浏览磁盘与文件夹，双击播放，右键可加入播放列表";
        }

        /// <summary>用户请求播放 / 添加 / 排进下一首；路径可能是文件或文件夹。</summary>
        internal event EventHandler<IReadOnlyList<string>>? PlayRequested;

        internal event EventHandler<IReadOnlyList<string>>? AddRequested;

        internal event EventHandler<IReadOnlyList<string>>? QueueRequested;

        internal event EventHandler<string>? PlayFolderRequested;

        /// <summary>导航失败 / 动作结果要显示到状态栏上。</summary>
        internal event EventHandler<string>? StatusRequested;

        /// <summary>收藏 / 最近 / 排序 / 过滤 / 最后目录变了，主窗体要同步设置。</summary>
        internal event EventHandler? StateChanged;

        /// <summary>用户点了「清除最近浏览」（主窗体负责当场落盘）。</summary>
        internal event EventHandler? RecentCleared;

        /// <summary>用户点了「定位当前播放文件」；由主窗体去找当前媒体并导航。</summary>
        internal event EventHandler? LocateRequested;

        internal FileBrowserState State => _state;

        internal ListView List => _list;

        internal string StatusText => _status.Text ?? string.Empty;

        internal bool IsLoading => _loading;

        internal IReadOnlyList<FileBrowserEntry> Entries => _entries;

        /// <summary>测试钩子：搜索框（设置 Text 会走与用户输入相同的过滤路径）。</summary>
        internal TextBox SearchBoxForTest => _search;

        /// <summary>测试钩子：搜索范围开关（真实控件，用来验焦点与空格键）。</summary>
        internal CheckBox ScopeToggleForTest => _recursiveSearch;

        /// <summary>测试钩子：「位置」按钮（用来验文字宽度 / 右边界，别让它被裁掉）。</summary>
        internal Button PositionButtonForTest => _positionButton;

        internal void SetSearchTextForTest(string value) => _search.Text = value;

        /// <summary>测试钩子：换掉目录读取（假的慢 I/O / 固定结果）。</summary>
        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        internal FileBrowserService Service
        {
            get => _service;
            set => _service = value;
        }

        internal void ApplyPalette(ThemePalette palette)
        {
            _palette = palette;

            BackColor = palette.PanelBack;
            ForeColor = palette.ListFore;

            _toolbar.BackColor = palette.PanelBack;
            _status.BackColor = palette.PanelBack;
            _status.ForeColor = _statusIsError ? palette.ErrorFore : palette.HintFore;

            _address.BackColor = palette.ListBack;
            _address.ForeColor = palette.ListFore;

            _filterBar.BackColor = palette.PanelBack;
            _search.BackColor = palette.ListBack;
            _search.ForeColor = palette.ListFore;

            _recursiveSearch.BackColor = palette.MenuBack;
            _recursiveSearch.ForeColor = palette.MenuFore;
            _recursiveSearch.FlatAppearance.BorderColor = palette.MenuBorder;
            _recursiveSearch.FlatAppearance.MouseOverBackColor = palette.MenuHoverBack;
            _recursiveSearch.FlatAppearance.MouseDownBackColor = palette.MenuHoverBack;

            _list.BackColor = palette.ListBack;
            _list.ForeColor = palette.ListFore;

            _tree.ApplyPalette(palette, palette.IsDark);

            foreach (var button in new[] { _backButton, _forwardButton, _upButton, _refreshButton, _positionButton })
            {
                button.FlatStyle = FlatStyle.Flat;
                button.BackColor = palette.MenuBack;
                button.ForeColor = palette.MenuFore;
                button.FlatAppearance.BorderColor = palette.MenuBorder;
                button.FlatAppearance.MouseOverBackColor = palette.MenuHoverBack;
                button.FlatAppearance.MouseDownBackColor = palette.MenuHoverBack;
            }

            var renderer = new ThemedToolStripRenderer(palette);

            _contextMenu.Renderer = renderer;
            _positionMenu.Renderer = renderer;
            _contextMenu.BackColor = palette.MenuBack;
            _contextMenu.ForeColor = palette.MenuFore;
            _positionMenu.BackColor = palette.MenuBack;
            _positionMenu.ForeColor = palette.MenuFore;

            RebuildIcons();
            Invalidate();
        }

        /// <summary>列表的滚动条 / 表头由系统画，只能走原生主题。</summary>
        internal void ApplyNativeTheme(bool dark)
        {
            _nativeDark = dark;

            if (!_list.IsHandleCreated) return;

            NativeTheme.ApplyListView(_list.Handle, dark);
            _list.Invalidate();
        }

        /// <summary>
        /// 侧栏 / 悬浮窗的滚轮转发到这里：把列表按行滚动。
        /// <para>
        /// 1.29.0 修（所有者报「文件浏览器无法用鼠标滚轮向下翻动」）：平铺（大图标）那一支原来是
        /// "找当前第一项 + 3 个、再 <c>EnsureVisible</c>"。大图标一行能放 5～6 个，+3 还在**同一行**、
        /// 本来就在视野里 —— <c>EnsureVisible</c> 遇到"已经可见"的目标什么都不做，于是
        /// **从顶部第一次向下滚就滚不动**，用户看到的是"滚轮完全不能往下翻"。
        /// 现在统一按像素发 <c>LVM_SCROLL</c>（3 行 × 行高），两种视图一致，也不依赖"目标是否已可见"。
        /// </para>
        /// </summary>
        internal void ScrollBy(int delta)
        {
            if (_list.Items.Count == 0 || delta == 0) return;
            if (!_list.IsHandleCreated) return;

            var step = Math.Max(1, Math.Abs(delta) / 120) * 3;
            var itemHeight = Math.Max(1, _list.GetItemRect(0).Height);
            var pixels = step * itemHeight;

            // 滚轮向上（delta > 0）滚回前面，向下滚后面；LVM_SCROLL 的 dy 正数向下。
            SendMessage(_list.Handle, LvmScroll, IntPtr.Zero, (IntPtr)(delta > 0 ? -pixels : pixels));
        }

        /// <summary>测试用：当前第一项可见项的下标（详情用 TopItem；平铺按 Bounds 扫）。</summary>
        internal int FirstVisibleIndexForTest
        {
            get
            {
                if (_list.Items.Count == 0) return -1;

                if (_list.View != View.LargeIcon) return _list.TopItem?.Index ?? 0;

                var first = 0;
                var bestTop = int.MaxValue;

                for (var i = 0; i < _list.Items.Count; i++)
                {
                    var bounds = _list.Items[i].Bounds;

                    if (bounds.Bottom <= 0) continue;
                    if (bounds.Top >= bestTop) continue;

                    bestTop = bounds.Top;
                    first = i;
                }

                return first;
            }
        }

        private const int LvmScroll = 0x1014;

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        /// <summary>
        /// 视图自己的滚轮转发（1.29.0）：鼠标落在列表上、但焦点在地址栏 / 搜索框 / 按钮上时，
        /// 把滚轮转给 <see cref="ScrollBy"/>。
        /// <para>
        /// 两条不接管的例外：目标本来就是列表（原生滚动更顺手）、指针不在列表范围里（别抢工具栏的滚轮）。
        /// </para>
        /// </summary>
        internal bool TryHandleWheelForTest(int delta, Point screenPoint, IntPtr targetHandle) =>
            TryForwardWheel(delta, screenPoint, targetHandle);

        private bool TryForwardWheel(int delta, Point screenPoint, IntPtr targetHandle)
        {
            if (delta == 0 || !IsHandleCreated || !Visible) return false;
            if (_list.Items.Count == 0 || !_list.IsHandleCreated || !_list.Visible) return false;

            // 目标是列表本身：交给 WinForms / ListView 原生滚（人手感一致，也不会滚两遍）。
            var target = targetHandle == IntPtr.Zero ? null : Control.FromHandle(targetHandle);
            if (ReferenceEquals(target, _list)) return false;

            if (!_list.RectangleToScreen(_list.ClientRectangle).Contains(screenPoint)) return false;

            ScrollBy(delta);
            return true;
        }

        /// <summary>只处理滚轮的过滤器；判断逻辑在 <see cref="TryForwardWheel"/>（可以单独断言）。</summary>
        private sealed class WheelForwardFilter : IMessageFilter
        {
            private const int WmMouseWheel = 0x020A;

            private readonly FileBrowserView _owner;

            public WheelForwardFilter(FileBrowserView owner) => _owner = owner;

            public bool PreFilterMessage(ref Message m)
            {
                if (m.Msg != WmMouseWheel) return false;

                var value = (long)m.LParam;
                var screen = new Point((short)(value & 0xFFFF), (short)((value >> 16) & 0xFFFF));
                var delta = (short)(((long)m.WParam >> 16) & 0xFFFF);

                return _owner.TryForwardWheel(delta, screen, m.HWnd);
            }
        }

        /// <summary>恢复一份状态（启动 / 载入设置方案）。已经加载过且目录变了才重新读。</summary>
        internal void RestoreState(FileBrowserState state)
        {
            if (state == null) return;

            var pathChanged = !string.Equals(_state.CurrentPath, state.CurrentPath, StringComparison.OrdinalIgnoreCase);
            var filterChanged = _state.ShowAllFiles != state.ShowAllFiles;
            var sortChanged = _state.SortField != state.SortField || _state.SortDescending != state.SortDescending;
            var wasLoaded = _loadedOnce;

            ResetFilter();
            _pendingSelectPath = null;
            _state = state;
            MaxThumbnails = FileBrowserThumbnailBudgets.Normalize(state.ThumbnailBudget);

            UpdateAddressText();
            UpdateSortHeaders();
            UpdateNavigationButtons();

            // 设置方案里可能只换了「缩略图」这一项：形态与排队都要跟着摆回来。
            ApplyThumbnailMode();
            RefreshThumbnails();

            UpdateTreeAvailability();

            if ((pathChanged || filterChanged) && wasLoaded)
            {
                StartLoad();
                return;
            }

            if (sortChanged && _entries.Count > 0)
            {
                FileBrowserService.Sort(_entries, _state.SortField, _state.SortDescending);
                FillList();
            }

            if (!_loadedOnce && Visible && IsHandleCreated)
                EnsureLoaded();
        }

        // ---- 控件搭建 ---------------------------------------------------------

        private void BuildToolbar()
        {
            _toolbar.Dock = DockStyle.Top;
            _toolbar.Height = Scaled(30);

            _backButton.Text = "←";
            _backButton.AccessibleName = "返回";
            _backButton.Click += (s, e) => GoBack();

            _forwardButton.Text = "→";
            _forwardButton.AccessibleName = "前进";
            _forwardButton.Click += (s, e) => GoForward();

            _upButton.Text = "↑";
            _upButton.AccessibleName = "上一级";
            _upButton.Click += (s, e) => GoUp();

            _refreshButton.Text = "⟳";
            _refreshButton.AccessibleName = "刷新";
            _refreshButton.Click += (s, e) => Reload();

            _positionButton.Text = "位置";
            _positionButton.AccessibleName = "收藏与最近浏览";
            _positionButton.Click += (s, e) =>
                _positionMenu.Show(_positionButton, new Point(0, _positionButton.Height));

            _address.BorderStyle = BorderStyle.FixedSingle;
            _address.AccessibleName = "文件夹路径";
            _address.KeyDown += OnAddressKeyDown;

            foreach (var control in new Control[]
                     { _backButton, _forwardButton, _upButton, _refreshButton, _address, _positionButton })
            {
                _toolbar.Controls.Add(control);
            }
        }

        private void BuildFilterBar()
        {
            _filterBar.Dock = DockStyle.Top;

            _search.Dock = DockStyle.None;
            _search.BorderStyle = BorderStyle.FixedSingle;
            _search.Font = UiFonts.Sidebar;
            _search.AccessibleName = "搜索当前文件夹";

            _recursiveSearch.Dock = DockStyle.None;
            _recursiveSearch.AutoSize = false;
            _recursiveSearch.Appearance = Appearance.Button;
            _recursiveSearch.FlatStyle = FlatStyle.Flat;
            _recursiveSearch.TextAlign = ContentAlignment.MiddleCenter;
            _recursiveSearch.TabStop = true;
            _recursiveSearch.Text = "含子文件夹";
            _recursiveSearch.AccessibleName = "搜索子文件夹";
            _recursiveSearch.AccessibleDescription =
                "勾选后按 Enter 搜索当前文件夹及子文件夹；输入时不会扫盘";

            _search.TextChanged += (s, e) =>
            {
                if (_suppressFilterEvents) return;
                _query = _search.Text;
                ApplyFilter();
            };

            _search.KeyDown += OnSearchKeyDown;

            _recursiveSearch.CheckedChanged += (s, e) => OnRecursiveSearchChanged();

            _filterBar.Controls.Add(_search);
            _filterBar.Controls.Add(_recursiveSearch);

            UpdateSearchPlaceholder();
            LayoutFilterBar();
        }

        private void LayoutFilterBar()
        {
            _filterBar.Height = Scaled(30);
            _filterBar.Padding = Padding.Empty;

            var pad = Scaled(6);
            var gap = Scaled(4);
            var top = Scaled(4);
            var height = Scaled(22);
            var scopeWidth = MeasureButtonWidth(_recursiveSearch, 88);
            var right = Width - pad;

            _recursiveSearch.SetBounds(
                Math.Max(pad, right - scopeWidth),
                top,
                scopeWidth,
                height);

            _search.SetBounds(
                pad,
                top,
                Math.Max(0, right - scopeWidth - gap - pad),
                height);
        }

        private void UpdateSearchPlaceholder()
        {
            _search.PlaceholderText = _recursiveSearch.Checked
                ? "输入关键字，按 Enter 搜索子文件夹"
                : "搜索当前文件夹（空格分隔多关键字）";

            _search.AccessibleDescription = _recursiveSearch.Checked
                ? "输入关键字后按 Enter 搜索当前文件夹及子文件夹，不建索引；Esc 清空"
                : "按文件名筛选当前文件夹，不递归；Esc 清空";
        }

        private void BuildList()
        {
            _list.Dock = DockStyle.Fill;
            _list.View = View.Details;
            _list.FullRowSelect = true;
            _list.MultiSelect = true;
            _list.HideSelection = false;
            _list.ShowItemToolTips = true;
            _list.HeaderStyle = ColumnHeaderStyle.Clickable;
            _list.UseCompatibleStateImageBehavior = false;
            _list.GridLines = false;
            _list.LabelEdit = false;
            _list.Scrollable = true;
            _list.ShowGroups = false;

            _nameColumn.Width = Scaled(160);
            _sizeColumn.Width = Scaled(78);
            _sizeColumn.TextAlign = HorizontalAlignment.Right;
            _folderColumn.Width = 0;

            _list.Columns.Add(_nameColumn);
            _list.Columns.Add(_sizeColumn);
            _list.Columns.Add(_folderColumn);

            _list.DoubleClick += (s, e) => ActivateSelection();
            _list.KeyDown += OnListKeyDown;
            _list.ColumnClick += OnColumnClick;
            _list.MouseUp += OnListMouseUp;
            _list.ItemDrag += OnFileItemDrag;
        }

        private void BuildStatus()
        {
            _status.Dock = DockStyle.Bottom;
            _status.Height = Scaled(22);
            _status.AutoEllipsis = true;
            _status.TextAlign = ContentAlignment.MiddleLeft;
            _status.Padding = new Padding(Scaled(8), 0, 0, 0);
            _status.Text = string.Empty;
        }

        // ---- 尺寸与主题 -------------------------------------------------------

        private float DpiScale => DeviceDpi / 96f;

        private int Scaled(int value) => (int)Math.Round(value * DpiScale);

        /// <summary>
        /// 按钮宽度：取"设计值"与"文字宽度 + 两侧留白"里大的那个。
        /// <para>
        /// 以前这两个控件是写死的逻辑尺寸（位置 42 / 含子文件夹 88）：在 125% 与宽字形字体下
        /// 「位置」只剩一个「位」、「含子文件夹」的「夹」被切掉（所有者 2026-11-20 截图）。
        /// 文字宽度必须现量，不能拿一个常量去赌所有 DPI / 字体替换。
        /// </para>
        /// </summary>
        private int MeasureButtonWidth(Control control, int minimum)
        {
            var text = TextRenderer.MeasureText(control.Text, control.Font);
            return Math.Max(Scaled(minimum), text.Width + Scaled(18));
        }

        private void LayoutToolbar()
        {
            var pad = Scaled(6);
            var gap = Scaled(4);
            var top = Scaled(4);
            var side = Scaled(24);

            _toolbar.Height = Scaled(30);

            var x = pad;

            foreach (var button in new[] { _backButton, _forwardButton, _upButton, _refreshButton })
            {
                button.SetBounds(x, top, side, side);
                x += side + gap;
            }

            var positionWidth = MeasureButtonWidth(_positionButton, 42);
            _positionButton.SetBounds(Width - pad - positionWidth, top, positionWidth, side);

            var addressLeft = x;
            var addressRight = Width - pad - positionWidth - gap;

            _address.SetBounds(
                addressLeft,
                Scaled(5),
                Math.Max(Scaled(40), addressRight - addressLeft),
                Scaled(22));
        }

        private void AdjustColumns()
        {
            // 平铺（大图标）没有表头，列宽不参与显示；切回详情时会再调一次。
            if (_list.View == View.LargeIcon) return;

            var width = _list.ClientSize.Width;
            if (width <= 0) return;

            var size = Scaled(78);
            var folder = _searchActive || _searching ? Scaled(84) : 0;

            _sizeColumn.Width = size;
            _folderColumn.Width = folder;
            _nameColumn.Width = Math.Max(Scaled(100), width - size - folder - 4);
        }

        private void RebuildIcons()
        {
            // 先摘掉，再释放旧的一组：ListView 重绘时不能再摸已经 Dispose 的 ImageList。
            _list.SmallImageList = null;
            _list.LargeImageList = null;

            _icons?.Dispose();
            _icons = new FileIconSet(_palette, Scaled(16));

            _list.SmallImageList = _icons.Images;

            RefreshTreeIcons();

            // 大图标与缩略图缓存的索引都是按旧的一套算的，跟着一起作废。
            RebuildThumbnails();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);

            LayoutToolbar();
            LayoutFilterBar();
            AdjustColumns();
            RebuildIcons();
            ApplyNativeTheme(_nativeDark);
            UpdateSearchScopeEnabled();
            UpdateTreeAvailability();

            if (Visible) EnsureLoaded();
        }

        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);

            LayoutToolbar();
            LayoutFilterBar();
            AdjustColumns();
            RebuildIcons();
            LayoutTree();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);

            LayoutToolbar();
            LayoutFilterBar();
            AdjustColumns();
            UpdateTreeAvailability();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);

            if (Visible && IsHandleCreated) EnsureLoaded();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Application.RemoveMessageFilter(_wheelFilter);

                CancelLoad();
                CancelSearch();
                StopWatch();

                DisposeThumbnails();

                _list.SmallImageList = null;
                _icons?.Dispose();
                _icons = null;
                _contextMenu.Dispose();
                _positionMenu.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
