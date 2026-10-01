using System;
using System.Windows.Forms;
using 播放器.Core;
using 播放器.Ui;

namespace 播放器
{
    /// <summary>
    /// 歌词时间轴偏移：歌词比声音快 / 慢时，整体提前或延后。
    /// <para>
    /// 做法是"换一份平移过的歌词"（<see cref="LyricsDocument.Shifted"/>），
    /// 而不是把偏移塞进播放位置的每一处计算里：侧栏歌词、桌面歌词、
    /// 点歌词跳转、悬停显示时间全都从同一份文档取时间，只改一处就全都对上了。
    /// </para>
    /// <para>
    /// 偏移<b>按文件记</b>（存在播放历史里，和音画 / 字幕延迟同一处），
    /// 但只记"用户调出来的那个值"，没调过的文件就是 0，不会平白多出记录。
    /// </para>
    /// </summary>
    public partial class MainForm
    {
        private const string LyricsOffsetAdvanceTag = "lyrics-offset-advance";
        private const string LyricsOffsetDelayTag = "lyrics-offset-delay";
        private const string LyricsOffsetResetTag = "lyrics-offset-reset";
        private const string LyricsOffsetEditTag = "lyrics-offset-edit";
        private const string LyricsOffsetValueTag = "lyrics-offset-value";

        private ToolStripMenuItem? _menuLyricsOffset;

        /// <summary>只读入口：视图菜单里的「歌词偏移」（给冒烟测试用）。</summary>
        internal ToolStripMenuItem? MenuLyricsOffset => _menuLyricsOffset;

        private ContextMenuStrip? _lyricsContextMenu;

        /// <summary>歌词偏移那一套项：视图菜单 / 歌词页右键 / 桌面歌词右键 / 托盘各一份，一起刷。</summary>
        private MenuSection? _lyricsOffsetSection;

        /// <summary>托盘里的「歌词偏移」（给冒烟测试用；桌面歌词锁定后这里是唯一的出口）。</summary>
        private ToolStripMenuItem? _trayLyricsOffset;

        internal ToolStripMenuItem? TrayLyricsOffset => _trayLyricsOffset;

        // 当前曲目的歌词偏移不在这里：它跟着曲目走，存在 _track（TrackLyricsSession）里

        // =====================================================================
        // 菜单
        // =====================================================================

        /// <summary>
        /// 建「歌词偏移」菜单，并给侧栏歌词页挂一份同样的右键菜单。
        /// <para>
        /// 三处入口展开同一套项：视图菜单里是子菜单，歌词页与桌面歌词上是平铺的几行。
        /// 三份是三个独立的对象（一个项只能有一个父级），统一交给
        /// <see cref="_lyricsOffsetSection"/> 去挂宿主与刷新。
        /// </para>
        /// </summary>
        private void ConfigureLyricsOffsetMenu()
        {
            _lyricsOffsetSection = new MenuSection(RefreshLyricsOffsetItem);

            _menuLyricsOffset = new ToolStripMenuItem("歌词偏移(&Y)");
            BuildLyricsOffsetItems(_menuLyricsOffset.DropDownItems);
            _lyricsOffsetSection.Attach(_menuLyricsOffset);
            InsertLyricsOffsetMenu();

            // 正看着歌词的时候手边就能调：侧栏歌词页的右键菜单
            _lyricsContextMenu = new ContextMenuStrip();
            BuildLyricsOffsetItems(_lyricsContextMenu.Items);
            _lyricsOffsetSection.Attach(_lyricsContextMenu);
            sidebarPanel.LyricsContextMenu = _lyricsContextMenu;

            // 桌面歌词的右键菜单里那一段是 ConfigureDesktopLyricsMenu 建的，
            // 那时这一套还没造出来，所以在这里补挂上
            _lyricsOffsetSection.Attach(_desktopLyricsMenu);

            // 建完先刷一遍：菜单还没展开过时，文字也应当是对的
            _lyricsOffsetSection.Refresh();
        }

        /// <summary>插在「在线歌词与封面」之后：都属于"歌词从哪来、对不对得上"这一类。</summary>
        private void InsertLyricsOffsetMenu()
        {
            if (_menuLyricsOffset == null) return;

            var index = _menuOnlineLookup == null ? -1 : menuView.DropDownItems.IndexOf(_menuOnlineLookup);

            if (index < 0) menuView.DropDownItems.Add(_menuLyricsOffset);
            else menuView.DropDownItems.Insert(index + 1, _menuLyricsOffset);
        }

