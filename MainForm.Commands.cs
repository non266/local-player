using System;
using System.Collections.Generic;
using System.Windows.Forms;
using 播放器.Core;
using 播放器.Ui;

namespace 播放器
{
    /// <summary>
    /// 一条"菜单项 / 工具条项 ↔ 命令"的绑定。
    /// <para>
    /// 只给冒烟测试与状态刷新用：测试按它核对"菜单上写的键"与目录一致，
    /// 刷新按它把可用性 / 勾选态推回去。
    /// </para>
    /// </summary>
    internal sealed class CommandBinding
    {
        public CommandBinding(ToolStripItem item, PlayerCommandId id)
            : this(item, id, CaptureTooltip(item))
        {
        }

        private CommandBinding(ToolStripItem item, PlayerCommandId id, string? tooltipBase)
        {
            Item = item;
            Id = id;
            TooltipBase = tooltipBase;
        }

        public ToolStripItem Item { get; }

        public PlayerCommandId Id { get; }

        /// <summary>
        /// 绑定那一刻的提示文字（去掉末尾「(键)」那一段）。1.29.0 改了键之后，
        /// 提示要能重新拼成「名称 (新键)」——不记下原文就只能显示旧键。
        /// </summary>
        public string? TooltipBase { get; }

        private static string? CaptureTooltip(ToolStripItem item)
        {
            var text = item.ToolTipText;

            if (string.IsNullOrEmpty(text)) return null;

            var index = text.LastIndexOf(" (", StringComparison.Ordinal);

            return index > 0 && text.EndsWith(")", StringComparison.Ordinal)
                ? text[..index]
                : text;
        }
    }

    /// <summary>
    /// 命令层：目录（<see cref="PlayerCommandCatalog"/>）之外的"执行"那一半。
    /// <para>
    /// 所有界面入口——菜单、快捷键、工具栏、托盘、任务栏缩略图按钮、全局媒体键 / SMTC、
    /// 卡片上的播放控件条、桌面歌词控制条——都只调 <see cref="ExecuteCommand"/>。
    /// 这样"这个按钮到底做了什么"只有一个答案，也不会再出现"菜单里改了、托盘那句没改"。
    /// </para>
    /// <para>
    /// 这里只做分派与状态刷新，业务仍在各自的方法里（下一轮才会搬进
    /// <c>PlaybackCoordinator</c>）。<b>不碰具体控件文字</b>：菜单上的中文是用户找东西的
    /// 依据，测试也按文字找菜单项，一个字都不在这里改。
    /// </para>
    /// </summary>
    public partial class MainForm : Form
    {
        private readonly List<CommandBinding> _commandBindings = new List<CommandBinding>();

        private readonly Dictionary<PlayerCommandId, int> _commandExecutionCounts =
            new Dictionary<PlayerCommandId, int>();

        private PlayerCommandId? _lastCommand;

        /// <summary>测试用：当前所有"菜单项 / 工具条项 → 命令"的绑定。</summary>
        internal IReadOnlyList<CommandBinding> CommandBindings => _commandBindings;

        /// <summary>测试用：最近一次真正执行过的命令。</summary>
        internal PlayerCommandId? LastCommand => _lastCommand;

        /// <summary>测试用：某条命令被执行过几次（"六张脸共用一套命令"靠它验证）。</summary>
        internal int CommandExecutionCount(PlayerCommandId id) =>
            _commandExecutionCounts.TryGetValue(id, out var count) ? count : 0;

        /// <summary>测试用：当前状态快照。</summary>
        internal CommandAvailability CurrentCommandAvailability => BuildCommandAvailability();

        /// <summary>测试用：按当前状态，这条命令能不能点。</summary>
        internal bool CanExecuteCommand(PlayerCommandId id) =>
            PlayerCommandCatalog.CanExecute(id, BuildCommandAvailability());

        private CommandAvailability BuildCommandAvailability() => new CommandAvailability(
            _engine.HasMedia,
            _engine.IsPlaying,
            _playlist.Count > 0,
            IsAudioWindow,
            IsMiniWindow,
            IsFullscreen);

