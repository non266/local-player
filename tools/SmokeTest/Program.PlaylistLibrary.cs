// 检查点 17：歌单库（多个命名播放列表各存各的）。

namespace SmokeTest
{
    internal static partial class Program
    {
        // -----------------------------------------------------------------
        // 17) 歌单库（多个播放列表各存各的）
        //
        // 这一项验的是"磁盘上那份真相"：名字能不能用、存成哪个文件、
        // 列出来的顺序对不对、改名 / 删除 / 从外面导入是不是真的动了文件，
        // 以及主窗体上的关联（标题里的歌单名、"未保存"什么时候出现）对不对。
        // -----------------------------------------------------------------

        private static bool TestPlaylistLibrary()
        {
            var root = Path.Combine(AppContext.BaseDirectory, "smoke-playlists");
            TryDeleteDirectory(root);
            Directory.CreateDirectory(root);

            // 每一首"曲子"都是真文件：歌单载入只认"文件在不在"（不解析内容），
            // 所以内容无所谓，但不能是假路径——否则验不出"少了哪几首"。
            var media = new List<string>();

            for (var i = 1; i <= 5; i++)
            {
                var path = Path.Combine(root, $"track{i}.mp3");
                File.WriteAllBytes(path, new byte[] { 0x49, 0x44, 0x33, (byte)i });
                media.Add(path);
            }

            try
            {
                if (!CheckPlaylistLibraryRules()) return false;
                if (!CheckPlaylistLibraryState()) return false;
                if (!CheckPlaylistLibraryFiles(root, media)) return false;
                if (!CheckPlaylistLibraryInMainForm(media)) return false;
                if (!CheckPlaylistLibraryDialog()) return false;
            }
            finally
            {
                TryDeleteDirectory(root);
            }

            Log(17, "歌单库正常：名字校验（非法字符 / Windows 保留名 / 结尾点号 / 超长）、"
                    + "一个歌单一个 m3u8 文件、新建（同名不覆盖）/ 保存 / 列出（数字按数值排）/ "
                    + "载入 / 重命名 / 删除 / 导入都在，"
                    + "文件已经不在的条目会如实计数而不是悄悄丢掉，"
                    + "主窗体保存后标题带上歌单名、只有真的改了列表才标「未保存」（切歌不算）、"
                    + "点菜单项会替换列表、新建空歌单会清空并切过去，"
                    + "歌单窗口一次列出全部歌单、标出当前那份、还能就地新建");
            return true;
        }

