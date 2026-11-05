// 检查点 24：播放业务与数据所有权（1.12.0 R15 / R16）。
//
// 这一轮把三块数据各自交给一个主人：
//   PlaylistWorkspace（列表 / 队列 / 排序 / 筛选）、MediaSession（当前曲目）、
//   HistoryService（历史与按文件调整）；把"放哪一首 / 播完怎么办"交给 PlaybackCoordinator。
// 这里验的是"主人真的管得住"：顺延、插播优先、末尾回绕、筛选大小写、会话版本号、历史落盘。

namespace SmokeTest
{
    internal static partial class Program
    {
        private static bool CheckPlaybackBusinessAndOwnership()
        {
            try
            {
                if (!CheckPlaylistWorkspace()) return false;
                if (!CheckMediaSession()) return false;
                if (!CheckHistoryService()) return false;
                if (!CheckPlaybackCoordinator()) return false;

                Log(24, "播放业务与所有权正常：列表 / 会话 / 历史各有唯一主人，"
                        + "顺延跳过打不开的项、插播队列优先、末尾「下一首」回绕、不循环时停在末尾");
                return true;
            }
            catch (Exception ex)
            {
                Log(24, "播放业务与所有权检查失败: " + ex);
                return false;
            }
        }

        /// <summary>列表 / 队列 / 排序 / 筛选都归 PlaylistWorkspace：筛选按 OrdinalIgnoreCase、排序写回设置。</summary>
        private static bool CheckPlaylistWorkspace()
        {
            var settings = new AppSettings();
            var workspace = new PlaylistWorkspace(settings);

            workspace.List.AddRange(new[]
            {
                @"C:\x\ID3-alpha.mp3",
                @"C:\x\beta.mp3"
            });

            workspace.SetFilter("id");

            if (!workspace.Matches(workspace.List.Items[0]) || workspace.Matches(workspace.List.Items[1]))
            {
                Log(24, "播放列表：筛选没有按 OrdinalIgnoreCase 匹配（搜 id 匹配不上 ID3）");
                return false;
            }

            workspace.SetFilter(string.Empty);

            // 排序：按名称升序 → 降序，并把依据写回设置
            workspace.List.AddRange(new[] { @"C:\x\gamma.mp3" });
            workspace.ApplySort(PlaylistSortField.Name, descending: false);

            if (!workspace.List.Items.Select(item => item.DisplayName).SequenceEqual(new[]
                {
                    "beta", "gamma", "ID3-alpha"
                }))
            {
                Log(24, "播放列表：按名称升序排完顺序不对（"
                        + string.Join(" / ", workspace.List.Items.Select(item => item.DisplayName)) + "）");
                return false;
            }

            if (workspace.SortField != PlaylistSortField.Name || settings.SortField != PlaylistSortField.Name)
            {
                Log(24, "播放列表：排序依据没有写回设置（下次启动会丢）");
                return false;
            }

            // 总时长：已知与未知分开算
            workspace.List.SetDuration(@"C:\x\beta.mp3", TimeSpan.FromSeconds(30));
            workspace.List.SetDuration(@"C:\x\gamma.mp3", TimeSpan.FromSeconds(10));

            var (total, known, unknown) = workspace.DurationSummary();

            if (known != 2 || unknown != 1 || Math.Abs(total.TotalSeconds - 40) > 0.5)
            {
                Log(24, $"播放列表：总时长汇总不对（已知 {known}、未知 {unknown}、共 {total}）");
                return false;
            }

            // 筛选之后只算可见的那几条
            var (filtered, filteredKnown, filteredUnknown) =
                workspace.DurationSummary(new[] { 0, 1 });

            if (filteredKnown != 2 || filteredUnknown != 0 || Math.Abs(filtered.TotalSeconds - 40) > 0.5)
            {
                Log(24, $"播放列表：筛选后的总时长不对（已知 {filteredKnown}、未知 {filteredUnknown}、共 {filtered}）");
                return false;
            }

            return true;
        }