        /// <summary>
        /// 把菜单与工具栏接到命令上。只接"目录里有"的那些；主题 / 倍速 / 画面比例 /
        /// 循环模式这些单选设置仍由各自的处理器管（它们只有菜单一个宿主）。
        /// </summary>
        private void ConfigureCommandBindings()
        {
            // ---- 文件菜单 ----
            BindCommand(menuFileOpen, PlayerCommandId.OpenFiles);
            BindCommand(menuFileOpenFolder, PlayerCommandId.OpenFolder);
            BindCommand(menuFileOpenUrl, PlayerCommandId.OpenUrl);

            // ---- 播放菜单 ----
            BindCommand(menuPlayPause, PlayerCommandId.PlayPause);
            BindCommand(menuStop, PlayerCommandId.Stop);
            BindCommand(menuPrev, PlayerCommandId.Previous);
            BindCommand(menuNext, PlayerCommandId.Next);
            BindCommand(menuSeekBack, PlayerCommandId.SeekBack);
            BindCommand(menuSeekForward, PlayerCommandId.SeekForward);
            BindCommand(menuVolumeUp, PlayerCommandId.VolumeUp);
            BindCommand(menuVolumeDown, PlayerCommandId.VolumeDown);
            BindCommand(menuMute, PlayerCommandId.Mute);

            // ---- 字幕菜单 ----
            BindCommand(menuSubtitleOpen, PlayerCommandId.OpenSubtitle);
            BindCommand(menuSubtitleDisable, PlayerCommandId.ToggleSubtitles);

            // ---- 视图菜单 ----
            BindCommand(menuViewPlaylist, PlayerCommandId.TogglePlaylist);
            BindCommand(menuViewFullscreen, PlayerCommandId.ToggleFullscreen);

            // 迷你模式那一项是 ConfigureWindowModeMenu 建的（跟着「窗口模式」一起插进视图菜单）
            if (_menuMiniMode != null)
                BindCommand(_menuMiniMode, PlayerCommandId.ToggleMiniMode);

            // ---- 桌面歌词（菜单里那一份是运行时插进去的）----
            if (_menuDesktopLyricsEnabled != null)
                BindCommand(_menuDesktopLyricsEnabled, PlayerCommandId.ToggleDesktopLyrics);

            // ---- 工具栏 ----
            BindCommand(tsbOpen, PlayerCommandId.OpenFiles);
            BindCommand(tsbOpenFolder, PlayerCommandId.OpenFolder);
            BindCommand(tsbPlay, PlayerCommandId.Play);
            BindCommand(tsbPause, PlayerCommandId.Pause);
            BindCommand(tsbStop, PlayerCommandId.Stop);
            BindCommand(tsbPrev, PlayerCommandId.Previous);
            BindCommand(tsbNext, PlayerCommandId.Next);
            BindCommand(tsbRewind, PlayerCommandId.SeekBack);
            BindCommand(tsbForward, PlayerCommandId.SeekForward);
            BindCommand(tsbSnapshot, PlayerCommandId.Snapshot);
            BindCommand(tsbTogglePlaylist, PlayerCommandId.TogglePlaylist);

            // 工具栏提示里的「(键)」也要跟用户改过的键一致（1.29.0）。
            RefreshCommandShortcutDisplays();
        }

        // ---- 快捷键覆盖（1.29.0）---------------------------------------------

        private string? _shortcutWarning;

        /// <summary>启动时"有快捷键条目认不识"的说明（并进 ReportLoadWarnings 显示一次）。</summary>
        internal string? ShortcutWarning => _shortcutWarning;

        /// <summary>
        /// 把设置里的快捷键覆盖套到命令目录上。启动、载入设置方案、保存对话框之后都要调。
        /// <para>
        /// 认不识的条目不会让程序起不来：目录退回默认键，这里留一句话给状态栏。
        /// </para>
        /// </summary>
        private void ApplyShortcutOverrides()
        {
            var overrides = ShortcutBinding.Resolve(_settings.ShortcutOverrides, out var warnings);
            PlayerCommandCatalog.Configure(overrides);

            _shortcutWarning = warnings.Count == 0
                ? null
                : "快捷键有 " + warnings.Count + " 条认不识（已按默认键）：" + string.Join("；", warnings);
        }

        /// <summary>
        /// 覆盖变了之后，把菜单上的键串与工具栏提示刷一遍。F1 说明每次现生成（<c>BuildShortcutsText</c>），
        /// 不用刷。
        /// </summary>
        internal void RefreshCommandShortcutDisplays()
        {
            foreach (var binding in _commandBindings)
            {
                var display = PlayerCommandCatalog.Get(binding.Id).MenuDisplay;

                if (binding.Item is ToolStripMenuItem item)
                    item.ShortcutKeyDisplayString = display;

                if (binding.TooltipBase != null)
                {
                    binding.Item.ToolTipText = display == null
                        ? binding.TooltipBase
                        : binding.TooltipBase + " (" + display + ")";
                }
            }
        }

        /// <summary>「帮助 → 快捷键设置…」：改完立刻生效并落盘。</summary>
        private void ShowShortcutSettings()
        {
            if (!ShortcutDialog.Show(this, _palette, out var entries)) return;

            _settings.ShortcutOverrides = entries;
            _settings.Save();

            ApplyShortcutOverrides();
            RefreshCommandShortcutDisplays();
            RefreshLyricsOffsetMenu();

            SetStatus(entries.Count == 0
                ? "快捷键已全部恢复默认"
                : $"快捷键已保存（{entries.Count} 条与默认不同）");
        }

