using System.Diagnostics;

// 检查点 33 的 1.28.0 段：视频首帧缩略图。
//   ① 探针（GATED）：证明 libvlc 的 vmem 回调能只解码不出窗地拿到一帧，并量出线程 / 句柄代价；
//   ② 断言（默认跑）：用假的帧来源验"封面 → 帧 → 类型图标"的顺序与上限。

namespace SmokeTest
{
    internal static partial class Program
    {
        // -----------------------------------------------------------------
        // 1.28.0 的断言段：封面 → 帧 → 图标 / 落盘缓存 / 预算 / 视图菜单入口
        // -----------------------------------------------------------------

        private static bool CheckFileBrowserThumbnailRoundTwo(
            播放器.MainForm form, FileBrowserView view, string uiRoot)
        {
            var green = Color.FromArgb(0x33, 0xCC, 0x55);
            var frameColor = Color.FromArgb(0xCC, 0x66, 0x00);

            var frames = Path.Combine(uiRoot, "frames");
            var many = Path.Combine(uiRoot, "frames-many");

            Directory.CreateDirectory(frames);
            Directory.CreateDirectory(many);

            File.WriteAllText(Path.Combine(frames, "clip.mp4"), "clip");
            File.WriteAllBytes(
                Path.Combine(frames, "covered.mp4"), BuildMp4("封面", "歌手", "", MakeSolidPng(green, 64), 1));
            File.WriteAllText(Path.Combine(frames, "song.mp3"), "song");

            for (var i = 0; i < 61; i++)
                File.WriteAllText(Path.Combine(many, "v" + i.ToString("D2") + ".mp4"), "v");

            // ---- 视图菜单入口：两个入口、同一份状态 ------------------------------
            var menu = form.MenuViewFileBrowserThumbnails;

            if (menu == null)
            {
                Log(33, "文件页检查：「视图」菜单里没有「文件浏览器缩略图（平铺）」");
                return false;
            }

            if (menu.Checked)
            {
                Log(33, "文件页检查：视图菜单里的缩略图项一开始就勾着");
                return false;
            }

            SetMenuChecked(menu, true);
            PumpMessages(250);

            if (!menu.Checked)
            {
                Log(33, "文件页检查：从视图菜单切了之后菜单自己的勾选没跟上");
                return false;
            }

            if (!view.IsTilesForTest)
            {
                Log(33, "文件页检查：从视图菜单切了之后文件页还是详情");
                return false;
            }

            var positionItem = view.PositionMenuItemForTest("缩略图（平铺）");

            if (positionItem == null || !positionItem.Checked)
            {
                Log(33, "文件页检查：从视图菜单切了之后「位置」里那一项没跟上");
                return false;
            }

            view.SetThumbnailsForTest(false);
            PumpMessages(150);

            if (menu.Checked)
            {
                Log(33, "文件页检查：从「位置」切了之后视图菜单没跟上");
                return false;
            }

            // ---- 帧来源：没有封面才截帧，有封面 / 音频 / 不请求 ------------------
            var source = new CountingCoverArtLoader();
            var fake = new FakeVideoFrameSource(frameColor);

            view.ThumbnailSource = source;
            view.SetVideoFramesForTest(fake);
            view.MaxThumbnails = 300;

            view.NavigateForTest(frames);

            if (!PumpUntil(() => !view.IsLoading && view.List.Items.Count > 0, 8000))
            {
                Log(33, "文件页检查：帧缩略图夹具目录没有读出来");
                return false;
            }

            view.SetThumbnailsForTest(true);

            if (!PumpUntil(
                    () => view.ThumbnailCountForTest >= 2
                          && view.ImageIndexForTest("clip.mp4") >= view.TypeIconCountForTest
                          && view.PendingThumbnailCountForTest == 0, 8000))
            {
                Log(33, $"文件页检查：帧缩略图没有做出来（{view.ThumbnailCountForTest} 张、"
                        + $"排队 {view.PendingThumbnailCountForTest}、帧调用 {fake.Calls}）");
                return false;
            }

            var typeIcons = view.TypeIconCountForTest;
            var clipIndex = view.ImageIndexForTest("clip.mp4");

            if (clipIndex < typeIcons)
            {
                Log(33, "文件页检查：没有封面的视频没有拿到首帧缩略图");
                return false;
            }

            if (!ExpectThumbnailColor(view, clipIndex, frameColor, "clip.mp4")) return false;
            if (!ExpectThumbnailColor(view, view.ImageIndexForTest("covered.mp4"), green, "covered.mp4")) return false;

            if (fake.CallsFor("covered.mp4") != 0)
            {
                Log(33, "文件页检查：有封面的视频也去截帧了（封面应当优先）");
                return false;
            }

            if (fake.CallsFor("song.mp3") != 0)
            {
                Log(33, "文件页检查：音频也去截帧了");
                return false;
            }

            // ---- 落盘缓存：第二次进来封面 / 帧来源一次都不该被调用 ----------------
            var coverCalls = source.CallsFor("covered.mp4");
            var frameCalls = fake.CallsFor("clip.mp4");
            var stats = view.ThumbnailCacheStatsForTest;

            if (stats.Count < 2)
            {
                Log(33, $"文件页检查：缩略图没有落盘（缓存目录 {stats.Count} 个文件）");
                return false;
            }

            view.ClearThumbnailMemoryForTest();
            PumpMessages(50);

            if (!PumpUntil(
                    () => view.ThumbnailCountForTest >= 2 && view.PendingThumbnailCountForTest == 0, 8000))
            {
                Log(33, "文件页检查：清掉内存缓存之后没有从磁盘缓存贴回来");
                return false;
            }

            if (source.CallsFor("covered.mp4") != coverCalls)
            {
                Log(33, $"文件页检查：再进目录又读了一遍封面（{source.CallsFor("covered.mp4")} → "
                        + $"{coverCalls} 次）");
                return false;
            }

            if (fake.CallsFor("clip.mp4") != frameCalls)
            {
                Log(33, "文件页检查：再进目录又截了一遍帧（磁盘缓存没命中）");
                return false;
            }

            // 坏文件：删掉重做（这里故意写坏 covered.mp4 那一张，验"坏缓存不会让缩略图永久消失"）。
            var corrupt = view.ThumbnailCacheFileForTest(Path.Combine(frames, "covered.mp4"));

            if (corrupt != null)
            {
                var beforeCoverCalls = source.CallsFor("covered.mp4");

                File.WriteAllText(corrupt, "not a png");

                view.ClearThumbnailMemoryForTest();

                if (!PumpUntil(
                        () => view.ThumbnailCountForTest >= 2 && view.PendingThumbnailCountForTest == 0, 8000))
                {
                    Log(33, "文件页检查：缓存文件坏掉之后没有重做");
                    return false;
                }

                if (source.CallsFor("covered.mp4") <= beforeCoverCalls)
                {
                    Log(33, "文件页检查：缓存文件坏掉之后没有重读封面（调用次数没变）");
                    return false;
                }

                if (!ExpectThumbnailColor(view, view.ImageIndexForTest("covered.mp4"), green, "covered.mp4"))
                    return false;
            }
            else
            {
                Log(33, "文件页检查：找不到 covered.mp4 的磁盘缓存文件");
                return false;
            }

            // ---- 换了封面（修改时间变了）必须重做 --------------------------------
            var coveredPath = Path.Combine(frames, "covered.mp4");
            var red = Color.FromArgb(0xE0, 0x33, 0x33);
            var beforeChange = source.CallsFor("covered.mp4");

            File.WriteAllBytes(coveredPath, BuildMp4("封面二", "歌手", "", MakeSolidPng(red, 64), 2));
            File.SetLastWriteTimeUtc(coveredPath, DateTime.UtcNow.AddMinutes(1));

            view.NavigateForTest(frames);

            if (!PumpUntil(() => !view.IsLoading, 8000))
            {
                Log(33, "文件页检查：换了封面之后目录没有读回来");
                return false;
            }

            view.ClearThumbnailMemoryForTest();

            if (!PumpUntil(
                    () => view.ThumbnailCountForTest >= 2 && view.PendingThumbnailCountForTest == 0, 8000))
            {
                Log(33, "文件页检查：换了封面之后缩略图没有重做");
                return false;
            }

            if (!ExpectThumbnailColor(view, view.ImageIndexForTest("covered.mp4"), red, "covered.mp4"))
            {
                return false;
            }

            if (source.CallsFor("covered.mp4") <= beforeChange)
            {
                Log(33, "文件页检查：换了封面之后没有重读封面（调用次数没变）");
                return false;
            }

            // ---- 网络路径不做（封面与帧都不做） ---------------------------------
            var localVideo = new FileBrowserEntry(
                "a.mp4", @"C:\media\a.mp4", FileBrowserKind.Video, 1, DateTime.MinValue);
            var networkVideo = new FileBrowserEntry(
                "a.mp4", @"\\server\share\a.mp4", FileBrowserKind.Video, 1, DateTime.MinValue);

            if (!FileBrowserView.ThumbnailAppliesForTest(localVideo))
            {
                Log(33, "文件页检查：本地视频没有进缩略图队列");
                return false;
            }

            if (FileBrowserView.ThumbnailAppliesForTest(networkVideo))
            {
                Log(33, "文件页检查：网络路径拿到了画面缩略图");
                return false;
            }

            // ---- 预算真的会截断（设成 2，3 个音视频里只做 2 个并说明） ------------
            view.NavigateForTest(frames);

            if (!PumpUntil(() => !view.IsLoading && view.List.Items.Count > 0, 8000))
            {
                Log(33, "文件页检查：预算测试前没有读回夹具目录");
                return false;
            }

            view.MaxThumbnails = 2;
            view.SetThumbnailsForTest(false);
            view.SetThumbnailsForTest(true);

            if (!PumpUntil(() => view.PendingThumbnailCountForTest == 0, 8000))
            {
                Log(33, "文件页检查：预算测试的缩略图没有排完");
                return false;
            }

            if (view.ThumbnailsSkippedForTest != 1)
            {
                Log(33, $"文件页检查：预算 2 却没有截掉多余的（跳过 {view.ThumbnailsSkippedForTest} 个；"
                        + $"最大预算 {view.MaxThumbnailsForTest}、已选 {view.ThumbnailChosenCountForTest}、"
                        + $"可见 {view.List.Items.Count}、缩略图 {view.ThumbnailCountForTest}、"
                        + $"状态「{view.StatusText}」）");
                return false;
            }

            if (!view.StatusText.Contains("缩略图只做了前 2", StringComparison.Ordinal))
            {
                Log(33, "文件页检查：预算截断没有写进状态行（" + view.StatusText + "）");
                return false;
            }

            view.MaxThumbnails = 300;

            // ---- 预算：300 / 600 / 1000 三档 ------------------------------------
            view.SetThumbnailBudgetForTest(600);

            if (view.MaxThumbnailsForTest != 600)
            {
                Log(33, $"文件页检查：缩略图预算没生效（{view.MaxThumbnailsForTest}）");
                return false;
            }

            view.SetThumbnailBudgetForTest(300);

            if (view.MaxThumbnailsForTest != 300)
            {
                Log(33, "文件页检查：缩略图预算没有回到 300");
                return false;
            }

            // ---- 每目录 60 帧的上限 ---------------------------------------------
            view.NavigateForTest(many);

            if (!PumpUntil(() => !view.IsLoading && view.List.Items.Count == 61, 8000))
            {
                Log(33, "文件页检查：61 个视频的目录没有读出来");
                return false;
            }

            // 1.30.0 起只做可见范围的帧：滚过整张表，让每个范围都有机会排到 60 帧上限。
            for (var index = 0; index < 61; index += 5)
            {
                view.List.Items[index].EnsureVisible();
                PumpMessages(800);
                PumpUntil(
                    () => view.PendingThumbnailCountForTest == 0 && !view.IsThumbnailBusyForTest,
                    8000);
            }

            view.List.Items[60].EnsureVisible();
            PumpMessages(450);

            if (!PumpUntil(
                    () => view.PendingThumbnailCountForTest == 0 && !view.IsThumbnailBusyForTest,
                    20000))
            {
                Log(33, $"文件页检查：61 个视频的缩略图没有停稳（{view.ThumbnailCountForTest} 张、"
                        + $"排队 {view.PendingThumbnailCountForTest}）");
                return false;
            }

            var manyThumbs = view.List.Items
                .Cast<ListViewItem>()
                .Count(item => item.ImageIndex >= view.TypeIconCountForTest);

            if (manyThumbs > 60)
            {
                Log(33, $"文件页检查：61 个视频做出了 {manyThumbs} 张缩略图（上限 60）");
                return false;
            }

            if (view.FrameChosenCountForTest != 60)
            {
                Log(33, $"文件页检查：截帧预算没有用满（已选 {view.FrameChosenCountForTest} 个，期望 60）");
                return false;
            }

            if (view.FramesSkippedForTest != 1)
            {
                Log(33, $"文件页检查：截帧上限不对（跳过了 {view.FramesSkippedForTest} 个）");
                return false;
            }

            if (view.ImageIndexForTest("v60.mp4") >= view.TypeIconCountForTest)
            {
                Log(33, "文件页检查：第 61 个视频也做了画面缩略图");
                return false;
            }

            if (!view.StatusText.Contains("视频画面只做了前", StringComparison.Ordinal))
            {
                Log(33, "文件页检查：截帧上限没有写进状态行（" + view.StatusText + "）");
                return false;
            }

            // ---- 清空缓存（先关掉缩略图，否则清完马上又会重做）-------------------
            view.SetThumbnailsForTest(false);
            view.ClearThumbnailCacheForTest();

            if (view.ThumbnailCacheStatsForTest.Count != 0)
            {
                Log(33, $"文件页检查：清空缓存之后还剩 {view.ThumbnailCacheStatsForTest.Count} 个文件");
                return false;
            }

            if (!view.StatusText.Contains("已清空缩略图缓存", StringComparison.Ordinal))
            {
                Log(33, "文件页检查：清空缓存之后状态行没说明（" + view.StatusText + "）");
                return false;
            }

            view.SetVideoFramesForTest(null);

            Log(33, "文件页缩略图二轮正确：视图菜单双入口同步、无封面才截帧、封面优先、音频不截帧、"
                    + "落盘缓存命中与坏文件重做、预算三档、每目录 60 帧上限、清空缓存");
            return true;
        }