        /// <summary>媒体会话只认最新那次异步读标签结果，偏移钳在 ±30 秒。</summary>
        private static bool CheckMediaSession()
        {
            var session = new MediaSession();

            var firstToken = session.BeginMetadata();
            session.SetPath(@"C:\x\first.mp3");

            if (!session.IsCurrent(firstToken))
            {
                Log(24, "媒体会话：刚借出去的版本号立刻就不算当前了");
                return false;
            }

            var secondToken = session.BeginMetadata();
            session.SetPath(@"C:\x\second.mp3");

            if (session.IsCurrent(firstToken))
            {
                Log(24, "媒体会话：切歌之后旧的读标签结果还算当前的");
                return false;
            }

            if (!session.IsCurrent(secondToken) || session.Path != @"C:\x\second.mp3")
            {
                Log(24, "媒体会话：切歌之后新那一份反而不算当前");
                return false;
            }

            var clamped = session.SetOffset(999_999);
            if (clamped != LyricsOffset.Clamp(999_999) || session.LyricsOffsetMilliseconds != clamped)
            {
                Log(24, $"媒体会话：歌词偏移没有钳制（设 {clamped} 毫秒）");
                return false;
            }

            session.Apply(null, null, null);

            if (session.HasTrack || !session.Lyrics.IsEmpty)
            {
                Log(24, "媒体会话：整首换掉之后还留着上一首的路径 / 歌词");
                return false;
            }

            return true;
        }

        /// <summary>历史的记 / 落盘 / 续播查询都归 HistoryService：关掉"记住位置"也照样记"听过"。</summary>
        private static bool CheckHistoryService()
        {
            var service = new HistoryService();
            var path = Path.Combine(AppContext.BaseDirectory, "ownership-history.wav");

            service.Commit(path, 30_000, 60_000, rememberPosition: true);

            if (!service.ResumePositionLookup(path).HasValue)
            {
                Log(24, "历史：记了位置却查不到续播点");
                return false;
            }

            if (service.History.TryGetResumePosition(path, out var position) &&
                Math.Abs(position.TotalSeconds - 30) > 1)
            {
                Log(24, $"历史：续播点不是记的那个（{position}）");
                return false;
            }

            // 关掉"记住位置"之后仍然要记"听过"（1.9.0 的 A4 规矩，收进服务之后也不能丢）
            var other = Path.Combine(AppContext.BaseDirectory, "ownership-history-other.wav");
            service.Tick(other, 0, 60_000, rememberPosition: false);
            service.Flush(other, 0, 60_000, rememberPosition: false);

            if (!service.History.Recent.Contains(other, StringComparer.OrdinalIgnoreCase))
            {
                Log(24, "历史：关掉「记住播放进度」之后播过的文件没有进最近播放");
                return false;
            }

            if (service.History.TryGetResumePosition(other, out _))
            {
                Log(24, "历史：关掉「记住播放进度」之后还是写了位置");
                return false;
            }

            return true;
        }

