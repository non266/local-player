using System;

namespace 播放器.Core
{
    /// <summary>替换当前列表之前要问哪一句。</summary>
    public enum PlaylistReplacePrompt
    {
        /// <summary>不用问：列表是空的，替换掉什么都不会丢。</summary>
        None,

        /// <summary>当前歌单有未保存的改动：要问"先保存 / 不保存 / 取消"。</summary>
        UnsavedChanges,

        /// <summary>当前列表还没存成歌单：替换掉就找不回来了，要问一句。</summary>
        NotSavedYet
    }

    /// <summary>对上面那句询问的回答。</summary>
    public enum PlaylistReplaceAnswer
    {
        /// <summary>什么都不做。</summary>
        Cancel,

        /// <summary>先把当前列表覆盖保存，再继续。</summary>
        Save,

        /// <summary>不保存，直接继续。</summary>
        Discard
    }

    /// <summary>询问时要摆在界面上的上下文。</summary>
    public readonly struct PlaylistReplaceContext
    {
        public PlaylistReplaceContext(string currentName, string incomingName, string nextAction, int itemCount)
        {
            CurrentName = currentName;
            IncomingName = incomingName;
            NextAction = nextAction;
            ItemCount = itemCount;
        }

        /// <summary>当前对应的歌单名（可能为空 = 还没存成歌单）。</summary>
        public string CurrentName { get; }

        /// <summary>要载入 / 切过去的那一份。</summary>
        public string IncomingName { get; }

        /// <summary>紧接着要做的动作（"载入" / "新建"），只用来拼提示语。</summary>
        public string NextAction { get; }

        /// <summary>当前列表有几项。</summary>
        public int ItemCount { get; }
    }

    /// <summary>
    /// 把"要不要替换当前列表"这件事问出去。窗体用它弹真的对话框，
    /// 测试给一个假的就能把三条分支都走一遍——不用真去点模态窗口。
    /// </summary>
    public interface IPlaylistReplacePrompter
    {
        PlaylistReplaceAnswer Ask(PlaylistReplacePrompt prompt, PlaylistReplaceContext context);
    }

    /// <summary>
    /// 歌单库的状态与规则：当前列表对应哪份歌单、有没有未保存的改动、
    /// 替换之前要不要问一句、标题前缀长什么样。
    /// <para>
    /// 这几样原来散在 <c>MainForm.Playlists.cs</c> 的四个私有字段里
    /// （<c>_currentPlaylistName</c> / <c>_playlistModified</c> /
    /// <c>_suppressPlaylistModified</c> / <c>_playlistLibraryWarning</c>），
    /// 规则和"弹哪个对话框、写哪行设置、刷哪块界面"揉在同一个方法里。
    /// 抽出来之后：<b>状态和规则在这里，对话框、设置、界面刷新留在窗体</b>。
    /// </para>
    /// <para>
    /// 关键约定：<b>不自动写回</b>。改了列表只把状态标成「未保存」，
    /// 只有用户点了保存（或菜单里的「覆盖保存」）才动磁盘上的歌单。
    /// 这里不碰磁盘、不碰界面，只回答"现在算不算有改动""该问哪一句"。
    /// </para>
    /// </summary>
    public sealed class PlaylistLibraryController
    {
        /// <summary>当前列表对应歌单库里的哪一份（名字）；空表示还没存成歌单。</summary>
        public string CurrentName { get; private set; } = string.Empty;

        /// <summary>当前列表相对它对应的歌单有没有改动（只算用户改的，切歌不算）。</summary>
        public bool Modified { get; private set; }

        /// <summary>恢复会话的过程中不要标记「未保存」——那时的变化不是用户刚改的。</summary>
        public bool SuppressModified { get; set; }

        /// <summary>启动时发现「当前歌单」已经不在歌单库里，留一句话给状态栏。</summary>
        public string? StartupWarning { get; private set; }

