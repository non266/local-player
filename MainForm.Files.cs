using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using 播放器.Core;
using 播放器.Ui;

namespace 播放器
{
    /// <summary>文件与画面：打开文件 / 文件夹 / 串流、保存与载入播放列表、截图、拖放接收。</summary>
    public partial class MainForm : Form
    {
        // =====================================================================
        // 截图
        // =====================================================================
        private void TakeSnapshot()
        {
            if (!_engine.HasMedia || !_engine.IsPlaying)
            {
                SetStatus("只有在播放画面时才能截图");
                return;
            }

            // MyPictures 有可能解析成空串（某些精简系统 / 未登录用户配置），
            // 那样 Path.Combine 会得到一个相对路径，截图会落到"当前工作目录"下，
            // 用户根本找不到。这里显式兜一个绝对目录。
            var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (string.IsNullOrWhiteSpace(pictures))
                pictures = AppSettings.SettingsDirectory;

            var folder = Path.Combine(pictures, "播放器截图");

            // 文件名的时间戳固定用不变文化：默认插值走当前区域设置，
            // 泰国佛历会把年份写成 2567、阿拉伯语区会写成回历年份，
            // 于是文件名和文件的真实时间对不上。
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            var path = Path.Combine(folder, $"snapshot_{stamp}.png");

            SetStatus(_engine.TakeSnapshot(path) ? "截图已保存：" + path : "截图失败");
        }