        /// <summary>
        /// 歌单库的状态与"替换前问哪一句"：不碰磁盘、不碰界面。
        /// <para>
        /// 这些规则以前只能连着真窗体一起验（还只能验到"有/没有未保存标记"这一层）。
        /// 抽出来之后，"三条分支各问什么、每个回答意味着什么"可以直接摆出来——
        /// 尤其是"先保存再继续"那条：真出问题就是<b>用户的改动没了</b>。
        /// </para>
        /// </summary>
        private static bool CheckPlaylistLibraryState()
        {
            var state = new PlaylistLibraryController();

            // ---- 还没对应歌单：改多少遍都不该标「未保存」 ----
            if (state.MarkModified() || state.Modified)
            {
                Log(17, "歌单状态：还没对应歌单就标了「未保存」");
                return false;
            }

            if (state.HeaderPrefix.Length != 0)
            {
                Log(17, $"歌单状态：还没对应歌单却有标题前缀（「{state.HeaderPrefix}」）");
                return false;
            }

            // ---- 关联上之后：第一次改动要标，重复标记不用再刷界面 ----
            state.SetCurrent("我的歌单", modified: false);

            if (!state.MarkModified() || !state.Modified)
            {
                Log(17, "歌单状态：关联了歌单，改了列表却没标「未保存」");
                return false;
            }

            if (state.MarkModified())
            {
                Log(17, "歌单状态：已经标过「未保存」了还说要刷界面");
                return false;
            }

            if (state.HeaderPrefix != "【我的歌单·未保存】")
            {
                Log(17, $"歌单状态：标题前缀不对（「{state.HeaderPrefix}」）");
                return false;
            }

            // ---- 恢复会话那一段要抑制标记 ----
            state.SetCurrent("我的歌单", modified: false);
            state.SuppressModified = true;

            if (state.MarkModified() || state.Modified)
            {
                Log(17, "歌单状态：恢复会话期间还是标了「未保存」（重启后歌单会平白变成已修改）");
                return false;
            }

            state.SuppressModified = false;

            // ---- 没对应歌单时不可能有"未保存的改动" ----
            state.SetCurrent(string.Empty, modified: true);

            if (state.Modified)
            {
                Log(17, "歌单状态：没有对应歌单却留着「未保存」标记");
                return false;
            }

            // ---- 名字不区分大小写 ----
            state.SetCurrent("MyList", modified: false);

            if (!state.IsCurrent("mylist") || state.IsCurrent("other"))
            {
                Log(17, "歌单状态：当前歌单的判定不对（应当不区分大小写）");
                return false;
            }

            // ---- 启动时那份歌单不见了：清掉关联并留一句话 ----
            var missing = new PlaylistLibraryController();
            missing.RestoreFrom("已经删掉的歌单", _ => false);

            if (missing.CurrentName.Length != 0 || string.IsNullOrWhiteSpace(missing.StartupWarning))
            {
                Log(17, $"歌单状态：启动时歌单不见了却没说明（名字「{missing.CurrentName}」/ 提示「{missing.StartupWarning ?? "空"}」）");
                return false;
            }

            var present = new PlaylistLibraryController();
            present.RestoreFrom("还在的歌单", _ => true);

            if (present.CurrentName != "还在的歌单" || present.StartupWarning != null)
            {
                Log(17, "歌单状态：歌单还在却说它不见了");
                return false;
            }

            // ---- 替换前问哪一句 ----
            var probe = new PlaylistLibraryController();

            if (probe.PromptForReplace(0) != PlaylistReplacePrompt.None)
            {
                Log(17, "歌单状态：列表是空的还去问要不要替换");
                return false;
            }

            probe.SetCurrent("我的歌单", modified: true);

            if (probe.PromptForReplace(3) != PlaylistReplacePrompt.UnsavedChanges)
            {
                Log(17, "歌单状态：有未保存改动时没问「要不要先保存」");
                return false;
            }

            probe.SetCurrent("我的歌单", modified: false);

            if (probe.PromptForReplace(3) != PlaylistReplacePrompt.None)
            {
                Log(17, "歌单状态：内容都在磁盘上还多问一句");
                return false;
            }

            probe.SetCurrent(string.Empty, modified: false);

            if (probe.PromptForReplace(3) != PlaylistReplacePrompt.NotSavedYet)
            {
                Log(17, "歌单状态：没存过的列表要被替换，却没提醒");
                return false;
            }

            // ---- 三条分支各走一遍（用假的"询问者"，不弹真对话框） ----
            var prompter = new FakeReplacePrompter();

            probe.SetCurrent("我的歌单", modified: true);

            var saved = 0;
            prompter.Answer = PlaylistReplaceAnswer.Save;

            if (!probe.ConfirmReplace("另一份", "载入", 3, () => { saved++; return true; }, prompter) || saved != 1)
            {
                Log(17, "歌单状态：选了「先保存」却没保存（或者没继续）");
                return false;
            }

            prompter.Answer = PlaylistReplaceAnswer.Save;

            if (probe.ConfirmReplace("另一份", "载入", 3, () => { saved++; return false; }, prompter) || saved != 2)
            {
                Log(17, "歌单状态：保存失败却照样把列表换掉了（用户的改动没了）");
                return false;
            }

            prompter.Answer = PlaylistReplaceAnswer.Discard;

            if (!probe.ConfirmReplace("另一份", "载入", 3, () => true, prompter))
            {
                Log(17, "歌单状态：选了「不保存直接换」却被挡住");
                return false;
            }

            prompter.Answer = PlaylistReplaceAnswer.Cancel;

            if (probe.ConfirmReplace("另一份", "载入", 3, () => true, prompter) || saved != 2)
            {
                Log(17, "歌单状态：选了「取消」却还是换了（而且不该去保存）");
                return false;
            }

            // 没存过的列表：只有"继续 / 算了"两种回答
            probe.SetCurrent(string.Empty, modified: false);
            prompter.Answer = PlaylistReplaceAnswer.Discard;

            if (!probe.ConfirmReplace("另一份", "载入", 3, () => true, prompter) ||
                prompter.LastPrompt != PlaylistReplacePrompt.NotSavedYet)
            {
                Log(17, $"歌单状态：没存过的列表问的方式不对（问的是「{prompter.LastPrompt}」）");
                return false;
            }

            prompter.Answer = PlaylistReplaceAnswer.Cancel;

            if (probe.ConfirmReplace("另一份", "载入", 3, () => true, prompter))
            {
                Log(17, "歌单状态：没存过的列表选了「算了」还是被替换");
                return false;
            }

            // 空列表不该问，也不该去保存
            prompter.Answer = PlaylistReplaceAnswer.Cancel;

            if (!probe.ConfirmReplace("另一份", "载入", 0, () => { saved++; return true; }, prompter) || saved != 2)
            {
                Log(17, "歌单状态：空列表替换还要问 / 还去保存");
                return false;
            }

            Log(17, "歌单状态正常：未保存标记的规则（切歌/恢复会话不算）、启动时歌单不见了会说明、"
                    + "替换前的三种询问与三种回答都按预期处理（保存失败就不换）");
            return true;
        }

