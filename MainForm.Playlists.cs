using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using 播放器.Core;
using 播放器.Ui;

namespace 播放器
{
    /// <summary>
    /// 主窗口的「播放列表」菜单：<b>一个入口管"列表存哪儿、从哪儿来"</b>。
    /// <para>
    /// 里面是两件事，用分隔线分开：
    /// </para>
    /// <list type="bullet">
    /// <item><b>歌单库</b>——自己常用的那几个列表：固定目录（<c>数据目录\播放列表\</c>）、
    /// 有名字、菜单里一行一个，点一下就切过去；还可以新建 / 覆盖 / 重命名 / 删除。</item>
    /// <item><b>m3u 文件</b>——外来的、外发的：从别处拿一个 m3u 追加进来或整个替换，
    /// 或者把当前列表另存成 m3u 放到任意位置（给别人、拷进别的播放器）。</item>
    /// </list>
    /// <para>
    /// 这两件事以前分在两个地方（文件菜单里孤零零的「保存 / 加载播放列表」+「我的歌单」子菜单），
    /// 于是同一个动作有了两个变体而菜单上没有任何提示，连"载入"的语义都不一样
    /// （文件那条是追加、歌单那条是替换）。合到一起之后：
    /// <b>追加和替换是两个菜单项</b>，语义写在名字里；Ctrl+S 是"存进歌单库"、
    /// Ctrl+Shift+S 是"另存为 m3u"、Ctrl+L 是"从 m3u 追加"。
    /// </para>
    /// <para>
    /// 关键约定：<b>不自动写回</b>。改了列表只把标题标成「未保存」，
    /// 只有用户点了保存（或菜单里的「覆盖保存」）才动磁盘上的歌单。
    /// 谁也不希望自己整理好的歌单被一次误操作悄悄改掉。
    /// </para>
    /// </summary>
    public partial class MainForm
    {
        /// <summary>下拉菜单里最多直接列几份歌单；再多就让用户去歌单窗口里选。</summary>
        private const int MaximumPlaylistMenuEntries = 20;

        /// <summary>
        /// 歌单库的状态与规则：当前歌单名、有没有未保存的改动、"启动时那份歌单不见了"的提示。
        /// <para>
        /// 状态、规则和"替换前该问哪一句"都在 <see cref="PlaylistLibraryController"/> 里；
        /// 这里只负责弹对话框、写设置、刷界面。原来的四个私有字段收成了这一个。
        /// </para>
        /// </summary>
        private readonly PlaylistLibraryController _playlists = new PlaylistLibraryController();

        private MessageBoxReplacePrompter? _replacePrompter;

        /// <summary>替换列表前的那些询问：默认弹真对话框。</summary>
        private IPlaylistReplacePrompter ReplacePrompter =>
            _replacePrompter ??= new MessageBoxReplacePrompter(this);

        /// <summary>
        /// 只读入口，给冒烟测试用（主工程里有 <c>InternalsVisibleTo("SmokeTest")</c>）。
        /// <para>
        /// 测试原来靠反射去读 <c>_currentPlaylistName</c> / <c>_playlistModified</c>，
        /// 现在直接读这里。<c>ConfirmReplace</c> 也在这里，
        /// 测试可以给一个假的"询问者"把三条分支都走一遍（不用点模态窗口）。
        /// </para>
        /// </summary>
        internal PlaylistLibraryController Playlists => _playlists;

        /// <summary>只读入口：文件菜单里的「播放列表」（设计器字段，给冒烟测试用）。</summary>
        internal ToolStripMenuItem MenuFilePlaylists => menuFilePlaylists;

        /// <summary>只读入口：文件菜单本身（冒烟测试要验"那两项确实没有单挂在文件菜单下"）。</summary>
        internal ToolStripMenuItem MenuFile => menuFile;

        // =====================================================================
        // 初始化
        // =====================================================================

