using System.Diagnostics;

// 检查点 33 的 1.27.0 段：文件页封面缩略图（平铺形态 / 后台线程 / 缓存 / 上限 / 搜索结果回详情）。

namespace SmokeTest
{
    internal static partial class Program
    {
        private static bool CheckFileBrowserThumbnails(播放器.MainForm form, FileBrowserView view, string uiRoot)
        {
            var uiThread = Environment.CurrentManagedThreadId;

            // 1.27 那一段只验封面：帧来源换成"永远没有"，既不碰 libvlc，也让变异确定。
            view.SetVideoFramesForTest(new NullFrameSource());

            var root = Path.Combine(uiRoot, "thumbs");
            var embedded = Path.Combine(root, "embedded");
            var sidecar = Path.Combine(root, "sidecar");
            var bad = Path.Combine(root, "bad");
            var many = Path.Combine(root, "many");
            var late1 = Path.Combine(root, "late1");
            var late2 = Path.Combine(root, "late2");

            foreach (var folder in new[] { embedded, sidecar, bad, many, late1, late2 })
                Directory.CreateDirectory(folder);

            var blue = Color.FromArgb(0x33, 0x99, 0xFF);
            var green = Color.FromArgb(0x33, 0xCC, 0x55);
            var red = Color.FromArgb(0xE0, 0x33, 0x33);

            // 内嵌封面：ID3v2.3 + APIC（纯色 PNG，缩放后仍是同色，方便逐像素断言）。
            WriteCoverMp3(Path.Combine(embedded, "a.mp3"), blue);

            // 没有标签，也没有外挂封面。
            File.WriteAllText(Path.Combine(embedded, "b.mp3"), "plain");

            // 视频的 MP4 内嵌封面（covr）也要能当缩略图。
            File.WriteAllBytes(
                Path.Combine(embedded, "c.mp4"),
                BuildMp4("封面丙", "歌手", "", MakeSolidPng(green, 64), 1));

            // 外挂封面：同目录 cover.png。
            File.WriteAllText(Path.Combine(sidecar, "d.mp3"), "plain");
            File.WriteAllBytes(Path.Combine(sidecar, "cover.png"), MakeSolidPng(red, 64));

            // 坏封面：APIC 里放的不是图片。
            File.WriteAllBytes(Path.Combine(bad, "broken.mp3"), BuildId3File(3, new List<byte[]>
            {
                Id3Frame("APIC", PictureBody(3, "image/png", 3, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }), 3)
            }));

            for (var i = 0; i < 5; i++)
                WriteCoverMp3(Path.Combine(many, "m" + i + ".mp3"), blue);

            WriteCoverMp3(Path.Combine(late1, "late1-a.mp3"), blue);
            WriteCoverMp3(Path.Combine(late2, "late2-b.mp3"), green);

            var source = new CountingCoverArtLoader();
            view.ThumbnailSource = source;
            view.MaxThumbnails = 300;

            view.NavigateForTest(embedded);

            if (!PumpUntil(() => !view.IsLoading && view.List.Items.Count > 0, 8000))
            {
                Log(33, "文件页检查：缩略图测试前没有读出目录（状态：" + view.StatusText + "）");
                return false;
            }

            var before = ListViewNames(view.List);

            view.SetThumbnailsForTest(true);

            if (!view.IsTilesForTest || view.List.View != View.LargeIcon)
            {
                Log(33, "文件页检查：打开缩略图之后没有切到平铺（" + view.List.View + "）");
                return false;
            }

            if (view.List.LargeImageList == null
                || view.List.LargeImageList.ImageSize.Width != view.ThumbnailEdgeForTest)
            {
                Log(33, "文件页检查：缩略图尺寸与 ImageList 不一致（"
                        + (view.List.LargeImageList?.ImageSize.Width ?? 0) + " / "
                        + view.ThumbnailEdgeForTest + "）");
                return false;
            }

            if (before.Count != view.List.Items.Count || view.ThumbnailEdgeForTest < 16)
            {
                Log(33, "文件页检查：切到平铺之后条目数变了（" + view.List.Items.Count + " / " + before.Count + "）");
                return false;
            }