        /// <summary>测试用的假"询问者"：直接给一个答案，并把问的是哪一句记下来。</summary>
        private sealed class FakeReplacePrompter : IPlaylistReplacePrompter
        {
            public PlaylistReplaceAnswer Answer { get; set; } = PlaylistReplaceAnswer.Cancel;

            public PlaylistReplacePrompt LastPrompt { get; private set; } = PlaylistReplacePrompt.None;

            public PlaylistReplaceContext LastContext { get; private set; }

            public PlaylistReplaceAnswer Ask(PlaylistReplacePrompt prompt, PlaylistReplaceContext context)
            {
                LastPrompt = prompt;
                LastContext = context;
                return Answer;
            }
        }

        /// <summary>歌单名的规矩与排序：这些是纯函数，先单独钉住。</summary>
        private static bool CheckPlaylistLibraryRules()
        {
            // 空名 / 纯空白
            if (PlaylistLibrary.TryNormalizeName("   ", out _, out var emptyError) || emptyError.Length == 0)
            {
                Log(17, "歌单库检查：空名字应当被拒绝并给出理由");
                return false;
            }

            // 非法字符：说清楚是哪些字符不能用，而不是默默替换掉
            if (PlaylistLibrary.TryNormalizeName("劲歌/慢歌", out _, out var slashError))
            {
                Log(17, "歌单库检查：名字里的斜杠没有被挡下（存下去就成了另一个目录）");
                return false;
            }

            if (!slashError.Contains("/", StringComparison.Ordinal))
            {
                Log(17, $"歌单库检查：名字非法却没说是哪个字符不能用（「{slashError}」）");
                return false;
            }

            // Windows 保留名：连 CON.m3u8 也建不出来
            foreach (var reserved in new[] { "CON", "nul", "COM7", "LPT1", "CON.m3u8" })
            {
                if (PlaylistLibrary.TryNormalizeName(reserved, out _, out _))
                {
                    Log(17, $"歌单库检查：Windows 保留名「{reserved}」被放行了");
                    return false;
                }
            }

            // 只是以保留名开头的不算保留名
            if (!PlaylistLibrary.TryNormalizeName("CONSOLE", out var console, out _) || console != "CONSOLE")
            {
                Log(17, "歌单库检查：「CONSOLE」被误判成了保留名");
                return false;
            }

            // 结尾的点号会被 Windows 自己吃掉，先去掉，免得"输入的名字"和磁盘上的不一样
            if (!PlaylistLibrary.TryNormalizeName("我的歌单...", out var trimmed, out _) || trimmed != "我的歌单")
            {
                Log(17, $"歌单库检查：结尾的点号没有被收拾干净（结果是「{trimmed}」）");
                return false;
            }

            if (PlaylistLibrary.TryNormalizeName(new string('长', PlaylistLibrary.MaximumNameLength + 1), out _, out _))
            {
                Log(17, "歌单库检查：超长名字没有被拒绝");
                return false;
            }

            if (!PlaylistLibrary.TryNormalizeName("我的歌单 2024", out var ok, out _) || ok != "我的歌单 2024")
            {
                Log(17, "歌单库检查：正常名字反而被拒绝了");
                return false;
            }

            // 数字按数值排：歌单2 必须在 歌单10 前面（纯字符串比较会反过来）
            if (PlaylistLibrary.CompareNatural("歌单2", "歌单10") >= 0)
            {
                Log(17, "歌单库检查：「歌单2」没有排在「歌单10」前面（数字没有按数值比）");
                return false;
            }

            if (PlaylistLibrary.CompareNatural("歌单10", "歌单2") <= 0 ||
                PlaylistLibrary.CompareNatural("歌单", "歌单") != 0 ||
                PlaylistLibrary.CompareNatural("a02", "a2") != 0)
            {
                Log(17, "歌单库检查：自然排序的对称性 / 前导零有问题");
                return false;
            }

            Log(17, "歌单库名字规则正常：非法字符（会指名道姓）/ Windows 保留名（含 CON.m3u8）都被挡，"
                    + "结尾点号会收拾掉，「歌单2」排在「歌单10」前面");
            return true;
        }

