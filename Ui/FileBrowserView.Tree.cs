using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using 播放器.Core;

namespace 播放器.Ui
{
    /// <summary>
    /// 文件夹树（1.28.0）：懒展开、跟列表互相同步、网络目录按需读。
    /// <para>
    /// 三条规矩：① <b>枚举都在后台</b>（本地目录也走 <see cref="Task.Run(Func{object})"/>，
    /// 离线 SMB 上展开一个节点会等系统超时，界面不能冻）；② 每个节点最多
    /// <see cref="FolderTreeModel.MaxChildren"/> 个子目录，触顶在状态行说明；③ 目录联接 /
    /// 符号链接当叶子（点了只导航，不给展开箭头），与 1.24 / 1.26 的递归口径一致。
    /// </para>
    /// <para>
    /// 树默认为关：默认侧栏只有 300 逻辑像素，塞进 160 的树之后列表只剩一点点；
    /// 宽度不足 <see cref="TreeMinimumWidthLogical"/> 时开关置灰并说明（拖宽侧栏或分离成独立窗口）。
    /// </para>
    /// </summary>
    internal sealed partial class FileBrowserView
    {
        /// <summary>视图窄于这个宽度就不放树（逻辑像素）。</summary>
        internal const int TreeMinimumWidthLogical = 360;

        /// <summary>树的默认宽度（逻辑像素）。</summary>
        private const int TreeWidthLogical = 160;

        private const int TreeRowHeightLogical = 22;

        /// <summary>节点上的数据：路径（此电脑为 null）与是不是驱动器。</summary>
        private sealed class TreeEntryTag
        {
            internal TreeEntryTag(string? path, bool isDrive)
            {
                Path = path;
                IsDrive = isDrive;
            }

            internal string? Path { get; }

            internal bool IsDrive { get; }
        }

        /// <summary>一次"列子目录"的结果（后台任务交给界面）。</summary>
        private sealed class TreeLoadResult
        {
            internal TreeLoadResult(
                List<FileBrowserEntry> folders, HashSet<string> links, bool truncated, string? error)
            {
                Folders = folders;
                Links = links;
                Truncated = truncated;
                Error = error;
            }

            internal List<FileBrowserEntry> Folders { get; }

            internal HashSet<string> Links { get; }

            internal bool Truncated { get; }

            internal string? Error { get; }
        }

        private readonly FolderTree _tree = new FolderTree();

        /// <summary>占位子节点的标记：有它在就说明"还没展开过"。</summary>
        private readonly object _treeDummy = new object();

        private readonly HashSet<TreeNode> _treeLoading = new HashSet<TreeNode>();
        private readonly HashSet<TreeNode> _treeFailed = new HashSet<TreeNode>();
        private readonly Dictionary<TreeNode, int> _treeLoadVersions = new Dictionary<TreeNode, int>();

        private int _treeLoadSerial;
        private int _revealVersion;
        private string? _revealTarget;
        private bool _syncingTree;

        // ---- 测试钩子 ---------------------------------------------------------

        internal bool IsTreeVisibleForTest => _tree.Visible;

        internal int TreeWidthForTest => _tree.Width;

        internal bool TreeLoadingForTest => _treeLoading.Count > 0;

        internal string? SelectedTreePathForTest => (_tree.SelectedNode?.Tag as TreeEntryTag)?.Path;

        internal IReadOnlyList<string> TreeNodePathsForTest
        {
            get
            {
                var paths = new List<string>();

                foreach (TreeNode root in _tree.Nodes)
                {
                    CollectNodePaths(root, paths);
                }

                return paths;
            }
        }

        internal void SetFolderTreeForTest(bool value) => SetFolderTree(value);

        internal void SelectTreePathForTest(string? path) => SelectTreePath(path);

        internal bool ExpandTreeNodeForTest(string path)
        {
            var node = FindNodeByPath(path);

            if (node == null) return false;

            node.Expand();
            return true;
        }

        internal int TreeNodeChildCountForTest(string path) => FindNodeByPath(path)?.Nodes.Count ?? -1;

