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
        private readonly TextBox _address = new TextBox();
        private readonly ListView _list = new ListView();
        private readonly ColumnHeader _nameColumn = new ColumnHeader { Text = "名称" };
        private readonly ColumnHeader _sizeColumn = new ColumnHeader { Text = "大小" };
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
        private FileIconSet? _icons;

        private CancellationTokenSource? _loadCts;
        private int _loadVersion;
        private bool _loading;
        private bool _loadedOnce;
        private bool _statusIsError;

        private ThemePalette _palette = Themes.Light;
        private bool _nativeDark;

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
            BuildList();
            BuildStatus();

            // 先加 Fill 再加 Top / Bottom：WinForms 的停靠顺序是反的，
            // 这样列表占剩下的区域，工具栏与状态行不会被它盖住。
            Controls.Add(_list);
            Controls.Add(_toolbar);
            Controls.Add(_status);

            BuildContextMenu();
            BuildPositionMenu();

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

        internal FileBrowserState State => _state;

        internal ListView List => _list;

        internal string StatusText => _status.Text ?? string.Empty;

        internal bool IsLoading => _loading;

        internal IReadOnlyList<FileBrowserEntry> Entries => _entries;

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

            _list.BackColor = palette.ListBack;
            _list.ForeColor = palette.ListFore;

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

        /// <summary>侧栏的滚轮转发到这里：按行滚动列表（ListView 没焦点时收不到滚轮）。</summary>
        internal void ScrollBy(int delta)
        {
            if (_list.Items.Count == 0 || delta == 0) return;

            var step = Math.Max(1, Math.Abs(delta) / 120) * 3;
            var top = _list.TopItem?.Index ?? 0;
            var target = delta > 0 ? top - step : top + step;

            target = Math.Clamp(target, 0, _list.Items.Count - 1);
            _list.TopItem = _list.Items[target];
        }

        /// <summary>恢复一份状态（启动 / 载入设置方案）。已经加载过且目录变了才重新读。</summary>
        internal void RestoreState(FileBrowserState state)
        {
            if (state == null) return;

            var pathChanged = !string.Equals(_state.CurrentPath, state.CurrentPath, StringComparison.OrdinalIgnoreCase);
            var filterChanged = _state.ShowAllFiles != state.ShowAllFiles;
            var sortChanged = _state.SortField != state.SortField || _state.SortDescending != state.SortDescending;
            var wasLoaded = _loadedOnce;

            _state = state;

            UpdateAddressText();
            UpdateSortHeaders();
            UpdateNavigationButtons();

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

            _list.Columns.Add(_nameColumn);
            _list.Columns.Add(_sizeColumn);

            _list.DoubleClick += (s, e) => ActivateSelection();
            _list.KeyDown += OnListKeyDown;
            _list.ColumnClick += OnColumnClick;
            _list.MouseUp += OnListMouseUp;
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

            var positionWidth = Scaled(42);
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
            var width = _list.ClientSize.Width;
            if (width <= 0) return;

            var size = Scaled(78);
            _sizeColumn.Width = size;
            _nameColumn.Width = Math.Max(Scaled(100), width - size - 4);
        }

        private void RebuildIcons()
        {
            // 先摘掉，再释放旧的一组：ListView 重绘时不能再摸已经 Dispose 的 ImageList。
            _list.SmallImageList = null;

            _icons?.Dispose();
            _icons = new FileIconSet(_palette, Scaled(16));

            _list.SmallImageList = _icons.Images;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);

            LayoutToolbar();
            AdjustColumns();
            RebuildIcons();
            ApplyNativeTheme(_nativeDark);

            if (Visible) EnsureLoaded();
        }

        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);

            LayoutToolbar();
            AdjustColumns();
            RebuildIcons();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);

            LayoutToolbar();
            AdjustColumns();
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
                CancelLoad();
                _icons?.Dispose();
                _icons = null;
                _contextMenu.Dispose();
                _positionMenu.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