            if (!PumpUntil(
                    () => view.ThumbnailCountForTest >= 2 && view.PendingThumbnailCountForTest == 0,
                    8000))
            {
                Log(33, $"文件页检查：平铺之后缩略图没有做出来（{view.ThumbnailCountForTest} 张、"
                        + $"排队 {view.PendingThumbnailCountForTest}、调用 {source.Calls} 次）");
                return false;
            }

            var typeIcons = view.TypeIconCountForTest;
            var aIndex = view.ImageIndexForTest("a.mp3");

            if (typeIcons <= 0 || aIndex < typeIcons)
            {
                Log(33, $"文件页检查：有内嵌封面的 a.mp3 用的还是类型图标（索引 {aIndex}，类型图标 {typeIcons} 个）");
                return false;
            }

            if (!ExpectThumbnailColor(view, aIndex, blue, "a.mp3")) return false;
            if (!ExpectThumbnailColor(view, view.ImageIndexForTest("c.mp4"), green, "c.mp4")) return false;

            // 平铺长什么样，留一张图给所有者看（只在布局探针打开时）。
            if (Environment.GetEnvironmentVariable("播放器_TEST_LAYOUTPROBE") == "1")
            {
                try
                {
                    var shot = Path.Combine(Path.GetTempPath(), "file-browser-tiles.png");

                    using var bitmap = new Bitmap(Math.Max(1, view.Width), Math.Max(1, view.Height));
                    view.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
                    bitmap.Save(shot, System.Drawing.Imaging.ImageFormat.Png);
                    Log(33, "文件页布局探针：平铺截图 " + shot);
                }
                catch (Exception ex)
                {
                    Log(33, "文件页布局探针：平铺截图失败：" + ex.Message);
                }
            }

            var bIndex = view.ImageIndexForTest("b.mp3");

            if (bIndex >= typeIcons)
            {
                Log(33, $"文件页检查：没有封面的 b.mp3 也用上了缩略图（索引 {bIndex}）");
                return false;
            }

            // 后台线程：封面读取一次都不能落在界面线程上。
            lock (source.Threads)
            {
                if (source.Threads.Count == 0 || source.Threads.Contains(uiThread))
                {
                    Log(33, "文件页检查：封面在界面线程上读了（线程 "
                            + string.Join(" / ", source.Threads) + "，界面线程 " + uiThread + "）");
                    return false;
                }
            }

            // 外挂封面：同目录 cover.png 也要认。
            view.NavigateForTest(sidecar);

            if (!PumpUntil(() => !view.IsLoading && view.PendingThumbnailCountForTest == 0, 8000))
            {
                Log(33, "文件页检查：外挂封面目录没有读完（状态：" + view.StatusText + "）");
                return false;
            }

            var dIndex = view.ImageIndexForTest("d.mp3");

            if (dIndex < typeIcons || !ExpectThumbnailColor(view, dIndex, red, "d.mp3")) return false;

            // 坏封面：类型图标 + 不崩；负缓存，重进目录不再重读。
            view.NavigateForTest(bad);

            if (!PumpUntil(() => !view.IsLoading && view.PendingThumbnailCountForTest == 0, 8000))
            {
                Log(33, "文件页检查：坏封面目录没有读完");
                return false;
            }

            if (view.ImageIndexForTest("broken.mp3") >= typeIcons)
            {
                Log(33, "文件页检查：解不开的封面仍然被当成缩略图贴上了");
                return false;
            }

            var brokenCalls = source.CallsFor("broken.mp3");

            view.NavigateForTest(embedded);
            PumpUntil(() => !view.IsLoading && view.PendingThumbnailCountForTest == 0, 8000);
            view.NavigateForTest(bad);

            if (!PumpUntil(() => !view.IsLoading && view.PendingThumbnailCountForTest == 0, 8000))
            {
                Log(33, "文件页检查：第二次进坏封面目录没有读完");
                return false;
            }

            if (source.CallsFor("broken.mp3") != brokenCalls)
            {
                Log(33, $"文件页检查：封面坏掉的文件每次进目录都重新读（调用 {source.CallsFor("broken.mp3")} 次，"
                        + $"第一次 {brokenCalls} 次）");
                return false;
            }

            // 正缓存：再进一次 embedded，a.mp3 不该再读一遍。
            var aCalls = source.CallsFor("a.mp3");
            view.NavigateForTest(embedded);

            if (!PumpUntil(() => !view.IsLoading && view.PendingThumbnailCountForTest == 0, 8000))
            {
                Log(33, "文件页检查：缩略图缓存目录没有读完");
                return false;
            }