        internal bool ClickTreeNodeForTest(string path)
        {
            var node = FindNodeByPath(path);

            if (node == null) return false;

            _syncingTree = false;
            _tree.SelectedNode = node;

            return true;
        }

        /// <summary>「位置」菜单里的某一项（会先按当前状态重建一次菜单）。</summary>
        internal ToolStripMenuItem? PositionMenuItemForTest(string prefix)
        {
            RebuildPositionMenu();

            foreach (ToolStripItem item in _positionMenu.Items)
            {
                if (item is ToolStripMenuItem menu && menu.Text.StartsWith(prefix, StringComparison.Ordinal))
                    return menu;
            }

            return null;
        }

        private TreeNode? FindNodeByPath(string path)
        {
            foreach (TreeNode root in _tree.Nodes)
            {
                var found = FindNode(root, path);

                if (found != null) return found;
            }

            return null;
        }

        private static TreeNode? FindNode(TreeNode node, string path)
        {
            if (node.Tag is TreeEntryTag tag && tag.Path != null &&
                string.Equals(tag.Path, path, StringComparison.OrdinalIgnoreCase))
                return node;

            foreach (TreeNode child in node.Nodes)
            {
                var found = FindNode(child, path);

                if (found != null) return found;
            }

            return null;
        }

        private static void CollectNodePaths(TreeNode node, List<string> paths)
        {
            if (node.Tag is TreeEntryTag tag && tag.Path != null) paths.Add(tag.Path);

            foreach (TreeNode child in node.Nodes) CollectNodePaths(child, paths);
        }

        // ---- 开关 / 布局 -------------------------------------------------------

        /// <summary>视图宽度够不够放树。</summary>
        private bool TreeCanShow => Width >= Scaled(TreeMinimumWidthLogical);

        internal bool CanShowFolderTree => TreeCanShow;

        private void BuildTree()
        {
            _tree.Dock = DockStyle.Left;
            _tree.Visible = false;
            _tree.Width = Scaled(TreeWidthLogical);
            _tree.SetMetrics(Scaled(TreeRowHeightLogical));

            _tree.BeforeExpand += OnTreeBeforeExpand;
            _tree.AfterSelect += OnTreeAfterSelect;
            _tree.KeyDown += OnTreeKeyDown;
        }

        internal void SetFolderTree(bool value)
        {
            if (_state.ShowFolderTree == value) return;

            _state.ShowFolderTree = value;

            RaiseStateChanged();
            UpdateTreeAvailability();

            if (!value)
            {
                SetStatus("文件夹树已关闭", false);
                return;
            }

            if (!TreeCanShow)
            {
                SetStatus("窗口太窄，放不下文件夹树（把它拖宽或分离成独立窗口）", true);
                return;
            }

            SetStatus("文件夹树已打开", false);
        }

        /// <summary>按"开关 + 当前宽度"决定显不显示；刚显示出来时把当前目录选上。</summary>
        private void UpdateTreeAvailability()
        {
            var wanted = _state.ShowFolderTree && TreeCanShow;

            if (_tree.Visible == wanted)
            {
                LayoutTree();
                return;
            }

            _tree.Visible = wanted;

            if (!wanted) return;

            EnsureTreeRoot();
            LayoutTree();
            SelectTreePath(_state.CurrentPath);
        }

        private void LayoutTree()
        {
            if (!_tree.Visible) return;

            var maximum = Math.Max(Scaled(120), (Width - Scaled(120)) / 2);

            _tree.Width = Math.Clamp(Scaled(TreeWidthLogical), Scaled(120), maximum);
            _tree.SetMetrics(Scaled(TreeRowHeightLogical));
        }

        private void RefreshTreeIcons()
        {
            if (_tree.IsDisposed) return;

            _tree.ImageList = _icons?.Images;

            foreach (TreeNode root in _tree.Nodes) RefreshNodeIcons(root, _icons);
        }

