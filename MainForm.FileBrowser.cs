using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using 播放器.Core;
using 播放器.Ui;

namespace 播放器
{
    /// <summary>
    /// 文件浏览器（1.24.0）：侧栏第 5 页与「视图」菜单的接线。
    /// <para>
    /// 这一层只做"翻译"：把文件页发出来的动作接到现有的
    /// <see cref="MainForm.AddPathsWithFolders"/> / <see cref="MainForm.AddPathsToPlaylist"/> /
    /// <see cref="MainForm.PlayIndex(int)"/> / <see cref="PlaybackQueue"/> 上。
    /// 文件页不认识 VLC，播放列表也不认识文件页。
    /// </para>
    /// <para>分离窗口那一半在 <c>MainForm.Floating.FileBrowser.cs</c>。</para>
    /// </summary>
    public partial class MainForm
    {
        private ToolStripMenuItem? _menuViewFileBrowser;
        private ToolStripMenuItem? _menuViewFileBrowserFloating;

        /// <summary>只读入口：文件页（给冒烟测试用）。</summary>
        internal FileBrowserView FileBrowser => sidebarPanel.FileBrowser;

        internal ToolStripMenuItem? MenuViewFileBrowser => _menuViewFileBrowser;

        internal ToolStripMenuItem? MenuViewFileBrowserFloating => _menuViewFileBrowserFloating;

        /// <summary>把设置里的文件页状态装到视图上，并接上播放 / 添加 / 下一首三个动作。</summary>
        internal void InitializeFileBrowser()
        {
            var view = sidebarPanel.FileBrowser;

            view.RestoreState(FileBrowserState.FromSettings(_settings));

            view.PlayRequested += (s, paths) => PlayFileBrowserPaths(paths);
            view.AddRequested += (s, paths) => AddFileBrowserPaths(paths);
            view.QueueRequested += (s, paths) => QueueFileBrowserPaths(paths);
            view.PlayFolderRequested += (s, folder) => PlayFileBrowserFolder(folder);
            view.StatusRequested += (s, text) => SetStatus(text);
            view.StateChanged += (s, e) => SyncFileBrowserSettings();
            view.RecentCleared += (s, e) => SaveFileBrowserSettingsNow();

            sidebarPanel.FileBrowserDockRequested += (s, e) => SetFileBrowserFloating(false);
        }

        /// <summary>「视图」菜单里的两项：打开文件页 / 把它分离成独立窗口。</summary>
        internal void ConfigureFileBrowserMenu()
        {
            if (_menuViewFileBrowser != null) return;

            _menuViewFileBrowser = new ToolStripMenuItem("文件浏览器(&B)");
            _menuViewFileBrowser.Click += (s, e) => ShowFileBrowser();

            _menuViewFileBrowserFloating = new ToolStripMenuItem("文件浏览器分离为独立窗口")
            {
                CheckOnClick = true
            };
            _menuViewFileBrowserFloating.Click += (s, e) =>
                SetFileBrowserFloating(_menuViewFileBrowserFloating.Checked);

            var index = _menuViewSidebarFloating == null
                ? -1
                : menuView.DropDownItems.IndexOf(_menuViewSidebarFloating);

            if (index < 0)
            {
                menuView.DropDownItems.Add(_menuViewFileBrowser);
                menuView.DropDownItems.Add(_menuViewFileBrowserFloating);
                return;
            }

            menuView.DropDownItems.Insert(index + 1, _menuViewFileBrowser);
            menuView.DropDownItems.Insert(index + 2, _menuViewFileBrowserFloating);
        }

        /// <summary>「文件浏览器」菜单：有独立窗口就亮出来，否则打开侧栏并切到文件页。</summary>
        private void ShowFileBrowser()
        {
            if (_settings.FileBrowserFloating)
            {
                if (_fileBrowserWindow == null) return;

                if (!_fileBrowserWindow.Visible) _fileBrowserWindow.Show();

                _fileBrowserWindow.Activate();
                return;
            }

            if (IsAudioWindow || IsMiniWindow)
            {
                SetStatus("音频窗口 / 迷你模式里没有侧栏；按 Ctrl+Alt+M 或 Ctrl+Alt+N 回视频窗口");
                return;
            }

            _settings.ShowSidebar = true;
            ApplySidebarVisibility(true);
            sidebarPanel.ActiveTab = SidebarTab.Files;
            sidebarPanel.FileBrowser.EnsureLoaded();
        }