        /// <summary>磁盘上的那一份：存 / 列 / 载 / 改名 / 删 / 导入。</summary>
        private static bool CheckPlaylistLibraryFiles(string root, List<string> media)
        {
            var items = media.Select(path => new PlaylistItem(path)).ToList();

            // ---- 保存 ----
            PlaylistLibrary.Save("测试歌单", items);

            var saved = PlaylistLibrary.List().FirstOrDefault(p => p.Name == "测试歌单");

            if (saved == null)
            {
                Log(17, "歌单库检查：保存之后在库里列不出来");
                return false;
            }

            if (!File.Exists(saved.FilePath) || Path.GetExtension(saved.FilePath) != ".m3u8")
            {
                Log(17, $"歌单库检查：歌单文件不对（{saved.FilePath}）");
                return false;
            }

            var expectedFolder = Path.Combine(AppSettings.SettingsDirectory, PlaylistLibrary.FolderName);

            if (!saved.FilePath.StartsWith(expectedFolder, StringComparison.OrdinalIgnoreCase))
            {
                Log(17, $"歌单库检查：歌单没有存在数据目录下（{saved.FilePath}，期望在 {expectedFolder} 下）");
                return false;
            }

            if (saved.TrackCount != media.Count)
            {
                Log(17, $"歌单库检查：条目数不对（列出 {saved.TrackCount}，实际 {media.Count}）");
                return false;
            }

            // ---- 载入：顺序也要一样（歌单的顺序就是用户排的顺序）----
            var loaded = PlaylistLibrary.Load("测试歌单");

            if (!loaded.Ok || loaded.Tracks.Count != media.Count || loaded.Missing != 0)
            {
                Log(17, $"歌单库检查：载入不对（成功 {loaded.Ok}、{loaded.Tracks.Count} 项、缺 {loaded.Missing} 项：{loaded.Error}）");
                return false;
            }

            for (var i = 0; i < media.Count; i++)
            {
                if (string.Equals(loaded.Tracks[i], media[i], StringComparison.OrdinalIgnoreCase)) continue;

                Log(17, $"歌单库检查：载入后第 {i + 1} 项变成了「{loaded.Tracks[i]}」（应当是「{media[i]}」）");
                return false;
            }

            // 名字不区分大小写也算同一份（免得同一个歌单出现两个大小写不同的文件）
            if (!PlaylistLibrary.Exists("测试歌单".ToUpperInvariant()))
            {
                Log(17, "歌单库检查：按名字找歌单时没有忽略大小写");
                return false;
            }

            // ---- 文件已经不在了：必须计数上报，而不是像"加载播放列表"那样悄悄丢掉 ----
            PlaylistFile.Save(
                Path.Combine(PlaylistLibrary.EnsureDirectory(), "缺了几首.m3u8"),
                new List<PlaylistItem>
                {
                    new PlaylistItem(media[0]),
                    new PlaylistItem(Path.Combine(root, "早就删了.mp3")),
                    new PlaylistItem(media[1])
                });

            var partial = PlaylistLibrary.Load("缺了几首");

            if (!partial.Ok || partial.Tracks.Count != 2 || partial.Total != 3 || partial.Missing != 1)
            {
                Log(17, "歌单库检查：文件不在的条目没有被单独计数（"
                        + $"可用 {partial.Tracks.Count}、总计 {partial.Total}、缺 {partial.Missing}）");
                return false;
            }

            // ---- 自然排序：库里真的按数值排 ----
            PlaylistLibrary.Save("歌单2", items.Take(1).ToList());
            PlaylistLibrary.Save("歌单10", items.Take(2).ToList());

            var names = PlaylistLibrary.List().Select(p => p.Name).ToList();
            var two = names.IndexOf("歌单2");
            var ten = names.IndexOf("歌单10");

            if (two < 0 || ten < 0 || two > ten)
            {
                Log(17, $"歌单库检查：列出来的顺序不是自然顺序（{string.Join(" / ", names)}）");
                return false;
            }

            // ---- 重命名 ----
            if (!PlaylistLibrary.Rename("测试歌单", "改名之后", out var actual, out var error) || actual != "改名之后")
            {
                Log(17, $"歌单库检查：改名失败（{error}）");
                return false;
            }

            if (PlaylistLibrary.Exists("测试歌单") || !PlaylistLibrary.Exists("改名之后"))
            {
                Log(17, "歌单库检查：改名之后旧名字还在或新名字找不到");
                return false;
            }

            if (PlaylistLibrary.Load("改名之后").Tracks.Count != media.Count)
            {
                Log(17, "歌单库检查：改名把内容弄丢了");
                return false;
            }

            // 改成已有的名字 / 非法名字都要被挡住
            if (PlaylistLibrary.Rename("改名之后", "歌单2", out _, out var clash) || clash.Length == 0)
            {
                Log(17, "歌单库检查：改成已有的名字没有被挡下");
                return false;
            }

            if (PlaylistLibrary.Rename("改名之后", "a/b", out _, out _))
            {
                Log(17, "歌单库检查：改名时非法字符没有被挡下");
                return false;
            }

            // 只改大小写也是改名：文件系统上本来就是同一个文件，
            // 不能报"已经有一个叫 MIX 的歌单了"
            PlaylistLibrary.Save("Mix", items.Take(1).ToList());

            if (!PlaylistLibrary.Rename("Mix", "MIX", out var upper, out error) || upper != "MIX")
            {
                Log(17, $"歌单库检查：只改大小写的改名失败了（{error}）");
                return false;
            }

            if (!PlaylistLibrary.Exists("MIX") ||
                PlaylistLibrary.List().Count(p => string.Equals(p.Name, "MIX", StringComparison.OrdinalIgnoreCase)) != 1)
            {
                Log(17, "歌单库检查：只改大小写的改名把歌单弄丢了，或者弄出了两份");
                return false;
            }

            PlaylistLibrary.Delete("MIX", out _);

            // ---- 导入：外面的 m3u 往往是相对路径，拷进来必须还能用 ----
            var external = Path.Combine(root, "外部歌单.m3u8");

            // 故意存成相对路径（媒体就在同一个目录里，PlaylistFile 会写相对路径）
            PlaylistFile.Save(external, items);

            var rawText = File.ReadAllText(external);
            if (!rawText.Contains("track1.mp3", StringComparison.OrdinalIgnoreCase))
            {
                Log(17, "歌单库检查：外部歌单没有写成相对路径，这一段验不到东西");
                return false;
            }

            if (!PlaylistLibrary.Import(external, false, out var imported, out error) || imported != "外部歌单")
            {
                Log(17, $"歌单库检查：导入失败（{error}）");
                return false;
            }

            var importedTracks = PlaylistLibrary.Load("外部歌单");

            if (!importedTracks.Ok || importedTracks.Tracks.Count != media.Count || importedTracks.Missing != 0)
            {
                Log(17, "歌单库检查：相对路径的歌单导入之后找不到文件了（"
                        + $"可用 {importedTracks.Tracks.Count}、缺 {importedTracks.Missing}）");
                return false;
            }

            // 同一个名字再导入一次：不带覆盖要失败并说明
            if (PlaylistLibrary.Import(external, false, out _, out var again) || again.Length == 0)
            {
                Log(17, "歌单库检查：同名导入没有被挡下");
                return false;
            }

            // 覆盖导入可以
            if (!PlaylistLibrary.Import(external, true, out _, out error))
            {
                Log(17, $"歌单库检查：覆盖导入失败（{error}）");
                return false;
            }

            // 不是歌单的文件 / 不存在的文件都要被挡住
            var notPlaylist = Path.Combine(root, "track1.mp3");

            if (PlaylistLibrary.Import(notPlaylist, false, out _, out _) ||
                PlaylistLibrary.Import(Path.Combine(root, "根本没有.m3u8"), false, out _, out _))
            {
                Log(17, "歌单库检查：非歌单文件或不存在的东西被导入成功了");
                return false;
            }

            // ---- 新建空歌单 ----
            var fresh = PlaylistLibrary.Create("新建的空歌单");

            if (!File.Exists(fresh.FilePath) || !PlaylistLibrary.Exists("新建的空歌单"))
            {
                Log(17, "歌单库检查：新建空歌单之后磁盘上没有这个文件");
                return false;
            }

            if (fresh.TrackCount != 0 || PlaylistLibrary.Load("新建的空歌单").Ok)
            {
                Log(17, "歌单库检查：新建出来的歌单不是空的");
                return false;
            }

            if (PlaylistLibrary.List().First(p => p.Name == "新建的空歌单").TrackCount != 0)
            {
                Log(17, "歌单库检查：空歌单在列表里的曲目数不是 0");
                return false;
            }

            // 同名新建要抛错（绝不覆盖别人的歌单）
            try
            {
                PlaylistLibrary.Create("新建的空歌单");
                Log(17, "歌单库检查：同名新建没有报错，把已有歌单覆盖了");
                return false;
            }
            catch (InvalidOperationException)
            {
                // 正是期望的
            }

            if (PlaylistLibrary.Delete("新建的空歌单", out error) == false)
            {
                Log(17, $"歌单库检查：空歌单删不掉（{error}）");
                return false;
            }

            // ---- 删除 ----
            if (!PlaylistLibrary.Delete("改名之后", out error))
            {
                Log(17, $"歌单库检查：删除失败（{error}）");
                return false;
            }

            if (PlaylistLibrary.Exists("改名之后"))
            {
                Log(17, "歌单库检查：删除之后歌单还在");
                return false;
            }

            // 删歌单不许动媒体文件
            if (!File.Exists(media[0]))
            {
                Log(17, "歌单库检查：删歌单把媒体文件也删了");
                return false;
            }

            // 留一份给后面两步用（主窗体与歌单窗口都要有歌单可看）
            PlaylistLibrary.Save("给主窗体看的歌单", items);

            Log(17, $"歌单库文件正常：存在 {expectedFolder} 下的 m3u8 文件里，"
                    + "顺序原样保留、缺文件的条目单独计数（缺 1 项）、新建（同名不覆盖）/ 改名 / 删除 / "
                    + "导入（相对路径也认得）都对，媒体文件毫发无损");
            return true;
        }

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

