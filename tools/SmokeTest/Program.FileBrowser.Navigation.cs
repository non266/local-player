using System.Diagnostics;

// 检查点 34（第 29 个运行步骤）：文件页的导航与新鲜度（1.28.0）。
//   ① 文件夹树：链 / 子目录（隐藏项、联接、上限、错误）、懒展开、与列表互相同步、窄视图说明；
//   ② 自动刷新：静默（不闪 / 保留选中）、增删改、退出开关、网络不盯、搜索页不打扰。

namespace SmokeTest
{
    internal static partial class Program
    {
        private static bool TestFileBrowserNavigation()
        {
            var root = Path.Combine(AppContext.BaseDirectory, "smoke-fb-nav");
            TryDeleteDirectory(root);

            try
            {
                Directory.CreateDirectory(root);

                if (!CheckFolderTreeModel(root)) return false;
                if (!CheckFileBrowserFolderTree(root)) return false;
                if (!CheckFileBrowserAutoRefresh(root)) return false;

                Log(34, "文件页导航正常：文件夹树（懒展开 / 同步 / 窄视图说明 / 不跟联接）与"
                        + "自动刷新（静默 / 保留选中 / 网络不盯 / 搜索页不打扰）都按预期工作");
                return true;
            }
            catch (Exception ex)
            {
                Log(34, "文件页导航检查失败: " + ex);
                return false;
            }
            finally
            {
                TryDeleteDirectory(root);
            }
        }

        /// <summary>两套夹具：树用的目录层级、自动刷新用的目录。</summary>
        private static (string Tree, string Watch) BuildNavigationFixtures(string root)
        {
            var tree = Path.Combine(root, "tree");
            var deep = Path.Combine(tree, "alpha", "b", "c", "d");

            Directory.CreateDirectory(deep);
            Directory.CreateDirectory(Path.Combine(tree, "beta"));
            Directory.CreateDirectory(Path.Combine(tree, "empty"));

            var hidden = Path.Combine(tree, "hidden-dir");
            Directory.CreateDirectory(hidden);
            File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);

            File.WriteAllText(Path.Combine(tree, "keep.mp3"), "keep");
            File.WriteAllText(Path.Combine(deep, "deep.mp3"), "deep");

            var watch = Path.Combine(root, "watch");
            Directory.CreateDirectory(watch);
            File.WriteAllText(Path.Combine(watch, "keep.mp3"), "keep");
            File.WriteAllText(Path.Combine(watch, "other.mp3"), "other");

            return (tree, watch);
        }

        // -----------------------------------------------------------------
        // 纯逻辑：路径链 / 列子目录
        // -----------------------------------------------------------------

