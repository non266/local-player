// 歌单库 —— 主窗体上的整条链路（菜单、状态栏、恢复会话、重开之后还在）。
//
// 从 Program.PlaylistLibrary.cs 按 ARCHITECTURE 17.2 的规矩整块搬过来：成员一个字没动。


namespace SmokeTest
{
    internal static partial class Program
    {
        /// <summary>主窗体上的关联：保存、标题里的歌单名、"未保存"、替换载入、菜单项。</summary>
        private static bool CheckPlaylistLibraryInMainForm(List<string> media)
        {
            using var form = new 播放器.MainForm(Array.Empty<string>());
            form.Show();
            PumpMessages(250);

            var playlist = PlaylistOf(form);

            // 会话恢复出来的内容不要影响这一段的判断
            playlist.Clear();
            playlist.AddRange(media);
            PumpMessages(200);

            var header = Find(form, "lblPlaylistHeader");
            if (header == null)
            {
                Log(17, "歌单库检查：找不到播放列表标题");
                return false;
            }

            // ---- 保存为歌单（走真实那条路，只是不弹输入框）----
            if (!form.SaveCurrentListToPlaylist("主窗体歌单", "已保存歌单"))
            {
                Log(17, "歌单库检查：保存为歌单失败");
                return false;
            }

            if (!PlaylistLibrary.Exists("主窗体歌单"))
            {
                Log(17, "歌单库检查：保存之后磁盘上没有这个歌单");
                return false;
            }

            if (PlaylistsOf(form).CurrentName != "主窗体歌单")
            {
                Log(17, "歌单库检查：保存之后主窗体没有记住当前歌单");
                return false;
            }

            if (!header.Text.Contains("【主窗体歌单】", StringComparison.Ordinal))
            {
                Log(17, $"歌单库检查：标题里没有写出当前歌单（「{header.Text}」）");
                return false;
            }

            if (header.Text.Contains("未保存", StringComparison.Ordinal))
            {
                Log(17, $"歌单库检查：刚保存完就标着「未保存」（「{header.Text}」）");
                return false;
            }

            // ---- 切歌不算改动：每播一首就标"未保存"的话，这个提示就没人看了 ----
            playlist.SetCurrent(1);
            PumpMessages(150);

            if (PlaylistsOf(form).Modified)
            {
                Log(17, "歌单库检查：只切换了一下当前项，就被当成「歌单有改动」了");
                return false;
            }

            // ---- 真的加了东西才算 ----
            var extra = Path.Combine(AppContext.BaseDirectory, "smoke-playlists", "track6.mp3");
            File.WriteAllBytes(extra, new byte[] { 0x49, 0x44, 0x33, 6 });
            playlist.Add(extra);
            PumpMessages(150);

            if (!PlaylistsOf(form).Modified ||
                !header.Text.Contains("未保存", StringComparison.Ordinal))
            {
                Log(17, $"歌单库检查：改了列表却没有标「未保存」（「{header.Text}」）");
                return false;
            }

            // ---- 覆盖保存之后回到干净 ----
            form.OverwriteCurrentPlaylist();
            PumpMessages(150);

            if (PlaylistsOf(form).Modified || header.Text.Contains("未保存", StringComparison.Ordinal))
            {
                Log(17, "歌单库检查：覆盖保存之后还挂着「未保存」");
                return false;
            }

            if (PlaylistLibrary.Load("主窗体歌单").Tracks.Count != media.Count + 1)
            {
                Log(17, "歌单库检查：覆盖保存没有把新增的那一项写进歌单");
                return false;
            }

            // ---- 工具条上的「歌单」按钮：默认宽度下不能被挤进溢出菜单（点不到等于没有）----
            var bar = Find(form, "toolStripPlaylist") as ToolStrip;
            var playlistsButton = bar?.Items.Cast<ToolStripItem>().FirstOrDefault(i => i.Name == "tspPlaylists");

            if (bar == null || playlistsButton == null)
            {
                Log(17, "歌单库检查：播放列表工具条上没有「歌单」按钮");
                return false;
            }

            var needed = bar.Items.Cast<ToolStripItem>().Where(i => i.Visible).Sum(i => i.Width)
                         + bar.Padding.Horizontal;

            // 直接在"工具条主区里"才算真的点得到：放不下时 WinForms 会把它挪进溢出菜单，
            // 那时它的 Width 反而变小（所以不能只看宽度总和，实测踩过）
            if (playlistsButton.Placement != ToolStripItemPlacement.Main)
            {
                Log(17, $"歌单库检查：「歌单」按钮没在工具条主区里（placement={playlistsButton.Placement}，"
                        + $"需要约 {needed}px / 可用 {bar.ClientSize.Width}px），会被收进溢出菜单");
                return false;
            }

            if (playlistsButton.DisplayStyle != ToolStripItemDisplayStyle.Text ||
                !playlistsButton.Text.Contains("歌单", StringComparison.Ordinal))
            {
                Log(17, $"歌单库检查：工具条上的歌单按钮不对（{playlistsButton.DisplayStyle} /「{playlistsButton.Text}」）");
                return false;
            }

            Log(17, $"播放列表工具条放得下「歌单」按钮（它自己占 {playlistsButton.Width}px，"
                    + $"整条工具条需要 {needed}px / 可用 {bar.ClientSize.Width}px）");

            // ---- 菜单里能看到这份歌单，点下去会替换当前列表 ----
            if (form.MenuFilePlaylists is not { } menu)
            {
                Log(17, "歌单库检查：文件菜单里没有「播放列表」子菜单");
                return false;
            }

            form.RebuildPlaylistLibraryMenu(menu.DropDownItems);
            var texts = menu.DropDownItems.OfType<ToolStripMenuItem>().Select(i => i.Text).ToList();

            if (!texts.Any(t => t.Contains("打开歌单窗口", StringComparison.Ordinal)))
            {
                Log(17, $"歌单库检查：歌单菜单里没有「打开歌单窗口」（{string.Join(" / ", texts)}）");
                return false;
            }

            if (!texts.Any(t => t.Contains("主窗体歌单（" + (media.Count + 1) + " 项）", StringComparison.Ordinal)))
            {
                Log(17, $"歌单库检查：歌单菜单里没有列出手上的歌单（{string.Join(" / ", texts)}）");
                return false;
            }

            // ---- 合并之后：菜单里只有一个「播放列表」，歌单库和 m3u 都在里面 ----
            // 每次都从当前集合里现取：重建菜单会把旧项 Dispose 掉，留着引用会炸。
            ToolStripMenuItem? Entry(string prefix) =>
                menu.DropDownItems.OfType<ToolStripMenuItem>()
                    .FirstOrDefault(i => i.Text.StartsWith(prefix, StringComparison.Ordinal));

            var fileMenu = form.MenuFile;

            if (fileMenu == null)
            {
                Log(17, "歌单库检查：主窗体上没有文件菜单");
                return false;
            }

            var fileTop = fileMenu.DropDownItems.OfType<ToolStripMenuItem>().Select(i => i.Text).ToList();

            // 合并的验收点就是这一条：文件菜单下不再单挂着「保存 / 加载播放列表」
            if (fileTop.Any(t => t.Contains("保存播放列表", StringComparison.Ordinal)) ||
                fileTop.Any(t => t.Contains("加载播放列表", StringComparison.Ordinal)))
            {
                Log(17, $"歌单库检查：文件菜单里还单挂着保存 / 加载播放列表（{string.Join(" / ", fileTop)}）");
                return false;
            }

            if (!fileTop.Any(t => t.Contains("播放列表(&Y)", StringComparison.Ordinal)))
            {
                Log(17, $"歌单库检查：文件菜单里没有「播放列表(&Y)」子菜单（{string.Join(" / ", fileTop)}）");
                return false;
            }

            var saveAsPlaylist = Entry("保存当前列表为歌单");
            var appendM3u = Entry("从 m3u 文件追加");
            var replaceM3u = Entry("从 m3u 文件替换当前列表");
            var exportM3u = Entry("另存为 m3u 文件");

            if (saveAsPlaylist == null || appendM3u == null || replaceM3u == null || exportM3u == null)
            {
                Log(17, $"歌单库检查：合并后的菜单里少了项（{string.Join(" / ", texts)}）");
                return false;
            }

            // 追加和替换必须是两个菜单项：只写「载入」的话没人分得清是哪种，
            // 而这两种点错了就是"多出一倍条目"或"当前列表没了"。
            if (appendM3u.Text == replaceM3u.Text)
            {
                Log(17, "歌单库检查：追加和替换被写成了同一个菜单项");
                return false;
            }

            // 菜单上写着的键必须和 MainForm.Shortcuts.cs 里真正接的键一致：
            // 提示归提示、接键归接键，两处代码最容易各走各的。
            if (saveAsPlaylist.ShortcutKeyDisplayString != "Ctrl+S" ||
                appendM3u.ShortcutKeyDisplayString != "Ctrl+L" ||
                exportM3u.ShortcutKeyDisplayString != "Ctrl+Shift+S" ||
                !string.IsNullOrEmpty(replaceM3u.ShortcutKeyDisplayString))
            {
                Log(17, "歌单库检查：菜单上的快捷键提示不对（"
                        + $"保存为歌单「{saveAsPlaylist.ShortcutKeyDisplayString}」、"
                        + $"追加「{appendM3u.ShortcutKeyDisplayString}」、"
                        + $"替换「{replaceM3u.ShortcutKeyDisplayString}」、"
                        + $"另存为「{exportM3u.ShortcutKeyDisplayString}」）");
                return false;
            }

            // 列表里有东西：这两项都该点得动
            if (!saveAsPlaylist.Enabled || !exportM3u.Enabled)
            {
                Log(17, "歌单库检查：列表里有内容，「保存为歌单 / 另存为 m3u」却是灰的");
                return false;
            }

            // 列表清空（清空会标脏，这里手动把关联重置回"干净"），
            // 这样点菜单项走的是"直接载入、不弹询问"那条路
            playlist.Clear();
            form.SetCurrentPlaylist("主窗体歌单", false);
            PumpMessages(150);

            // 列表空着的时候这两项都得点不动（点下去才说一句"没有内容"是另一种烦人）
            form.RebuildPlaylistLibraryMenu(menu.DropDownItems);

            if (Entry("保存当前列表为歌单")?.Enabled != false || Entry("另存为 m3u 文件")?.Enabled != false)
            {
                Log(17, "歌单库检查：列表空着，「保存为歌单 / 另存为 m3u」还能点");
                return false;
            }

            // 注意别选中上面那几个「覆盖保存「主窗体歌单」」「重新载入…」——它们是命令，
            // 这一条要的是列表里那一行歌单本身
            var entry = menu.DropDownItems.OfType<ToolStripMenuItem>()
                .First(i => i.Text.Contains("主窗体歌单（" + (media.Count + 1) + " 项）", StringComparison.Ordinal));

            // 菜单没展开时 PerformClick 什么也不做，所以走 ClickMenuItem 直接调 OnClick ——
            // 要验的是"点下去接的是不是载入歌单"。
            if (!ClickMenuItem(entry))
            {
                Log(17, "歌单库检查：菜单项上点不动（找不到 OnClick？）");
                return false;
            }

            PumpMessages(250);

            if (playlist.Count != media.Count + 1)
            {
                var direct = PlaylistLibrary.Load("主窗体歌单");

                Log(17, $"歌单库检查：点菜单项没有把歌单载回来（列表里 {playlist.Count} 项，期望 {media.Count + 1} 项；"
                        + $"菜单项「{entry.Text}」；状态栏「{ReadStatus(form)}」；"
                        + $"直接读歌单 {direct.Tracks.Count} 项、缺 {direct.Missing}、错误 {direct.Error}）");
                return false;
            }

            if (PlaylistsOf(form).Modified)
            {
                Log(17, "歌单库检查：载入歌单之后立刻标成了「未保存」");
                return false;
            }

            // ---- m3u 文件那条路：另存为 → 追加（不去重）→ 替换（整个换掉）----
            var exported = Path.Combine(AppContext.BaseDirectory, "smoke-playlists", "导出.m3u");

            playlist.Clear();
            playlist.AddRange(media);
            form.SetCurrentPlaylist("主窗体歌单", false);
            PumpMessages(150);

            if (!form.SavePlaylistToFile(exported) || !File.Exists(exported))
            {
                Log(17, $"m3u 检查：另存为没有写出文件（{exported}）");
                return false;
            }

            if (PlaylistFile.Load(exported).Count != media.Count)
            {
                Log(17, $"m3u 检查：另存为写出来的条目数不对"
                        + $"（{PlaylistFile.Load(exported).Count}，期望 {media.Count}）");
                return false;
            }

            // 追加：已有的那些不动，同一个文件出现两次是允许的（所以刻意不去重）
            if (!form.LoadPlaylistFromFile(exported, replace: false) || playlist.Count != media.Count * 2)
            {
                Log(17, $"m3u 检查：追加没把条目并进来（{playlist.Count} 项，期望 {media.Count * 2}；"
                        + $"状态栏「{ReadStatus(form)}」）");
                return false;
            }

            if (!ReadStatus(form).Contains("追加", StringComparison.Ordinal))
            {
                Log(17, $"m3u 检查：追加之后状态栏没说是追加（「{ReadStatus(form)}」）");
                return false;
            }

            // 替换前会先问一句（有未保存的改动才问），所以先把关联重置成"干净"，
            // 这样走的是"直接替换、不弹询问"那条路
            form.SetCurrentPlaylist("主窗体歌单", false);

            if (!form.LoadPlaylistFromFile(exported, replace: true) || playlist.Count != media.Count)
            {
                Log(17, $"m3u 检查：替换没把当前列表整个换掉（{playlist.Count} 项，期望 {media.Count}）");
                return false;
            }

            // 外来的 m3u 不是歌单库里的东西：关联留着，但内容算「有改动」——
            // 想收进歌单库就接着点「覆盖保存」，不想要就点「重新载入」退回原来那份
            if (PlaylistsOf(form).CurrentName != "主窗体歌单" || !PlaylistsOf(form).Modified)
            {
                Log(17, "m3u 检查：替换之后当前歌单的处理不对（"
                        + $"「{PlaylistsOf(form).CurrentName}」、未保存 {PlaylistsOf(form).Modified}）");
                return false;
            }

            // ---- 把 m3u 拖进窗口：按「追加」处理，而不是当成一条媒体塞进去 ----
            // （m3u 交给 VLC 只会得到一条"无法播放"，而拖它进来的人要的是"把里面的条目加进来"）
            playlist.Clear();
            form.SetCurrentPlaylist("主窗体歌单", false);
            PumpMessages(150);

            form.AddPathsForTest(new[] { exported });
            PumpMessages(200);

            if (playlist.Count != media.Count)
            {
                Log(17, $"拖放检查：把 m3u 拖进窗口没有按「追加」处理"
                        + $"（{playlist.Count} 项，期望 {media.Count}；状态栏「{ReadStatus(form)}」）");
                return false;
            }

            if (playlist.Items.Any(item => MediaFormats.IsPlaylistFile(item.FilePath)))
            {
                Log(17, "拖放检查：m3u 自己也被当成一条媒体塞进了列表");
                return false;
            }

            if (!ReadStatus(form).Contains("追加", StringComparison.Ordinal))
            {
                Log(17, $"拖放检查：追加之后状态栏没说是追加（「{ReadStatus(form)}」）");
                return false;
            }

            // 普通媒体文件照旧：拖进来就是加进列表
            playlist.Clear();
            form.SetCurrentPlaylist("主窗体歌单", false);
            PumpMessages(150);

            form.AddPathsForTest(media.Take(2));
            PumpMessages(200);

            if (playlist.Count != 2)
            {
                Log(17, $"拖放检查：拖媒体文件进窗口没有加进列表（{playlist.Count} 项，期望 2）");
                return false;
            }

            // ---- 新建空歌单：建文件 → 清空当前列表并切过去 → 往里加东西就能存 ----
            form.RebuildPlaylistLibraryMenu(menu.DropDownItems);

            if (!menu.DropDownItems.OfType<ToolStripMenuItem>()
                    .Any(i => i.Text.Contains("新建空歌单", StringComparison.Ordinal)))
            {
                Log(17, "歌单库检查：歌单菜单里没有「新建空歌单…」");
                return false;
            }

            // 菜单那条路会先弹输入框问名字，再建文件、再切过去。
            // 名字输入框是模态的（第 15 步验过排版），这里替它建好文件，验后面两步。
            PlaylistLibrary.Create("测试新建歌单");

            // 列表里先放点东西：这样"新建会清空当前列表"才是真的被验到
            // （空列表上清不清都看不出来）。关联重置成"干净"以免弹询问。
            playlist.Clear();
            playlist.AddRange(media.Take(2));
            form.SetCurrentPlaylist("主窗体歌单", false);
            PumpMessages(150);

            if (!form.SwitchToNewPlaylist("测试新建歌单"))
            {
                Log(17, "歌单库检查：切到新建的空歌单失败");
                return false;
            }

            PumpMessages(250);

            if (playlist.Count != 0 ||
                PlaylistsOf(form).CurrentName != "测试新建歌单")
            {
                Log(17, $"歌单库检查：新建之后当前列表没有清空 / 没切过去"
                        + $"（{playlist.Count} 项，当前歌单「{PlaylistsOf(form).CurrentName}」）");
                return false;
            }

            if (!header.Text.Contains("【测试新建歌单】", StringComparison.Ordinal) ||
                !header.Text.Contains("（0）", StringComparison.Ordinal))
            {
                Log(17, $"歌单库检查：新建之后标题不对（「{header.Text}」）");
                return false;
            }

            if (PlaylistsOf(form).Modified)
            {
                Log(17, "歌单库检查：刚新建的空歌单就被标成了「未保存」");
                return false;
            }

            // 新歌单是能写进去的（不是个只在列表里好看的空壳）
            playlist.Add(extra);
            PumpMessages(150);
            form.OverwriteCurrentPlaylist();
            PumpMessages(150);

            if (PlaylistLibrary.Load("测试新建歌单").Tracks.Count != 1)
            {
                Log(17, "歌单库检查：往新建的歌单里加了一项，保存之后没写进去");
                return false;
            }

            // ---- 关掉再开：当前歌单要跟着设置回来 ----
            // 这一条现在归「启动时恢复上次播放列表」管：那个开关默认是关的（打开就是空列表、
            // 也不挂歌单名），所以要显式打开它才验得到。验完还原，免得影响后面的步骤。
            var restoreSetting = SettingsOf(form).RestoreLastPlaylist;
            SettingsOf(form).RestoreLastPlaylist = true;

            form.Close();
            PumpMessages(250);

            using (var reopened = new 播放器.MainForm(Array.Empty<string>()))
            {
                reopened.Show();
                PumpMessages(300);

                var reopenedHeader = Find(reopened, "lblPlaylistHeader");

                if (reopenedHeader == null || !reopenedHeader.Text.Contains("【测试新建歌单】", StringComparison.Ordinal))
                {
                    Log(17, $"歌单库检查：重新启动之后没有恢复「当前歌单」（「{reopenedHeader?.Text}」）");
                    return false;
                }

                reopened.Close();
                PumpMessages(150);
            }

            var afterRestart = AppSettings.Load();
            afterRestart.RestoreLastPlaylist = restoreSetting;
            afterRestart.Save();

            Log(17, "歌单库与主窗体联动正常：保存后标题带上歌单名、切歌不算改动、"
                    + "加/删项才标「未保存」、覆盖保存写回磁盘、点菜单项会替换当前列表、"
                    + "新建空歌单会清空当前列表并切过去（新歌单能写进去），"
                    + "重启后当前歌单还在（开着「启动时恢复上次播放列表」时）；"
                    + "「文件 → 播放列表」把歌单库和 m3u 收在一处（文件菜单下不再单挂那两项、"
                    + "追加与替换分列两项、快捷键提示与实际接的键一致、列表空时保存两项点不动），"
                    + "m3u 的另存为 / 追加 / 替换各按自己的语义生效");
            return true;
        }
    }
}