        private static void RefreshNodeIcons(TreeNode node, FileIconSet? icons)
        {
            if (node.Tag is TreeEntryTag tag)
            {
                var index = icons?.IndexOf(tag.IsDrive ? FileBrowserKind.Drive : FileBrowserKind.Folder) ?? 0;

                node.ImageIndex = index;
                node.SelectedImageIndex = index;
            }

            foreach (TreeNode child in node.Nodes) RefreshNodeIcons(child, icons);
        }

        // ---- 加载（后台） ------------------------------------------------------

        private TreeNode EnsureTreeRoot()
        {
            if (_tree.Nodes.Count > 0) return _tree.Nodes[0];

            _tree.ImageList = _icons?.Images;

            var root = new TreeNode(FileBrowserPaths.ComputerLabel)
            {
                Tag = new TreeEntryTag(null, false),
                ImageIndex = _icons?.IndexOf(FileBrowserKind.Drive) ?? 0,
                SelectedImageIndex = _icons?.IndexOf(FileBrowserKind.Drive) ?? 0,
                ToolTipText = FileBrowserPaths.ComputerLabel
            };

            root.Nodes.Add(new TreeNode(string.Empty) { Tag = _treeDummy });

            _tree.Nodes.Add(root);

            LoadChildren(root);

            return root;
        }

        private void OnTreeBeforeExpand(object? sender, TreeViewCancelEventArgs e)
        {
            var node = e.Node;

            if (node == null) return;
            if (_treeLoading.Contains(node) || _treeFailed.Contains(node)) return;

            if (node.Nodes.Count == 1 && ReferenceEquals(node.Nodes[0].Tag, _treeDummy)) LoadChildren(node);
        }