        /// <summary>建一套歌词偏移项（供视图菜单、歌词右键菜单、桌面歌词右键菜单共用）。</summary>
        private void BuildLyricsOffsetItems(ToolStripItemCollection target)
        {
            var advance = new ToolStripMenuItem("歌词提前 0.5 秒")
            {
                Tag = LyricsOffsetAdvanceTag,
                ShortcutKeyDisplayString = "Ctrl+]",
                ToolTipText = "歌词比声音慢（总是慢半拍才出来）时点这个"
            };
            advance.Click += (s, e) => AdjustLyricsOffset(+1);

            var delay = new ToolStripMenuItem("歌词延后 0.5 秒")
            {
                Tag = LyricsOffsetDelayTag,
                ShortcutKeyDisplayString = "Ctrl+[",
                ToolTipText = "歌词比声音快（还没唱到就先出来了）时点这个"
            };
            delay.Click += (s, e) => AdjustLyricsOffset(-1);

            var reset = new ToolStripMenuItem("偏移归零") { Tag = LyricsOffsetResetTag };
            reset.Click += (s, e) => ResetLyricsOffset();

            var edit = new ToolStripMenuItem("手动输入偏移…") { Tag = LyricsOffsetEditTag };
            edit.Click += (s, e) => EditLyricsOffset();

            // 当前值直接写在菜单上：一眼看到现在是多少，不用试着点一下
            var value = new ToolStripMenuItem("当前偏移：不偏移") { Tag = LyricsOffsetValueTag, Enabled = false };

            target.Add(advance);
            target.Add(delay);
            target.Add(reset);
            target.Add(edit);
            target.Add(new ToolStripSeparator());
            target.Add(value);
        }

        /// <summary>按当前状态刷一项（能不能点、当前值是多少）。递归与"刷哪些宿主"由 MenuSection 管。</summary>
        private void RefreshLyricsOffsetItem(ToolStripMenuItem menuItem)
        {
            switch (menuItem.Tag as string)
            {
                case LyricsOffsetValueTag:
                    // 不能调的时候把原因写在菜单上，而不是给一个点不动的灰项让人猜
                    var reason = BlockerReason(LyricsOffsetBlockerNow());

                    menuItem.Text = "当前偏移：" + LyricsOffset.Describe(_track.LyricsOffsetMilliseconds)
                                    + (reason.Length > 0 ? $"（{reason}）" : string.Empty);
                    break;

                case LyricsOffsetAdvanceTag:
                case LyricsOffsetDelayTag:
                case LyricsOffsetResetTag:
                case LyricsOffsetEditTag:
                    // 没有时间轴（纯文本歌词、或者干脆没在放歌）时偏移没有意义，如实禁用
                    menuItem.Enabled = HasSynchronizedLyrics();
                    break;
            }
        }

        private bool HasSynchronizedLyrics() => LyricsOffsetBlockerNow() == LyricsOffsetBlocker.None;

        /// <summary>
        /// 把「歌词偏移」那一套菜单刷一遍（展开前就是这么刷的）。
        /// <para>
        /// 只读入口，给冒烟测试用（主工程里有 <c>InternalsVisibleTo("SmokeTest")</c>）。
        /// 测试以前反射调私有的 <c>RefreshLyricsOffsetItems</c>——那个方法在 P2 里并进了
        /// <see cref="Ui.MenuSection"/>，一改名测试就<b>静默</b>失效了（断言读到的还是旧文字）。
        /// 给它一个有名字的入口，改名再也不会悄悄绕过检查。
        /// </para>
        /// </summary>
        internal void RefreshLyricsOffsetMenu() => _lyricsOffsetSection?.Refresh();

        // =====================================================================
        // 调整
        // =====================================================================

        /// <summary>按一档加减（<paramref name="direction"/> 为正 = 歌词提前）。</summary>
        internal void AdjustLyricsOffset(int direction)
        {
            if (!EnsureLyricsOffsetUsable()) return;

            var before = _track.LyricsOffsetMilliseconds;
            var after = LyricsOffset.Step(before, direction);

            if (after == before)
            {
                // 顶到极限了：说清楚，别让用户以为按键没反应
                SetStatus($"歌词偏移已经到头了（最多 ±{LyricsOffset.LimitMilliseconds / 1000} 秒）");
                return;
            }

            ApplyLyricsOffset(after, direction > 0 ? "歌词提前" : "歌词延后");
        }

        internal void ResetLyricsOffset()
        {
            if (!EnsureLyricsOffsetUsable()) return;

            ApplyLyricsOffset(0, "歌词偏移归零");
        }

