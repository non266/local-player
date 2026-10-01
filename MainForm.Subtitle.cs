using System;
using System.IO;
using System.Windows.Forms;
using 播放器.Core;

namespace 播放器
{
    /// <summary>字幕与轨道：选字幕文件、关字幕，以及音视频轨菜单。</summary>
    public partial class MainForm : Form
    {
        // =====================================================================
        // 字幕
        // =====================================================================
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
                    SetStatus("已加载字幕：" + Path.GetFileName(dialog.FileName));
                else
                    SetStatus("字幕加载失败：" + Path.GetFileName(dialog.FileName));
            }
        }

        private void DisableSubtitle()
        {
            if (!_engine.HasMedia) return;
            _engine.DisableSubtitle();
            SetStatus("已关闭字幕");
        }

        private void RebuildTrackMenus()
        {
            // ---- 字幕轨道 ----
            ClearMenuItems(menuSubtitleTracks.DropDownItems);

            var currentSubtitle = _engine.HasMedia ? _engine.CurrentSubtitleTrack : -1;
            var disableItem = new ToolStripMenuItem("关闭字幕") { Checked = currentSubtitle < 0 };
            disableItem.Click += (s, e) => DisableSubtitle();
            menuSubtitleTracks.DropDownItems.Add(disableItem);

            var subtitleTracks = _engine.HasMedia
                ? _engine.GetSubtitleTracks()
                : Array.Empty<LibVLCSharp.Shared.Structures.TrackDescription>();

            if (subtitleTracks.Count > 0)
            {
                menuSubtitleTracks.DropDownItems.Add(new ToolStripSeparator());
                foreach (var track in subtitleTracks)
                {
                    var id = track.Id;
                    var item = new ToolStripMenuItem(DescribeTrack(track.Name, "字幕", id))
                    {
                        Checked = id == currentSubtitle
                    };
                    item.Click += (s, e) => _engine.SetSubtitleTrack(id);
                    menuSubtitleTracks.DropDownItems.Add(item);
                }
            }
            else
            {
                menuSubtitleTracks.DropDownItems.Add(new ToolStripMenuItem("（无可用字幕轨道）") { Enabled = false });
            }

            // ---- 音轨 ----
            ClearMenuItems(menuViewAudioTrack.DropDownItems);

            var currentAudio = _engine.HasMedia ? _engine.CurrentAudioTrack : -1;
            var audioTracks = _engine.HasMedia
                ? _engine.GetAudioTracks()
                : Array.Empty<LibVLCSharp.Shared.Structures.TrackDescription>();

            if (audioTracks.Count == 0)
            {
                menuViewAudioTrack.DropDownItems.Add(
                    new ToolStripMenuItem("（无可用音轨）") { Enabled = false });
            }
            else
            {
                foreach (var track in audioTracks)
                {
                    var id = track.Id;
                    var item = new ToolStripMenuItem(DescribeTrack(track.Name, "音轨", id))
                    {
                        Checked = id == currentAudio
                    };
                    item.Click += (s, e) => _engine.SetAudioTrack(id);
                    menuViewAudioTrack.DropDownItems.Add(item);
                }
            }

            // 章节列表同样依赖"媒体已经被解析"，和轨道一起重建
            RebuildChapterMenu();
        }

        private static string DescribeTrack(string? name, string category, int id) =>
            string.IsNullOrWhiteSpace(name) ? $"{category} {id}" : name!;
    }
}