        /// <summary>这份歌单就是当前列表对应的那一份吗（名字不区分大小写）。</summary>
        public bool IsCurrent(string name) =>
            CurrentName.Length > 0 &&
            string.Equals(name, CurrentName, StringComparison.CurrentCultureIgnoreCase);

        /// <summary>设置"当前列表对应哪份歌单"。没有对应歌单时无所谓"未保存的改动"。</summary>
        public void SetCurrent(string? name, bool modified)
        {
            CurrentName = name ?? string.Empty;
            Modified = modified && CurrentName.Length > 0;
        }

        /// <summary>
        /// 列表内容变了，标「未保存」。返回<b>要不要刷界面</b>。
        /// <para>正在恢复会话（<see cref="SuppressModified"/>）、还没对应歌单、已经标过——
        /// 这三种都不用刷，也不该标。</para>
        /// </summary>
        public bool MarkModified()
        {
            if (SuppressModified) return false;
            if (CurrentName.Length == 0) return false;
            if (Modified) return false;

            Modified = true;
            return true;
        }

        /// <summary>标题前缀："【歌单名】"；有未保存改动时是"【歌单名·未保存】"。</summary>
        public string HeaderPrefix =>
            CurrentName.Length == 0
                ? string.Empty
                : Modified
                    ? $"【{CurrentName}·未保存】"
                    : $"【{CurrentName}】";

        /// <summary>
        /// 启动时把设置里记的「当前歌单」接上。对应的文件已经不在了就如实说明：
        /// 名字清掉、留下 <see cref="StartupWarning"/>（静默少一个关联，用户只会以为程序忘了）。
        /// </summary>
        public void RestoreFrom(string? savedName, Func<string, bool> exists)
        {
            if (exists == null) throw new ArgumentNullException(nameof(exists));

            CurrentName = savedName ?? string.Empty;

            if (CurrentName.Length == 0) return;
            if (exists(CurrentName)) return;

            StartupWarning = $"歌单「{CurrentName}」已经不在歌单库里了，本次启动没有关联任何歌单。";
            CurrentName = string.Empty;
        }

        /// <summary>替换当前列表之前要问哪一句。</summary>
        public PlaylistReplacePrompt PromptForReplace(int itemCount)
        {
            if (itemCount == 0) return PlaylistReplacePrompt.None;

            if (Modified && CurrentName.Length > 0) return PlaylistReplacePrompt.UnsavedChanges;

            // 有对应歌单又没有改动：直接换（内容都在磁盘上，丢不了）
            if (CurrentName.Length > 0) return PlaylistReplacePrompt.None;

            // 一点点攒起来的列表：替换掉就找不回来了
            return PlaylistReplacePrompt.NotSavedYet;
        }

        /// <summary>
        /// 替换当前列表前的询问，以及"先保存再继续"这一步。
        /// <para><paramref name="saveCurrent"/> 就是"把当前列表覆盖保存"（由调用方提供，
        /// 所以这里既不碰界面也不碰磁盘）；它返回 <c>false</c> 表示保存没成功，那就别继续替换。</para>
        /// </summary>
        public bool ConfirmReplace(
            string incomingName,
            string nextAction,
            int itemCount,
            Func<bool> saveCurrent,
            IPlaylistReplacePrompter prompter)
        {
            if (saveCurrent == null) throw new ArgumentNullException(nameof(saveCurrent));
            if (prompter == null) throw new ArgumentNullException(nameof(prompter));

            var prompt = PromptForReplace(itemCount);

            if (prompt == PlaylistReplacePrompt.None) return true;

            var context = new PlaylistReplaceContext(CurrentName, incomingName, nextAction, itemCount);
            var answer = prompter.Ask(prompt, context);

            if (prompt == PlaylistReplacePrompt.UnsavedChanges && answer == PlaylistReplaceAnswer.Save)
                return saveCurrent();

            return answer != PlaylistReplaceAnswer.Cancel;
        }
    }
}
