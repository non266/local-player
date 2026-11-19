using System.Diagnostics;

// 检查点 33 的 1.26.0 段：文件页递归搜索（有界 / 可取消 / 相对位置 / 进度）。

namespace SmokeTest
{
    internal static partial class Program
    {
        // -----------------------------------------------------------------
        // 纯逻辑：有界遍历
        // -----------------------------------------------------------------

        private static bool CheckFileBrowserRecursiveSearchCore(string root)
        {
            var folder = Path.Combine(root, "search-core");
            TryDeleteDirectory(folder);
            Directory.CreateDirectory(folder);

            Directory.CreateDirectory(Path.Combine(folder, "nested", "sub"));

            var deepFolder = Path.Combine(folder, "deep");

            for (var depth = 0; depth < 40; depth++)
            {
                deepFolder = Path.Combine(deepFolder, "d" + depth.ToString("D2"));
                Directory.CreateDirectory(deepFolder);
            }

            File.WriteAllText(Path.Combine(folder, "direct-hit.mp3"), "hit");
            File.WriteAllText(Path.Combine(folder, "nested", "nested-hit.mp4"), "hit");
            File.WriteAllText(Path.Combine(folder, "nested", "sub", "deep-hit.srt"), "hit");
            File.WriteAllText(Path.Combine(deepFolder, "deep35-probe.mp3"), "probe");

            var hiddenFile = Path.Combine(folder, "hidden-hit.mp3");
            File.WriteAllText(hiddenFile, "hit");
            File.SetAttributes(hiddenFile, FileAttributes.Hidden);

            var hiddenFolder = Path.Combine(folder, "hidden-dir");
            Directory.CreateDirectory(hiddenFolder);
            File.WriteAllText(Path.Combine(hiddenFolder, "hidden-hit.mp3"), "hit");
            File.SetAttributes(hiddenFolder, File.GetAttributes(hiddenFolder) | FileAttributes.Hidden);

            var search = new FileBrowserSearch();
            var result = search.Search(folder, "hit", showAllFiles: false, CancellationToken.None, null);

            if (result.Error != null)
            {
                Log(33, "递归搜索：普通目录读失败（" + result.Error + "）");
                return false;
            }

            var names = result.Entries.Select(entry => entry.Name).ToList();

            if (names.Count != 3
                || !names.Contains("direct-hit.mp3", StringComparer.OrdinalIgnoreCase)
                || !names.Contains("nested-hit.mp4", StringComparer.OrdinalIgnoreCase)
                || !names.Contains("deep-hit.srt", StringComparer.OrdinalIgnoreCase))
            {
                Log(33, "递归搜索：三层以内的命中不对（" + string.Join(" / ", names) + "）");
                return false;
            }

            if (names.Any(name => name.Contains("hidden", StringComparison.OrdinalIgnoreCase)))
            {
                Log(33, "递归搜索：默认把隐藏项带出来了（" + string.Join(" / ", names) + "）");
                return false;
            }

            if (!ExpectRelative(result, folder, "direct-hit.mp3", string.Empty)
                || !ExpectRelative(result, folder, "nested-hit.mp4", "nested")
                || !ExpectRelative(result, folder, "deep-hit.srt", @"nested\sub"))
            {
                return false;
            }

            var upper = search.Search(folder, "HIT", false, CancellationToken.None, null);
            if (upper.Entries.Count != 3)
            {
                Log(33, "递归搜索：大写查询没有同样命中（" + upper.Entries.Count + " 条）");
                return false;
            }

            var and = search.Search(folder, "nested hit", false, CancellationToken.None, null);
            if (and.Entries.Count != 1
                || !string.Equals(and.Entries[0].Name, "nested-hit.mp4", StringComparison.OrdinalIgnoreCase))
            {
                Log(33, "递归搜索：多关键字没有按与关系匹配（" + string.Join(" / ", and.Entries.Select(e => e.Name)) + "）");
                return false;
            }

            var folderHit = search.Search(folder, "sub", false, CancellationToken.None, null);
            if (folderHit.Entries.Count != 1 || !folderHit.Entries[0].IsDirectory)
            {
                Log(33, "递归搜索：名字命中的文件夹没有被收下（"
                        + string.Join(" / ", folderHit.Entries.Select(e => e.Name)) + "）");
                return false;
            }

            if (!ExpectRelative(folderHit, folder, "sub", "nested"))
            {
                return false;
            }

            var all = search.Search(folder, "hidden", true, CancellationToken.None, null);
            if (all.Entries.Count != 3)
            {
                Log(33, "递归搜索：显示所有文件之后隐藏项数量不对（" + all.Entries.Count + " 条）");
                return false;
            }

            var visibleOnly = search.Search(folder, "hidden", false, CancellationToken.None, null);
            if (visibleOnly.Entries.Count != 0)
            {
                Log(33, "递归搜索：隐藏项在未打开「显示所有文件」时仍然出现");
                return false;
            }

            var shallow = new FileBrowserSearch
            {
                Limits = new FileBrowserSearchLimits { MaxDepth = 32, MaxResults = 100, MaxEntriesVisited = 1000 }
            };
            var shallowResult = shallow.Search(folder, "probe", false, CancellationToken.None, null);

            if (!shallowResult.Truncated)
            {
                Log(33, "递归搜索：深度上限没有报告截断");
                return false;
            }

            if (shallowResult.Entries.Any(entry => entry.Name == "deep35-probe.mp3"))
            {
                Log(33, "递归搜索：超过深度上限的文件仍然被找到了");
                return false;
            }

            var limited = new FileBrowserSearch
            {
                Limits = new FileBrowserSearchLimits { MaxDepth = 32, MaxResults = 2, MaxEntriesVisited = 1000 }
            };
            var limitedResult = limited.Search(folder, "hit", false, CancellationToken.None, null);

            if (limitedResult.Entries.Count != 2 || !limitedResult.Truncated)
            {
                Log(33, $"递归搜索：结果上限没有截断（{limitedResult.Entries.Count} 条，截断 {limitedResult.Truncated}）");
                return false;
            }

            var visited = new FileBrowserSearch
            {
                Limits = new FileBrowserSearchLimits { MaxDepth = 32, MaxResults = 100, MaxEntriesVisited = 1 }
            };
            var visitedResult = visited.Search(folder, "hit", false, CancellationToken.None, null);

            if (!visitedResult.Truncated)
            {
                Log(33, "递归搜索：已看条目上限没有报告截断");
                return false;
            }

            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                var cancelled = search.Search(folder, "hit", false, cts.Token, null);

                if (!cancelled.Cancelled)
                {
                    Log(33, "递归搜索：取消之后没有报告 Cancelled");
                    return false;
                }

                if (cancelled.Entries.Count != 0)
                {
                    Log(33, "递归搜索：取消之后还返回了结果");
                    return false;
                }
            }