        /// <summary>
        /// 建好三个入口（文件菜单、播放列表右键菜单、播放列表工具栏按钮）。
        /// <para>三处共用 <see cref="RebuildPlaylistLibraryMenu"/>：内容永远一致，
        /// 只在展开的那一刻重建（歌单可能被资源管理器改过）。</para>
        /// </summary>
        private void ConfigurePlaylistLibraryMenu()
        {
            foreach (var menu in new[]
                     {
                         menuFilePlaylists.DropDownItems,
                         cmsPlaylists.DropDownItems,
                         tspPlaylists.DropDownItems
                     })
            {
                menu.Add(new ToolStripMenuItem("（点开显示歌单）") { Enabled = false });
            }

            menuFilePlaylists.DropDownOpening += (s, e) => RebuildPlaylistLibraryMenu(menuFilePlaylists.DropDownItems);
            cmsPlaylists.DropDownOpening += (s, e) => RebuildPlaylistLibraryMenu(cmsPlaylists.DropDownItems);
            tspPlaylists.DropDownOpening += (s, e) => RebuildPlaylistLibraryMenu(tspPlaylists.DropDownItems);

            // 内容变了才标「未保存」：切歌（SetCurrent）刻意不触发这个事件，
            // 否则每播一首歌单都会变成"已修改"。
            _playlist.ContentChanged += (s, e) => MarkPlaylistModified();
        }

        /// <summary>把设置里记的「当前歌单」接上；对应的文件已经不在了就如实说明。</summary>
        private void ApplyPlaylistLibrarySettings()
        {
            _playlists.RestoreFrom(_settings.CurrentPlaylistName, PlaylistLibrary.Exists);

            // 名字已经不在库里时会被清掉，设置里那份也要跟着清
            _settings.CurrentPlaylistName = _playlists.CurrentName;
        }

        /// <summary>启动时的歌单提示，交给 <c>ReportLoadWarnings</c> 一起显示。</summary>
        private string? PlaylistLibraryWarning => _playlists.StartupWarning;

        // =====================================================================
        // 菜单
        // =====================================================================

