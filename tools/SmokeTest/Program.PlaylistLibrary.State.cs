// 歌单库 —— 状态与规则（未保存标记、丢文件说明、替换询问的三问三答）。
//
// 从 Program.PlaylistLibrary.cs 按 ARCHITECTURE 17.2 的规矩整块搬过来：成员一个字没动。


namespace SmokeTest
{
    internal static partial class Program
    {
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
    }
}