        /// <summary>一条命令现在按哪个键（状态栏提示里用；改了键之后不会再写着旧键）。</summary>
        private static string KeyOf(PlayerCommandId id) =>
            PlayerCommandCatalog.Get(id).MenuDisplay ?? "（没有键）";

        /// <summary>绑定一个菜单项 / 工具条项（登记起来供状态刷新与测试核对）。</summary>
        private void BindCommand(ToolStripItem item, PlayerCommandId id)
        {
            CommandBinder.Bind(item, id, ExecuteCommand);
            _commandBindings.Add(new CommandBinding(item, id));
        }

        /// <summary>绑定一个普通按钮（卡片 / 迷你卡上的播放控件条）。</summary>
        private void BindCommand(Button button, PlayerCommandId id) =>
            CommandBinder.Bind(button, id, ExecuteCommand);

        /// <summary>
        /// 执行一条命令——所有界面入口的唯一落点。
        /// <para>
        /// 点不动的命令直接不执行，并在能从键盘 / 托盘够到的时候说清理由
        /// （菜单项已经置灰，点不动；键盘仍按得下去，不能让它静默无效）。
        /// </para>
        /// </summary>
        internal void ExecuteCommand(PlayerCommandId id)
        {
            if (!PlayerCommandCatalog.CanExecute(id, BuildCommandAvailability()))
            {
                ExplainBlockedCommand(id);
                return;
            }

            _commandExecutionCounts[id] = CommandExecutionCount(id) + 1;
            _lastCommand = id;

            switch (id)
            {
                case PlayerCommandId.PlayPause:
                    TogglePlayPause();
                    break;
                case PlayerCommandId.Play:
                    PlayButtonClicked();
                    break;
                case PlayerCommandId.Pause:
                    _engine.Pause();
                    break;
                case PlayerCommandId.Stop:
                    StopPlayback();
                    break;
                case PlayerCommandId.Previous:
                    PlayPrevious();
                    break;
                case PlayerCommandId.Next:
                    PlayNext(userInitiated: true);
                    break;
                case PlayerCommandId.SeekBack:
                    SeekRelative(-SeekStepMilliseconds);
                    break;
                case PlayerCommandId.SeekForward:
                    SeekRelative(SeekStepMilliseconds);
                    break;
                case PlayerCommandId.VolumeUp:
                    AdjustVolume(5);
                    break;
                case PlayerCommandId.VolumeDown:
                    AdjustVolume(-5);
                    break;
                case PlayerCommandId.Mute:
                    ToggleMute();
                    break;
                case PlayerCommandId.Snapshot:
                    TakeSnapshot();
                    break;
                case PlayerCommandId.FrameStepForward:
                    StepFrame(true);
                    break;
                case PlayerCommandId.FrameStepBackward:
                    StepFrame(false);
                    break;
                case PlayerCommandId.AudioDelayDown:
                    StepDelay(audio: true, direction: -1);
                    break;
                case PlayerCommandId.AudioDelayUp:
                    StepDelay(audio: true, direction: 1);
                    break;
                case PlayerCommandId.SubtitleDelayDown:
                    StepDelay(audio: false, direction: -1);
                    break;
                case PlayerCommandId.SubtitleDelayUp:
                    StepDelay(audio: false, direction: 1);
                    break;
                case PlayerCommandId.LyricsOffsetEarlier:
                    AdjustLyricsOffset(1);
                    break;
                case PlayerCommandId.LyricsOffsetLater:
                    AdjustLyricsOffset(-1);
                    break;
                case PlayerCommandId.ToggleSubtitles:
                    ToggleSubtitles();
                    break;
                case PlayerCommandId.OpenSubtitle:
                    OpenSubtitleDialog();
                    break;
                case PlayerCommandId.TogglePlaylist:
                    menuViewPlaylist.Checked = !menuViewPlaylist.Checked;
                    break;
                case PlayerCommandId.ToggleFullscreen:
                    ToggleFullscreen();
                    break;
                case PlayerCommandId.ToggleWindowMode:
                    ToggleWindowMode();
                    break;
                case PlayerCommandId.ToggleMiniMode:
                    ToggleMini();
                    break;
                case PlayerCommandId.ToggleDesktopLyrics:
                    SetDesktopLyricsEnabled(!_settings.DesktopLyricsEnabled);
                    break;
                case PlayerCommandId.OpenFiles:
                    OpenFilesDialog();
                    break;
                case PlayerCommandId.OpenFolder:
                    OpenFolderDialog();
                    break;
                case PlayerCommandId.OpenUrl:
                    OpenUrlDialog();
                    break;
                case PlayerCommandId.LoadPlaylistFile:
                    OpenPlaylistFileDialog(replace: false);
                    break;
                case PlayerCommandId.SaveAsPlaylist:
                    SaveCurrentListAsPlaylist();
                    break;
                case PlayerCommandId.ExportPlaylist:
                    ExportPlaylistDialog();
                    break;
                default:
                    // 目录里新增了命令却忘了在这里接：早红比"点了没反应"好。
                    throw new ArgumentOutOfRangeException(nameof(id), id, "命令目录里有这一条，但执行入口没接");
            }

            RefreshCommandSurfaces();
        }