        internal void RebuildPlaylistLibraryMenu(ToolStripItemCollection items)
        {
            ClearMenuItems(items);

            var playlists = PlaylistLibrary.List();

            // 「打开歌单窗口」放在第一位：这是主要入口（在窗口里能看全、能改名删掉）
            var manage = new ToolStripMenuItem(playlists.Count == 0
                ? "打开歌单窗口（还没有歌单）…"
                : $"打开歌单窗口（{playlists.Count} 个歌单）…");

            manage.Click += (s, e) => ShowPlaylistLibraryDialog();
            items.Add(manage);

            items.Add(new ToolStripSeparator());

            var create = new ToolStripMenuItem("新建空歌单…")
            {
                ToolTipText = "新建一个空歌单并切过去（当前列表会被清空，会先问一句）"
            };
            create.Click += (s, e) => NewPlaylist();
            items.Add(create);

            var save = new ToolStripMenuItem("保存当前列表为歌单…")
            {
                Enabled = _playlist.Count > 0,
                ShortcutKeyDisplayString = "Ctrl+S",
                ToolTipText = "存进歌单库（数据目录的「播放列表」文件夹），以后在这里点一下就切回来"
            };
            save.Click += (s, e) => SaveCurrentListAsPlaylist();
            items.Add(save);

            if (_playlists.CurrentName.Length > 0)
            {
                var caption = MenuEscape(_playlists.CurrentName);

                var overwrite = new ToolStripMenuItem($"覆盖保存「{caption}」" + (_playlists.Modified ? "（有改动）" : string.Empty))
                {
                    Enabled = _playlist.Count > 0
                };
                overwrite.Click += (s, e) => OverwriteCurrentPlaylist();
                items.Add(overwrite);

                var reload = new ToolStripMenuItem($"重新载入「{caption}」（放弃改动）") { Enabled = _playlists.Modified };
                reload.Click += (s, e) => TryLoadPlaylist(_playlists.CurrentName, append: false);
                items.Add(reload);

                var rename = new ToolStripMenuItem($"重命名「{caption}」…");
                rename.Click += (s, e) => RenameCurrentPlaylist();
                items.Add(rename);

                var delete = new ToolStripMenuItem($"删除「{caption}」…");
                delete.Click += (s, e) => DeletePlaylist(_playlists.CurrentName, confirm: true);
                items.Add(delete);
            }

            items.Add(new ToolStripSeparator());

            if (playlists.Count == 0)
            {
                items.Add(new ToolStripMenuItem("（还没有保存过歌单）") { Enabled = false });
            }
            else
            {
                foreach (var playlist in playlists.Take(MaximumPlaylistMenuEntries))
                {
                    var name = playlist.Name;
                    var isCurrent = _playlists.IsCurrent(name);

                    // 菜单里的 & 是加速键前缀，歌单名里带着的话必须转义。
                    var text = MenuEscape(PlaylistLibrary.Describe(playlist));
                    if (isCurrent && _playlists.Modified) text += "（未保存）";

                    var item = new ToolStripMenuItem(text)
                    {
                        ToolTipText = playlist.FilePath,
                        Checked = isCurrent
                    };

                    item.Click += (s, e) => TryLoadPlaylist(name, append: false);
                    items.Add(item);
                }

                if (playlists.Count > MaximumPlaylistMenuEntries)
                {
                    items.Add(new ToolStripMenuItem(
                        $"（还有 {playlists.Count - MaximumPlaylistMenuEntries} 个，见「打开歌单窗口」）")
                    {
                        Enabled = false
                    });
                }
            }

            items.Add(new ToolStripSeparator());

            // ---- m3u 文件：外来的、外发的 ----
            // 「追加」和「替换」必须是两项：以前文件菜单里那个「加载播放列表」是追加，
            // 而歌单那一行是替换，菜单上却都叫"载入"——点错一次就少一个列表。
            var appendFile = new ToolStripMenuItem("从 m3u 文件追加…")
            {
                ShortcutKeyDisplayString = "Ctrl+L",
                ToolTipText = "选一个 m3u / m3u8，把里面的条目并进当前列表（已有的那些不动）"
            };
            appendFile.Click += (s, e) => OpenPlaylistFileDialog(replace: false);
            items.Add(appendFile);

            var replaceFile = new ToolStripMenuItem("从 m3u 文件替换当前列表…")
            {
                ToolTipText = "选一个 m3u / m3u8，用它整个替换当前列表（替换前会问一句）"
            };
            replaceFile.Click += (s, e) => OpenPlaylistFileDialog(replace: true);
            items.Add(replaceFile);

            var exportFile = new ToolStripMenuItem("另存为 m3u 文件…")
            {
                Enabled = _playlist.Count > 0,
                ShortcutKeyDisplayString = "Ctrl+Shift+S",
                ToolTipText = "把当前列表写成一个 m3u 文件，放在你自己挑的位置（给别人、拷进别的播放器）"
            };
            exportFile.Click += (s, e) => ExportPlaylistDialog();
            items.Add(exportFile);

            items.Add(new ToolStripSeparator());

            var folder = new ToolStripMenuItem("打开歌单文件夹");
            folder.Click += (s, e) => OpenPlaylistFolder();
            items.Add(folder);
        }

        private static string MenuEscape(string text) => text.Replace("&", "&&");

        // =====================================================================
        // 保存 / 覆盖 / 改名 / 删除
        // =====================================================================

        /// <summary>
        /// 新建一个空歌单，并把它设为当前歌单（"新建"的直觉就是"给我一张白纸"）。
        /// <para>
        /// 当前列表会被清空，所以先问一句——尤其是那份没存过的列表，清掉就真没了。
        /// </para>
        /// </summary>
        private void NewPlaylist()
        {
            var raw = InputDialog.Show(
                this,
                "新建歌单",
                "新歌单的名字。建好之后当前播放列表会清空并切到它，接着往里添加文件就行。",
                SuggestNewPlaylistName());

            if (raw == null) return;

            if (!PlaylistLibrary.TryNormalizeName(raw, out var name, out var error))
            {
                Warn("歌单名不可用", error);
                return;
            }

            if (PlaylistLibrary.Exists(name))
            {
                Warn("新建歌单失败", $"已经有一个叫「{name}」的歌单了。\n\n"
                                     + "换个名字，或者用「载入」直接切过去。");
                return;
            }

            try
            {
                PlaylistLibrary.Create(name);
            }
            catch (Exception ex)
            {
                ShowError("新建歌单失败", ex);
                return;
            }

            SwitchToNewPlaylist(name);
        }

