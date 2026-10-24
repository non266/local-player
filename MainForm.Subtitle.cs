using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using 播放器.Core;

namespace 播放器
{
    /// <summary>字幕与轨道：选字幕文件、关字幕，以及音视频轨菜单。</summary>
    public partial class MainForm : Form
    {
        // =====================================================================
        // F3：按文件记住音轨 / 字幕轨
        //
        // 挂起点是 PlayIndex（换到这一首时 PrepareTrackRestore）；libvlc 的轨道清单要等媒体解析
        // 出来才有，所以每次 RebuildTrackMenus（TracksChanged 会调）都试一次：只在这个路径还是
        // "刚打开的那一首"时试；用户手动选过、或两样都满足之后就把挂起清掉——免得外部字幕加载、
        // 切歌之后又被"记忆"盖回去。
        // =====================================================================

        private string? _trackRestorePath;

        private bool _restoringTracks;

        /// <summary>上一次开着的字幕轨 id（V 关掉之后再按一次要回到它）。</summary>
        private int? _lastSubtitleTrackId;

        /// <summary>换到这一首时挂起"恢复上次选的音轨 / 字幕轨"（由 <see cref="RestoreTracksForCurrentMedia"/> 消费）。</summary>
        private void PrepareTrackRestore(string? path) => _trackRestorePath = path;