        /// <summary>假帧来源：永远返回 null（1.27 那一段只验封面，别去碰 libvlc）。</summary>
        private sealed class NullFrameSource : IVideoFrameSource
        {
            public Bitmap? Grab(string path, int edge, Func<bool>? isCancelled = null) => null;
        }

        /// <summary>假帧来源：给一张纯色图，顺便数调用次数（不碰 libvlc，测试才确定）。</summary>
        private sealed class FakeVideoFrameSource : IVideoFrameSource
        {
            private readonly Color _color;
            private readonly Dictionary<string, int> _callsByFile =
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            internal FakeVideoFrameSource(Color color) => _color = color;

            internal int Calls { get; private set; }

            internal int CallsFor(string fileName)
            {
                lock (_callsByFile)
                    return _callsByFile.TryGetValue(fileName, out var count) ? count : 0;
            }

            public Bitmap? Grab(string path, int edge, Func<bool>? isCancelled = null)
            {
                Calls++;

                lock (_callsByFile)
                {
                    var name = Path.GetFileName(path);
                    _callsByFile[name] = CallsFor(name) + 1;
                }

                return MakeSolidBitmap(_color, edge);
            }
        }

        private static Bitmap MakeSolidBitmap(Color color, int edge)
        {
            var bitmap = new Bitmap(Math.Max(1, edge), Math.Max(1, edge));

            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(color);

            return bitmap;
        }