        /// <summary>
        /// 空歌单文件已经建好了，这里只负责"切过去"：清空当前列表并挂上新名字。
        /// <para>歌单窗口里点「新建」走的也是这条路（文件由窗口建）。</para>
        /// </summary>
        internal bool SwitchToNewPlaylist(string name)
        {
            if (!ConfirmReplacePlaylist(name, "新建"))
            {
                SetStatus($"已新建空歌单「{name}」（当前列表没有动，可在「播放列表」菜单里切过去）");
                return false;
            }

            // 和载入一样：列表里已经没有任何正在播的东西了，先停掉再清
            StopPlayback();
            _scanner.ClearPending();

            _playlists.SuppressModified = true;

            try
            {
                _playlist.Clear();
                ResetSortToAddedOrder();
            }
            finally
            {
                _playlists.SuppressModified = false;
            }

            ClearPlaylistFilter();
            SetCurrentPlaylist(name, modified: false);
            UpdateWindowTitle();
            UpdateTransportState();

            SetStatus($"已新建空歌单「{name}」，往里添加文件就行");
            AppLog.Info($"新建空歌单并切过去：{name}");
            return true;
        }

        /// <summary>新歌单的默认名：新歌单 / 新歌单 2 / …（避开已经用掉的名字）。</summary>
        private static string SuggestNewPlaylistName()
        {
            const string stem = "新歌单";

            var candidate = stem;

            for (var suffix = 2; suffix <= 99 && PlaylistLibrary.Exists(candidate); suffix++)
                candidate = $"{stem} {suffix}";

            return candidate;
        }