            var missing = search.Search(Path.Combine(folder, "not-there"), "hit", false, CancellationToken.None, null);
            if (missing.Error == null || !missing.Error.Contains("不存在", StringComparison.Ordinal))
            {
                Log(33, "递归搜索：不存在的目录没有报「不存在」");
                return false;
            }

            var fileRoot = search.Search(Path.Combine(folder, "direct-hit.mp3"), "hit", false, CancellationToken.None, null);
            if (fileRoot.Error == null || !fileRoot.Error.Contains("文件", StringComparison.Ordinal))
            {
                Log(33, "递归搜索：把文件当根目录没有报错");
                return false;
            }

            var progressCount = 0;
            var progress = new InlineProgress<FileBrowserSearchProgress>(_ => progressCount++);
            search.Search(folder, "hit", false, CancellationToken.None, progress);

            if (progressCount == 0)
            {
                Log(33, "递归搜索：一次都没有报告进度");
                return false;
            }

            if (!CheckRelativeFolder(folder))
            {
                return false;
            }

            var local = FileBrowserSearchLimits.ForPath(folder);
            var network = FileBrowserSearchLimits.ForPath(@"\\server\share");

            if (local.MaxDepth != 32 || local.MaxResults != 20_000 || local.MaxEntriesVisited != 200_000)
            {
                Log(33, "递归搜索：本地默认上限不对");
                return false;
            }