        /// <summary>后台列这一层的子目录；结果用 BeginInvoke 搬回界面（测试宿主里没有同步上下文）。</summary>
        private void LoadChildren(TreeNode node)
        {
            var id = ++_treeLoadSerial;

            _treeLoadVersions[node] = id;
            _treeLoading.Add(node);

            var tag = node.Tag as TreeEntryTag;
            var path = tag?.Path;
            var showAll = _state.ShowAllFiles;

            node.Nodes.Clear();
            node.Nodes.Add(new TreeNode("（正在读取…）") { Tag = _treeDummy });

            Task.Run(() => LoadChildrenCore(path, showAll))
                .ContinueWith(
                    task =>
                    {
                        if (IsDisposed || !IsHandleCreated)
                        {
                            _treeLoading.Remove(node);
                            return;
                        }

                        try
                        {
                            BeginInvoke(new Action(() => ApplyChildren(node, id, task)));
                        }
                        catch (InvalidOperationException)
                        {
                            _treeLoading.Remove(node);
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
        }

        private TreeLoadResult LoadChildrenCore(string? path, bool showAll)
        {
            if (path == null)
            {
                var roots = _service.ListRoots(CancellationToken.None);

                return new TreeLoadResult(
                    roots.Entries, new HashSet<string>(StringComparer.OrdinalIgnoreCase), roots.Truncated, roots.Error);
            }

            var children = FolderTreeModel.ChildFolders(
                path, showAll, FolderTreeModel.MaxChildren, CancellationToken.None);

            return new TreeLoadResult(children.Folders, children.Links, children.Truncated, children.Error);
        }

        private void ApplyChildren(TreeNode node, int id, Task<TreeLoadResult> task)
        {
            _treeLoading.Remove(node);

            if (IsDisposed) return;

            if (!_treeLoadVersions.TryGetValue(node, out var current) || current != id) return;

            _treeLoadVersions.Remove(node);

            node.Nodes.Clear();

            if (!task.IsCompletedSuccessfully)
            {
                _treeFailed.Add(node);
                node.Nodes.Add(DisabledNode("（打不开）"));
                AppLog.Swallowed("文件夹树：加载子目录失败", task.Exception);

                ContinueReveal();
                return;
            }

            var result = task.Result;

            if (!string.IsNullOrEmpty(result.Error))
            {
                _treeFailed.Add(node);
                node.Nodes.Add(DisabledNode("（" + result.Error + "）"));

                ContinueReveal();
                return;
            }

            foreach (var entry in result.Folders)
            {
                var isLink = result.Links.Contains(entry.FullPath);

                var child = new TreeNode(entry.Name)
                {
                    Tag = new TreeEntryTag(entry.FullPath, entry.Kind == FileBrowserKind.Drive),
                    ImageIndex = _icons?.IndexOf(entry.Kind) ?? 0,
                    SelectedImageIndex = _icons?.IndexOf(entry.Kind) ?? 0,
                    ToolTipText = entry.FullPath
                };

                // 目录联接 / 符号链接当叶子：不给展开箭头，也不跟进去。
                if (!isLink) child.Nodes.Add(new TreeNode(string.Empty) { Tag = _treeDummy });

                node.Nodes.Add(child);
            }

            if (result.Folders.Count == 0) node.Nodes.Add(DisabledNode("（空文件夹）"));

            if (result.Truncated)
            {
                node.Nodes.Add(DisabledNode($"（只列出了前 {FolderTreeModel.MaxChildren:N0} 个）"));

                SetStatus($"树只列出了前 {FolderTreeModel.MaxChildren:N0} 个文件夹", false);
            }

            ContinueReveal();
        }

        private TreeNode DisabledNode(string text) =>
            new TreeNode(text) { Tag = null, ForeColor = _palette.HintFore };

        // ---- 选中同步 ---------------------------------------------------------

        /// <summary>把树选到 <paramref name="path"/>；逐层展开（每层一次后台读），完成后再继续。</summary>
        private void SelectTreePath(string? path)
        {
            if (!_tree.Visible || IsDisposed) return;

            _revealTarget = path;
            _revealVersion++;

            ContinueReveal();
        }

        private void ContinueReveal()
        {
            if (!_tree.Visible || IsDisposed) return;

            var chain = FolderTreeModel.ChainOf(_revealTarget);
            var node = EnsureTreeRoot();

            foreach (var segment in chain)
            {
                var child = FindChildNode(node, segment);

                if (child == null)
                {
                    // 这一层还没读出来：读它，读完后 ApplyChildren 会再调回来。
                    if (_treeLoading.Contains(node) || _treeFailed.Contains(node)) return;

                    if (!node.IsExpanded) node.Expand();
                    else LoadChildren(node);

                    return;
                }

                if (!child.IsExpanded && !_treeLoading.Contains(child) && !_treeFailed.Contains(child))
                    child.Expand();

                node = child;
            }

            SetSelectedTreeNode(node);
        }

        private static TreeNode? FindChildNode(TreeNode parent, string path)
        {
            foreach (TreeNode child in parent.Nodes)
            {
                if (child.Tag is not TreeEntryTag tag || tag.Path == null) continue;
                if (!string.Equals(tag.Path, path, StringComparison.OrdinalIgnoreCase)) continue;

                return child;
            }

            return null;
        }

        private void SetSelectedTreeNode(TreeNode node)
        {
            if (ReferenceEquals(_tree.SelectedNode, node))
            {
                node.EnsureVisible();
                return;
            }

            _syncingTree = true;

            try
            {
                _tree.SelectedNode = node;
                node.EnsureVisible();
            }
            finally
            {
                _syncingTree = false;
            }
        }

        private void OnTreeAfterSelect(object? sender, TreeViewEventArgs e)
        {
            if (_syncingTree) return;

            if (e.Node?.Tag is not TreeEntryTag tag) return;

            if (string.Equals(tag.Path, _state.CurrentPath, StringComparison.OrdinalIgnoreCase)) return;

            NavigateTo(tag.Path);
        }

        private void OnTreeKeyDown(object? sender, KeyEventArgs e)
        {
            switch (e.KeyCode)
            {
                case Keys.Enter:
                    e.Handled = true;

                    if (_tree.SelectedNode?.Tag is TreeEntryTag tag) NavigateTo(tag.Path);
                    return;

                case Keys.Escape:
                    e.Handled = true;
                    _list.Focus();
                    return;
            }
        }
    }
}
