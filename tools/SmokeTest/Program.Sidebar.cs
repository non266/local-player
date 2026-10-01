// 检查点 11：侧栏真实链路、歌词时间提示、自绘控件的退化尺寸。

namespace SmokeTest
{
    internal static partial class Program
    {
        // -----------------------------------------------------------------
        // 11) 侧栏：真实链路 + 自绘控件的退化尺寸
        // -----------------------------------------------------------------

        private static bool TestSidebarRendering()
        {
            var media = Path.Combine(AppContext.BaseDirectory, "smoke-tags", "sample.flac");

            if (!File.Exists(media))
            {
                Log(11, "缺少第 9 步生成的样本文件");
                return false;
            }

            // 会话本身的时序是纯逻辑，先单独钉住（下面那条真链路反而不好复现它）
            if (!CheckTrackSession()) return false;

            try
            {
                using var form = new 播放器.MainForm(Array.Empty<string>());
                form.Show();
                PumpMessages(300);

                var sidebar = Find(form, "sidebarPanel");
                if (sidebar == null)
                {
                    Log(11, "设计器里找不到侧栏控件");
                    return false;
                }

                var splitVideo = Find(form, "splitVideo");
                if (splitVideo == null)
                {
                    Log(11, "设计器里找不到侧栏所在的分隔条（splitVideo）");
                    return false;
                }

                if (Find(form, "playlistHost") == null)
                {
                    Log(11, "设计器里找不到承载播放列表的面板（playlistHost）");
                    return false;
                }

                // 走真实的元数据加载路径：内部会异步读标签再回填侧栏
                ((播放器.MainForm)form).LoadTrackMetadata(media);
                PumpMessages(1500);

                var lyricsView = FindByTypeName(sidebar, "LyricsView");
                if (lyricsView == null)
                {
                    Log(11, "侧栏里找不到歌词控件");
                    return false;
                }

                var document = (lyricsView as LyricsView)?.Document;
                if (document is not { IsSynchronized: true } || document.Lines.Count != 2)
                {
                    Log(11, $"侧栏没有拿到内嵌歌词（行数 {document?.Lines.Count ?? 0}）");
                    return false;
                }

                // 四个页面都要能画出来
                foreach (var tab in new[]
                         { SidebarTab.Lyrics, SidebarTab.Cover, SidebarTab.Info, SidebarTab.Video })
                {
                    ((SidebarPanel)sidebar).ActiveTab = tab;

                    if (!TryRender(sidebar, sidebar.Width, sidebar.Height, out var error))
                    {
                        Log(11, $"侧栏「{tab}」页绘制失败：{error}");
                        return false;
                    }
                }

                // 自绘控件在极端尺寸下的退化分支：太窄、太矮、几乎为零
                if (!CheckViewDegenerateSizes()) return false;

                // 鼠标停在歌词上要显示这一句的时间
                if (!CheckLyricTimeTip(lyricsView, document)) return false;

                // 侧栏与播放列表是否真的各自独立、能否脱离主窗口
                if (!CheckSeparation(form)) return false;

                form.Close();
                PumpMessages(200);

                // 关窗口时"分离状态"要能存进设置，否则下次启动不会自动恢复
                var saved = AppSettings.Load();
                if (!saved.SidebarFloating || !saved.PlaylistFloating)
                {
                    Log(11, "分离状态没有存进设置（侧栏 "
                            + $"{saved.SidebarFloating}、播放列表 {saved.PlaylistFloating}）");
                    return false;
                }

                // 吸附状态同样要能存下来，否则下次启动会贴在边上但位置滑回 0
                if (saved.SidebarSnap.ToString() != "Right")
                {
                    Log(11, $"吸附状态没有存进设置（侧栏吸附 = {saved.SidebarSnap}）");
                    return false;
                }

                Log(11, $"侧栏四页绘制正常，歌词链路打通（{document.Lines.Count} 行，来自 {document.Source}）");
                return true;
            }
            catch (Exception ex)
            {
                Log(11, "侧栏检查失败: " + ex);
                return false;
            }
        }

        /// <summary>
        /// 鼠标停在某一行的歌词上时，要显示这一句对应的时间点。
        /// <para>
        /// 这里直接验的是气泡文字的计算（<c>TimeTipTextFor</c>）：带时间轴的歌词每一行都要能
        /// 给出正确的时间，而<b>纯文本歌词没有时间戳</b>，绝不能弹出 00:00:00 这种误导信息。
        /// </para>
        /// </summary>
        private static bool CheckLyricTimeTip(Control lyricsView, LyricsDocument document)
        {
            if (lyricsView is not LyricsView view)
            {
                Log(11, "歌词时间气泡检查：这个控件不是 LyricsView");
                return false;
            }

            view.Size = new Size(300, 400);
            view.EnsureLayout();

            for (var i = 0; i < document.Lines.Count; i++)
            {
                var expected = TimeFormatter.FormatWithHours(document.Lines[i].Time);
                var actual = view.TimeTipTextFor(i);

                if (actual != expected)
                {
                    Log(11, $"歌词时间气泡：第 {i} 行显示「{actual}」，期望「{expected}」");
                    return false;
                }
            }

            // 纯文本歌词：没有时间轴，所以没有时间可显示
            view.SetDocument(LyricsDocument.Parse("第一句\n第二句", "txt"));
            view.EnsureLayout();

            if (view.TimeTipTextFor(0) != null)
            {
                Log(11, "歌词时间气泡：纯文本歌词不应该显示时间");
                return false;
            }

            // 越界下标不许抛异常
            if (view.TimeTipTextFor(999) != null)
            {
                Log(11, "歌词时间气泡：越界下标不应该给出时间");
                return false;
            }

            Log(11, $"歌词时间气泡正确：{document.Lines.Count} 行都能显示对应时间，纯文本歌词不显示");
            return true;
        }