        // =====================================================================
        // 打开 / 保存
        // =====================================================================
        private void OpenFilesDialog()
        {
            using (var dialog = new OpenFileDialog
            {
                Title = "打开媒体文件",
                Filter = MediaFormats.OpenFileDialogFilter,
                Multiselect = true,
                InitialDirectory = Directory.Exists(_settings.LastDirectory) ? _settings.LastDirectory : string.Empty
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                RememberDirectory(dialog.FileNames.FirstOrDefault());
                AddPathsWithFolders(dialog.FileNames, AddPlayback.IfIdle);
            }
        }

        private void OpenFolderDialog()
        {
            using (var dialog = new FolderBrowserDialog
            {
                Description = "选择包含媒体文件的文件夹（会递归扫描子文件夹）",
                ShowNewFolderButton = false,
                SelectedPath = Directory.Exists(_settings.LastDirectory) ? _settings.LastDirectory : string.Empty
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                _settings.LastDirectory = dialog.SelectedPath;
                AddPathsWithFolders(new[] { dialog.SelectedPath }, AddPlayback.IfIdle);
            }
        }

        private void OpenUrlDialog()
        {
            var url = InputDialog.Show(this, "打开网络串流",
                "请输入媒体地址（http / https / rtsp / rtmp / udp 等）：", "http://");

            if (string.IsNullOrWhiteSpace(url)) return;

            AddPathsWithFolders(new[] { url }, AddPlayback.Always);
        }

        /// <summary>
        /// 「另存为 m3u 文件…」（Ctrl+Shift+S）：把当前列表写成一个文件，放在用户自己挑的地方。
        /// <para>
        /// 和「保存当前列表为歌单…」（Ctrl+S）的分工：那个存进歌单库（固定目录、有名字、菜单里一行一个），
        /// 这个存到任意位置——给别人、拷到 U 盘、放进别的播放器。两边都是标准 m3u，互相都认得。
        /// </para>
        /// </summary>
        private void ExportPlaylistDialog()
        {
            if (_playlist.Count == 0)
            {
                SetStatus("播放列表为空，没有可保存的内容");
                return;
            }

            using (var dialog = new SaveFileDialog
            {
                Title = "另存为 m3u 播放列表",
                Filter = MediaFormats.PlaylistDialogFilter,
                DefaultExt = "m3u",
                FileName = SuggestPlaylistFileName(),
                InitialDirectory = Directory.Exists(_settings.LastDirectory) ? _settings.LastDirectory : string.Empty
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                SavePlaylistToFile(dialog.FileName);
            }
        }

        /// <summary>
        /// 另存为对话框里的默认文件名：当前对应着歌单就用歌单名，省得每次都去改「播放列表.m3u」。
        /// <para>歌单名能落成文件名（<c>PlaylistLibrary.TryNormalizeName</c> 就是按这条校验的），
        /// 所以这里不用再洗一遍。</para>
        /// </summary>
        private string SuggestPlaylistFileName() =>
            _playlists.CurrentName.Length > 0 ? _playlists.CurrentName + ".m3u" : "播放列表.m3u";

        /// <summary>
        /// 把当前列表写成 m3u 文件（<b>不弹对话框</b>）；菜单和 Ctrl+Shift+S 走
        /// <see cref="ExportPlaylistDialog"/>，自动化测试直接调这里。
        /// </summary>
        internal bool SavePlaylistToFile(string path)
        {
            try
            {
                PlaylistFile.Save(path, _playlist.Items);

                _settings.LastDirectory = Path.GetDirectoryName(path) ?? _settings.LastDirectory;
                SetStatus($"播放列表已另存为：{path}（{_playlist.Count} 项）");
                return true;
            }
            catch (Exception ex)
            {
                ShowError("保存播放列表失败", ex);
                return false;
            }
        }

        /// <summary>
        /// 「从 m3u 文件追加 / 替换当前列表…」：选一个 m3u（或 m3u8）把里面的条目读进来。
        /// <para>
        /// 追加和替换分成两个菜单项：同一件事两种语义，只写「载入」没人分得清是哪种。
        /// </para>
        /// </summary>
        private void OpenPlaylistFileDialog(bool replace)
        {
            using (var dialog = new OpenFileDialog
            {
                Title = replace ? "从 m3u 文件替换当前列表" : "从 m3u 文件追加到当前列表",
                Filter = MediaFormats.PlaylistDialogFilter,
                InitialDirectory = Directory.Exists(_settings.LastDirectory) ? _settings.LastDirectory : string.Empty
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                LoadPlaylistFromFile(dialog.FileName, replace);
            }
        }

        /// <summary>
        /// 载入一个 m3u 文件（<b>不弹选择框</b>）。
        /// <para>
        /// <paramref name="replace"/> 为真时整个替换当前列表，替换前按和歌单载入<b>同一套规则</b>问一句
        /// （<see cref="ConfirmReplacePlaylist"/>：有未保存的改动就给你"先保存"的机会，
        /// 攒起来还没存过的列表也问一句）。
        /// </para>
        /// <para>
        /// 替换的是一份"外来的"m3u——它不在歌单库里，所以列表不能算成某份歌单的干净副本：
        /// 当前歌单的关联留着，内容标成「未保存」。想把它收进歌单库，接着点「覆盖保存」就行；
        /// 不想要就点「重新载入」退回原来那份。
        /// </para>
        /// </summary>
        internal bool LoadPlaylistFromFile(string path, bool replace)
        {
            var files = PlaylistFile.Load(path);

            if (files.Count == 0)
            {
                SetStatus($"这个 m3u 里没有找到可用的媒体文件：{path}");
                return false;
            }

            // 先问再动列表：回答"取消"时磁盘上的东西一个都没少
            if (replace && !ConfirmReplacePlaylist(Path.GetFileName(path))) return false;

            _settings.LastDirectory = Path.GetDirectoryName(path) ?? _settings.LastDirectory;

            if (!replace)
            {
                // 追加是刻意不去重的：同一个文件在列表里出现两次是合法的用法
                // （同一段听两遍），要合并得用歌单窗口里的「追加」（那边会去重）。
                _playlist.AddRange(files);
                _scanner.Enqueue(files);

                SetStatus($"已从这个 m3u 追加 {files.Count} 项（当前共 {_playlist.Count} 项）：{path}");
                return true;
            }

            // 替换列表时要停播：列表里已经没有正在放的那一项了，
            // 不停会出现"列表空着但还在响"的矛盾状态（和载入歌单同样的处理）。
            StopPlayback();
            _scanner.ClearPending();

            _playlist.Clear();
            _playlist.AddRange(files);

            // 文件里的顺序就是用户存的顺序，载入后按它显示（与载入歌单一致）
            ResetSortToAddedOrder();

            ClearPlaylistFilter();
            _scanner.Enqueue(files);

            SetCurrentPlaylist(_playlists.CurrentName, _playlists.CurrentName.Length > 0);
            UpdateWindowTitle();
            UpdateTransportState();

            SetStatus($"已用这个 m3u 替换当前列表：{files.Count} 项（{path}）");
            return true;
        }

        private void RememberDirectory(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return;

            try
            {
                var folder = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                    _settings.LastDirectory = folder;
            }
            catch (ArgumentException)
            {
                // 网络流地址等没有目录信息，忽略。
            }
        }

        // =====================================================================
        // 拖放
        // =====================================================================
        private void OnDragEnter(object? sender, DragEventArgs e)
        {
            // 列表项换位走的是另一套拖放，别用文件拖放的 Copy 效果。
            if (_draggingRow)
            {
                e.Effect = DragDropEffects.Move;
                return;
            }

            if (e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop))
                e.Effect = DragDropEffects.Copy;
        }

        private void OnDragDrop(object? sender, DragEventArgs e)
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0)
                return;

            RememberDirectory(paths.FirstOrDefault(p => File.Exists(p)));

            // 拖进来的可能是文件夹，展开走后台，别把窗口卡住。
            AddPathsWithFolders(paths, AddPlayback.IfIdle);
        }
    }
}
