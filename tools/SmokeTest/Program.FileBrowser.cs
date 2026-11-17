using System.Diagnostics;

// 检查点 33：文件浏览器（1.24.0）——路径 / 状态 / 目录读取 / 有界递归 / 侧栏文件页 / 分离窗口。

namespace SmokeTest
{
    internal static partial class Program
    {
        private static bool TestFileBrowser()
        {
            var root = Path.Combine(AppContext.BaseDirectory, "smoke-fb");
            TryDeleteDirectory(root);

            try
            {
                Directory.CreateDirectory(root);

                if (!CheckFileBrowserPaths()) return false;
                if (!CheckFileBrowserState()) return false;
                if (!CheckFileBrowserService(root)) return false;
                if (!CheckMediaScannerBounded(root)) return false;
                if (!CheckFileBrowserMainForm(root)) return false;
                if (!CheckFileBrowserProbe(root)) return false;

                Log(33, "文件浏览器正常：路径归一、历史 / 收藏 / 最近、当前层读取与过滤、有界递归、"
                        + "侧栏文件页与分离窗口都按预期工作");
                return true;
            }
            catch (Exception ex)
            {
                Log(33, "文件浏览器检查失败: " + ex);
                return false;
            }
            finally
            {
                TryDeleteDirectory(root);
            }
        }

        // -----------------------------------------------------------------
        // 纯逻辑：路径 / 状态
        // -----------------------------------------------------------------