        // -----------------------------------------------------------------
        // GATED 探针：门禁 G1（能不能做视频首帧）
        // -----------------------------------------------------------------

        private static bool CheckVideoFrameProbe()
        {
            if (Environment.GetEnvironmentVariable("播放器_TEST_FRAMEPROBE") != "1") return true;

            if (!TryCreateQuadrantClip(out var clip, out var reason))
            {
                Log(33, "抓帧探针：跳过（" + reason + "）");
                return true;
            }

            var process = Process.GetCurrentProcess();

            var threadsBefore = process.Threads.Count;
            var handlesBefore = process.HandleCount;
            var memoryBefore = GC.GetTotalMemory(forceFullCollection: true);

            var times = new List<long>();
            var failures = 0;
            var firstFrame = string.Empty;

            // 先单独验"缩放"这一步：合成一张四象限 BGRX，走产品里同一条 ScaleBgra。
            var synthetic = BuildBgraQuadrants(320, 240);

            using (var syntheticThumb = CoverThumbnails.ScaleBgra(synthetic, 320, 240, 320 * 4, 64))
            {
                Log(33, "抓帧探针：合成帧缩放 → "
                        + (syntheticThumb == null ? "null" : DescribeFrameQuadrants(syntheticThumb, 320, 240)));
            }
            var lastSize = (Width: 0, Height: 0);
            var disposeMs = 0L;
            var totalWatch = Stopwatch.StartNew();
            IReadOnlyList<(int Width, int Height, int Pitch)> formatSizes =
                Array.Empty<(int Width, int Height, int Pitch)>();

            try
            {
                var grabber = new VideoFrameGrabber();

                try
                {
                    // 第一次抓取含初始化（建 LibVLC + MediaPlayer），单独记一笔。
                    for (var i = 0; i < 20; i++)
                    {
                        var watch = Stopwatch.StartNew();
                        using var bitmap = grabber.Grab(clip, 64);

                        watch.Stop();
                        times.Add(watch.ElapsedMilliseconds);

                        if (bitmap == null)
                        {
                            failures++;
                            continue;
                        }

                        lastSize = grabber.LastFrameSize;

                        if (i == 0) firstFrame = DescribeFrameQuadrants(bitmap, lastSize.Width, lastSize.Height);

                        if (i == 0 && grabber.TryTakeLastFrame() is { } raw)
                        {
                            Log(33, $"抓帧探针：原始像素 {raw.Width}×{raw.Height}@{raw.Pitch}；"
                                    + string.Join(" / ", SampleRawPixels(raw.Pixels, raw.Width, raw.Height, raw.Pitch)));
                        }
                    }

                    formatSizes = grabber.FormatCalls;
                }
                finally
                {
                    var watch = Stopwatch.StartNew();
                    grabber.Dispose();
                    watch.Stop();
                    disposeMs = watch.ElapsedMilliseconds;
                }
            }
            catch (Exception ex)
            {
                Log(33, "抓帧探针：抛异常了（" + ex.GetType().Name + "：" + ex.Message + "）");
                return true;
            }

            totalWatch.Stop();

            var threadsAfter = process.Threads.Count;
            var handlesAfter = process.HandleCount;
            var memoryAfter = GC.GetTotalMemory(forceFullCollection: true);

            var first = times.Count > 0 ? times[0] : -1;
            var rest = times.Skip(1).ToList();
            var average = rest.Count > 0 ? (long)Math.Round(rest.Average()) : -1;
            var maximum = rest.Count > 0 ? rest.Max() : -1;

            Log(33, $"抓帧探针：20 次共 {totalWatch.ElapsedMilliseconds} ms；"
                    + $"首次 {first} ms、其余平均 {average} ms / 最大 {maximum} ms；"
                    + $"失败 {failures} 次；帧尺寸 {lastSize.Width}×{lastSize.Height}；"
                    + $"四象限 {firstFrame}；"
                    + $"线程 {threadsBefore} → {threadsAfter}（{(threadsAfter - threadsBefore >= 0 ? "+" : string.Empty)}{threadsAfter - threadsBefore}）、"
                    + $"句柄 {handlesBefore} → {handlesAfter}（{(handlesAfter - handlesBefore >= 0 ? "+" : string.Empty)}{handlesAfter - handlesBefore}）、"
                    + $"托管内存 {memoryBefore / 1024} → {memoryAfter / 1024} KB；"
                    + $"释放 {disposeMs} ms");

            if (formatSizes.Count > 0)
            {
                Log(33, "抓帧探针：格式回调 " + string.Join(
                    " / ", formatSizes.Select(size => $"{size.Width}×{size.Height}@{size.Pitch}")));
            }

            return true;
        }