        private void OpenSubtitleDialog()
        {
            if (!_engine.HasMedia)
            {
                SetStatus("请先播放一个视频，再加载字幕");
                return;
            }

            using (var dialog = new OpenFileDialog
            {
                Title = "加载字幕文件",
                Filter = MediaFormats.SubtitleDialogFilter,
                InitialDirectory = Directory.Exists(_settings.LastDirectory) ? _settings.LastDirectory : string.Empty
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                if (_engine.AddSubtitleFile(dialog.FileName))
                {
                    // 用户刚手动加了外挂字幕：别让"上次记住的嵌入式字幕轨"把它盖回去
                    _trackRestorePath = null;
                    SetStatus("已加载字幕：" + Path.GetFileName(dialog.FileName));
                }
                else
                {
                    SetStatus("字幕加载失败：" + Path.GetFileName(dialog.FileName));
                }
            }
        }

        /// <summary>
        /// V /「字幕 → 关闭字幕」：开着就关，关着就打开上一次那条（1.8.0 修：关了之后打不开）。
        /// <para>
        /// 这一下**不写记忆**：它是个临时开关（"探头看一眼画面"）；要"这部片子以后都关"，
        /// 请用「字幕轨道 → 关闭字幕」，那条才记 <c>-1</c>。
        /// </para>
        /// </summary>
        internal void ToggleSubtitles()
        {
            if (!_engine.HasMedia)
            {
                SetStatus("请先播放一个视频，再切换字幕");
                return;
            }

            var current = _engine.CurrentSubtitleTrack;

            if (current >= 0)
            {
                _lastSubtitleTrackId = current;
                _engine.DisableSubtitle();
                SetStatus("已关闭字幕（再按 V 打开）");
                UpdateSubtitleToggleText();
                return;
            }

            var tracks = _engine.GetSubtitleTracks().Where(track => track.Id >= 0).ToList();

            if (tracks.Count == 0)
            {
                SetStatus("这个视频没有字幕轨");
                return;
            }

            var target = _lastSubtitleTrackId is int id && tracks.Any(track => track.Id == id)
                ? id
                : tracks[0].Id;

            _engine.SetSubtitleTrack(target);
            _lastSubtitleTrackId = target;
            SetStatus("字幕已打开（按 V 关闭）");
            UpdateSubtitleToggleText();
        }

        /// <summary>
        /// 「字幕」菜单第一项的文字跟着开 / 关变。
        /// <para>
        /// 1.8.0 修：它原来一直写着「关闭字幕」——关掉之后这一项按下去还是"关"，用户以为坏了。
        /// </para>
        /// </summary>
        internal void UpdateSubtitleToggleText()
        {
            if (menuSubtitleDisable == null) return;

            var open = _engine.HasMedia && _engine.CurrentSubtitleTrack >= 0;
            menuSubtitleDisable.Text = open ? "关闭字幕(&C)" : "打开字幕(&C)";
        }

        private void ChooseAudioTrack(int id)
        {
            if (!_engine.HasMedia) return;

            _engine.SetAudioTrack(id);
            RememberTracks(chosenAudio: id);
            SetStatus("音轨：已记住（下次打开这部片子自动选回）");
        }

        private void ChooseSubtitleTrack(int id)
        {
            if (!_engine.HasMedia) return;

            _engine.SetSubtitleTrack(id);
            if (id >= 0) _lastSubtitleTrackId = id;
            RememberTracks(chosenSubtitle: id);
            SetStatus("字幕轨：已记住（下次打开这部片子自动选回）");
            UpdateSubtitleToggleText();
        }

        private void ChooseNoSubtitle()
        {
            if (!_engine.HasMedia) return;

            _engine.DisableSubtitle();
            RememberTracks(chosenSubtitle: -1);
            SetStatus("已关闭字幕；这部片子下次打开也会是关的");
            UpdateSubtitleToggleText();
        }

        /// <summary>
        /// 用户手动选过轨道：记下**被点的那一类**，另一类保留原来的记忆（不拿"当前值"去覆盖它——
        /// libvlc 打开媒体时可能自动选了一条字幕，那不代表用户的意愿）。
        /// <para>网络流不记（没有稳定的"同一个文件"，和延迟 / 旋转同一套规则）。</para>
        /// </summary>
        private void RememberTracks(int? chosenAudio = null, int? chosenSubtitle = null)
        {
            var path = _track.Path;

            if (string.IsNullOrWhiteSpace(path) || MediaFormats.IsStreamUri(path)) return;

            // 用户已经手动接管：把挂起的自动恢复清掉，别再盖回来
            _trackRestorePath = null;

            var saved = Adjustments.Get(path);

            // libvlc 的音轨清单里有一条 «Disable»（id = -1）：那不算"记住一条音轨"
            var audio = chosenAudio is int id && id < 0 ? null : chosenAudio ?? saved.AudioTrackId;

            Adjustments.RecordTracks(path, audio, chosenSubtitle ?? saved.SubtitleTrackId);
        }

        /// <summary>
        /// 把记住的音轨 / 字幕轨设回引擎（没有挂起、路径不对、没记过时什么都不做）。
        /// <para>字幕 <c>-1</c> = 用户特意关掉：恢复时也要真的关掉，而不是"当成没记过"。</para>
        /// </summary>
        private void RestoreTracksForCurrentMedia()
        {
            if (_trackRestorePath == null) return;

            var path = _track.Path;

            if (!string.Equals(path, _trackRestorePath, StringComparison.OrdinalIgnoreCase))
            {
                _trackRestorePath = null;
                return;
            }

            var saved = Adjustments.Get(path);

            if (saved.AudioTrackId == null && saved.SubtitleTrackId == null)
            {
                _trackRestorePath = null;
                return;
            }

            if (!_engine.HasMedia) return;

            var audioTracks = _engine.GetAudioTracks();
            var subtitleTracks = _engine.GetSubtitleTracks();

            var audioDone = saved.AudioTrackId == null;
            var subtitleDone = saved.SubtitleTrackId == null;
            var applied = false;

            _restoringTracks = true;

            try
            {
                if (!audioDone)
                {
                    var id = saved.AudioTrackId!.Value;

                    if (id >= 0 && audioTracks.Any(track => track.Id == id))
                    {
                        if (_engine.CurrentAudioTrack != id)
                        {
                            _engine.SetAudioTrack(id);
                            applied = true;
                        }

                        audioDone = _engine.CurrentAudioTrack == id;
                    }
                    else if (audioTracks.Count > 0)
                    {
                        // 这个文件里没有那条轨道（可能换过压制版本）：恢复不了，别无限重试
                        audioDone = true;
                        AppLog.Info($"音轨记忆：{path} 里已经没有上次选的音轨 {id}，保持默认");
                    }
                }

                if (!subtitleDone)
                {
                    var id = saved.SubtitleTrackId!.Value;

                    if (id == -1)
                    {
                        if (subtitleTracks.Count > 0 && _engine.CurrentSubtitleTrack != -1)
                        {
                            _engine.DisableSubtitle();
                            applied = true;
                        }

                        subtitleDone = subtitleTracks.Count == 0 || _engine.CurrentSubtitleTrack == -1;
                    }
                    else if (subtitleTracks.Any(track => track.Id == id))
                    {
                        if (_engine.CurrentSubtitleTrack != id)
                        {
                            _engine.SetSubtitleTrack(id);
                            applied = true;
                        }

                        subtitleDone = _engine.CurrentSubtitleTrack == id;
                    }
                    else if (subtitleTracks.Count > 0)
                    {
                        subtitleDone = true;
                        AppLog.Info($"字幕记忆：{path} 里已经没有上次选的字幕轨 {id}，保持默认");
                    }
                }
            }
            finally
            {
                _restoringTracks = false;
            }

            if (applied) AppLog.Info($"已恢复上次选的音轨 / 字幕轨：{path}");

            if (audioDone && subtitleDone) _trackRestorePath = null;
        }

        private void RebuildTrackMenus()
        {
            // 恢复轨道会改引擎状态、并可能触发一次 TracksChanged：那一次让外层继续把菜单建完，
            // 这样菜单里的勾选状态才是"恢复之后"的。
            if (_restoringTracks) return;

            RestoreTracksForCurrentMedia();

            FillSubtitleTrackMenu(menuSubtitleTracks.DropDownItems);
            FillAudioTrackMenu(menuViewAudioTrack.DropDownItems);

            UpdateSubtitleToggleText();

            // 章节列表同样依赖"媒体已经被解析"，和轨道一起重建
            RebuildChapterMenu();
        }

        /// <summary>
        /// 把「字幕轨道」菜单填一遍（主菜单和画面右键菜单共用这一份，两边不会走偏）。
        /// <para>
        /// libvlc 的轨道清单里有一条 <c>«Disable»</c>（id = -1）：字幕这边它就是"关字幕"
        /// （我们自己有一个），音轨那边它是"静音一切"——都不该当成可选轨道列出来
        /// （1.8.0 修：以前它会作为一个叫 <c>Disable</c> 的项出现在列表最上面，点它像是没反应）。
        /// </para>
        /// </summary>
        internal void FillSubtitleTrackMenu(ToolStripItemCollection items)
        {
            ClearMenuItems(items);

            var current = _engine.HasMedia ? _engine.CurrentSubtitleTrack : -1;

            if (current >= 0) _lastSubtitleTrackId = current;

            var disableItem = new ToolStripMenuItem("关闭字幕") { Checked = current < 0 };
            disableItem.Click += (s, e) => ChooseNoSubtitle();
            items.Add(disableItem);

            var tracks = (_engine.HasMedia
                    ? _engine.GetSubtitleTracks()
                    : Array.Empty<LibVLCSharp.Shared.Structures.TrackDescription>())
                .Where(track => track.Id >= 0)
                .ToList();

            if (tracks.Count == 0)
            {
                items.Add(new ToolStripMenuItem("（无可用字幕轨道）") { Enabled = false });
                return;
            }

            items.Add(new ToolStripSeparator());

            foreach (var track in tracks)
            {
                var id = track.Id;
                var item = new ToolStripMenuItem(DescribeTrack(track.Name, "字幕", id))
                {
                    Checked = id == current,
                    Tag = id
                };
                item.Click += (s, e) => ChooseSubtitleTrack(id);
                items.Add(item);
            }
        }

        /// <summary>把「音轨」菜单填一遍（主菜单和画面右键菜单共用）。</summary>
        internal void FillAudioTrackMenu(ToolStripItemCollection items)
        {
            ClearMenuItems(items);

            var current = _engine.HasMedia ? _engine.CurrentAudioTrack : -1;
            var tracks = (_engine.HasMedia
                    ? _engine.GetAudioTracks()
                    : Array.Empty<LibVLCSharp.Shared.Structures.TrackDescription>())
                .Where(track => track.Id >= 0)
                .ToList();

            if (tracks.Count == 0)
            {
                items.Add(new ToolStripMenuItem("（无可用音轨）") { Enabled = false });
                return;
            }

            foreach (var track in tracks)
            {
                var id = track.Id;
                var item = new ToolStripMenuItem(DescribeTrack(track.Name, "音轨", id))
                {
                    Checked = id == current,
                    Tag = id
                };
                item.Click += (s, e) => ChooseAudioTrack(id);
                items.Add(item);
            }
        }

        private static string DescribeTrack(string? name, string category, int id) =>
            string.IsNullOrWhiteSpace(name) ? $"{category} {id}" : name!;
    }
}
