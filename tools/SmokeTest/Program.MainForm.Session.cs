
// 启动恢复、启动偏好、媒体键报告。

namespace SmokeTest
{
    internal static partial class Program
    {
        /// <summary>
        /// A1：启动恢复上次的播放列表时<b>不做同步磁盘查询</b>。
        /// <para>
        /// 判据里不含计时（那会在慢机器上抖）：构造函数返回时，那条"已经不存在"的路径
        /// <b>仍然在列表里</b>——旧实现在这一步同步 <c>File.Exists</c>，把它过滤掉了，
        /// 而网络盘离线时每个路径还要等一次 SMB 超时（"双击之后半天不出窗口"）。
        /// 打不开的条目改由后台核对标出来（同一个检查的后半段）。
        /// </para>
        /// </summary>
        private static bool CheckRestoreSessionOptimistic()
        {
            var root = Path.Combine(AppContext.BaseDirectory, "smoke-session");
            TryDeleteDirectory(root);
            Directory.CreateDirectory(root);

            var existing = Path.Combine(root, "在的.mp3");
            File.WriteAllBytes(existing, new byte[] { 0x49, 0x44, 0x33, 3 });

            var missing = Path.Combine(root, "不在的.mp3");
            var stream = "http://127.0.0.1:9/连不上也不要紧.mp3";

            var settings = AppSettings.Load();
            var previousPlaylist = settings.LastPlaylist;
            var previousIndex = settings.LastPlaylistIndex;
            var previousRestore = settings.RestoreLastPlaylist;

            try
            {
                settings.LastPlaylist = new List<string> { existing, missing, stream };
                settings.LastPlaylistIndex = -1;

                // 这一步验的是"恢复这条路上不做同步磁盘查询"，所以必须真的让它恢复：
                // 那个开关默认是关的（默认打开程序 = 空列表），不显式打开的话这里什么都验不到。
                settings.RestoreLastPlaylist = true;
                settings.Save();

                using (var form = new 播放器.MainForm(Array.Empty<string>()))
                {
                    var playlist = PlaylistOf(form);

                    // 构造函数返回时三条都要在（含不存在的那条）
                    if (playlist.Count != 3)
                    {
                        Log(6, $"启动恢复检查：构造函数返回时列表里有 {playlist.Count} 项，期望 3"
                                + "（打不开的那条不该在启动时被过滤掉，也不该在启动时去查磁盘）");
                        return false;
                    }

                    form.Show();
                    PumpMessages(900);      // 等后台核对

                    var missingItem = playlist.Items.FirstOrDefault(item => item.FilePath == missing);
                    var existingItem = playlist.Items.FirstOrDefault(item => item.FilePath == existing);
                    var streamItem = playlist.Items.FirstOrDefault(item => item.FilePath == stream);

                    if (missingItem == null)
                    {
                        Log(6, "启动恢复检查：不存在的那条从列表里消失了");
                        return false;
                    }

                    if (!missingItem.HasError || missingItem.ErrorMessage != "文件不存在或无法访问")
                    {
                        Log(6, "启动恢复检查：后台核对没有把打不开的那条标出来"
                                + $"（HasError={missingItem.HasError}、原因「{missingItem.ErrorMessage ?? "（空）"}」）");
                        return false;
                    }

                    if (existingItem == null || existingItem.HasError)
                    {
                        Log(6, "启动恢复检查：明明在的那条被标成了打不开");
                        return false;
                    }

                    if (streamItem == null || streamItem.HasError)
                    {
                        Log(6, "启动恢复检查：网络串流被当成「文件不存在」标了（串流本来就不查磁盘）");
                        return false;
                    }

                    if (!ReadStatus(form).Contains("打不开", StringComparison.Ordinal))
                    {
                        Log(6, $"启动恢复检查：标出来了但状态栏没说（「{ReadStatus(form)}」）");
                        return false;
                    }

                    form.Close();
                    PumpMessages(200);
                }

                Log(6, "启动恢复检查正常：上次的列表原样恢复（含已经打不开的那条、不做同步磁盘查询），"
                        + "打不开的由后台核对标出来并在状态栏说明，在的与网络串流都不误标");
                return true;
            }
            finally
            {
                // 这一步改过设置文件里的"上次列表"，必须还原：
                // 后面的步骤会从设置里恢复会话，留着这几条（其中一条不存在）会污染别人
                var restore = AppSettings.Load();
                restore.LastPlaylist = previousPlaylist;
                restore.LastPlaylistIndex = previousIndex;
                restore.RestoreLastPlaylist = previousRestore;
                restore.Save();

                TryDeleteDirectory(root);
            }
        }