            if (source.CallsFor("a.mp3") != aCalls || view.ImageIndexForTest("a.mp3") < typeIcons)
            {
                Log(33, $"文件页检查：缓存没有命中（a.mp3 读了 {source.CallsFor("a.mp3")} 次，"
                        + $"第一次 {aCalls} 次；索引 {view.ImageIndexForTest("a.mp3")}）");
                return false;
            }

            // 缓存键要带大小 / 修改时间：同一个路径换了封面，缩略图必须跟着换。
            var changedColor = Color.FromArgb(0xE0, 0xCC, 0x33);
            WriteCoverMp3(Path.Combine(embedded, "a.mp3"), changedColor);
            File.SetLastWriteTime(Path.Combine(embedded, "a.mp3"), DateTime.Now.AddMinutes(5));

            view.NavigateForTest(embedded);

            if (!PumpUntil(() => !view.IsLoading && view.PendingThumbnailCountForTest == 0, 8000))
            {
                Log(33, "文件页检查：换过封面之后目录没有读完");
                return false;
            }

            if (!ExpectThumbnailColor(view, view.ImageIndexForTest("a.mp3"), changedColor, "a.mp3（换过封面）"))
            {
                return false;
            }

            // 迟到结果：后台卡在 late1 的文件上时导航走；放行之后旧结果不能贴到 late2 的行上。
            var gated = new BlockingCoverArtLoader("late1-a.mp3");
            view.ThumbnailSource = gated;
            view.NavigateForTest(late1);

            if (!PumpUntil(() => gated.Started, 8000))
            {
                Log(33, "文件页检查：后台没有开始读 late1 的封面");
                return false;
            }

            if (!view.IsThumbnailBusyForTest || view.PendingThumbnailCountForTest > ThumbnailQueue.MaxPending)
            {
                Log(33, $"文件页检查：缩略图队列上限不对（在飞 {view.IsThumbnailBusyForTest}、"
                        + $"排队 {view.PendingThumbnailCountForTest}）");
                return false;
            }

            var navigateWatch = Stopwatch.StartNew();
            view.NavigateForTest(late2);
            navigateWatch.Stop();

            if (navigateWatch.ElapsedMilliseconds >= 200)
            {
                Log(33, $"文件页检查：缩略图在飞时导航等了 {navigateWatch.ElapsedMilliseconds} ms（期望 < 200 ms）");
                return false;
            }

            gated.Gate.Set();

            if (!PumpUntil(
                    () => !view.IsLoading
                          && view.PendingThumbnailCountForTest == 0
                          && view.ImageIndexForTest("late2-b.mp3") >= typeIcons,
                    8000))
            {
                Log(33, "文件页检查：放行之后 late2 的缩略图没有出来（索引 "
                        + view.ImageIndexForTest("late2-b.mp3") + "）");
                return false;
            }

            if (view.ThumbnailKeysForTest.Any(key => key.Contains("late1-a.mp3", StringComparison.OrdinalIgnoreCase)))
            {
                Log(33, "文件页检查：离开目录之后迟到的封面贴到了别的文件上");
                return false;
            }

            // 每目录预算：MaxThumbnails = 3 时 many 里 5 个封面只做 3 个，并在状态行说明。
            view.ThumbnailSource = new CountingCoverArtLoader();
            view.MaxThumbnails = 3;
            view.NavigateForTest(many);

            if (!PumpUntil(() => !view.IsLoading && view.PendingThumbnailCountForTest == 0, 8000))
            {
                Log(33, "文件页检查：缩略图上限目录没有读完");
                return false;
            }

            PumpMessages(200);

            var withThumbnails = view.List.Items
                .Cast<ListViewItem>()
                .Count(item => item.ImageIndex >= typeIcons);

            if (withThumbnails != 3)
            {
                Log(33, $"文件页检查：超过上限仍然给全部条目排了缩略图（{withThumbnails} 个）");
                return false;
            }

            if (!view.StatusText.Contains("缩略图只做了前 3 个文件", StringComparison.Ordinal))
            {
                Log(33, "文件页检查：触到缩略图上限之后状态行没有说明（" + view.StatusText + "）");
                return false;
            }

            view.MaxThumbnails = 300;