        private void EditLyricsOffset()
        {
            if (!EnsureLyricsOffsetUsable()) return;

            var current = LyricsOffset.Describe(_track.LyricsOffsetMilliseconds);

            var raw = InputDialog.Show(
                this,
                "歌词偏移",
                "输入要偏移多少（单位秒，正数 = 歌词提前，负数 = 歌词延后）。\n"
                + "例如 0.5 表示歌词提前 0.5 秒；也可以写 750ms。\n"
                + $"当前：{current}，范围 ±{LyricsOffset.LimitMilliseconds / 1000} 秒。",
                _track.LyricsOffsetMilliseconds == 0
                    ? "0.5"
                    : (_track.LyricsOffsetMilliseconds / 1000.0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));

            if (raw == null) return;

            if (!LyricsOffset.TryParse(raw, out var milliseconds, out var error))
            {
                MessageBox.Show(this, error, "歌词偏移", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            ApplyLyricsOffset(milliseconds, "歌词偏移");
        }

        /// <summary>没在放歌、或这首歌词没有时间轴时，偏移无从下手，先把原因说清楚。</summary>
        private bool EnsureLyricsOffsetUsable()
        {
            var blocker = LyricsOffsetBlockerNow();

            if (blocker == LyricsOffsetBlocker.None) return true;

            SetStatus(BlockerStatus(blocker));
            return false;
        }

        /// <summary>现在为什么调不了偏移。</summary>
        private enum LyricsOffsetBlocker
        {
            None,

            /// <summary>还没有在放的曲目。</summary>
            NoMedia,

            /// <summary>这首根本没有歌词。</summary>
            NoLyrics,

            /// <summary>有歌词但是没有时间轴（纯文本）。</summary>
            NotSynchronized
        }

        private LyricsOffsetBlocker LyricsOffsetBlockerNow()
        {
            if (string.IsNullOrEmpty(_track.Path)) return LyricsOffsetBlocker.NoMedia;
            if (_track.Lyrics.IsEmpty) return LyricsOffsetBlocker.NoLyrics;
            if (!_track.Lyrics.IsSynchronized) return LyricsOffsetBlocker.NotSynchronized;

            return LyricsOffsetBlocker.None;
        }

        /// <summary>菜单上那行"为什么不能调"的说明。</summary>
        private static string BlockerReason(LyricsOffsetBlocker blocker) => blocker switch
        {
            LyricsOffsetBlocker.NoMedia => "还没有在放的曲目",
            LyricsOffsetBlocker.NoLyrics => "这首没有歌词",
            LyricsOffsetBlocker.NotSynchronized => "这首歌词没有时间轴",
            _ => string.Empty
        };

        /// <summary>状态栏里那句话：比菜单上多说一句"该怎么办"。</summary>
        private static string BlockerStatus(LyricsOffsetBlocker blocker) => blocker switch
        {
            LyricsOffsetBlocker.NoMedia => "先播放一首歌，再调歌词偏移",
            LyricsOffsetBlocker.NoLyrics => "这首没有歌词，没有可偏移的时间轴",
            _ => "这首的歌词没有时间轴（纯文本），偏移无从下手"
        };

        /// <summary>
        /// 应用一个新的偏移：套到歌词上、记到文件上、刷新界面。
        /// <para><paramref name="action"/> 是这次动作的名字，用来拼状态栏那句话。</para>
        /// </summary>
        private void ApplyLyricsOffset(long milliseconds, string action)
        {
            // 钳制与"现在的偏移是多少"都归会话管，这里只负责记到文件上并刷新界面
            var clamped = _track.SetOffset(milliseconds);

            // 记在这个文件名下；顺手触发一次后台落盘，别等 20 秒的定时（用户可能马上切歌）
            Adjustments.RecordLyricsOffset(_track.Path, clamped);

            RefreshLyricsViews();
            RefreshInfoPanel();

            SetStatus($"{action}：{LyricsOffset.Describe(clamped)}"
                      + (clamped != milliseconds ? $"（已按上限 ±{LyricsOffset.LimitMilliseconds / 1000} 秒处理）" : string.Empty));
        }

        // =====================================================================
        // 换歌 / 应用
        // =====================================================================

        /// <summary>
        /// 把当前文件的偏移接上（换歌时调）。没记过就是 0。
        /// </summary>
        private void RestoreLyricsOffsetForCurrentMedia(string? path)
        {
            _track.SetOffset(Adjustments.Get(path).LyricsOffsetMilliseconds);

            UpdateLyricsOffsetHint();
        }

        /// <summary>偏移变了：重新把平移过的歌词推给侧栏与桌面歌词，并立刻重新定位当前行。</summary>
        private void RefreshLyricsViews()
        {
            // 平移过的歌词只从会话取（侧栏与桌面歌词看的是同一份，保证两边一致）
            var shifted = _track.ShiftedLyrics;

            sidebarPanel.SetLyrics(shifted);
            UpdateLyricsOffsetHint();

            if (_desktopLyrics != null)
            {
                _desktopLyrics.SetTrack(CurrentTrackTitle(), shifted);
                _desktopLyrics.UpdatePosition(
                    _engine.HasMedia ? TimeSpan.FromMilliseconds(_engine.Time) : TimeSpan.Zero);
            }

            // SetDocument 会把滚动位置与当前行清掉，这里马上按当前时间重新定位，
            // 不然要等下一次界面计时（最多 200ms）才跳回该在的地方。
            if (_engine.HasMedia)
            {
                var position = TimeSpan.FromMilliseconds(_engine.Time);

                sidebarPanel.UpdatePosition(position);
                _desktopLyrics?.UpdatePosition(position);
            }
        }

        /// <summary>右上角那行小字：偏移为 0 时清掉。</summary>
        private void UpdateLyricsOffsetHint() =>
            sidebarPanel.SetLyricsOffsetHint(LyricsOffset.DescribeShort(_track.LyricsOffsetMilliseconds));
    }
}