        /// <summary>
        /// 「启动时恢复上次播放列表」开关：<b>默认关 —— 打开程序就是一张空列表</b>。
        /// <para>
        /// 判据是<b>同一份设置、两次启动的对比</b>：
        /// 关着的时候，设置里明明存着上次的列表和「当前歌单」，启动后<b>两样都不该回来</b>
        /// （空列表挂着歌单名是危险的：往里加两首再点「覆盖保存」，那份歌单就被两首换掉了，
        /// 所以判据落在"菜单里有没有那个「覆盖保存」入口"上）；
        /// 开着的时候，同一个歌单名必须真的恢复出来、并有对应的入口。
        /// 两侧都验才算数——只验"关着是空的"的话，歌单文件压根没建出来也会绿。
        /// </para>
        /// </summary>
        private static bool CheckStartupPlaylistPreference()
        {
            // ---- 纯函数那半：默认值 + ForgetSession 清的是哪三项 ----
            if (new AppSettings().RestoreLastPlaylist)
            {
                Log(6, "启动开关检查：新设置的默认值不是「关」（打开程序应当就是空列表）");
                return false;
            }

            var probe = new AppSettings { RestoreLastPlaylist = true, LastPlaylistIndex = 3, CurrentPlaylistName = "某个歌单" };
            probe.LastPlaylist.Add(@"D:\媒体\某首歌.mp3");
            probe.ForgetSession();

            if (probe.LastPlaylist.Count != 0 || probe.LastPlaylistIndex != -1 || probe.CurrentPlaylistName.Length != 0)
            {
                Log(6, "启动开关检查：ForgetSession 没有把三项会话状态都清掉"
                        + $"（列表 {probe.LastPlaylist.Count} 项、当前项 {probe.LastPlaylistIndex}、"
                        + $"当前歌单「{probe.CurrentPlaylistName}」）");
                return false;
            }

            const string name = "启动开关检查";
            var keep = Path.Combine(AppContext.BaseDirectory, "startup-keep.mp3");
            var before = AppSettings.Load();

            try
            {
                PlaylistLibrary.Delete(name, out _);
                PlaylistLibrary.Create(name);       // 真在库里建一份，"开着能恢复"才有意义
                PlaylistLibrary.Invalidate();

                File.WriteAllBytes(keep, new byte[] { 0x49, 0x44, 0x33, 4 });

                // ---- 关：设置里存着上次的列表和歌单名，启动后两样都不该回来 ----
                var off = AppSettings.Load();
                off.RestoreLastPlaylist = false;
                off.LastPlaylist = new List<string> { keep };
                off.LastPlaylistIndex = 0;
                off.CurrentPlaylistName = name;
                off.Save();

                using (var form = new 播放器.MainForm(Array.Empty<string>()))
                {
                    // 显示出来再验：菜单勾选和"关窗落盘"这两件事都要走真实的窗口生命周期
                    // （没建过句柄的窗体 Close() 不会走 FormClosing，设置也就写不下去）。
                    form.Show();
                    PumpMessages(200);

                    var playlist = PlaylistOf(form);

                    if (playlist.Count != 0)
                    {
                        Log(6, $"启动开关检查：开关关着，却恢复了 {playlist.Count} 项");
                        return false;
                    }

                    if (PlaylistsOf(form).CurrentName.Length != 0)
                    {
                        Log(6, $"启动开关检查：空列表却挂着歌单名「{PlaylistsOf(form).CurrentName}」"
                                + "（往里加两首再点「覆盖保存」就会把那份歌单换掉）");
                        return false;
                    }

                    if (PlaylistMenuTexts(form).Any(t => t.Contains("覆盖保存", StringComparison.Ordinal)))
                    {
                        Log(6, "启动开关检查：空列表却给出了「覆盖保存」入口，点下去会把那份歌单换成空列表");
                        return false;
                    }

                    // 状态栏那句"空列表 + 歌单在哪儿"是给用户看的指路牌。
                    // 启动警告（比如某个媒体键被别的程序占着）会把它整句换掉，那种情况不算失败。
                    var status = ReadStatus(form);

                    if (!status.Contains("空列表", StringComparison.Ordinal) &&
                        !status.Contains("没注册上", StringComparison.Ordinal))
                    {
                        Log(6, $"启动开关检查：空着启动，但状态栏没说（「{status}」）");
                        return false;
                    }

                    var item = FindRestorePlaylistMenuItem(form);

                    if (item == null)
                    {
                        Log(6, "启动开关检查：播放菜单里没有「启动时恢复上次播放列表」这一项");
                        return false;
                    }

                    if (item.Checked)
                    {
                        Log(6, "启动开关检查：开关关着，菜单却勾着");
                        return false;
                    }

                    // 勾上只写设置，不能当场动这张列表（那会盖掉用户正开着的东西）
                    SetMenuChecked(item, true);

                    if (playlist.Count != 0)
                    {
                        Log(6, "启动开关检查：勾上开关当场就把列表改了（应当下次启动才生效）");
                        return false;
                    }

                    form.Close();
                    PumpMessages(200);
                }

                if (!AppSettings.Load().RestoreLastPlaylist)
                {
                    Log(6, "启动开关检查：菜单里勾上之后没有写进设置（关窗时应当落盘）");
                    return false;
                }

                // ---- 开：同一份设置，这次开关是开的，必须真的恢复 ----
                var on = AppSettings.Load();
                on.RestoreLastPlaylist = true;
                on.LastPlaylist = new List<string> { keep };
                on.LastPlaylistIndex = 0;
                on.CurrentPlaylistName = name;
                on.Save();

                using (var form = new 播放器.MainForm(Array.Empty<string>()))
                {
                    form.Show();
                    PumpMessages(200);

                    var playlist = PlaylistOf(form);

                    if (playlist.Count != 1 ||
                        !string.Equals(playlist.Items[0].FilePath, keep, StringComparison.OrdinalIgnoreCase))
                    {
                        Log(6, $"启动开关检查：开关开着却没恢复上次的列表（{playlist.Count} 项）");
                        return false;
                    }

                    if (!PlaylistsOf(form).IsCurrent(name))
                    {
                        Log(6, "启动开关检查：开关开着却没恢复「当前歌单」关联"
                                + $"（现在挂着「{PlaylistsOf(form).CurrentName}」）");
                        return false;
                    }

                    if (!PlaylistMenuTexts(form).Any(t => t.Contains("覆盖保存", StringComparison.Ordinal)))
                    {
                        Log(6, "启动开关检查：恢复了列表和歌单关联，菜单里却没有「覆盖保存」入口");
                        return false;
                    }

                    var item = FindRestorePlaylistMenuItem(form);

                    if (item == null || !item.Checked)
                    {
                        Log(6, "启动开关检查：开关开着，菜单却没勾上");
                        return false;
                    }

                    if (ReadStatus(form).Contains("空列表", StringComparison.Ordinal))
                    {
                        Log(6, $"启动开关检查：开关开着却说自己是空列表（「{ReadStatus(form)}」）");
                        return false;
                    }

                    form.Close();
                    PumpMessages(150);
                }

                Log(6, "启动开关检查正常：默认关（打开就是空列表、不挂歌单名、不给「覆盖保存」），"
                        + "勾上会写进设置、下次启动真的恢复列表与歌单关联（歌单文件全程不动）");
                return true;
            }
            finally
            {
                PlaylistLibrary.Delete(name, out _);
                PlaylistLibrary.Invalidate();
                TryDelete(keep);

                // 这一步改过设置，必须还原：后面还有十几步各自建窗体读这份设置
                var restore = AppSettings.Load();
                restore.RestoreLastPlaylist = before.RestoreLastPlaylist;
                restore.LastPlaylist = before.LastPlaylist;
                restore.LastPlaylistIndex = before.LastPlaylistIndex;
                restore.CurrentPlaylistName = before.CurrentPlaylistName;
                restore.Save();
            }
        }