            // 网络路径不做缩略图（离线共享上的读盘不可取消）。
            if (ThumbnailApplies(@"\\server\share\music.mp3", FileBrowserKind.Audio)
                || !ThumbnailApplies(@"C:\music.mp3", FileBrowserKind.Audio)
                || ThumbnailApplies(@"C:\music", FileBrowserKind.Folder))
            {
                Log(33, "文件页检查：网络路径也去读了封面");
                return false;
            }

            // 搜索结果强制回详情：平铺里放不下「位置」列。
            view.SetThumbnailsForTest(false);
            view.NavigateForTest(uiRoot);
            PumpUntil(() => !view.IsLoading && view.List.Items.Count > 0, 8000);

            view.SetThumbnailsForTest(true);

            if (!view.IsTilesForTest)
            {
                Log(33, "文件页检查：搜索之前没有回到平铺（" + view.List.View + "）");
                return false;
            }

            view.SetRecursiveSearchForTest(true);
            view.SetSearchTextForTest("search-hit");
            view.ProcessSearchKeyForTest(Keys.Enter);

            if (!PumpUntil(() => !view.IsSearchingForTest() && view.IsSearchActiveForTest(), 8000))
            {
                Log(33, "文件页检查：缩略图测试里的递归搜索没有结束");
                return false;
            }

            if (view.List.View != View.Details || view.List.Columns.Count < 3 || view.List.Columns[2].Width <= 0)
            {
                Log(33, "文件页检查：递归搜索结果里还停在平铺，「位置」列看不见");
                return false;
            }

            view.ClearSearchForTest();

            if (!PumpUntil(() => !view.IsLoading && view.List.Items.Count > 0, 8000) || !view.IsTilesForTest)
            {
                Log(33, "文件页检查：清空搜索之后没有回到平铺");
                return false;
            }

            view.SetRecursiveSearchForTest(false);

            // 切回详情要在"行上真的贴着缩略图"的目录里验，否则复位与否根本看不出来。
            view.NavigateForTest(embedded);

            if (!PumpUntil(
                    () => !view.IsLoading
                          && view.PendingThumbnailCountForTest == 0
                          && view.ImageIndexForTest("a.mp3") >= typeIcons,
                    8000))
            {
                Log(33, "文件页检查：切回详情之前缩略图没有回来（索引 "
                        + view.ImageIndexForTest("a.mp3") + "）");
                return false;
            }

            view.SetThumbnailsForTest(false);

            if (view.IsTilesForTest || !view.List.FullRowSelect)
            {
                Log(33, "文件页检查：关掉缩略图之后没有回到详情视图");
                return false;
            }

            if (view.List.Columns[0].Width <= 0 || view.List.Columns[2].Width != 0)
            {
                Log(33, $"文件页检查：切回详情之后列宽不对（名称 {view.List.Columns[0].Width}、"
                        + $"位置 {view.List.Columns[2].Width}）");
                return false;
            }

            var lingering = view.List.Items
                .Cast<ListViewItem>()
                .FirstOrDefault(item => item.ImageIndex >= typeIcons);

            if (lingering != null)
            {
                Log(33, $"文件页检查：切回详情之后行的图标索引还是缩略图那套（{lingering.Text} → {lingering.ImageIndex}）");
                return false;
            }

            // 平铺下渲染与滚轮不能抛。
            view.SetThumbnailsForTest(true);

            if (!TryRender(view, 220, 200, out var renderError))
            {
                Log(33, "文件页检查：平铺 220×200 绘制失败（" + renderError + "）");
                return false;
            }

            view.ScrollByForTest(120);
            view.ScrollByForTest(-120);
            PumpMessages(100);

            // 选择要能进设置对象（落盘在退出时统一做，外层会再验一次磁盘）。
            if (!view.State.ShowThumbnails)
            {
                Log(33, "文件页检查：勾选缩略图之后状态对象里没记下来");
                return false;
            }

            var settings = new AppSettings();
            view.State.ApplyTo(settings);

            if (!settings.FileBrowserThumbnails
                || !FileBrowserState.FromSettings(settings).ShowThumbnails)
            {
                Log(33, "文件页检查：缩略图开关没有进设置对象 / 读不回来");
                return false;
            }