            if (network.MaxDepth != 16 || network.MaxResults != 10_000 || network.MaxEntriesVisited != 100_000)
            {
                Log(33, "递归搜索：网络默认上限不对");
                return false;
            }

            Log(33, "递归搜索逻辑正确：三层命中 / 相对位置 / 大小写 / 与关系 / 隐藏项 / "
                    + "深度与结果上限 / 取消 / 根目录错误 / 进度 / 本地与网络上限");
            return true;
        }

        private static bool ExpectRelative(
            FileBrowserSearchResult result, string root, string name, string expected)
        {
            var entry = result.Entries.FirstOrDefault(item =>
                string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));

            if (entry == null)
            {
                Log(33, $"递归搜索：结果里找不到「{name}」");
                return false;
            }

            var actual = FileBrowserPaths.RelativeFolder(root, entry.FullPath);

            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                Log(33, $"递归搜索：「{name}」的位置是「{actual}」，期望「{expected}」");
                return false;
            }

            return true;
        }

        private static bool CheckRelativeFolder(string root)
        {
            var direct = Path.Combine(root, "direct-hit.mp3");
            var nested = Path.Combine(root, "nested", "nested-hit.mp4");
            var deep = Path.Combine(root, "nested", "sub", "deep-hit.srt");
            var nestedFolder = Path.Combine(root, "nested");
            var subFolder = Path.Combine(root, "nested", "sub");

            if (FileBrowserPaths.RelativeFolder(root, direct) != string.Empty
                || FileBrowserPaths.RelativeFolder(root, nested) != "nested"
                || FileBrowserPaths.RelativeFolder(root, deep) != @"nested\sub"
                || FileBrowserPaths.RelativeFolder(root, nestedFolder) != string.Empty
                || FileBrowserPaths.RelativeFolder(root, subFolder) != "nested")
            {
                Log(33, "递归搜索：相对位置换算不对");
                return false;
            }

            if (FileBrowserPaths.RelativeFolder(root, @"C:\outside\file.mp3") != string.Empty)
            {
                Log(33, "递归搜索：根目录之外的文件没有把位置留空");
                return false;
            }

            return true;
        }

        // -----------------------------------------------------------------
        // 真窗体：搜索范围 / 位置列 / 类型 / 取消 / 代际
        // -----------------------------------------------------------------

        private static bool CheckFileBrowserRecursiveSearch(
            播放器.MainForm form, FileBrowserView view, string uiRoot)
        {
            var all = ListViewNames(view.List);

            view.SetRecursiveSearchForTest(true);

            if (!view.IsRecursiveSearchForTest())
            {
                Log(33, "文件页检查：勾选「含子文件夹」没有生效");
                return false;
            }

            if (view.List.Items.Count != all.Count)
            {
                Log(33, "文件页检查：勾选「含子文件夹」但还没搜索时列表就变了");
                return false;
            }

            view.SetSearchTextForTest("search-hit");

            var watch = Stopwatch.StartNew();
            view.ProcessSearchKeyForTest(Keys.Enter);
            watch.Stop();

            if (watch.ElapsedMilliseconds >= 200)
            {
                Log(33, $"文件页检查：启动递归搜索在 UI 线程上等了 {watch.ElapsedMilliseconds} ms（期望 < 200 ms）");
                return false;
            }

            if (!view.IsSearchingForTest() && !view.IsSearchActiveForTest())
            {
                Log(33, "文件页检查：按 Enter 没有开始递归搜索（状态：" + view.StatusText + "）");
                return false;
            }

            if (!PumpUntil(() => !view.IsSearchingForTest(), 6000) || !view.IsSearchActiveForTest())
            {
                Log(33, "文件页检查：递归搜索没有结束（状态：" + view.StatusText + "）");
                return false;
            }

            var names = ListViewNames(view.List);

            if (names.Count != 3
                || !names.Contains("search-hit.mp3", StringComparer.OrdinalIgnoreCase)
                || !names.Contains("search-hit.mp4", StringComparer.OrdinalIgnoreCase)
                || !names.Contains("search-hit.srt", StringComparer.OrdinalIgnoreCase))
            {
                Log(33, "文件页检查：递归搜索没有找到子文件夹里的 search-hit 文件（"
                        + string.Join(" / ", names) + "）");
                return false;
            }

            if (view.List.Columns.Count < 3 || view.List.Columns[2].Width <= 0)
            {
                Log(33, "文件页检查：递归结果没有显示「位置」列");
                return false;
            }

            if (!ExpectListLocation(view, "search-hit.mp3", string.Empty)
                || !ExpectListLocation(view, "search-hit.mp4", "nested")
                || !ExpectListLocation(view, "search-hit.srt", @"nested\sub"))
            {
                return false;
            }

            if (!view.StatusText.Contains("含子文件夹", StringComparison.Ordinal))
            {
                Log(33, "文件页检查：递归搜索结果的状态行没有写「含子文件夹」（" + view.StatusText + "）");
                return false;
            }

            // 导航必须取消结果并回到当前层；不能把上一轮的搜索结果留在列表里。
            var nestedPath = Path.Combine(uiRoot, "nested");
            view.NavigateForTest(nestedPath);

            if (!PumpUntil(() => !view.IsLoading, 6000))
            {
                Log(33, "文件页检查：搜索结果页导航到 nested 没有结束");
                return false;
            }

            if (view.IsSearchActiveForTest()
                || !string.Equals(view.State.CurrentPath, nestedPath, StringComparison.OrdinalIgnoreCase)
                || view.StatusText.Contains("搜索「search-hit」", StringComparison.Ordinal))
            {
                Log(33, "文件页检查：导航到别的目录之后还显示着上一轮搜索结果");
                return false;
            }

            view.NavigateForTest(uiRoot);
            PumpUntil(() => !view.IsLoading, 6000);

            view.SetRecursiveSearchForTest(true);
            view.SetSearchTextForTest("search-hit");
            view.ProcessSearchKeyForTest(Keys.Enter);

            if (!PumpUntil(() => !view.IsSearchingForTest(), 6000) || !view.IsSearchActiveForTest()
                || view.List.Items.Count != 3)
            {
                Log(33, "文件页检查：导航收尾之后重新搜索没有回到 3 条结果");
                return false;
            }

            view.SetTypeFilterForTest(FileBrowserTypeFilter.Audio);

            if (view.List.Items.Count != 1 || !string.Equals(view.List.Items[0].Text, "search-hit.mp3", StringComparison.OrdinalIgnoreCase))
            {
                Log(33, "文件页检查：只看音频时搜索结果里还有视频或字幕（"
                        + string.Join(" / ", ListViewNames(view.List)) + "）");
                return false;
            }

            view.SetTypeFilterForTest(FileBrowserTypeFilter.Video);

            if (view.List.Items.Count != 1 || !string.Equals(view.List.Items[0].Text, "search-hit.mp4", StringComparison.OrdinalIgnoreCase))
            {
                Log(33, "文件页检查：只看视频时搜索结果里还有音频或字幕（"
                        + string.Join(" / ", ListViewNames(view.List)) + "）");
                return false;
            }

            view.SetTypeFilterForTest(FileBrowserTypeFilter.All);

            if (view.List.Items.Count != 3)
            {
                Log(33, "文件页检查：切回全部之后搜索结果没有恢复（" + view.List.Items.Count + " 条）");
                return false;
            }

            // 改查询：旧结果必须立刻消失，等 Enter 再搜。
            view.SetSearchTextForTest("inside");

            if (view.IsSearchActiveForTest() || view.IsSearchingForTest())
            {
                Log(33, "文件页检查：改了查询之后旧搜索结果还留着");
                return false;
            }

            if (!view.StatusText.Contains("按 Enter", StringComparison.Ordinal))
            {
                Log(33, "文件页检查：改了查询之后没有提示按 Enter（" + view.StatusText + "）");
                return false;
            }

            view.ProcessSearchKeyForTest(Keys.Enter);

            if (!PumpUntil(() => !view.IsSearchingForTest(), 6000) || !view.IsSearchActiveForTest())
            {
                Log(33, "文件页检查：改查询之后按 Enter 没有重新搜索");
                return false;
            }

            if (view.List.Items.Count != 1
                || !string.Equals(view.List.Items[0].Text, "inside.mp3", StringComparison.OrdinalIgnoreCase)
                || !ExpectListLocation(view, "inside.mp3", @"nested\sub"))
            {
                Log(33, "文件页检查：第二次递归搜索的结果不对（"
                        + string.Join(" / ", ListViewNames(view.List)) + "）");
                return false;
            }

            view.ProcessSearchKeyForTest(Keys.Escape);

            if (view.IsSearchActiveForTest() || view.IsSearchingForTest() || view.List.Items.Count != all.Count)
            {
                Log(33, "文件页检查：Esc 之后没有回到当前层（" + view.List.Items.Count + " / " + all.Count + "）");
                return false;
            }

            // 焦点在开关上：空格不能被命令层当成播放 / 暂停。
            view.ScopeToggleForTest.Focus();

            if (form.ProcessShortcut(Keys.Space))
            {
                Log(33, "文件页检查：焦点在「含子文件夹」开关上时空格被命令层抢走了");
                return false;
            }

            // 空格是否真的翻转了开关由 WinForms 管；这里只关心它没被命令层吃掉，
            // 后面的流程显式钉回“勾选”状态。
            view.SetRecursiveSearchForTest(true);

            // F5 在结果页要重搜，而不是只重读当前层。
            var counting = new CountingFileBrowserSearch();
            view.SetSearchServiceForTest(counting);
            view.SetSearchTextForTest("search-hit");
            view.ProcessSearchKeyForTest(Keys.Enter);

            if (!PumpUntil(() => !view.IsSearchingForTest(), 6000) || counting.Calls != 1)
            {
                Log(33, $"文件页检查：第一次搜索没有调用搜索服务（{counting.Calls} 次）");
                return false;
            }

            view.Reload();

            if (!PumpUntil(() => !view.IsSearchingForTest() && counting.Calls == 2, 6000))
            {
                Log(33, $"文件页检查：F5 没有重新递归搜索（调用 {counting.Calls} 次）");
                return false;
            }

            if (view.List.Items.Count != 3)
            {
                Log(33, "文件页检查：F5 之后搜索结果不对（" + view.List.Items.Count + " 条）");
                return false;
            }

            // 慢搜索：启动必须立刻返回；改查询 / Esc 之后迟到的结果不能盖回来。
            view.SetSearchServiceForTest(new SlowFileBrowserSearch(uiRoot));
            view.SetSearchTextForTest("stale");

            watch = Stopwatch.StartNew();
            view.ProcessSearchKeyForTest(Keys.Enter);
            watch.Stop();

            if (watch.ElapsedMilliseconds >= 200)
            {
                Log(33, $"文件页检查：慢搜索启动等了 {watch.ElapsedMilliseconds} ms（期望 < 200 ms）");
                return false;
            }

            view.SetSearchTextForTest("other");
            PumpMessages(900);

            if (ListViewNames(view.List).Contains("stale-hit.mp3", StringComparer.OrdinalIgnoreCase)
                || view.IsSearchActiveForTest())
            {
                Log(33, "文件页检查：改查询之后迟到的搜索结果盖了回来");
                return false;
            }

            view.SetSearchTextForTest("stale");
            view.ProcessSearchKeyForTest(Keys.Enter);
            view.ProcessSearchKeyForTest(Keys.Escape);
            PumpMessages(900);

            if (view.IsSearchingForTest() || view.IsSearchActiveForTest()
                || ListViewNames(view.List).Contains("stale-hit.mp3", StringComparer.OrdinalIgnoreCase))
            {
                Log(33, "文件页检查：Esc 之后迟到的搜索结果盖了回来");
                return false;
            }

            // 此电脑没有“当前文件夹”，递归开关必须禁用且自动落下。
            view.SetSearchServiceForTest(new FileBrowserSearch());
            view.SetTypeFilterForTest(FileBrowserTypeFilter.All);
            view.ClearSearchForTest();
            view.SetRecursiveSearchForTest(true);
            view.NavigateForTest(null);

            if (!PumpUntil(() => !view.IsLoading, 6000))
            {
                Log(33, "文件页检查：导航到此电脑没有结束");
                return false;
            }

            if (view.ScopeToggleForTest.Enabled || view.IsRecursiveSearchForTest())
            {
                Log(33, "文件页检查：此电脑上「含子文件夹」没有禁用 / 落下");
                return false;
            }

            // 收尾：换回真搜索、清范围，别把状态留给后面的拖放 / 定位。
            view.SetSearchServiceForTest(new FileBrowserSearch());
            view.SetTypeFilterForTest(FileBrowserTypeFilter.All);
            view.ClearSearchForTest();
            view.SetRecursiveSearchForTest(false);
            view.NavigateForTest(uiRoot);

            if (!PumpUntil(() => !view.IsLoading && view.List.Items.Count > 0, 8000))
            {
                Log(33, "文件页检查：递归搜索收尾之后没有回到当前层");
                return false;
            }

            return true;
        }

        private static bool ExpectListLocation(FileBrowserView view, string name, string expected)
        {
            var item = view.List.Items
                .Cast<ListViewItem>()
                .FirstOrDefault(row => string.Equals(row.Text, name, StringComparison.OrdinalIgnoreCase));

            if (item == null)
            {
                Log(33, $"文件页检查：结果里找不到「{name}」");
                return false;
            }

            var actual = item.SubItems.Count > 2 ? item.SubItems[2].Text : string.Empty;

            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                Log(33, $"文件页检查：搜索结果的位置列是「{actual}」，期望「{expected}」（{name}）");
                return false;
            }

            return true;
        }

        private sealed class InlineProgress<T> : IProgress<T>
        {
            private readonly Action<T> _report;

            internal InlineProgress(Action<T> report) => _report = report;

            public void Report(T value) => _report(value);
        }

        private sealed class CountingFileBrowserSearch : FileBrowserSearch
        {
            internal int Calls { get; private set; }

            internal override FileBrowserSearchResult Search(
                string root, string? query, bool showAllFiles,
                CancellationToken token, IProgress<FileBrowserSearchProgress>? progress)
            {
                Calls++;
                return base.Search(root, query, showAllFiles, token, progress);
            }
        }

        private sealed class SlowFileBrowserSearch : FileBrowserSearch
        {
            private readonly string _root;

            internal SlowFileBrowserSearch(string root) => _root = root;

            internal override FileBrowserSearchResult Search(
                string root, string? query, bool showAllFiles,
                CancellationToken token, IProgress<FileBrowserSearchProgress>? progress)
            {
                // 真实的 SMB 枚举不响应取消；这里也不看 token，专门制造“迟到的结果”。
                Thread.Sleep(600);

                var result = new FileBrowserSearchResult();
                result.Entries.Add(new FileBrowserEntry(
                    "stale-hit.mp3",
                    Path.Combine(_root, "stale-hit.mp3"),
                    FileBrowserKind.Audio,
                    0,
                    DateTime.MinValue));

                return result;
            }
        }
    }
}