        /// <summary>命令点不动时说明理由（菜单已经灰了，能从键盘按到的只有这几种）。</summary>
        private void ExplainBlockedCommand(PlayerCommandId id)
        {
            switch (id)
            {
                case PlayerCommandId.TogglePlaylist when IsAudioWindow:
                    SetStatus($"音频窗口没有播放列表；按 {KeyOf(PlayerCommandId.ToggleWindowMode)} 切回视频窗口");
                    break;

                case PlayerCommandId.TogglePlaylist when IsMiniWindow:
                    SetStatus($"迷你模式没有播放列表；按 {KeyOf(PlayerCommandId.ToggleMiniMode)} 还原窗口");
                    break;

                case PlayerCommandId.ToggleFullscreen when IsAudioWindow:
                    SetStatus($"音频窗口没有全屏；按 {KeyOf(PlayerCommandId.ToggleWindowMode)} 切回视频窗口再按 "
                              + KeyOf(PlayerCommandId.ToggleFullscreen));
                    break;

                case PlayerCommandId.ToggleFullscreen when IsMiniWindow:
                    SetStatus($"迷你模式没有全屏；按 {KeyOf(PlayerCommandId.ToggleMiniMode)} 还原窗口");
                    break;

                case PlayerCommandId.ToggleWindowMode when IsFullscreen:
                    SetStatus("先退出全屏，再切换音频 / 视频窗口");
                    break;

                case PlayerCommandId.ToggleMiniMode when IsFullscreen:
                    SetStatus("先退出全屏，再进入迷你模式");
                    break;
            }
        }

        /// <summary>勾选类命令当前该不该画勾（"真值"只有一处：设置或状态）。</summary>
        private bool IsCommandChecked(PlayerCommandId id) => id switch
        {
            PlayerCommandId.TogglePlaylist => _settings.ShowPlaylist,
            PlayerCommandId.ToggleFullscreen => IsFullscreen,
            PlayerCommandId.ToggleMiniMode => IsMiniWindow,
            PlayerCommandId.ToggleDesktopLyrics => _settings.DesktopLyricsEnabled,
            PlayerCommandId.Mute => _engine.Muted,
            _ => false
        };

        /// <summary>
        /// 按当前状态把所有命令表面刷一遍：菜单 / 工具栏的可用性与勾选、工具栏那个 ⏸ 的特殊门禁、
        /// 任务栏缩略图按钮、音频卡片、桌面歌词控制条。
        /// <para>
        /// 一次快照推全部，避免"工具栏能点、菜单是灰的"这种各说各话。设置勾选态时套
        /// <c>_suspendUiEvents</c>（并在结束时恢复原值），否则 <c>menuViewPlaylist.Checked</c>
        /// 会顺带触发一次布局变化。
        /// </para>
        /// </summary>
        internal void RefreshCommandSurfaces()
        {
            var availability = BuildCommandAvailability();

            var previousSuspend = _suspendUiEvents;
            _suspendUiEvents = true;

            try
            {
                foreach (var binding in _commandBindings)
                    CommandBinder.Apply(binding.Item, binding.Id, availability, IsCommandChecked);
            }
            finally
            {
                _suspendUiEvents = previousSuspend;
            }

            // 工具栏上的 ▶ 与 ⏸ 是两条命令、两个门禁：⏸ 只在真的在播时能点。
            if (tsbPause.Enabled != (availability.HasMedia && availability.IsPlaying))
                tsbPause.Enabled = availability.HasMedia && availability.IsPlaying;

            // 任务栏缩略图按钮（上一个 / 播放暂停 / 下一个 / 停止）跟着同一份快照走。
            _taskbarButtons?.SetTransportState(
                availability.HasMedia, availability.IsPlaying, availability.HasPlaylistItems, availability.HasPlaylistItems);

            // 卡片上的播放控件条（音频窗口 / 迷你模式）。
            _surfaces.UpdateTransportState(
                availability.HasMedia, availability.IsPlaying, availability.HasPlaylistItems);
        }
    }
}