            view.SetThumbnailsForTest(false);
            view.ThumbnailSource = new CoverArtLoader();
            view.SetRecursiveSearchForTest(false);
            view.ClearSearchForTest();
            view.NavigateForTest(uiRoot);

            if (!PumpUntil(() => !view.IsLoading && view.List.Items.Count > 0, 8000))
            {
                Log(33, "文件页检查：缩略图测试收尾之后没有回到当前层");
                return false;
            }

            Log(33, "文件页缩略图正确：内嵌 / 外挂封面、无封面与坏封面回类型图标、后台线程、正负缓存、"
                    + "迟到结果丢弃、每目录上限与说明、搜索结果回详情、切回详情复位图标与列宽");
            return true;
        }

        /// <summary>GATED 探针：只看文件页的几何与文字宽度，只量不判。</summary>
        private static bool CheckFileBrowserLayoutProbe(播放器.MainForm form, FileBrowserView view)
        {
            if (Environment.GetEnvironmentVariable("播放器_TEST_LAYOUTPROBE") != "1") return true;

            var button = view.PositionButtonForTest;
            var scope = view.ScopeToggleForTest;
            var buttonText = TextRenderer.MeasureText(button.Text, button.Font);
            var scopeText = TextRenderer.MeasureText(scope.Text, scope.Font);
            var list = view.List;

            Log(33, $"文件页布局探针：窗体 {form.ClientSize.Width}×{form.ClientSize.Height}、"
                    + $"侧栏 {view.Parent?.Parent?.Width}、文件页 {view.Width}×{view.Height}、DPI {view.DeviceDpi}；"
                    + $"「位置」按钮 {button.Left}+{button.Width}（右 {button.Right}）、文字宽 {buttonText.Width}、"
                    + $"内宽 {button.ClientSize.Width}；"
                    + $"「含子文件夹」 {scope.Left}+{scope.Width}（右 {scope.Right}）、文字宽 {scopeText.Width}、"
                    + $"内宽 {scope.ClientSize.Width}；"
                    + $"列表 {list.ClientSize.Width}、列 {string.Join("/", list.Columns.Cast<ColumnHeader>().Select(c => c.Text + c.Width))}");

            // 顺手存一张截图：所有者能直接看"两个按钮的文字是不是全的"。
            var shot = Path.Combine(Path.GetTempPath(), "file-browser-layout.png");

            try
            {
                using var bitmap = new Bitmap(Math.Max(1, view.Width), Math.Max(1, view.Height));
                view.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
                bitmap.Save(shot, System.Drawing.Imaging.ImageFormat.Png);
                Log(33, "文件页布局探针：截图 " + shot);
            }
            catch (Exception ex)
            {
                Log(33, "文件页布局探针：截图失败：" + ex.Message);
            }

            return true;
        }

        /// <summary>GATED 探针：读 + 解码 100 张封面，只量不判（默认全量不设）。</summary>
        private static bool CheckFileBrowserThumbnailProbe(string root)
        {
            if (Environment.GetEnvironmentVariable("播放器_TEST_THUMBNAILPROBE") != "1") return true;

            var folder = Path.Combine(root, "thumb-probe");
            TryDeleteDirectory(folder);
            Directory.CreateDirectory(folder);

            var png = MakeSolidPng(Color.FromArgb(0x33, 0x99, 0xFF), 256);

            for (var i = 0; i < 100; i++)
            {
                File.WriteAllBytes(Path.Combine(folder, "t" + i.ToString("D3") + ".mp3"), BuildId3File(3, new List<byte[]>
                {
                    Id3Frame("APIC", PictureBody(3, "image/png", 3, png), 3)
                }));
            }

            var loader = new CoverArtLoader();
            var watch = Stopwatch.StartNew();
            var hits = 0;
            var bytes = 0L;

            for (var i = 0; i < 100; i++)
            {
                var art = loader.Load(Path.Combine(folder, "t" + i.ToString("D3") + ".mp3"));

                if (art != null) bytes += art.Length;

                using var thumbnail = CoverThumbnails.Decode(art, 56);
                if (thumbnail != null) hits++;
            }

            watch.Stop();

            Log(33, $"文件页缩略图探针：100 张封面读取 + 解码 {watch.ElapsedMilliseconds} ms、命中 {hits}、"
                    + $"封面字节合计 {bytes / 1024} KB、托管内存 {GC.GetTotalMemory(false) / 1024} KB");

            TryDeleteDirectory(folder);
            return true;
        }

