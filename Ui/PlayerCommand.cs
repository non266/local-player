using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace 播放器.Ui
{
    /// <summary>
    /// 播放器里"用户能下的一条命令"。
    /// <para>
    /// 单列一份目录，是因为同一件事在界面上有六张脸：菜单、快捷键、工具栏、托盘、
    /// 任务栏缩略图按钮、媒体键 / SMTC / 卡片 / 桌面歌词控制条。以前六处各接各的
    /// （<see cref="MainForm.Shortcuts"/> 手写 switch、F1 帮助手写长字符串、设计器里
    /// 又写一遍 <c>ShortcutKeyDisplayString</c>），漏一处就是"菜单里有、按下去没反应"。
    /// 现在这些都从这一份目录来：显示什么键、帮助里怎么写、按下后执行哪条命令、
    /// 能不能点，只有一个答案。
    /// </para>
    /// <para>
    /// <b>只放"命令"，不放"设置项"</b>：主题 / 倍速 / 画面比例 / 循环模式这些是单选设置，
    /// 只有菜单一个宿主（<c>ApplyRateToMenu</c> 这类整组刷新），搬进目录没有收益、还会把
    /// 目录撑成设置表。判据：一个动作出现在 ≥2 个宿主上，或者有快捷键，才算命令。
    /// </para>
    /// <para>
    /// 放在 <c>Ui</c> 而不是 <c>Core</c>：它要用 <see cref="Keys"/>（WinForms 的类型），
    /// 而 <c>Core</c> 一直不引用 WinForms。目录本身不碰窗口、不碰引擎。
    /// </para>
    /// </summary>
    internal enum PlayerCommandId
    {
        /// <summary>播放 / 暂停（菜单、Space、托盘、任务栏、媒体键、两张卡片、桌面歌词控制条）。</summary>
        PlayPause,

        /// <summary>播放（"已经在播就什么都不做"）。工具栏上那个 ▶ 用这个，语义与「播放 / 暂停」不同。</summary>
        Play,

        /// <summary>暂停（"没在播就什么都不做"）。工具栏上那个 ⏸ 与 SMTC 用这个。</summary>
        Pause,

        Stop,
        Previous,
        Next,
        SeekBack,
        SeekForward,
        VolumeUp,
        VolumeDown,
        Mute,
        Snapshot,
        ToggleSubtitles,
        OpenSubtitle,
        TogglePlaylist,
        ToggleFullscreen,
        ToggleWindowMode,
        ToggleMiniMode,
        ToggleDesktopLyrics,
        OpenFiles,
        OpenFolder,
        OpenUrl,
        LoadPlaylistFile,
        SaveAsPlaylist,
        ExportPlaylist
    }

    /// <summary>一条命令的说明：帮助里那句话、归哪一组、有没有勾选态、默认快捷键。</summary>
    internal sealed class PlayerCommandDefinition
    {
        private readonly Keys[] _shortcuts;

        public PlayerCommandDefinition(
            PlayerCommandId id,
            string helpText,
            string category,
            bool checkable,
            params Keys[] shortcuts)
        {
            Id = id;
            HelpText = helpText;
            Category = category;
            Checkable = checkable;
            _shortcuts = shortcuts ?? Array.Empty<Keys>();
        }

        public PlayerCommandId Id { get; }

        /// <summary>F1 帮助里那句说明（不带加速键的 &amp;）。</summary>
        public string HelpText { get; }

        /// <summary>帮助里的分组标题（播放控制 / 声音 / 文件 / 字幕与画面 / 界面）。</summary>
        public string Category { get; }

        /// <summary>菜单项上要不要画勾（播放列表、全屏、桌面歌词、静音这些）。</summary>
        public bool Checkable { get; }

        /// <summary>有快捷键才进 F1 帮助：没有键的命令（播放 / 暂停这种）写进帮助只会占地方。</summary>
        public bool ShowInHelp => _shortcuts.Length > 0;

        /// <summary>全部快捷键（测试要逐条核对"按下去执行的是不是同一条命令"）。</summary>
        internal IReadOnlyList<Keys> AllShortcuts => _shortcuts;

        /// <summary>全部快捷键的显示串，帮助里用（例如 <c>PgUp / Ctrl+←</c>）。</summary>
        public string Display => string.Join(" / ", _shortcuts.Select(PlayerCommandCatalog.FormatKeys));

        /// <summary>菜单项只显示第一个键（与 1.11.0 的菜单保持一致，避免"菜单上突然多出一截"）。</summary>
        public string? MenuDisplay =>
            _shortcuts.Length == 0 ? null : PlayerCommandCatalog.FormatKeys(_shortcuts[0]);

        /// <summary>按下这个键该执行哪条命令（无映射时返回 false）。</summary>
        public bool Matches(Keys keyData) => _shortcuts.Any(shortcut => shortcut == keyData);
    }

    /// <summary>当前界面状态：命令的可用性与勾选态都由它推出来（一次算好，六张脸共用）。</summary>
    internal readonly record struct CommandAvailability(
        bool HasMedia,
        bool IsPlaying,
        bool HasPlaylistItems,
        bool IsAudioWindow,
        bool IsMiniWindow,
        bool IsFullscreen);

    /// <summary>
    /// 命令目录：唯一的命令清单 + 快捷键映射 + F1 帮助正文 + 可用性规则。
    /// <para>纯数据与纯函数，不碰窗口、不碰引擎，因此可以直接拿来断言。</para>
    /// </summary>
    internal static class PlayerCommandCatalog
    {
        /// <summary>帮助里分组的顺序（按用户找东西的顺序排，不按内部枚举）。</summary>
        private static readonly string[] CategoryOrder =
        {
            "播放控制", "声音", "文件与字幕", "界面"
        };

        /// <summary>全部命令。顺序即帮助里的顺序；没有快捷键的条目不会出现在帮助里。</summary>
        public static IReadOnlyList<PlayerCommandDefinition> All { get; } = new[]
        {
            // ---- 播放控制 ----
            new PlayerCommandDefinition(PlayerCommandId.PlayPause, "播放 / 暂停", "播放控制", false, Keys.Space),
            new PlayerCommandDefinition(PlayerCommandId.Play, "播放（不切换）", "播放控制", false),
            new PlayerCommandDefinition(PlayerCommandId.Pause, "暂停", "播放控制", false),
            new PlayerCommandDefinition(PlayerCommandId.Stop, "停止", "播放控制", false, Keys.S),
            new PlayerCommandDefinition(
                PlayerCommandId.Previous, "上一个", "播放控制", false, Keys.PageUp, Keys.Control | Keys.Left),
            new PlayerCommandDefinition(
                PlayerCommandId.Next, "下一个", "播放控制", false, Keys.PageDown, Keys.Control | Keys.Right),
            new PlayerCommandDefinition(PlayerCommandId.SeekBack, "快退 5 秒", "播放控制", false, Keys.Left),
            new PlayerCommandDefinition(PlayerCommandId.SeekForward, "快进 5 秒", "播放控制", false, Keys.Right),

            // ---- 声音 ----
            new PlayerCommandDefinition(PlayerCommandId.VolumeUp, "音量增大", "声音", false, Keys.Up),
            new PlayerCommandDefinition(PlayerCommandId.VolumeDown, "音量减小", "声音", false, Keys.Down),
            new PlayerCommandDefinition(PlayerCommandId.Mute, "静音开关", "声音", true, Keys.M),

            // ---- 文件与字幕 ----
            new PlayerCommandDefinition(PlayerCommandId.OpenFiles, "打开文件", "文件与字幕", false, Keys.Control | Keys.O),
            new PlayerCommandDefinition(
                PlayerCommandId.OpenFolder, "打开文件夹（递归）", "文件与字幕", false, Keys.Control | Keys.Shift | Keys.O),
            new PlayerCommandDefinition(PlayerCommandId.OpenUrl, "打开网络串流", "文件与字幕", false, Keys.Control | Keys.U),
            new PlayerCommandDefinition(
                PlayerCommandId.SaveAsPlaylist,
                "保存当前列表为歌单（存进歌单库）",
                "文件与字幕",
                false,
                Keys.Control | Keys.S),
            new PlayerCommandDefinition(
                PlayerCommandId.ExportPlaylist, "另存为 m3u 文件", "文件与字幕", false, Keys.Control | Keys.Shift | Keys.S),
            new PlayerCommandDefinition(
                PlayerCommandId.LoadPlaylistFile,
                "从 m3u 文件追加到当前列表",
                "文件与字幕",
                false,
                Keys.Control | Keys.L),
            new PlayerCommandDefinition(PlayerCommandId.OpenSubtitle, "加载字幕文件", "文件与字幕", false, Keys.Control | Keys.T),
            new PlayerCommandDefinition(
                PlayerCommandId.ToggleSubtitles, "关闭 / 打开字幕", "文件与字幕", false, Keys.V),
            new PlayerCommandDefinition(PlayerCommandId.Snapshot, "截取当前画面", "文件与字幕", false, Keys.Control | Keys.P),

            // ---- 界面 ----
            new PlayerCommandDefinition(PlayerCommandId.TogglePlaylist, "显示 / 隐藏播放列表", "界面", true, Keys.F9),
            new PlayerCommandDefinition(PlayerCommandId.ToggleDesktopLyrics, "开 / 关桌面歌词", "界面", true, Keys.Control | Keys.D),
            new PlayerCommandDefinition(
                PlayerCommandId.ToggleWindowMode,
                "音频窗口 / 视频窗口（翻一下，并固定成那个形态）",
                "界面",
                false,
                Keys.Control | Keys.Alt | Keys.M),
            new PlayerCommandDefinition(
                PlayerCommandId.ToggleMiniMode,
                "迷你模式（只留一张小卡片）",
                "界面",
                true,
                Keys.Control | Keys.Alt | Keys.N),
            new PlayerCommandDefinition(
                PlayerCommandId.ToggleFullscreen, "进入 / 退出全屏", "界面", false, Keys.F11, Keys.F)
        };

        /// <summary>按 id 取定义；目录里没有就是编程错误，直接抛（早红比静默空转好）。</summary>
        public static PlayerCommandDefinition Get(PlayerCommandId id)
        {
            foreach (var definition in All)
            {
                if (definition.Id == id) return definition;
            }

            throw new ArgumentOutOfRangeException(nameof(id), id, "命令目录里没有这一条");
        }

        /// <summary>把一次按键翻译成命令；没人接的键返回 <c>false</c>。</summary>
        public static bool TryMap(Keys keyData, out PlayerCommandId id)
        {
            foreach (var definition in All)
            {
                if (!definition.Matches(keyData)) continue;

                id = definition.Id;
                return true;
            }

            id = default;
            return false;
        }

        /// <summary>
        /// 这条命令现在能不能点。
        /// <para>
        /// 规则与 1.11.0 的 <c>UpdateTransportState</c> 完全一致（播放看"有媒体或有列表项"、
        /// 停止看"有媒体"、上一首 / 下一首看"有列表项"、截图看"正在播"）；
        /// 音频 / 迷你窗口里没有播放列表与全屏，那几项点不动。
        /// </para>
        /// </summary>
        public static bool CanExecute(PlayerCommandId id, CommandAvailability availability) => id switch
        {
            PlayerCommandId.PlayPause => availability.HasMedia || availability.HasPlaylistItems,
            PlayerCommandId.Play => availability.HasMedia || availability.HasPlaylistItems,
            PlayerCommandId.Pause => availability.HasMedia && availability.IsPlaying,
            PlayerCommandId.Stop => availability.HasMedia,
            PlayerCommandId.Previous => availability.HasPlaylistItems,
            PlayerCommandId.Next => availability.HasPlaylistItems,
            PlayerCommandId.SeekBack => availability.HasMedia,
            PlayerCommandId.SeekForward => availability.HasMedia,
            PlayerCommandId.VolumeUp => true,
            PlayerCommandId.VolumeDown => true,
            PlayerCommandId.Mute => true,
            PlayerCommandId.Snapshot => availability.HasMedia && availability.IsPlaying,
            PlayerCommandId.ToggleSubtitles => availability.HasMedia,
            PlayerCommandId.OpenSubtitle => availability.HasMedia,
            PlayerCommandId.TogglePlaylist => !availability.IsAudioWindow && !availability.IsMiniWindow,
            PlayerCommandId.ToggleFullscreen => !availability.IsAudioWindow && !availability.IsMiniWindow,
            PlayerCommandId.ToggleWindowMode => !availability.IsFullscreen,
            PlayerCommandId.ToggleMiniMode => !availability.IsFullscreen,
            PlayerCommandId.ToggleDesktopLyrics => true,
            PlayerCommandId.OpenFiles => true,
            PlayerCommandId.OpenFolder => true,
            PlayerCommandId.OpenUrl => true,
            PlayerCommandId.LoadPlaylistFile => true,
            PlayerCommandId.SaveAsPlaylist => availability.HasPlaylistItems,
            PlayerCommandId.ExportPlaylist => availability.HasPlaylistItems,
            _ => false
        };

        /// <summary>F1 帮助正文（只有目录里的条目；鼠标手势 / 视频逐帧那些另有一段，见 MainForm）。</summary>
        public static string FormatHelp()
        {
            var text = new StringBuilder();

            foreach (var category in CategoryOrder)
            {
                var entries = All
                    .Where(definition => definition.ShowInHelp && definition.Category == category)
                    .ToList();

                if (entries.Count == 0) continue;

                text.Append(category).AppendLine();

                foreach (var definition in entries)
                {
                    text.Append("    ").Append(definition.Display.PadRight(17)).AppendLine(definition.HelpText);
                }

                text.AppendLine();
            }

            return text.ToString();
        }

        /// <summary>把一个 <see cref="Keys"/> 画成菜单 / 帮助里的样子（与 1.11.0 的字面量逐字一致）。</summary>
        public static string FormatKeys(Keys keys)
        {
            var parts = new List<string>();

            if ((keys & Keys.Control) != 0) parts.Add("Ctrl");
            if ((keys & Keys.Alt) != 0) parts.Add("Alt");
            if ((keys & Keys.Shift) != 0) parts.Add("Shift");

            var code = keys & Keys.KeyCode;

            parts.Add(code switch
            {
                Keys.Space => "Space",
                Keys.Left => "←",
                Keys.Right => "→",
                Keys.Up => "↑",
                Keys.Down => "↓",
                Keys.PageUp => "PgUp",
                Keys.PageDown => "PgDn",
                Keys.Enter => "Enter",
                Keys.Escape => "Esc",
                Keys.Delete => "Delete",
                Keys.OemCloseBrackets => "]",
                Keys.OemOpenBrackets => "[",
                _ => code.ToString()
            });

            return string.Join("+", parts);
        }
    }
}