        /// <summary>播放菜单里的「启动时恢复上次播放列表」那一项（找不到返回 <c>null</c>）。</summary>
        private static ToolStripMenuItem? FindRestorePlaylistMenuItem(Form form)
        {
            var play = FindTopMenuItem(form, "播放");
            return play == null ? null : FindMenuItem(play.DropDownItems, "启动时恢复上次播放列表");
        }

        /// <summary>把「文件 → 播放列表」那份菜单重建一遍，返回里面的菜单文字（判据：有没有哪个入口）。</summary>
        private static List<string> PlaylistMenuTexts(播放器.MainForm form)
        {
            form.RebuildPlaylistLibraryMenu(form.MenuFilePlaylists.DropDownItems);

            return form.MenuFilePlaylists.DropDownItems.OfType<ToolStripMenuItem>()
                .Select(item => item.Text)
                .ToList();
        }

        /// <summary>
        /// A2：全局媒体键"哪几个没注册上"要写清名字。
        /// <para>
        /// 两段：纯函数（缺哪几个就点哪几个的名字）+ <b>真实那条路</b>——测试自己先用
        /// <c>RegisterHotKey</c> 占住其中一个键（同一个虚拟键系统只允许一个窗口注册），
        /// 再开主窗体，它必然少注册一个，必须报出那一个的名字。
        /// </para>
        /// </summary>
        private static bool CheckMediaKeyReporting()
        {
            var all = MediaKeys.Requested.Select(key => key.Id).ToList();

            if (MediaKeys.DescribeMissing(all) != null)
            {
                Log(6, "媒体键检查：四个都注册上了，却说有没注册上的");
                return false;
            }

            var target = MediaKeys.Requested[2];        // 下一首
            var withoutTarget = all.Where(id => id != target.Id).ToList();

            if (MediaKeys.DescribeMissing(withoutTarget) is not { } partial ||
                !partial.Contains(target.Name, StringComparison.Ordinal))
            {
                Log(6, $"媒体键检查：缺了「{target.Name}」但提示里没写出名字"
                        + $"（「{MediaKeys.DescribeMissing(withoutTarget) ?? "（什么都没有）"}」）");
                return false;
            }

            if (MediaKeys.DescribeMissing(Array.Empty<int>()) is not { } none ||
                !none.Contains("一个都没注册上", StringComparison.Ordinal))
            {
                Log(6, "媒体键检查：一个都没注册上时，提示没写清是全部失败");
                return false;
            }

            // 注册发生在建句柄的时候（InitializeSystemIntegration），所以先把开关写进设置
            var settings = AppSettings.Load();
            settings.GlobalMediaKeys = true;
            settings.Save();

            using var probe = new Form { ShowInTaskbar = false, Opacity = 0 };
            probe.Show();
            PumpMessages(150);

            const int probeHotKeyId = 0x7F01;   // 测试自己的 id，和媒体键的 0x9E0x 段不冲突

            if (!RegisterHotKey(probe.Handle, probeHotKeyId, 0, (uint)target.VirtualKey))
            {
                Log(6, $"媒体键检查：「{target.Name}」在这台机器上已经被别的程序占着，"
                        + "抢占那条路这次跳过（纯函数那几条照旧生效）");
                return true;
            }

            try
            {
                using var victim = new 播放器.MainForm(Array.Empty<string>());

                victim.Show();
                PumpMessages(450);

                var warning = victim.MediaKeyWarning;

                if (victim.RegisteredMediaKeys.Contains(target.Id))
                {
                    Log(6, $"媒体键检查：明明被测试占着，「{target.Name}」却被算成注册成功了");
                    return false;
                }

                if (warning == null || !warning.Contains(target.Name, StringComparison.Ordinal))
                {
                    Log(6, "媒体键检查：自己占住一个媒体键之后，主窗体没有说清是哪一个没注册上"
                            + $"（提示「{warning ?? "（什么都没有）"}」、成功 {victim.RegisteredMediaKeys.Count}/4）");
                    return false;
                }

                // 关窗会注销掉那些键，所以这个数要在关之前取
                var registeredCount = victim.RegisteredMediaKeys.Count;

                victim.Close();
                PumpMessages(200);

                Log(6, $"媒体键检查正常：占住「{target.Name}」之后主窗体报出了它的名字"
                        + $"（当时成功注册 {registeredCount}/4，提示："
                        + (warning.Length > 60 ? warning.Substring(0, 60) + "…" : warning) + "）");
                return true;
            }
            finally
            {
                UnregisterHotKey(probe.Handle, probeHotKeyId);
            }
        }
    }
}