        /// <summary>条目的「该不该做缩略图」规则：只认本机音视频（网络位置与目录都不做）。</summary>

        private static bool ThumbnailApplies(string path, FileBrowserKind kind)
        {
            var entry = new FileBrowserEntry(
                Path.GetFileName(path), path, kind, 1024, new DateTime(2024, 1, 1));

            return FileBrowserView.ThumbnailAppliesForTest(entry);
        }

        private static bool ExpectThumbnailColor(FileBrowserView view, int index, Color expected, string name)
        {
            var image = view.LargeImageForTest(index);

            if (image == null)
            {
                Log(33, $"文件页检查：{name} 的缩略图取不出来（索引 {index}）");
                return false;
            }

            if (image.Width != view.ThumbnailEdgeForTest || image.Height != view.ThumbnailEdgeForTest)
            {
                Log(33, $"文件页检查：{name} 的缩略图尺寸不对（{image.Width}×{image.Height}，"
                        + $"期望 {view.ThumbnailEdgeForTest}）");
                return false;
            }

            using var copy = new Bitmap(image);
            var center = copy.GetPixel(copy.Width / 2, copy.Height / 2);

            if (!NearColor(center, expected, 16))
            {
                Log(33, $"文件页检查：{name} 的缩略图颜色不对（{center.R},{center.G},{center.B}，"
                        + $"期望 {expected.R},{expected.G},{expected.B}）");
                return false;
            }

            return true;
        }

        private static bool NearColor(Color actual, Color expected, int tolerance)
        {
            return Math.Abs(actual.R - expected.R) <= tolerance
                   && Math.Abs(actual.G - expected.G) <= tolerance
                   && Math.Abs(actual.B - expected.B) <= tolerance;
        }

        private static void WriteCoverMp3(string path, Color color)
        {
            File.WriteAllBytes(path, BuildId3File(3, new List<byte[]>
            {
                Id3Frame("TIT2", TextBody(3, Encoding.UTF8.GetBytes(Path.GetFileNameWithoutExtension(path))), 3),
                Id3Frame("APIC", PictureBody(3, "image/png", 3, MakeSolidPng(color, 64)), 3)
            }));
        }

        /// <summary>纯色 PNG：缩略图缩放之后仍应保持同一个颜色，像素断言才有意义。</summary>
        private static byte[] MakeSolidPng(Color color, int size)
        {
            using var bitmap = new Bitmap(size, size);

            using (var graphics = Graphics.FromImage(bitmap))
                graphics.Clear(color);

            using var stream = new MemoryStream();
            bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            return stream.ToArray();
        }

        private sealed class CountingCoverArtLoader : CoverArtLoader
        {
            private readonly Dictionary<string, int> _callsByFile =
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            internal int Calls { get; private set; }

            internal List<int> Threads { get; } = new List<int>();

            internal int CallsFor(string fileName)
            {
                lock (_callsByFile)
                    return _callsByFile.TryGetValue(fileName, out var count) ? count : 0;
            }

            internal override byte[]? Load(string path)
            {
                Calls++;

                var name = Path.GetFileName(path);

                lock (_callsByFile)
                {
                    _callsByFile[name] = CallsFor(name) + 1;
                }

                lock (Threads)
                {
                    Threads.Add(Environment.CurrentManagedThreadId);
                }

                return base.Load(path);
            }
        }

        /// <summary>
        /// 卡住一个文件的封面读取：用来制造"后台在飞时导航走"与"迟到结果"。
        /// 只卡指定的那个文件名，别的照常返回——否则队列被一条卡死的读盘拖住，验不了迟到。
        /// </summary>
        private sealed class BlockingCoverArtLoader : CoverArtLoader
        {
            private readonly string _blockedName;
            private volatile bool _started;

            internal BlockingCoverArtLoader(string blockedName) => _blockedName = blockedName;

            internal ManualResetEventSlim Gate { get; } = new ManualResetEventSlim(false);

            internal bool Started => _started;

            internal override byte[]? Load(string path)
            {
                if (string.Equals(Path.GetFileName(path), _blockedName, StringComparison.OrdinalIgnoreCase))
                {
                    _started = true;
                    Gate.Wait(8000);
                }

                return base.Load(path);
            }
        }
    }
}