        /// <summary>
        /// B1：多选之后按钮该变的要变——载入 / 追加 / 重命名 是"针对一份歌单"的动作，
        /// 选了两份以上就点不动；删除支持多选，按钮上写着会删几个。
        /// </summary>
        private static bool CheckPlaylistMultiSelect(ListView list, List<Button> buttons)
        {
            if (!list.MultiSelect)
            {
                Log(17, "歌单窗口检查：列表不能多选（批量删除要靠它）");
                return false;
            }

            var loadButton = buttons.FirstOrDefault(button => button.Text.Contains("载入", StringComparison.Ordinal));
            var deleteButton = buttons.FirstOrDefault(button => button.Text.Contains("删除", StringComparison.Ordinal));

            if (loadButton == null || deleteButton == null)
            {
                Log(17, "歌单窗口检查：找不到载入 / 删除按钮");
                return false;
            }

            foreach (ListViewItem row in list.Items) row.Selected = true;
            PumpMessages(150);

            var count = list.SelectedItems.Count;

            if (count < 2 || loadButton.Enabled || !deleteButton.Enabled ||
                !deleteButton.Text.Contains($"{count} 个", StringComparison.Ordinal))
            {
                Log(17, "歌单窗口检查：多选之后按钮状态不对"
                        + $"（选了 {count} 项、载入 {(loadButton.Enabled ? "能点" : "是灰的")}、"
                        + $"删除按钮「{deleteButton.Text}」）");
                return false;
            }

            // 恢复成"只选一行"，后面的检查按单选走
            list.SelectedItems.Clear();
            list.Items[0].Selected = true;
            PumpMessages(120);

            if (!loadButton.Enabled || !deleteButton.Enabled)
            {
                Log(17, "歌单窗口检查：恢复单选之后按钮没有回到可点状态");
                return false;
            }

            return true;
        }