        private static bool CheckFileBrowserPaths()
        {
            if (!ExpectFileBrowserPath("\"D:\\Movies\"", @"D:\Movies")) return false;
            if (!ExpectFileBrowserPath("D:/Movies/", @"D:\Movies")) return false;
            if (!ExpectFileBrowserPath("D:", @"D:\")) return false;
            if (!ExpectFileBrowserPath("file:///C:/Windows", @"C:\Windows")) return false;
            if (!ExpectFileBrowserPath("此电脑", null)) return false;
            if (!ExpectFileBrowserPath("", null)) return false;

            var temp = Path.GetTempPath().TrimEnd('\\');
            if (!ExpectFileBrowserPath("%TEMP%", temp)) return false;

            if (!ExpectFileBrowserPath(@"\\server\share", @"\\server\share")) return false;

            if (!ExpectFileBrowserPathError("http://example.com/a.m3u", "网络流")) return false;
            if (!ExpectFileBrowserPathError("relative\\path", "完整")) return false;
            if (!ExpectFileBrowserPathError(@"\\server", "网络路径")) return false;

            if (FileBrowserPaths.NormalizeStored("relative\\path") != "")
            {
                Log(33, "路径归一：设置里的相对路径没有被丢掉");
                return false;
            }

            if (!string.Equals(FileBrowserPaths.ParentOf(@"C:\A\B"), @"C:\A", StringComparison.OrdinalIgnoreCase))
            {
                Log(33, "路径归一：C:\\A\\B 的上一级不是 C:\\A");
                return false;
            }

            if (FileBrowserPaths.ParentOf(@"C:\") != null)
            {
                Log(33, "路径归一：盘根不应该还有上一级");
                return false;
            }

            if (!FileBrowserPaths.IsRoot(@"C:\") || FileBrowserPaths.IsRoot(@"C:\A"))
            {
                Log(33, "路径归一：根判断不对");
                return false;
            }

            if (FileBrowserPaths.Describe(@"D:\Movies\Anime") != @"Movies\Anime")
            {
                Log(33, "路径归一：菜单短标签不是最后两段（" + FileBrowserPaths.Describe(@"D:\Movies\Anime") + "）");
                return false;
            }

            if (FileBrowserPaths.Describe(null) != FileBrowserPaths.ComputerLabel)
            {
                Log(33, "路径归一：空路径的标签不是此电脑");
                return false;
            }

            Log(33, "路径归一正确：引号 / 斜杠 / 盘符 / file URI / 环境变量 / UNC 都认，相对路径与网络流被拒绝");
            return true;
        }

        private static bool ExpectFileBrowserPath(string raw, string? expected)
        {
            if (!FileBrowserPaths.TryNormalize(raw, out var actual, out var error))
            {
                Log(33, $"路径归一：「{raw}」被拒绝了（{error}）");
                return false;
            }

            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                Log(33, $"路径归一：「{raw}」→「{actual ?? "（此电脑）"}」，期望「{expected ?? "（此电脑）"}」");
                return false;
            }

            return true;
        }

        private static bool ExpectFileBrowserPathError(string raw, string fragment)
        {
            if (FileBrowserPaths.TryNormalize(raw, out _, out var error) || !error.Contains(fragment, StringComparison.Ordinal))
            {
                Log(33, $"路径归一：「{raw}」的错误不对（{error}）");
                return false;
            }

            return true;
        }

        private static bool CheckFileBrowserState()
        {
            var state = new FileBrowserState();

            state.Navigate(@"C:\A");
            state.Navigate(@"C:\B");
            state.Navigate(@"C:\C");

            if (!state.TryGoBack(out var back) || !string.Equals(back, @"C:\B", StringComparison.OrdinalIgnoreCase))
            {
                Log(33, "文件状态：后退没有回到 B");
                return false;
            }

            if (!state.TryGoForward(out var forward) || !string.Equals(forward, @"C:\C", StringComparison.OrdinalIgnoreCase))
            {
                Log(33, "文件状态：前进没有回到 C");
                return false;
            }

            state.TryGoBack(out _);
            state.Navigate(@"C:\D");

            if (state.CanGoForward)
            {
                Log(33, "文件状态：后退之后进新目录，前进栈没有清空");
                return false;
            }

            state.Navigate(null);
            if (state.CurrentPath != null || !state.TryGoBack(out var folder) || folder == null)
            {
                Log(33, "文件状态：从目录回到此电脑再后退不对");
                return false;
            }

            for (var i = 0; i < FileBrowserState.HistoryCapacity + 10; i++)
                state.Navigate(@"C:\H" + i);

            if (state.BackCount > FileBrowserState.HistoryCapacity)
            {
                Log(33, $"文件状态：后退历史上限没生效（{state.BackCount}）");
                return false;
            }

            for (var i = 0; i < FileBrowserState.FavoriteCapacity + 5; i++)
            {
                if (!state.AddFavorite(@"C:\F" + i))
                {
                    Log(33, "文件状态：收藏一个不存在的目录也应该按纯路径收下");
                    return false;
                }
            }

            if (state.Favorites.Count != FileBrowserState.FavoriteCapacity)
            {
                Log(33, $"文件状态：收藏上限不对（{state.Favorites.Count}）");
                return false;
            }

            if (state.AddFavorite(@"C:\F24"))
            {
                Log(33, "文件状态：重复收藏没有被拒绝");
                return false;
            }

            for (var i = 0; i < FileBrowserState.RecentCapacity + 3; i++)
                state.PushRecent(@"C:\R" + i);

            if (state.Recent.Count != FileBrowserState.RecentCapacity ||
                !string.Equals(state.Recent[0], @"C:\R" + (FileBrowserState.RecentCapacity + 2), StringComparison.OrdinalIgnoreCase))
            {
                Log(33, "文件状态：最近浏览的 MRU / 上限不对");
                return false;
            }

            var normalized = FileBrowserState.NormalizeList(
                new[] { @"C:\A", @"c:\a", "http://x", "relative", @"D:\B" }, 10);

            if (normalized.Count != 2)
            {
                Log(33, $"文件状态：设置列表归一不对（{normalized.Count} 条）");
                return false;
            }

            state.NoteSuccessfulLoad(@"C:\A");
            var settings = new AppSettings();
            state.ApplyTo(settings);

            if (!AppSettings.TryFromJson(settings.ToJson(), out var loaded))
            {
                Log(33, "文件状态：设置 JSON 往返失败");
                return false;
            }

            var restored = FileBrowserState.FromSettings(loaded);

            if (!string.Equals(restored.LastSuccessfulPath, @"C:\A", StringComparison.OrdinalIgnoreCase) ||
                restored.Favorites.Count == 0 || restored.Recent.Count == 0)
            {
                Log(33, "文件状态：设置往返之后目录 / 收藏 / 最近没回来");
                return false;
            }

            if (FileSortFields.Normalize("bogus") != FileSortFields.ByName ||
                FileSortFields.Parse(FileSortFields.BySize) != FileSortField.Size)
            {
                Log(33, "文件状态：排序字符串归一不对");
                return false;
            }

            Log(33, "文件状态正确：历史 / 前进栈截断、收藏与最近的上限和去重、设置往返、坏排序值回默认");
            return true;
        }

        // -----------------------------------------------------------------
        // 目录读取
        // -----------------------------------------------------------------

        private static bool CheckFileBrowserService(string root)
        {
            var dir = Path.Combine(root, "service");
            Directory.CreateDirectory(dir);
            Directory.CreateDirectory(Path.Combine(dir, "folderA"));
            Directory.CreateDirectory(Path.Combine(dir, "nested"));

            File.WriteAllBytes(Path.Combine(dir, "alpha.mkv"), new byte[1024]);
            File.WriteAllBytes(Path.Combine(dir, "bravo.mp3"), new byte[2048]);
            File.WriteAllText(Path.Combine(dir, "charlie.srt"), "1\n00:00:00,000 --> 00:00:01,000\nsub\n");
            File.WriteAllText(Path.Combine(dir, "delta.m3u"), "#EXTM3U\n");
            File.WriteAllText(Path.Combine(dir, "echo.txt"), "plain");
            File.WriteAllText(Path.Combine(dir, "foxtrot.bin"), "bin");
            File.WriteAllBytes(Path.Combine(dir, "zero.mp3"), Array.Empty<byte>());
            File.WriteAllText(Path.Combine(dir, "nested", "inside.mp3"), "inside");

            var hidden = Path.Combine(dir, "hidden.mp3");
            File.WriteAllBytes(hidden, new byte[4]);
            File.SetAttributes(hidden, FileAttributes.Hidden);

            var service = new FileBrowserService();
            var result = service.ReadDirectory(dir, showAllFiles: false, CancellationToken.None);

            if (result.Error != null)
            {
                Log(33, "目录读取：普通目录读失败（" + result.Error + "）");
                return false;
            }

            var names = result.Entries.Select(entry => entry.Name).ToList();

            foreach (var expected in new[] { "folderA", "nested", "alpha.mkv", "bravo.mp3", "charlie.srt", "delta.m3u", "echo.txt" })
            {
                if (names.Contains(expected, StringComparer.OrdinalIgnoreCase)) continue;

                Log(33, $"目录读取：默认视图里少了「{expected}」（有：{string.Join(" / ", names)}）");
                return false;
            }

            foreach (var unexpected in new[] { "foxtrot.bin", "hidden.mp3", "inside.mp3" })
            {
                if (!names.Contains(unexpected, StringComparer.OrdinalIgnoreCase)) continue;

                Log(33, $"目录读取：默认视图里不该出现「{unexpected}」");
                return false;
            }

            var alpha = result.Entries.First(entry => entry.Name == "alpha.mkv");
            var bravo = result.Entries.First(entry => entry.Name == "bravo.mp3");
            var charlie = result.Entries.First(entry => entry.Name == "charlie.srt");
            var delta = result.Entries.First(entry => entry.Name == "delta.m3u");
            var echo = result.Entries.First(entry => entry.Name == "echo.txt");
            var zero = result.Entries.First(entry => entry.Name == "zero.mp3");

            if (alpha.Kind != FileBrowserKind.Video || bravo.Kind != FileBrowserKind.Audio ||
                charlie.Kind != FileBrowserKind.Subtitle || delta.Kind != FileBrowserKind.Playlist ||
                echo.Kind != FileBrowserKind.Subtitle)
            {
                Log(33, "目录读取：文件种类判断不对");
                return false;
            }

            if (zero.Size != 0 || zero.SizeText != "0 字节")
            {
                Log(33, "目录读取：0 字节文件的大小显示不对（" + zero.SizeText + "）");
                return false;
            }

            var showAll = service.ReadDirectory(dir, showAllFiles: true, CancellationToken.None);
            var showAllNames = showAll.Entries.Select(entry => entry.Name).ToList();

            if (!showAllNames.Contains("foxtrot.bin", StringComparer.OrdinalIgnoreCase) ||
                !showAllNames.Contains("hidden.mp3", StringComparer.OrdinalIgnoreCase))
            {
                Log(33, "目录读取：打开「显示所有文件」之后仍然看不到 .bin / 隐藏文件");
                return false;
            }

            var limited = new FileBrowserService { MaxEntries = 3 };
            var truncated = limited.ReadDirectory(dir, showAllFiles: true, CancellationToken.None);

            if (truncated.Entries.Count != 3 || !truncated.Truncated)
            {
                Log(33, $"目录读取：上限没有截断（{truncated.Entries.Count} 条，截断 {truncated.Truncated}）");
                return false;
            }

            var missing = service.ReadDirectory(Path.Combine(dir, "not-there"), false, CancellationToken.None);
            if (missing.Error == null || !missing.Error.Contains("不存在", StringComparison.Ordinal))
            {
                Log(33, "目录读取：不存在的目录没有报「不存在」");
                return false;
            }

            var filePath = service.ReadDirectory(Path.Combine(dir, "alpha.mkv"), false, CancellationToken.None);
            if (filePath.Error == null || !filePath.Error.Contains("文件", StringComparison.Ordinal))
            {
                Log(33, "目录读取：把一个文件当目录读没有报错");
                return false;
            }

            FileBrowserService.Sort(result.Entries, FileSortField.Name, descending: false);

            if (!result.Entries[0].IsDirectory || result.Entries[0].Name != "folderA")
            {
                Log(33, "目录读取：按名称排序时文件夹没有排在最前");
                return false;
            }

            FileBrowserService.Sort(result.Entries, FileSortField.Size, descending: true);

            if (!result.Entries[0].IsDirectory)
            {
                Log(33, "目录读取：按大小降序时文件夹没有排在最前");
                return false;
            }

            var fileSizes = result.Entries.Where(entry => !entry.IsDirectory).Select(entry => entry.Size).ToList();
            for (var i = 1; i < fileSizes.Count; i++)
            {
                if (fileSizes[i - 1] < fileSizes[i])
                {
                    Log(33, "目录读取：按大小降序的文件没有递减");
                    return false;
                }
            }

            File.SetLastWriteTime(Path.Combine(dir, "alpha.mkv"), new DateTime(2020, 1, 1));
            File.SetLastWriteTime(Path.Combine(dir, "bravo.mp3"), new DateTime(2024, 1, 1));

            var modified = service.ReadDirectory(dir, false, CancellationToken.None);
            FileBrowserService.Sort(modified.Entries, FileSortField.Modified, descending: false);

            var alphaIndex = modified.Entries.FindIndex(entry => entry.Name == "alpha.mkv");
            var bravoIndex = modified.Entries.FindIndex(entry => entry.Name == "bravo.mp3");

            if (alphaIndex < 0 || bravoIndex < 0 || alphaIndex > bravoIndex)
            {
                Log(33, "目录读取：按修改时间排序不对");
                return false;
            }

            var typed = service.ReadDirectory(dir, false, CancellationToken.None);
            FileBrowserService.Sort(typed.Entries, FileSortField.Type, descending: false);

            var typedAlpha = typed.Entries.FindIndex(entry => entry.Name == "alpha.mkv");
            var typedBravo = typed.Entries.FindIndex(entry => entry.Name == "bravo.mp3");

            if (typedAlpha < 0 || typedBravo < 0 || typedAlpha > typedBravo)
            {
                Log(33, "目录读取：按类型排序不对");
                return false;
            }

            if (FileBrowserService.FormatSize(0) != "0 字节" ||
                FileBrowserService.FormatSize(1024) != "1 KB" ||
                !FileBrowserService.FormatSize(1536L * 1024).StartsWith("1.5", StringComparison.Ordinal))
            {
                Log(33, "目录读取：文件大小格式不对");
                return false;
            }

            var roots = service.ListRoots(CancellationToken.None);
            if (roots.Error != null || roots.Entries.Count == 0 || !roots.IsRoots ||
                roots.Entries.Any(entry => entry.Kind != FileBrowserKind.Drive))
            {
                Log(33, "目录读取：磁盘列表不对（" + (roots.Error ?? roots.Entries.Count + " 个驱动器") + "）");
                return false;
            }

            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var cancelled = service.ReadDirectory(dir, false, cts.Token);

            if (!cancelled.Cancelled)
            {
                Log(33, "目录读取：取消之后没有报告取消");
                return false;
            }

            Log(33, "目录读取正确：只读当前层、默认过滤 / 显示所有、隐藏项、四种排序、上限截断、错误与取消");
            return true;
        }

        private static bool CheckMediaScannerBounded(string root)
        {
            var deep = Path.Combine(root, "deep");
            Directory.CreateDirectory(deep);

            var current = deep;

            for (var depth = 0; depth < 40; depth++)
            {
                current = Path.Combine(current, "d" + depth);
                Directory.CreateDirectory(current);

                if (depth == 2) File.WriteAllText(Path.Combine(current, "d2.mp3"), "x");
                if (depth == 35) File.WriteAllText(Path.Combine(current, "d35.mp3"), "x");
            }

            var scan = MediaScanner.Walk(deep, MediaScanLimits.LocalBounded(), CancellationToken.None, null);

            if (!scan.Truncated)
            {
                Log(33, "有界递归：40 层目录没有触到深度上限");
                return false;
            }

            if (!scan.Files.Any(path => path.EndsWith("d2.mp3", StringComparison.OrdinalIgnoreCase)))
            {
                Log(33, "有界递归：32 层以内的文件没有找到");
                return false;
            }

            if (scan.Files.Any(path => path.EndsWith("d35.mp3", StringComparison.OrdinalIgnoreCase)))
            {
                Log(33, "有界递归：超过 32 层的文件仍然被收下了");
                return false;
            }

            Log(33, "有界递归正确：32 层以内照常找，超过的截断并说明");
            return true;
        }

        // -----------------------------------------------------------------
        // 真窗体
        // -----------------------------------------------------------------

        private static bool CheckFileBrowserMainForm(string root)
        {
            var uiRoot = Path.Combine(root, "ui");
            Directory.CreateDirectory(uiRoot);
            Directory.CreateDirectory(Path.Combine(uiRoot, "nested", "sub"));

            WriteWav(Path.Combine(uiRoot, "play.wav"), seconds: 2, frequency: 440);
            File.WriteAllText(Path.Combine(uiRoot, "movie.mkv"), "movie");
            File.WriteAllText(Path.Combine(uiRoot, "sub.srt"), "sub");
            File.WriteAllText(Path.Combine(uiRoot, "list.m3u"), "#EXTM3U\n");
            File.WriteAllText(Path.Combine(uiRoot, "extra.bin"), "bin");
            File.WriteAllText(Path.Combine(uiRoot, "nested", "sub", "inside.mp3"), "inside");

            var hidden = Path.Combine(uiRoot, "hidden.mp3");
            File.WriteAllText(hidden, "hidden");
            File.SetAttributes(hidden, FileAttributes.Hidden);

            // 前面几步可能把侧栏 / 播放列表留在分离状态；这里从"都停靠、文件页关闭"开始。
            var settings = AppSettings.Load();
            settings.SidebarFloating = false;
            settings.PlaylistFloating = false;
            settings.FileBrowserFloating = false;
            settings.ShowSidebar = true;
            settings.ShowPlaylist = true;
            settings.SidebarPage = SidebarTab.Lyrics;
            settings.WindowMode = WindowModes.Video;
            settings.MiniMode = false;
            settings.FileBrowserLastPath = uiRoot;
            settings.FileBrowserFavorites = new List<string>();
            settings.FileBrowserRecent = new List<string>();
            settings.FileBrowserSort = "Name";
            settings.FileBrowserSortDescending = false;
            settings.FileBrowserShowAllFiles = false;
            settings.Save();

            string? savedFavorite = null;
            var playlistCountBeforeDelete = 0;

            using (var form = new 播放器.MainForm(Array.Empty<string>()))
            {
                form.Show();
                PumpMessages(400);

                var sidebar = Find(form, "sidebarPanel") as SidebarPanel;
                if (sidebar == null)
                {
                    Log(33, "文件页检查：找不到侧栏");
                    return false;
                }

                sidebar.ActiveTab = SidebarTab.Files;

                var view = form.FileBrowser;

                if (!PumpUntil(() => !view.IsLoading && view.List.Items.Count > 0, 8000))
                {
                    Log(33, "文件页检查：没有读出目录（状态：" + view.StatusText + "）");
                    return false;
                }

                var names = ListViewNames(view.List);

                foreach (var expected in new[] { "nested", "play.wav", "movie.mkv", "sub.srt", "list.m3u" })
                {
                    if (names.Contains(expected, StringComparer.OrdinalIgnoreCase)) continue;

                    Log(33, $"文件页检查：列表里少了「{expected}」（有：{string.Join(" / ", names)}）");
                    return false;
                }

                if (names.Contains("inside.mp3", StringComparer.OrdinalIgnoreCase))
                {
                    Log(33, "文件页检查：进入目录时把子目录内容也列出来了（应该只读当前层）");
                    return false;
                }

                if (!TryRender(view, view.Width, view.Height, out var renderError))
                {
                    Log(33, "文件页检查：绘制失败：" + renderError);
                    return false;
                }

                if (!TryRender(view, 220, 200, out var smallError))
                {
                    Log(33, "文件页检查：220×200 绘制失败（" + smallError + "）");
                    return false;
                }

                if (!TryRender(view, 1, 1, out var tinyError))
                {
                    Log(33, "文件页检查：1×1 绘制失败（" + tinyError + "）");
                    return false;
                }

                // 假 I/O：迟到 600 ms。导航调用必须立刻返回，且慢结果不能盖掉后一个目录。
                var slow = Path.Combine(uiRoot, "slow");
                var fast = Path.Combine(uiRoot, "fast");
                view.Service = new SlowFileBrowserService(slow, fast);

                var watch = Stopwatch.StartNew();
                view.NavigateForTest(slow);
                watch.Stop();

                if (watch.ElapsedMilliseconds >= 200)
                {
                    Log(33, $"文件页检查：导航在 UI 线程上等了 {watch.ElapsedMilliseconds} ms（期望 < 200 ms）");
                    return false;
                }

                view.NavigateForTest(fast);

                if (!PumpUntil(() => !view.IsLoading, 4000))
                {
                    Log(33, "文件页检查：假 I/O 的第二次导航没有结束");
                    return false;
                }

                // 再等过慢请求的 600 ms：它不能回来把 fast 的结果盖掉。
                PumpMessages(900);

                if (!string.Equals(view.State.CurrentPath, fast, StringComparison.OrdinalIgnoreCase) ||
                    view.Entries.Count != 1 || view.Entries[0].Name != "fast")
                {
                    Log(33, "文件页检查：慢目录的结果盖到了后一个目录上");
                    return false;
                }

                view.Service = new FileBrowserService();
                view.NavigateForTest(uiRoot);

                if (!PumpUntil(() => !view.IsLoading && view.List.Items.Count > 0, 8000))
                {
                    Log(33, "文件页检查：换回真服务之后没读出来");
                    return false;
                }

                // 「显示所有文件」：.bin 与隐藏文件要出现，再关掉。
                view.SetShowAllFiles(true);

                if (!PumpUntil(
                        () => !view.IsLoading && ListViewNames(view.List).Contains("extra.bin", StringComparer.OrdinalIgnoreCase),
                        5000))
                {
                    Log(33, "文件页检查：打开「显示所有文件」之后仍然看不到 .bin");
                    return false;
                }

                view.SetShowAllFiles(false);
                PumpMessages(300);

                // 不存在的目录：状态要说清楚，不能一直停在"正在读取"。
                view.NavigateForTest(Path.Combine(uiRoot, "missing-" + Guid.NewGuid().ToString("N")));

                if (!PumpUntil(() => !view.IsLoading, 4000))
                {
                    Log(33, "文件页检查：不存在的目录没有读完");
                    return false;
                }

                if (!view.StatusText.Contains("不存在", StringComparison.Ordinal))
                {
                    Log(33, "文件页检查：不存在的目录没有报错（" + view.StatusText + "）");
                    return false;
                }

                view.NavigateForTest(uiRoot);
                PumpUntil(() => !view.IsLoading && view.List.Items.Count > 0, 8000);

                // 播放：文件页的「播放」事件要真的落到播放列表与引擎上。
                view.SelectByNameForTest("play.wav");
                view.InvokeActionForTest(FileBrowserAction.Play);

                if (!PumpUntil(() => form.Engine.HasMedia, 8000))
                {
                    Log(33, "文件页检查：双击播放没有打开媒体（状态：" + form.StatusText + "）");
                    return false;
                }

                form.Engine.Stop();
                PumpMessages(200);

                playlistCountBeforeDelete = form.Playlist.Count;

                // 焦点在文件列表时：Delete 必须被吞掉，方向键必须还给 ListView。
                if (!form.ProcessFileBrowserShortcutForTest(Keys.Delete))
                {
                    Log(33, "文件页检查：Delete 没有被窗口接住");
                    return false;
                }

                if (form.Playlist.Count != playlistCountBeforeDelete)
                {
                    Log(33, "文件页检查：在文件列表里按 Delete 把播放列表的项删了");
                    return false;
                }

                if (form.ProcessFileBrowserShortcutForTest(Keys.Up))
                {
                    Log(33, "文件页检查：方向键被命令层吃掉了（列表没法用键盘选）");
                    return false;
                }

                // 下一首播放。
                view.SelectByNameForTest("movie.mkv");
                view.InvokeActionForTest(FileBrowserAction.Queue);

                if (form.Workspace.Queue.Count == 0)
                {
                    Log(33, "文件页检查：右键「下一首播放」没有进队列");
                    return false;
                }

                // 整个文件夹（递归）加入：nested 里的 inside.mp3 要进来。
                view.SelectByNameForTest("nested");
                view.InvokeActionForTest(FileBrowserAction.Add);

                if (!PumpUntil(() => !form.IsScanning, 6000))
                {
                    Log(33, "文件页检查：添加整个文件夹之后扫描没有结束");
                    return false;
                }

                var playlistPaths = form.Playlist.Items.Select(item => item.FilePath).ToList();

                if (!playlistPaths.Any(path => path.EndsWith(@"nested\sub\inside.mp3", StringComparison.OrdinalIgnoreCase)))
                {
                    Log(33, "文件页检查：整个文件夹没有递归加入（nested\\sub\\inside.mp3 不在列表里）");
                    return false;
                }

                // 分离 / 归位。
                var floatItem = FindViewMenuItem(form, "文件浏览器分离为独立窗口");
                if (floatItem == null)
                {
                    Log(33, "文件页检查：「视图」菜单里没有文件浏览器分离项");
                    return false;
                }

                SetMenuChecked(floatItem, true);

                if (!PumpUntil(() => form.FileBrowserWindow != null && !ReferenceEquals(view.FindForm(), form), 4000))
                {
                    Log(33, "文件页检查：文件页没有搬进独立窗口");
                    return false;
                }

                if (!sidebar.FileBrowserDetached)
                {
                    Log(33, "文件页检查：分离之后侧栏没有留占位");
                    return false;
                }

                // 独立窗口也要能吸附主窗口，并跟着主窗口移动。
                var fileWindow = form.FileBrowserWindow;
                if (fileWindow == null)
                {
                    Log(33, "文件页检查：分离之后拿不到窗口");
                    return false;
                }

                fileWindow.Location = new Point(form.Right + 8, form.Top + 80);
                PumpMessages(300);

                if (form.FileBrowserSnap != WindowSnapEdge.Right || fileWindow.Left != form.Right)
                {
                    Log(33, $"文件页检查：文件窗口没有吸附到右侧（{form.FileBrowserSnap}，左边界 {fileWindow.Left} / 主窗右 {form.Right}）");
                    return false;
                }

                form.Location = new Point(form.Left - 40, form.Top + 30);
                PumpMessages(300);

                if (fileWindow.Left != form.Right)
                {
                    Log(33, "文件页检查：主窗口移动后文件窗口没有跟住（吸附失效）");
                    return false;
                }

                fileWindow.Location = new Point(form.Left - fileWindow.Width - 200, form.Top + 60);
                PumpMessages(300);

                if (form.FileBrowserSnap != WindowSnapEdge.None)
                {
                    Log(33, "文件页检查：拖远之后文件窗口没有脱离");
                    return false;
                }

                SetMenuChecked(floatItem, false);

                if (!PumpUntil(() => form.FileBrowserWindow == null && ReferenceEquals(view.FindForm(), form), 4000))
                {
                    Log(33, "文件页检查：文件页没有归位到主窗口");
                    return false;
                }

                if (sidebar.FileBrowserDetached)
                {
                    Log(33, "文件页检查：归位之后占位还在");
                    return false;
                }

                // 点窗口的 ✕ 也应当归位（和菜单取消勾选等价）。
                SetMenuChecked(floatItem, true);
                PumpUntil(() => form.FileBrowserWindow != null, 4000);

                var closeWindow = form.FileBrowserWindow;
                closeWindow?.Close();

                if (!PumpUntil(() => form.FileBrowserWindow == null && ReferenceEquals(view.FindForm(), form), 4000))
                {
                    Log(33, "文件页检查：点窗口 ✕ 没有归位");
                    return false;
                }

                // 收藏 / 排序，然后再次分离，关窗时应一起落盘。
                savedFavorite = uiRoot;
                view.State.AddFavorite(savedFavorite);
                view.SetSort(FileSortField.Modified, descending: true);
                form.SyncFileBrowserSettings();

                SetMenuChecked(floatItem, true);
                PumpUntil(() => form.FileBrowserWindow != null, 4000);

                form.Close();
                PumpMessages(400);
            }

            var saved = AppSettings.Load();

            if (!saved.FileBrowserFloating)
            {
                Log(33, "文件页检查：分离状态没有存进设置");
                return false;
            }

            if (!string.Equals(saved.FileBrowserLastPath, uiRoot, StringComparison.OrdinalIgnoreCase))
            {
                Log(33, "文件页检查：最后目录没有存进设置（" + saved.FileBrowserLastPath + "）");
                return false;
            }

            if (savedFavorite == null || !saved.FileBrowserFavorites.Contains(savedFavorite, StringComparer.OrdinalIgnoreCase))
            {
                Log(33, "文件页检查：收藏没有存进设置");
                return false;
            }

            if (saved.FileBrowserSort != "Modified" || !saved.FileBrowserSortDescending)
            {
                Log(33, "文件页检查：排序依据没有存进设置（" + saved.FileBrowserSort + "）");
                return false;
            }

            using (var second = new 播放器.MainForm(Array.Empty<string>()))
            {
                second.Show();
                PumpMessages(500);

                if (second.FileBrowserWindow == null)
                {
                    Log(33, "文件页检查：第二次启动没有恢复独立窗口");
                    return false;
                }

                if (!string.Equals(second.FileBrowser.State.LastSuccessfulPath, uiRoot, StringComparison.OrdinalIgnoreCase))
                {
                    Log(33, "文件页检查：第二次启动没有恢复最后目录（"
                            + (second.FileBrowser.State.LastSuccessfulPath ?? "空") + "）");
                    return false;
                }

                var dockItem = FindViewMenuItem(second, "文件浏览器分离为独立窗口");
                if (dockItem == null)
                {
                    Log(33, "文件页检查：第二次启动找不到分离菜单项");
                    return false;
                }

                SetMenuChecked(dockItem, false);
                PumpUntil(() => second.FileBrowserWindow == null, 4000);
                second.Close();
                PumpMessages(300);
            }

            Log(33, "文件页正常：只读当前层、显示所有、异步导航（<200 ms）、慢结果丢弃、"
                    + "播放 / 下一首 / 递归加文件夹、Delete 不误删、分离 / 归位与跨重启恢复");
            return true;
        }

        private static List<string> ListViewNames(ListView list) =>
            list.Items.Cast<ListViewItem>().Select(item => item.Text).ToList();

        // -----------------------------------------------------------------
        // GATED 探针
        // -----------------------------------------------------------------

        private static bool CheckFileBrowserProbe(string root)
        {
            if (Environment.GetEnvironmentVariable("播放器_TEST_FILEBROWSERPROBE") != "1") return true;

            var probeRoot = Path.Combine(root, "probe");
            Directory.CreateDirectory(probeRoot);

            for (var i = 0; i < 5000; i++)
                File.WriteAllBytes(Path.Combine(probeRoot, "p" + i.ToString("D5") + ".mp3"), Array.Empty<byte>());

            var service = new FileBrowserService();
            var watch = Stopwatch.StartNew();
            var result = service.ReadDirectory(probeRoot, showAllFiles: false, CancellationToken.None);
            watch.Stop();

            Log(33, $"文件浏览器探针：5,000 个文件读取 {watch.ElapsedMilliseconds} ms、"
                    + $"{result.Entries.Count} 条、截断 {result.Truncated}、"
                    + $"托管内存 {GC.GetTotalMemory(false) / 1024} KB");

            return true;
        }

        /// <summary>假服务：慢目录等 600 ms，用来证明导航没有在 UI 线程上读磁盘，并制造过期结果。</summary>
        private sealed class SlowFileBrowserService : FileBrowserService
        {
            private readonly string _slow;
            private readonly string _fast;

            internal SlowFileBrowserService(string slow, string fast)
            {
                _slow = slow;
                _fast = fast;
            }

            internal override FileBrowserReadResult ReadDirectory(
                string? path, bool showAllFiles, CancellationToken token)
            {
                var result = new FileBrowserReadResult();

                if (string.Equals(path, _slow, StringComparison.OrdinalIgnoreCase))
                {
                    // 真实磁盘枚举在 SMB 超时里是**不响应取消**的：这里用 Sleep 固定等 600 ms，
                    // 不看 token，用来证明"过期结果"靠代际号丢掉，而不是靠底层老实返回 Cancelled。
                    Thread.Sleep(600);

                    result.Entries.Add(new FileBrowserEntry(
                        "slow", path, FileBrowserKind.Folder, 0, DateTime.MinValue));
                    return result;
                }

                if (token.IsCancellationRequested)
                {
                    result.Cancelled = true;
                    return result;
                }

                var name = string.Equals(path, _fast, StringComparison.OrdinalIgnoreCase)
                    ? "fast"
                    : Path.GetFileName(path ?? "此电脑");

                result.Entries.Add(new FileBrowserEntry(
                    name, path ?? string.Empty, FileBrowserKind.Folder, 0, DateTime.MinValue));

                return result;
            }

            internal override FileBrowserReadResult ListRoots(CancellationToken token) =>
                new FileBrowserReadResult { IsRoots = true };
        }
    }
}