        /// <summary>双击文件 / 「播放」：已在列表里就直接播，不在就先加入再播。</summary>
        internal void PlayFileBrowserPaths(IReadOnlyList<string> paths)
        {
            var list = CleanPaths(paths);
            if (list.Count == 0) return;

            var media = new List<string>();
            var subtitles = new List<string>();
            var playlists = new List<string>();

            foreach (var path in list)
            {
                if (MediaFormats.IsSubtitleFile(path)) subtitles.Add(path);
                else if (MediaFormats.IsPlaylistFile(path)) playlists.Add(path);
                else media.Add(path);
            }

            var indices = new List<int>();
            var missing = new List<string>();

            foreach (var path in media)
            {
                var index = _playlist.IndexOfPath(path);

                if (index >= 0) indices.Add(index);
                else missing.Add(path);
            }

            if (missing.Count > 0)
                indices.AddRange(AddPathsToPlaylist(missing, distinct: true));

            foreach (var playlist in playlists)
                LoadPlaylistFromFile(playlist, replace: false);

            if (subtitles.Count > 0)
            {
                if (_engine.HasMedia)
                {
                    foreach (var subtitle in subtitles)
                        _engine.AddSubtitleFile(subtitle);

                    SetStatus("已加载字幕：" + System.IO.Path.GetFileName(subtitles[subtitles.Count - 1]));
                }
                else
                {
                    SetStatus("请先播放一个视频，再加载字幕文件");
                }
            }

            if (indices.Count > 0)
            {
                PlayIndex(indices[0]);
                return;
            }

            if (media.Count == 0 && playlists.Count == 0 && subtitles.Count == 0)
                SetStatus("没有可播放的文件");
        }

        /// <summary>「添加到播放列表」：文件夹递归、网络位置交给现有后台、字幕挂当前视频。</summary>
        internal void AddFileBrowserPaths(IReadOnlyList<string> paths)
        {
            var list = CleanPaths(paths);
            if (list.Count == 0) return;

            AddPathsWithFolders(list, AddPlayback.None, boundedLocalFolders: true);
        }

        /// <summary>「播放整个文件夹」：递归扫描后播第一条。</summary>
        internal void PlayFileBrowserFolder(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return;

            AddPathsWithFolders(new[] { folder }, AddPlayback.Always, boundedLocalFolders: true);
        }

        /// <summary>「下一首播放」：只收媒体文件（字幕 / m3u 排进播放队列没有意义）。</summary>
        internal void QueueFileBrowserPaths(IReadOnlyList<string> paths)
        {
            var media = CleanPaths(paths)
                .Where(MediaFormats.IsMediaFile)
                .ToList();

            if (media.Count == 0)
            {
                SetStatus("选中的项里没有可播放的媒体文件");
                return;
            }

            QueuePathsForNext(media);
        }

        /// <summary>
        /// 冒烟钩子：把一次按键按"焦点在文件列表里"喂给 <see cref="ProcessCmdKey"/>。
        /// 真实按键的 <c>Message.HWnd</c> 就是列表句柄；测试不合成鼠标 / 键盘消息，
        /// 但也不能绕过那层守卫——所以这里只补一个句柄。
        /// </summary>
        internal bool ProcessFileBrowserShortcutForTest(Keys keyData)
        {
            var message = new Message { HWnd = sidebarPanel.FileBrowser.List.Handle };
            return ProcessCmdKey(ref message, keyData);
        }

        /// <summary>把视图状态同步进当前设置对象（不落盘；退出时统一写）。</summary>
        internal void SyncFileBrowserSettings()
        {
            sidebarPanel.FileBrowser.State.ApplyTo(_settings);
        }

        /// <summary>「清除最近浏览」是用户明确要求的清除动作，当场落盘。</summary>
        private void SaveFileBrowserSettingsNow()
        {
            SyncFileBrowserSettings();

            try
            {
                _settings.Save();
            }
            catch (Exception ex)
            {
                ShowError("保存设置失败", ex);
            }
        }

        /// <summary>载入设置方案之后把文件页状态换过来（目录变了会重新读）。</summary>
        internal void ApplyFileBrowserSettings()
        {
            sidebarPanel.FileBrowser.RestoreState(FileBrowserState.FromSettings(_settings));

            if (_menuViewFileBrowserFloating != null)
                _menuViewFileBrowserFloating.Checked = _settings.FileBrowserFloating;

            UpdateFileBrowserMenuState();
        }

        /// <summary>简化形态里文件页属于侧栏那一栏；已分离的窗口独立存在，仍然可用。</summary>
        internal void UpdateFileBrowserMenuState()
        {
            var simplified = IsAudioWindow || IsMiniWindow;
            var available = !simplified || _settings.FileBrowserFloating;

            if (_menuViewFileBrowser != null) _menuViewFileBrowser.Enabled = available;

            if (_menuViewFileBrowserFloating != null)
            {
                _menuViewFileBrowserFloating.Enabled = available;
                _menuViewFileBrowserFloating.Checked = _settings.FileBrowserFloating;
            }
        }

        private static List<string> CleanPaths(IReadOnlyList<string> paths)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (paths == null) return result;

            foreach (var raw in paths)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;

                var path = raw.Trim();
                if (!seen.Add(path)) continue;

                result.Add(path);
            }

            return result;
        }
    }
}