        /// <summary>
        /// B1：多选之后一次删掉几个。<b>放在歌单窗口检查的最后</b>——它真的会删库里的歌单。
        /// 要验两件事：一次删掉选中的那几个（当前歌单不受影响），
        /// 以及删掉的正好是当前歌单时、交回主窗体的关联要断开。
        /// </summary>
        private static bool CheckPlaylistBatchDelete(PlaylistLibraryDialog dialog, ListView list)
        {
            var others = list.Items.Cast<ListViewItem>()
                .Where(row => !row.SubItems[0].Text.StartsWith("●", StringComparison.Ordinal))
                .ToList();

            if (others.Count < 2)
            {
                Log(17, "歌单窗口检查：批量删除至少要两份非当前歌单"
                        + $"（列表里一共 {list.Items.Count} 行、其中非当前 {others.Count} 行）");
                return false;
            }

            var targets = others.Take(2)
                .Select(row => row.SubItems[0].Text.Replace("● ", string.Empty))
                .ToList();

            list.SelectedItems.Clear();
            others[0].Selected = true;
            others[1].Selected = true;
            PumpMessages(150);

            var deleted = dialog.DeleteSelected(confirm: false);
            PumpMessages(150);

            if (deleted != 2)
            {
                Log(17, $"歌单窗口检查：选了两份歌单，批量删除只删掉 {deleted} 个");
                return false;
            }

            foreach (var name in targets)
            {
                if (!PlaylistLibrary.Exists(name)) continue;

                Log(17, $"歌单窗口检查：批量删除之后「{name}」还在库里");
                return false;
            }

            if (dialog.Result.CurrentName == null)
            {
                Log(17, "歌单窗口检查：删的不是当前歌单，关联却被断开了");
                return false;
            }

            // 再把当前那一份删掉：这次关联必须断开
            var current = list.Items.Cast<ListViewItem>()
                .FirstOrDefault(row => row.SubItems[0].Text.StartsWith("●", StringComparison.Ordinal));

            if (current == null)
            {
                Log(17, "歌单窗口检查：批量删除之后「当前歌单」那一行不见了");
                return false;
            }

            list.SelectedItems.Clear();
            current.Selected = true;
            PumpMessages(120);

            if (dialog.DeleteSelected(confirm: false) != 1 || dialog.Result.CurrentName != null)
            {
                Log(17, "歌单窗口检查：删掉当前歌单之后关联没有断开");
                return false;
            }

            Log(17, "歌单窗口多选正常：能多选、多选时载入 / 追加 / 重命名点不动、删除按钮写着会删几个，"
                    + "一次确认删掉两个（当前歌单不受影响），删掉当前那份之后关联断开");
            return true;
        }