        /// <summary>采几个已知位置（四象限中心）的原始 4 字节，看看通道顺序。</summary>
        private static IEnumerable<string> SampleRawPixels(byte[] pixels, int width, int height, int pitch)
        {
            (int X, int Y)[] points =
            {
                (width / 4, height / 4),
                (width * 3 / 4, height / 4),
                (width / 4, height * 3 / 4),
                (width * 3 / 4, height * 3 / 4)
            };

            foreach (var (x, y) in points)
            {
                var offset = y * pitch + x * 4;
                yield return $"({x},{y}) {pixels[offset]:X2}{pixels[offset + 1]:X2}"
                             + $"{pixels[offset + 2]:X2}{pixels[offset + 3]:X2}";
            }
        }

        /// <summary>合成一张四象限 BGRX（RV32）缓冲区：左上红、右上绿、左下蓝、右下黄。</summary>
        private static byte[] BuildBgraQuadrants(int width, int height)
        {
            var pixels = new byte[width * height * 4];
            var halfWidth = width / 2;
            var halfHeight = height / 2;

            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var offset = (y * width + x) * 4;

                    byte r, g, b;

                    if (y < halfHeight)
                    {
                        r = (byte)(x < halfWidth ? 255 : 0);
                        g = (byte)(x < halfWidth ? 0 : 255);
                        b = 0;
                    }
                    else
                    {
                        r = 0;
                        g = 0;
                        b = (byte)(x < halfWidth ? 255 : 0);

                        if (x >= halfWidth)
                        {
                            r = 255;
                            g = 255;
                        }
                    }

                    pixels[offset] = b;
                    pixels[offset + 1] = g;
                    pixels[offset + 2] = r;
                    pixels[offset + 3] = 255;
                }
            }