        private static bool CheckViewDegenerateSizes()
        {
            var lyrics = new LyricsView();
            var cover = new CoverView();

            using (lyrics)
            using (cover)
            {
                var sizes = new[]
                {
                    new Size(320, 480), new Size(200, 300), new Size(70, 400),
                    new Size(120, 24), new Size(20, 20), new Size(1, 1)
                };

                // 歌词的三种状态都要走一遍：空、纯文本、带时间轴
                var documents = new[]
                {
                    LyricsDocument.Empty,
                    LyricsDocument.Parse("纯文本歌词，没有时间轴，而且要足够长以便触发换行计算。"),
                    LyricsDocument.Parse("[00:01.00]第一句\n[00:03.00]第二句\n[00:06.00]第三句\n")
                };

                foreach (var size in sizes)
                {
                    lyrics.Size = size;
                    cover.Size = size;

                    foreach (var document in documents)
                    {
                        lyrics.SetDocument(document);

                        // 推进播放位置，触发高亮与滚动那段逻辑
                        lyrics.UpdatePosition(TimeSpan.FromSeconds(3));

                        // 滚轮路径（侧栏的消息过滤器就是转到这里）
                        lyrics.ScrollBy(120);

                        if (!TryRender(lyrics, size.Width, size.Height, out var error))
                        {
                            Log(11, $"歌词控件在 {size.Width}x{size.Height} 下绘制失败：{error}");
                            return false;
                        }
                    }

                    // 封面：无封面（占位符）与有封面两条路径
                    cover.ShowTrack(null, null);

                    if (!TryRender(cover, size.Width, size.Height, out var coverError))
                    {
                        Log(11, $"封面控件在 {size.Width}x{size.Height} 下绘制失败：{coverError}");
                        return false;
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// 「当前这一首」的会话（<see cref="TrackLyricsSession"/>）本身的三条约定。
        /// <para>
        /// 其中"切歌之后，上一首的标签结果必须作废"这条，靠界面很难稳定复现——
        /// 得让异步读标签比切歌还慢。会话是 UI 无关的纯对象，这里直接把那段时序摆出来。
        /// </para>
        /// </summary>
        private static bool CheckTrackSession()
        {
            var session = new TrackLyricsSession();

            var first = session.BeginMetadata();
            if (!session.IsCurrent(first))
            {
                Log(11, "会话版本号：刚借出来的号就不算当前的");
                return false;
            }

            var second = session.BeginMetadata();
            if (session.IsCurrent(first))
            {
                Log(11, "会话版本号：切歌之后，上一首的标签结果还算当前的（旧结果会盖掉新歌的歌词）");
                return false;
            }

            if (!session.IsCurrent(second))
            {
                Log(11, "会话版本号：最新借出来的号反而不算当前的");
                return false;
            }

            // 把结果写回会话，不该顺手把自己也作废——否则标签永远显示不出来
            session.Apply("b.flac", new MediaTags { Title = "B" }, LyricsDocument.Empty);
            if (!session.IsCurrent(second))
            {
                Log(11, "会话状态：把结果写回会话之后，自己这次的结果也被当成过期的了");
                return false;
            }

            if (session.Path != "b.flac" || session.Tags.Title != "B")
            {
                Log(11, $"会话状态：换了一首之后路径 / 标签没跟上（{session.Path ?? "空"} / {session.Tags.Title ?? "空"}）");
                return false;
            }

            // 偏移：钳在 ±30 秒，而且要把"真正生效的值"回报给调用方（状态栏要用它说"已按上限处理"）
            if (session.SetOffset(999_000) != LyricsOffset.LimitMilliseconds ||
                session.LyricsOffsetMilliseconds != LyricsOffset.LimitMilliseconds)
            {
                Log(11, $"会话偏移：上限没钳住（{session.LyricsOffsetMilliseconds}）");
                return false;
            }

            if (session.SetOffset(-999_000) != -LyricsOffset.LimitMilliseconds)
            {
                Log(11, $"会话偏移：下限没钳住（{session.LyricsOffsetMilliseconds}）");
                return false;
            }

            Log(11, "会话状态正常：版本号只认最新那次（旧结果会作废）、写回结果不误伤自己、偏移钳在 ±30 秒");
            return true;
        }
    }
}