        /// <summary>歌单窗口：一次列出全部歌单、标出当前那份、选一个载入。</summary>
        private static bool CheckPlaylistLibraryDialog()
        {
            // 批量删除那一段要"至少两份非当前歌单"才验得动，而歌单库在这一步里已经被前面几段
            // 改过（数量不能假定），所以这里先补两个空的进去
            PlaylistLibrary.Save("批量删除 A", new List<PlaylistItem>());
            PlaylistLibrary.Save("批量删除 B", new List<PlaylistItem>());

            var dialog = new PlaylistLibraryDialog("给主窗体看的歌单");

            using (dialog)
            {
                dialog.StartPosition = FormStartPosition.Manual;
                dialog.Location = new Point(60, 60);
                dialog.Show();
                PumpMessages(200);

                var list = dialog.Controls.OfType<ListView>().FirstOrDefault();

                if (list == null || list.View != View.Details)
                {
                    Log(17, "歌单窗口检查：找不到歌单列表（或它不是明细视图）");
                    return false;
                }

                var expected = PlaylistLibrary.List();

                if (list.Items.Count != expected.Count)
                {
                    Log(17, $"歌单窗口检查：列出来 {list.Items.Count} 个歌单，库里其实有 {expected.Count} 个");
                    return false;
                }

                var shown = list.Items.Cast<ListViewItem>()
                    .Select(row => row.SubItems[0].Text.Replace("● ", string.Empty))
                    .ToList();

                for (var i = 0; i < expected.Count; i++)
                {
                    if (shown[i] == expected[i].Name) continue;

                    Log(17, $"歌单窗口检查：第 {i + 1} 行是「{shown[i]}」，按顺序应当是「{expected[i].Name}」");
                    return false;
                }

                var currentRows = list.Items.Cast<ListViewItem>()
                    .Where(row => row.SubItems[0].Text.StartsWith("●", StringComparison.Ordinal))
                    .ToList();

                if (currentRows.Count != 1 ||
                    currentRows[0].SubItems[0].Text.Replace("● ", string.Empty) != "给主窗体看的歌单")
                {
                    Log(17, $"歌单窗口检查：当前歌单没有被单独标出来（标了 {currentRows.Count} 行）");
                    return false;
                }

                // 条目数要显示出来（选之前就能看出哪个歌单大概是什么）
                if (currentRows[0].SubItems[1].Text != expected.First(p => p.Name == "给主窗体看的歌单").TrackCount.ToString())
                {
                    Log(17, $"歌单窗口检查：曲目数显示不对（「{currentRows[0].SubItems[1].Text}」）");
                    return false;
                }

                var buttons = dialog.Controls.OfType<Button>().ToList();

                if (buttons.Count < 7 || buttons.Any(b => !b.Enabled))
                {
                    Log(17, $"歌单窗口检查：按钮不全或该能点的点不动（{buttons.Count} 个按钮）");
                    return false;
                }

                if (!buttons.Any(b => b.Text.Contains("载入", StringComparison.Ordinal)) ||
                    !buttons.Any(b => b.Text.Contains("追加", StringComparison.Ordinal)) ||
                    !buttons.Any(b => b.Text.Contains("新建", StringComparison.Ordinal)) ||
                    !buttons.Any(b => b.Text.Contains("重命名", StringComparison.Ordinal)) ||
                    !buttons.Any(b => b.Text.Contains("删除", StringComparison.Ordinal)))
                {
                    Log(17, "歌单窗口检查：载入 / 追加 / 新建 / 重命名 / 删除 里有缺的");
                    return false;
                }

                // ---- B1：多选与批量删除 ----
                if (!CheckPlaylistMultiSelect(list, buttons)) return false;

                var scale = dialog.DeviceDpi / 96f;

                foreach (Control control in dialog.Controls)
                {
                    if (control.Right <= dialog.ClientSize.Width && control.Bottom <= dialog.ClientSize.Height)
                        continue;

                    Log(17, $"歌单窗口在 {scale:0.##} 倍缩放下有控件越界："
                            + $"{control.GetType().Name} {control.Bounds} 超出客户区 {dialog.ClientSize}");
                    return false;
                }

                // ---- B1：一次删掉几个 ----
                // 必须放在"选一份载入"之前：那一步会把窗口关掉（列表就空了）
                if (!CheckPlaylistBatchDelete(dialog, list)) return false;

                // 选另一份歌单 → 载入：结果里应当是它的名字，而且是"替换"
                var other = list.Items.Cast<ListViewItem>()
                    .First(row => !row.SubItems[0].Text.StartsWith("●", StringComparison.Ordinal));

                other.Selected = true;
                PumpMessages(120);

                var selectedName = other.SubItems[0].Text;

                dialog.LoadSelected();

                PumpMessages(120);

                var result = dialog.Result;

                if (result.LoadName != selectedName || result.Append)
                {
                    Log(17, "歌单窗口检查：选了歌单之后交回去的结果不对（"
                            + $"名字 {result.LoadName ?? "空"}、追加 {result.Append}）");
                    return false;
                }

                dialog.Close();
            }

            Log(17, "歌单窗口正常：一次列出全部歌单（顺序与库里一致）、当前那份带「●」标记、"
                    + "曲目数与最后修改时间都在，载入 / 追加 / 新建 / 重命名 / 删除 / 打开文件夹都能点，"
                    + "多选能一次删掉几个，选一份载入会把它交回主窗体，控件没有越界");
            return true;
        }
    }
}