        /// <summary>播放业务：顺延、插播优先、末尾回绕、不循环时停在末尾。</summary>
        private static bool CheckPlaybackCoordinator()
        {
            var first = Path.Combine(AppContext.BaseDirectory, "business-1.wav");
            var second = Path.Combine(AppContext.BaseDirectory, "business-2.wav");
            var third = Path.Combine(AppContext.BaseDirectory, "business-3.wav");
            var missing1 = Path.Combine(AppContext.BaseDirectory, "missing-1.wav");
            var missing2 = Path.Combine(AppContext.BaseDirectory, "missing-2.wav");

            WriteWav(first, seconds: 8, frequency: 440);
            WriteWav(second, seconds: 8, frequency: 660);
            WriteWav(third, seconds: 8, frequency: 880);
            TryDelete(missing1);
            TryDelete(missing2);

            try
            {
                using var form = new 播放器.MainForm(Array.Empty<string>());
                form.Show();
                PumpMessages(200);

                form.Settings.RepeatMode = RepeatMode.None;
                form.Settings.Shuffle = false;

                // ---- 顺延：前两项打不开，应当落到第三项 ----
                form.Playlist.Clear();
                form.AddPathsForTest(new[] { missing1, missing2, first });

                if (form.Playlist.Count != 3)
                {
                    Log(24, $"播放业务：三项没有都进列表（现在 {form.Playlist.Count} 项）");
                    return false;
                }

                form.Playback.PlayIndex(0);

                if (!PumpUntil(
                        () => form.Engine.HasMedia &&
                              string.Equals(form.Engine.CurrentPath, first, StringComparison.OrdinalIgnoreCase),
                        15000))
                {
                    Log(24, "播放业务：列表前两项打不开时没有顺延到第三项（停在 "
                            + $"「{form.Engine.CurrentPath}」）");
                    return false;
                }

                if (!form.Playlist.Items[0].HasError || !form.Playlist.Items[1].HasError)
                {
                    Log(24, "播放业务：顺延时没有把打不开的那两项标出来");
                    return false;
                }

                // ---- 插播优先：队列里那一首先播，且不动列表当前项 ----
                form.Playlist.Clear();
                form.AddPathsForTest(new[] { first, second, third });
                form.Playback.PlayIndex(0);

                if (!PumpUntil(
                        () => form.Engine.HasMedia &&
                              string.Equals(form.Engine.CurrentPath, first, StringComparison.OrdinalIgnoreCase),
                        15000))
                {
                    Log(24, "播放业务：插播那一段的第一首没播起来");
                    return false;
                }

                var currentBefore = form.Playlist.CurrentIndex;
                form.Queue.Enqueue(third);
                form.Playback.HandleEnded();

                if (!PumpUntil(
                        () => string.Equals(form.Engine.CurrentPath, third, StringComparison.OrdinalIgnoreCase),
                        15000))
                {
                    Log(24, "播放业务：自然播完没有先播插播队列里的那一首（播了列表的下一项）");
                    return false;
                }

                if (form.Playlist.CurrentIndex != currentBefore)
                {
                    Log(24, "播放业务：插播时改动了列表的当前项（播完就回不到原来的位置了）");
                    return false;
                }

                // ---- 末尾「下一首」（用户点的）回绕到第一项 ----
                form.Playlist.Clear();
                form.AddPathsForTest(new[] { first, second });
                form.Playback.PlayIndex(1);

                if (!PumpUntil(
                        () => string.Equals(form.Engine.CurrentPath, second, StringComparison.OrdinalIgnoreCase),
                        15000))
                {
                    Log(24, "播放业务：回绕那一段的第二首没播起来");
                    return false;
                }

                form.Playback.PlayNext(userInitiated: true);

                if (!PumpUntil(
                        () => string.Equals(form.Engine.CurrentPath, first, StringComparison.OrdinalIgnoreCase),
                        15000))
                {
                    Log(24, "播放业务：列表末尾「下一首」（用户点的）没有回到第一项");
                    return false;
                }

                // ---- 不循环 + 自然播完：停在末尾，说清原因 ----
                form.Playlist.Clear();
                form.AddPathsForTest(new[] { first });
                form.Playback.PlayIndex(0);

                if (!PumpUntil(() => form.Engine.HasMedia, 15000))
                {
                    Log(24, "播放业务：停在末尾那一段没播起来");
                    return false;
                }

                form.Playback.PlayNext(userInitiated: false);

                if (!PumpUntil(() => !form.Engine.HasMedia, 8000))
                {
                    Log(24, "播放业务：不循环时列表末尾自然播完没有停下");
                    return false;
                }

                if (!form.StatusText.Contains("播放列表已结束", StringComparison.Ordinal))
                {
                    Log(24, "播放业务：停在末尾了，但状态栏没说「播放列表已结束」（现在「"
                            + form.StatusText + "」）");
                    return false;
                }

                return true;
            }
            finally
            {
                TryDelete(first);
                TryDelete(second);
                TryDelete(third);
            }
        }
    }
}