            return pixels;
        }

        /// <summary>按"等比放进方框"的落点采四个象限的颜色（缩略图是 letterbox 的，上下有透明边）。</summary>
        private static string DescribeFrameQuadrants(Bitmap bitmap, int sourceWidth, int sourceHeight)
        {
            var edge = bitmap.Width;
            var colored = 0;
            var center = Color.Empty;

            for (var y = 0; y < bitmap.Height; y++)
            {
                for (var x = 0; x < bitmap.Width; x++)
                {
                    var pixel = bitmap.GetPixel(x, y);

                    if (pixel.R + pixel.G + pixel.B > 30) colored++;
                }
            }

            center = bitmap.GetPixel(edge / 2, edge / 2);

            if (sourceWidth <= 0 || sourceHeight <= 0)
            {
                sourceWidth = 16;
                sourceHeight = 9;
            }

            // 56×56 的框里，画面按源宽高等比缩放、垂直居中；取每块的中心。
            var scale = Math.Min(edge / (double)sourceWidth, edge / (double)sourceHeight);
            var drawn = Math.Max(2, (int)Math.Round(sourceHeight * scale));
            var top = (edge - drawn) / 2;

            var left = Math.Max(1, edge / 4);
            var right = Math.Min(edge - 2, edge * 3 / 4);
            var upper = Math.Min(edge - 2, top + Math.Max(1, drawn / 4));
            var lower = Math.Min(edge - 2, top + Math.Max(1, drawn * 3 / 4));

            string Name(int x, int y)
            {
                var pixel = bitmap.GetPixel(x, y);
                return ClassifyColour(pixel.R, pixel.G, pixel.B);
            }

            return $"左上 {Name(left, upper)} / 右上 {Name(right, upper)} / "
                   + $"左下 {Name(left, lower)} / 右下 {Name(right, lower)}；"
                   + $"有颜色 {colored}/{bitmap.Width * bitmap.Height} 像素、中心 ({center.R},{center.G},{center.B})";
        }
    }
}