        private static bool CheckFolderTreeModel(string root)
        {
            var (tree, _) = BuildNavigationFixtures(root);

            // 链：本地三层 / 盘根 / UNC / 此电脑 / 坏路径
            var local = FolderTreeModel.ChainOf(@"C:\A\B");

            if (local.Count != 3 ||
                !string.Equals(local[0], @"C:\", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(local[2], @"C:\A\B", StringComparison.OrdinalIgnoreCase))
            {
                Log(34, "文件夹树：C:\\A\\B 的祖先链不对（" + string.Join(" / ", local) + "）");
                return false;
            }

            if (FolderTreeModel.ChainOf(@"D:\").Count != 1)
            {
                Log(34, "文件夹树：盘根的祖先链不是它自己");
                return false;
            }

            var unc = FolderTreeModel.ChainOf(@"\\server\share\a");

            if (unc.Count != 2 || !string.Equals(unc[0], @"\\server\share", StringComparison.OrdinalIgnoreCase))
            {
                Log(34, "文件夹树：UNC 的祖先链不对（" + string.Join(" / ", unc) + "）");
                return false;
            }

            if (FolderTreeModel.ChainOf(null).Count != 0 || FolderTreeModel.ChainOf("relative").Count != 0 ||
                FolderTreeModel.ChainOf("http://x/a").Count != 0)
            {
                Log(34, "文件夹树：此电脑 / 相对路径 / 网络流不该有祖先链");
                return false;
            }

            // 列子目录：只要文件夹、默认滤掉隐藏项、按名称排序、上限截断
            var children = FolderTreeModel.ChildFolders(tree, showAllFiles: false, 2000, CancellationToken.None);

            if (children.Error != null)
            {
                Log(34, "文件夹树：普通目录列失败（" + children.Error + "）");
                return false;
            }

            var names = children.Folders.Select(entry => entry.Name).ToList();

            if (!names.Contains("alpha", StringComparer.OrdinalIgnoreCase) ||
                !names.Contains("beta", StringComparer.OrdinalIgnoreCase) ||
                !names.Contains("empty", StringComparer.OrdinalIgnoreCase))
            {
                Log(34, "文件夹树：默认视图里少了目录（" + string.Join(" / ", names) + "）");
                return false;
            }

            if (names.Contains("hidden-dir", StringComparer.OrdinalIgnoreCase))
            {
                Log(34, "文件夹树：默认视图里出现了隐藏文件夹");
                return false;
            }

            if (names.Contains("keep.mp3", StringComparer.OrdinalIgnoreCase))
            {
                Log(34, "文件夹树：把文件也列进树里了");
                return false;
            }

            var showAll = FolderTreeModel.ChildFolders(tree, true, 2000, CancellationToken.None);

            if (!showAll.Folders.Any(entry => string.Equals(entry.Name, "hidden-dir", StringComparison.OrdinalIgnoreCase)))
            {
                Log(34, "文件夹树：打开「显示所有文件」之后仍然看不到隐藏文件夹");
                return false;
            }

            var limited = FolderTreeModel.ChildFolders(tree, true, 2, CancellationToken.None);

            if (limited.Folders.Count != 2 || !limited.Truncated)
            {
                Log(34, $"文件夹树：上限没有截断（{limited.Folders.Count} 条，截断 {limited.Truncated}）");
                return false;
            }

            var missing = FolderTreeModel.ChildFolders(Path.Combine(tree, "not-there"), true, 2000, CancellationToken.None);

            if (missing.Error == null || !missing.Error.Contains("不存在", StringComparison.Ordinal))
            {
                Log(34, "文件夹树：不存在的目录没有报「不存在」");
                return false;
            }

            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();

                var cancelled = FolderTreeModel.ChildFolders(tree, true, 2000, cts.Token);

                if (!cancelled.Cancelled)
                {
                    Log(34, "文件夹树：取消之后没有报告取消");
                    return false;
                }
            }

            // 目录联接：造得出来就验"照常列出、但标成联接（树里当叶子）"；造不出来如实跳过。
            var junction = Path.Combine(tree, "junction");
            var target = Path.Combine(tree, "beta");

            if (TryCreateJunction(junction, target))
            {
                var withLink = FolderTreeModel.ChildFolders(tree, true, 2000, CancellationToken.None);

                if (!withLink.Links.Contains(junction))
                {
                    Log(34, "文件夹树：目录联接没有被标出来（树里会被当成可展开的目录）");
                    return false;
                }
            }
            else
            {
                Log(34, "文件夹树：目录联接那一段跳过（这台机器上建不出 mklink /J）");
            }

            Log(34, "文件夹树逻辑正确：祖先链（本地 / 盘根 / UNC / 此电脑）、只列子目录、隐藏项过滤、"
                    + "上限截断、错误与取消、目录联接标记");
            return true;
        }

        /// <summary>建一个目录联接（mklink /J 不需要管理员，但受限环境可能不给建）。</summary>
        private static bool TryCreateJunction(string link, string target)
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });

                if (process == null) return false;
                if (!process.WaitForExit(5000)) return false;

                return process.ExitCode == 0 && Directory.Exists(link);
            }
            catch (Exception)
            {
                return false;
            }
        }

        // -----------------------------------------------------------------
        // 真窗体：树
        // -----------------------------------------------------------------

        private static bool CheckFileBrowserFolderTree(string root)
        {
            var (tree, _) = BuildNavigationFixtures(root);
            var deep = Path.Combine(tree, "alpha", "b", "c", "d");

            var settings = AppSettings.Load();
            settings.ShowSidebar = true;
            settings.SidebarFloating = false;
            settings.FileBrowserFloating = false;
            settings.SidebarPage = SidebarTab.Files;
            settings.SidebarWidth = 720;
            settings.FileBrowserLastPath = tree;
            settings.FileBrowserFolderTree = false;
            settings.FileBrowserAutoRefresh = false;
            settings.FileBrowserThumbnails = false;
            settings.FileBrowserShowAllFiles = false;
            settings.FileBrowserSort = "Name";
            settings.FileBrowserSortDescending = false;
            settings.Save();

            using (var form = new 播放器.MainForm(Array.Empty<string>()))
            {
                form.Show();
                PumpMessages(400);

                var sidebar = Find(form, "sidebarPanel") as SidebarPanel;

                if (sidebar == null)
                {
                    Log(34, "文件树检查：找不到侧栏");
                    return false;
                }

                sidebar.ActiveTab = SidebarTab.Files;

                var view = form.FileBrowser;

                if (!PumpUntil(() => !view.IsLoading && view.List.Items.Count > 0, 8000))
                {
                    Log(34, "文件树检查：没有读出目录（状态：" + view.StatusText + "）");
                    return false;
                }

                if (view.IsTreeVisibleForTest)
                {
                    Log(34, "文件树检查：默认就把树显示出来了（默认该是关的）");
                    return false;
                }

                var menu = view.PositionMenuItemForTest("文件夹树");

                if (menu == null)
                {
                    Log(34, "文件树检查：「位置」菜单里没有文件夹树");
                    return false;
                }

                if (!menu.Enabled)
                {
                    Log(34, "文件树检查：侧栏已经够宽，文件夹树那项却是灰的");
                    return false;
                }

                view.SetFolderTreeForTest(true);

                if (!PumpUntil(() => view.IsTreeVisibleForTest
                                     && string.Equals(view.SelectedTreePathForTest, tree, StringComparison.OrdinalIgnoreCase),
                        8000))
                {
                    Log(34, "文件树检查：打开之后没有把当前目录选上（选中："
                            + (view.SelectedTreePathForTest ?? "空") + "）");
                    return false;
                }

                var scale = form.DeviceDpi / 96.0;
                var expectedWidth = (int)Math.Round(160 * scale);

                if (Math.Abs(view.TreeWidthForTest - expectedWidth) > 3)
                {
                    Log(34, $"文件树检查：树的宽度不对（{view.TreeWidthForTest}，期望约 {expectedWidth}）");
                    return false;
                }

                if (view.List.Left < view.TreeWidthForTest - 2)
                {
                    Log(34, "文件树检查：树没有排在列表左边（停靠顺序不对）");
                    return false;
                }

                // 懒展开：展开 alpha 之后才出现 b。
                if (!view.ExpandTreeNodeForTest(Path.Combine(tree, "alpha")))
                {
                    Log(34, "文件树检查：找不到 alpha 节点");
                    return false;
                }

                if (!PumpUntil(() => view.TreeNodePathsForTest.Any(
                        path => string.Equals(path, Path.Combine(tree, "alpha", "b"), StringComparison.OrdinalIgnoreCase)),
                    6000))
                {
                    Log(34, "文件树检查：展开之后没有出现子文件夹 b（" + string.Join(" / ", view.TreeNodePathsForTest) + "）");
                    return false;
                }

                // 点树 → 列表导航过去。
                if (!view.ClickTreeNodeForTest(Path.Combine(tree, "alpha")))
                {
                    Log(34, "文件树检查：点不到 alpha 节点");
                    return false;
                }

                if (!PumpUntil(() => !view.IsLoading
                                     && string.Equals(view.State.CurrentPath, Path.Combine(tree, "alpha"), StringComparison.OrdinalIgnoreCase),
                        6000))
                {
                    Log(34, "文件树检查：点了树节点之后列表还停在「" + (view.State.CurrentPath ?? "此电脑") + "」");
                    return false;
                }

                // 列表导航 → 树跟着展开到深层（5 层）。
                view.NavigateForTest(deep);

                var watch = Stopwatch.StartNew();

                var revealed = PumpUntil(
                    () => string.Equals(view.SelectedTreePathForTest, deep, StringComparison.OrdinalIgnoreCase), 5000);

                watch.Stop();

                if (!revealed)
                {
                    Log(34, "文件树检查：导航到 5 层深路径之后树没有跟过去（选中："
                            + (view.SelectedTreePathForTest ?? "空") + "）");
                    return false;
                }

                if (watch.ElapsedMilliseconds > 3000)
                {
                    Log(34, $"文件树检查：树跟到 5 层用了 {watch.ElapsedMilliseconds} ms（期望 ≤ 3000）");
                    return false;
                }

                // 目录联接当叶子：造得出来就验它没有子节点。
                var junction = Path.Combine(tree, "junction");

                if (Directory.Exists(junction))
                {
                    view.NavigateForTest(tree);
                    PumpUntil(() => !view.IsLoading, 4000);

                    if (view.TreeNodeChildCountForTest(junction) > 0)
                    {
                        Log(34, "文件树检查：目录联接还有子节点（树里会跟进去）");
                        return false;
                    }
                }

                form.Close();
                PumpMessages(300);
            }

            // 窄视图：单独拿一个 300 宽的 FileBrowserView，看开关是不是真的挡住。
            if (!CheckFolderTreeTooNarrow(tree)) return false;

            Log(34, "文件夹树正确：默认关、菜单可用、懒展开、点树导航、导航跟到 5 层、窄视图挡住");
            return true;
        }

        private static bool CheckFolderTreeTooNarrow(string tree)
        {
            using var host = new Form
            {
                Text = "smoke-fb-nav-narrow",
                Width = 300,
                Height = 420,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(0, 0)
            };

            var view = new FileBrowserView { Dock = DockStyle.Fill };
            host.Controls.Add(view);
            host.Show();
            PumpMessages(250);

            var state = new FileBrowserState();
            state.Reset(tree);
            state.ShowFolderTree = false;
            state.AutoRefresh = false;

            view.RestoreState(state);
            PumpMessages(400);

            if (view.CanShowFolderTree)
            {
                Log(34, "文件树检查：300 宽的视图仍然允许显示树");
                return false;
            }

            view.SetFolderTreeForTest(true);

            if (view.IsTreeVisibleForTest)
            {
                Log(34, "文件树检查：太窄的视图还是把树显示出来了");
                return false;
            }

            if (!view.StatusText.Contains("太窄", StringComparison.Ordinal))
            {
                Log(34, "文件树检查：太窄时状态栏没有说明（" + view.StatusText + "）");
                return false;
            }

            host.Close();
            PumpMessages(200);

            return true;
        }

        // -----------------------------------------------------------------
        // 真窗体：自动刷新
        // -----------------------------------------------------------------

        private static bool CheckFileBrowserAutoRefresh(string root)
        {
            var (_, watchDir) = BuildNavigationFixtures(root);

            var settings = AppSettings.Load();
            settings.ShowSidebar = true;
            settings.SidebarFloating = false;
            settings.FileBrowserFloating = false;
            settings.SidebarPage = SidebarTab.Files;
            settings.SidebarWidth = 720;
            settings.FileBrowserLastPath = watchDir;
            settings.FileBrowserFolderTree = false;
            settings.FileBrowserAutoRefresh = true;
            settings.FileBrowserThumbnails = false;
            settings.FileBrowserShowAllFiles = false;
            settings.Save();

            using (var form = new 播放器.MainForm(Array.Empty<string>()))
            {
                form.Show();
                PumpMessages(400);

                var sidebar = Find(form, "sidebarPanel") as SidebarPanel;

                if (sidebar == null)
                {
                    Log(34, "自动刷新检查：找不到侧栏");
                    return false;
                }

                sidebar.ActiveTab = SidebarTab.Files;

                var view = form.FileBrowser;

                if (!PumpUntil(() => !view.IsLoading && view.List.Items.Count > 0, 8000))
                {
                    Log(34, "自动刷新检查：没有读出目录（状态：" + view.StatusText + "）");
                    return false;
                }

                if (!view.IsWatchingForTest)
                {
                    Log(34, "自动刷新检查：本地目录没有建 watcher");
                    return false;
                }

                // 没有变化时：静默刷新跑完但不重建。
                var refreshes = view.SilentRefreshCountForTest;
                var rebuilds = view.SilentRebuildCountForTest;

                view.RaiseWatchRefreshForTest();

                if (!PumpUntil(() => view.SilentRefreshCountForTest > refreshes, 6000))
                {
                    Log(34, "自动刷新检查：静默刷新没有跑完");
                    return false;
                }

                if (view.SilentRebuildCountForTest != rebuilds)
                {
                    Log(34, "自动刷新检查：内容没变也重建了列表");
                    return false;
                }

                // 新建文件：出现、选中项保留、全程没有进「正在读取」。
                view.SelectByNameForTest("keep.mp3");

                var created = Path.Combine(watchDir, "new-file.mp3");
                File.WriteAllText(created, "new");

                if (!PumpUntil(
                        () => ListViewNames(view.List).Any(name => name.Equals("new-file.mp3", StringComparison.OrdinalIgnoreCase)),
                        8000))
                {
                    Log(34, "自动刷新检查：新建的文件没有出现（状态：" + view.StatusText + "）");
                    return false;
                }

                if (view.IsLoading)
                {
                    Log(34, "自动刷新检查：自动刷新之后还停在「正在读取」");
                    return false;
                }

                if (view.List.SelectedItems.Count != 1 ||
                    !string.Equals(view.List.SelectedItems[0].Text, "keep.mp3", StringComparison.OrdinalIgnoreCase))
                {
                    Log(34, "自动刷新检查：自动刷新之后选中项丢了（现在选中 "
                            + view.List.SelectedItems.Count + " 项）");
                    return false;
                }

                if (view.SilentRebuildCountForTest <= rebuilds)
                {
                    Log(34, "自动刷新检查：内容变了却没有重建（计数没动）");
                    return false;
                }

                // 删除：消失。
                File.Delete(created);

                if (!PumpUntil(
                        () => !ListViewNames(view.List).Any(name => name.Equals("new-file.mp3", StringComparison.OrdinalIgnoreCase)),
                        8000))
                {
                    Log(34, "自动刷新检查：删掉的文件还留在列表里");
                    return false;
                }

                // 改名：出现新名字。
                var renamed = Path.Combine(watchDir, "renamed.mp3");
                File.Move(Path.Combine(watchDir, "other.mp3"), renamed);

                if (!PumpUntil(
                        () => ListViewNames(view.List).Any(name => name.Equals("renamed.mp3", StringComparison.OrdinalIgnoreCase)),
                        8000))
                {
                    Log(34, "自动刷新检查：改名的文件没有以新名字出现");
                    return false;
                }

                // 网络位置 / 此电脑不盯。
                if (FolderWatch.CanWatch(@"\\server\share") || FolderWatch.CanWatch(null) ||
                    FolderWatch.CanWatch("relative"))
                {
                    Log(34, "自动刷新检查：网络路径 / 此电脑 / 相对路径被当成可盯的目录");
                    return false;
                }

                // 关掉开关：不再跟随。
                view.SetAutoRefreshForTest(false);

                if (view.IsWatchingForTest)
                {
                    Log(34, "自动刷新检查：关掉开关之后 watcher 还在");
                    return false;
                }

                var offFile = Path.Combine(watchDir, "after-off.mp3");
                File.WriteAllText(offFile, "off");
                PumpMessages(1400);

                if (ListViewNames(view.List).Any(name => name.Equals("after-off.mp3", StringComparison.OrdinalIgnoreCase)))
                {
                    Log(34, "自动刷新检查：关掉开关之后还在跟着变化");
                    return false;
                }

                view.SetAutoRefreshForTest(true);

                if (!view.IsWatchingForTest)
                {
                    Log(34, "自动刷新检查：重新打开开关之后没有重建 watcher");
                    return false;
                }

                File.WriteAllText(Path.Combine(watchDir, "after-on.mp3"), "on");

                if (!PumpUntil(
                        () => ListViewNames(view.List).Any(name => name.Equals("after-on.mp3", StringComparison.OrdinalIgnoreCase)),
                        8000))
                {
                    Log(34, "自动刷新检查：重新打开开关之后新建的文件没有出现");
                    return false;
                }

                // 搜索页（递归结果）不被打扰：结果是一次快照。
                view.SetSearchTextForTest("keep");
                view.StartRecursiveSearchForTest();

                if (!PumpUntil(() => !view.IsLoading && !view.IsSearchingForTest(), 8000))
                {
                    Log(34, "自动刷新检查：递归搜索没有结束");
                    return false;
                }

                var searchNoise = Path.Combine(watchDir, "search-noise.mp3");
                File.WriteAllText(searchNoise, "noise");

                view.RaiseWatchRefreshForTest();
                PumpMessages(900);

                if (ListViewNames(view.List).Any(name => name.Equals("search-noise.mp3", StringComparison.OrdinalIgnoreCase)))
                {
                    Log(34, "自动刷新检查：搜索结果被自动刷新换掉了");
                    return false;
                }

                File.Delete(searchNoise);
                view.ClearSearchForTest();
                PumpMessages(300);

                // 此电脑：没有 watcher。
                view.NavigateForTest(null);

                if (!PumpUntil(() => !view.IsLoading, 5000))
                {
                    Log(34, "自动刷新检查：回此电脑没有读完");
                    return false;
                }

                if (view.IsWatchingForTest)
                {
                    Log(34, "自动刷新检查：此电脑也建了 watcher");
                    return false;
                }

                form.Close();
                PumpMessages(300);
            }

            Log(34, "自动刷新正确：本地目录静默刷新、选中项保留、增删改跟随、开关生效、"
                    + "搜索页不打扰、网络 / 此电脑不盯");
            return true;
        }
    }
}