        /// <summary>把当前列表存成一份新歌单（同名时先问一句要不要覆盖）。</summary>
        private void SaveCurrentListAsPlaylist()
        {
            if (_playlist.Count == 0)
            {
                SetStatus("播放列表为空，没有可保存的内容");
                return;
            }

            var raw = InputDialog.Show(
                this,
                "保存为歌单",
                "给这个播放列表起个名字吧。歌单会存到数据目录的「播放列表」文件夹里，"
                + "以后在「文件 → 播放列表」里点一下就能切回来。",
                SuggestPlaylistName());

            if (raw == null) return;

            if (!PlaylistLibrary.TryNormalizeName(raw, out var name, out var error))
            {
                Warn("歌单名不可用", error);
                return;
            }

            if (PlaylistLibrary.Exists(name))
            {
                var answer = MessageBox.Show(
                    this,
                    $"已经有一个叫「{name}」的歌单了，要覆盖它吗？\n\n"
                    + "（覆盖会把它原来的内容替换成当前的播放列表，不能撤销。）",
                    "覆盖歌单",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                if (answer != DialogResult.Yes) return;
            }

            SaveCurrentListToPlaylist(name, "已保存歌单");
        }

        /// <summary>
        /// 把当前列表写进指定歌单（不做任何询问）。
        /// <para>对话框那步已经确认过覆盖了，这里只负责写和报错。</para>
        /// </summary>
        internal bool SaveCurrentListToPlaylist(string name, string action)
        {
            try
            {
                var saved = PlaylistLibrary.Save(name, _playlist.Items);

                SetCurrentPlaylist(saved.Name, modified: false);
                SetStatus($"{action}「{saved.Name}」：{_playlist.Count} 项");

                return true;
            }
            catch (Exception ex)
            {
                ShowError("保存歌单失败", ex);
                return false;
            }
        }

        internal void OverwriteCurrentPlaylist()
        {
            if (_playlists.CurrentName.Length == 0) return;
            if (_playlist.Count == 0)
            {
                SetStatus("播放列表为空，没有可保存的内容");
                return;
            }

            SaveCurrentListToPlaylist(_playlists.CurrentName, "已更新歌单");
        }

        private void RenameCurrentPlaylist()
        {
            if (_playlists.CurrentName.Length == 0) return;

            var raw = InputDialog.Show(this, "重命名歌单", "新的名字：", _playlists.CurrentName);
            if (raw == null) return;

            RenamePlaylist(_playlists.CurrentName, raw);
        }

        /// <summary>歌单改名；改的正好是当前歌单时，关联跟着一起走（列表内容不动）。</summary>
        private bool RenamePlaylist(string oldName, string newName)
        {
            if (!PlaylistLibrary.Rename(oldName, newName, out var actual, out var error))
            {
                Warn("重命名歌单失败", error);
                return false;
            }

            if (_playlists.IsCurrent(oldName)) SetCurrentPlaylist(actual, _playlists.Modified);

            SetStatus($"歌单「{oldName}」已改名为「{actual}」");
            return true;
        }

        /// <summary>
        /// 删掉一份歌单。<b>只删歌单文件本身</b>，媒体文件一个都不动；
        /// 如果删的正是当前歌单，当前列表保留，只是不再对应任何歌单。
        /// </summary>
        private bool DeletePlaylist(string name, bool confirm)
        {
            if (confirm)
            {
                var answer = MessageBox.Show(
                    this,
                    $"要删除歌单「{name}」吗？\n\n"
                    + "（只删这个歌单文件，里面的歌曲、视频一个都不会动。）",
                    "删除歌单",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                if (answer != DialogResult.Yes) return false;
            }

            if (!PlaylistLibrary.Delete(name, out var error))
            {
                Warn("删除歌单失败", error);
                return false;
            }

            var wasCurrent = _playlists.IsCurrent(name);

            if (wasCurrent)
            {
                SetCurrentPlaylist(string.Empty, _playlists.Modified);
                SetStatus($"已删除歌单「{name}」；当前列表还在，只是不再对应歌单");
            }
            else
            {
                SetStatus($"已删除歌单「{name}」");
            }

            return true;
        }

        /// <summary>给新歌单起个默认名：优先"当前歌单的副本"，否则用第一项所在的文件夹名。</summary>
        private string SuggestPlaylistName()
        {
            if (_playlists.CurrentName.Length > 0) return _playlists.CurrentName + " 副本";

            string? stem = null;

            var first = _playlist.Items.FirstOrDefault(item => !item.IsStream)?.FilePath;

            if (!string.IsNullOrEmpty(first))
            {
                try
                {
                    stem = Path.GetFileName(Path.GetDirectoryName(first));
                }
                catch (Exception ex)
                {
// 路径畸形就用下面的兜底名字。
                    AppLog.Swallowed("路径畸形就用下面的兜底名字。", ex);
                }
            }

            if (string.IsNullOrWhiteSpace(stem)) stem = "我的歌单";

            var candidate = stem!;

            // 名字被占了就一路加编号，免得一打开对话框就是"要不要覆盖"。
            for (var suffix = 2; suffix <= 99 && PlaylistLibrary.Exists(candidate); suffix++)
                candidate = $"{stem} {suffix}";

            return candidate;
        }

        // =====================================================================
        // 载入 / 切换
        // =====================================================================

        /// <summary>
        /// 载入歌单（会先问一句是否替换当前列表）。菜单项和歌单窗口都走这里。
        /// </summary>
        internal bool TryLoadPlaylist(string name, bool append)
        {
            if (!append && !ConfirmReplacePlaylist(name)) return false;
            return LoadPlaylistFromLibrary(name, replace: !append);
        }

        /// <summary>
        /// 载入歌单内容。<b>不做任何询问</b>（询问在 <see cref="TryLoadPlaylist"/> 里），
        /// 这样自动化测试可以直接验"载入之后列表对不对"。
        /// </summary>
        private bool LoadPlaylistFromLibrary(string name, bool replace)
        {
            var result = PlaylistLibrary.Load(name);

            if (!result.Ok)
            {
                Warn("载入歌单失败", result.Error ?? "读不出来。");
                return false;
            }

            if (result.Tracks.Count == 0)
            {
                Warn("载入歌单失败", $"歌单「{name}」里的 {result.Missing} 项现在都找不到了。");
                return false;
            }

            if (replace)
            {
                // 替换列表时要停播：列表里已经没有正在放的那一项了，
                // 不停会出现"列表空着但还在响"的矛盾状态（和清空列表同样的处理）。
                StopPlayback();
                _scanner.ClearPending();

                _playlists.SuppressModified = true;

                try
                {
                    _playlist.Clear();
                    _playlist.AddRange(result.Tracks);

                    // 歌单文件里的顺序就是用户存的顺序，载入后按它显示：
                    // 把排序状态退回"原始顺序"，排序菜单的勾也得跟着改，
                    // 否则菜单勾着"按名称"而列表其实是歌单的顺序。
                    ResetSortToAddedOrder();
                }
                finally
                {
                    _playlists.SuppressModified = false;
                }

                ClearPlaylistFilter();
                _scanner.Enqueue(result.Tracks);

                SetCurrentPlaylist(name, modified: false);
                UpdateWindowTitle();
                UpdateTransportState();
            }
            else
            {
                var added = _playlist.AddRangeDistinct(result.Tracks);

                if (added.Count == 0)
                {
                    SetStatus($"歌单「{name}」里的 {result.Tracks.Count} 项都已经在当前列表里了");
                    return false;
                }

                _scanner.Enqueue(added.Select(i => _playlist.Items[i].FilePath));
                SetStatus($"已把歌单「{name}」的 {added.Count} 项追加到当前列表"
                          + (result.Missing > 0 ? $"（另外 {result.Missing} 项文件已经不在了）" : string.Empty));

                return true;
            }

            SetStatus(DescribeLoadedPlaylist(name, result));
            return true;
        }

        private static string DescribeLoadedPlaylist(string name, PlaylistLoadResult result)
        {
            var text = $"已载入歌单「{name}」：{result.Tracks.Count} 项";

            // 文件已经不在了的条目必须说出来：静默少几首，用户只会以为歌单坏了。
            if (result.Missing > 0) text += $"（另外 {result.Missing} 项文件已经不在了，已跳过）";

            return text;
        }

        /// <summary>
        /// 替换当前列表前问一句（有未保存改动时给"先保存"的机会）。
        /// <para>问哪一句、每个回答意味着什么都由
        /// <see cref="PlaylistLibraryController.ConfirmReplace"/> 决定；
        /// 这里只把"真对话框"和"覆盖保存"两件事交过去。</para>
        /// <para><paramref name="nextAction"/> 是紧接着要做的动作（"载入"/"新建"），只用来拼提示语。</para>
        /// </summary>
        private bool ConfirmReplacePlaylist(string incomingName, string nextAction = "载入") =>
            _playlists.ConfirmReplace(
                incomingName,
                nextAction,
                _playlist.Count,
                () => SaveCurrentListToPlaylist(_playlists.CurrentName, "已更新歌单"),
                ReplacePrompter);

        /// <summary>
        /// 替换列表前的询问：真对话框（文案都在这里）。
        /// <para>和 <see cref="PlaylistLibraryController"/> 的分工是——
        /// 那边决定"该问哪一句、这个回答代表什么"，这边只负责把字显示出来。</para>
        /// </summary>
        private sealed class MessageBoxReplacePrompter : IPlaylistReplacePrompter
        {
            private readonly MainForm _form;

            public MessageBoxReplacePrompter(MainForm form) => _form = form;

            public PlaylistReplaceAnswer Ask(PlaylistReplacePrompt prompt, PlaylistReplaceContext context)
            {
                if (prompt == PlaylistReplacePrompt.UnsavedChanges)
                {
                    var answer = MessageBox.Show(
                        _form,
                        $"歌单「{context.CurrentName}」有未保存的改动。\n\n"
                        + $"是：先覆盖保存「{context.CurrentName}」，再{context.NextAction}「{context.IncomingName}」\n"
                        + $"否：不保存，直接{context.NextAction}「{context.IncomingName}」\n"
                        + "取消：什么都不做",
                        "当前歌单有未保存的改动",
                        MessageBoxButtons.YesNoCancel,
                        MessageBoxIcon.Question);

                    return answer switch
                    {
                        DialogResult.Yes => PlaylistReplaceAnswer.Save,
                        DialogResult.No => PlaylistReplaceAnswer.Discard,
                        _ => PlaylistReplaceAnswer.Cancel
                    };
                }

                // 还没有对应歌单的列表（多半是一点点攒起来的）：替换掉就找不回来了，问一句。
                var plain = MessageBox.Show(
                    _form,
                    $"当前播放列表（{context.ItemCount} 项）还没有存成歌单，"
                    + $"{context.NextAction}「{context.IncomingName}」会把它整个替换掉。\n\n继续吗？",
                    "替换播放列表",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                return plain == DialogResult.Yes ? PlaylistReplaceAnswer.Discard : PlaylistReplaceAnswer.Cancel;
            }
        }

        // =====================================================================
        // 歌单窗口 / 文件夹
        // =====================================================================

        private void ShowPlaylistLibraryDialog()
        {
            var result = PlaylistLibraryDialog.Show(
                this,
                _palette,
                _playlists.CurrentName.Length > 0 ? _playlists.CurrentName : null);

            if (result == null) return;

            // 窗口里可能把当前歌单改名或删掉了：关联跟着更新，
            // 否则"当前歌单"会指向一个不存在的名字。
            var current = result.CurrentName ?? string.Empty;

            if (!string.Equals(current, _playlists.CurrentName, StringComparison.CurrentCultureIgnoreCase))
                SetCurrentPlaylist(current, _playlists.Modified);

            if (result.LoadName != null)
            {
                TryLoadPlaylist(result.LoadName, result.Append);
            }
            else if (result.NewName is { Length: > 0 } created)
            {
                // 空歌单在窗口里已经建好了，这里只问一句要不要切过去（切过去 = 清空当前列表）
                SwitchToNewPlaylist(created);
            }
        }

        private void OpenPlaylistFolder()
        {
            try
            {
                var folder = PlaylistLibrary.EnsureDirectory();

                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
                SetStatus("歌单文件夹：" + folder);
            }
            catch (Exception ex)
            {
                ShowError("打开歌单文件夹失败", ex);
            }
        }

        // =====================================================================
        // 状态
        // =====================================================================

        /// <summary>设置"当前列表对应哪份歌单"，并刷新标题与按钮提示。</summary>
        internal void SetCurrentPlaylist(string name, bool modified)
        {
            _playlists.SetCurrent(name, modified);

            _settings.CurrentPlaylistName = _playlists.CurrentName;

            RefreshPlaylistLibraryIndicators();
        }

        /// <summary>列表内容变了：标「未保存」（要不要标、要不要刷界面都由控制器回答）。</summary>
        private void MarkPlaylistModified()
        {
            if (!_playlists.MarkModified()) return;

            RefreshPlaylistLibraryIndicators();
        }

        /// <summary>刷新标题栏前缀与工具栏按钮提示（歌单名、有没有未保存的改动）。</summary>
        private void RefreshPlaylistLibraryIndicators()
        {
            if (tspPlaylists != null)
            {
                tspPlaylists.ToolTipText = _playlists.CurrentName.Length == 0
                    ? "歌单：把当前列表存成歌单，随时切换（当前列表还没存成歌单）"
                    : _playlists.Modified
                        ? $"歌单：当前「{_playlists.CurrentName}」有未保存的改动"
                        : $"歌单：当前「{_playlists.CurrentName}」";
            }

            // 标题里的前缀由 UpdatePlaylistSummary 统一拼（它可能因为筛选而换一种写法）
            UpdatePlaylistSummary();
        }

        /// <summary>标题前缀："【歌单名】"；有未保存改动时是"【歌单名·未保存】"。</summary>
        private string PlaylistHeaderPrefix() => _playlists.HeaderPrefix;

        private void Warn(string title, string message) =>
            MessageBox.Show(this, message, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
}
