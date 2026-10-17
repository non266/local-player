using System;
using System.Windows.Forms;
using 播放器.Core;
using 播放器.Ui;

namespace 播放器
{
    /// <summary>
    /// 睡眠定时器：`播放 → 睡眠定时器`。
    /// <para>
    /// 到点做的是**暂停**（不是退出程序）——退出会让人以为程序崩了；状态栏把"为什么停了"写清楚。
    /// 还有一种"听完这一首就停"：不看到点时间，等当前这一首自然放完就不再往下播。
    /// </para>
    /// <para>
    /// ⚠ 判定挂在**已有的** 200 ms 界面计时器里（<see cref="OnUiTimerTick"/> 那一条），
    /// 不另起计时器：1.3.0 那轮刚被"只 Dispose 没 Close 的表单留下僵尸计时器、
    /// 每 20 秒把旧历史写回磁盘"坑过，这里不再给每个窗体添一个能自己跑下去的定时器。
    /// </para>
    /// </summary>
    public partial class MainForm : Form
    {
        private SleepTimer? _sleepTimer;

        /// <summary>
        /// 到点之后要在状态栏说的那句话。
        /// <para>
        /// 为什么要存一份：暂停 / 停下会触发引擎的状态事件，那一支会把状态栏改写成通用的
        /// "已暂停 / 已停止"。存一句在这里、由状态那一支**最后**盖上去，
        /// 用户才能看到"是睡眠定时器让它停的"，而不是莫名其妙地停了。
        /// </para>
        /// </summary>
        private string? _sleepTimerMessage;

        private ToolStripMenuItem? _menuSleepTimer;
        private ToolStripMenuItem? _menuSleepTimerState;
        private ToolStripMenuItem? _menuSleepEndOfTrack;
        private ToolStripMenuItem? _menuSleepCancel;

        /// <summary>只读入口，给冒烟测试用（主工程里有 <c>InternalsVisibleTo("SmokeTest")</c>）。</summary>
        internal ToolStripMenuItem? MenuSleepTimer => _menuSleepTimer;

        internal bool SleepTimerActive => _sleepTimer != null;

        internal bool SleepTimerIsEndOfTrack => _sleepTimer?.IsEndOfTrack == true;

        internal long SleepTimerRemainingMilliseconds =>
            _sleepTimer?.RemainingMilliseconds(Environment.TickCount64) ?? 0;

        private void ConfigureSleepTimerMenu()
        {
            _menuSleepTimer = new ToolStripMenuItem("睡眠定时器") { Name = "menuSleepTimer" };

            // 第一行是"现在是什么状态"（只读）：设过之后在菜单里直接读得出还剩多久。
            _menuSleepTimerState = new ToolStripMenuItem("未设置")
            {
                Name = "menuSleepTimerState",
                Enabled = false
            };
            _menuSleepTimer.DropDownItems.Add(_menuSleepTimerState);
            _menuSleepTimer.DropDownItems.Add(new ToolStripSeparator());

            _menuSleepEndOfTrack = new ToolStripMenuItem("听完这一首就停") { Name = "menuSleepEndOfTrack" };
            _menuSleepEndOfTrack.Click += (s, e) => StartSleepTimer(SleepTimer.EndOfTrack(),
                "睡眠定时器：听完这一首就停");
            _menuSleepTimer.DropDownItems.Add(_menuSleepEndOfTrack);

            foreach (var minutes in new[] { 15, 30, 60 })
            {
                var item = new ToolStripMenuItem($"{minutes} 分钟后停")
                {
                    Name = "menuSleep" + minutes
                };

                var delay = TimeSpan.FromMinutes(minutes);
                item.Click += (s, e) => StartSleepTimer(SleepTimer.After(Environment.TickCount64, delay),
                    $"睡眠定时器：{minutes} 分钟后停");
                _menuSleepTimer.DropDownItems.Add(item);
            }

            var custom = new ToolStripMenuItem("自定义分钟数…") { Name = "menuSleepCustom" };
            custom.Click += (s, e) => AskSleepTimerMinutes();
            _menuSleepTimer.DropDownItems.Add(custom);

            _menuSleepTimer.DropDownItems.Add(new ToolStripSeparator());

            _menuSleepCancel = new ToolStripMenuItem("取消") { Name = "menuSleepCancel" };
            _menuSleepCancel.Click += (s, e) => CancelSleepTimerByUser();
            _menuSleepTimer.DropDownItems.Add(_menuSleepCancel);

            // "还剩多久"只有下拉那一刻才知道，所以每次都重写一遍文字。
            _menuSleepTimer.DropDownOpening += (s, e) => RefreshSleepTimerMenu();

            menuPlayback.DropDownItems.Add(_menuSleepTimer);
        }

        private void RefreshSleepTimerMenu()
        {
            if (_menuSleepTimerState == null) return;

            _menuSleepTimerState.Text = _sleepTimer == null
                ? "未设置"
                : "当前：" + _sleepTimer.Describe(Environment.TickCount64);

            if (_menuSleepEndOfTrack != null)
                _menuSleepEndOfTrack.Enabled = _sleepTimer?.IsEndOfTrack != true;

            if (_menuSleepCancel != null)
                _menuSleepCancel.Enabled = _sleepTimer != null;
        }

        /// <summary>
        /// 设一个定时器（菜单与冒烟测试都走这一条路，测到的就是用户点的那条路）。
        /// <paramref name="announcement"/> 是设置成功时状态栏那句话。
        /// </summary>
        internal void StartSleepTimer(SleepTimer timer, string announcement)
        {
            _sleepTimer = timer;
            _sleepTimerMessage = null;

            RefreshSleepTimerMenu();
            SetStatus(announcement);
        }

        private void CancelSleepTimerByUser()
        {
            if (_sleepTimer == null)
            {
                SetStatus("当前没有在走睡眠定时器");
                return;
            }

            _sleepTimer = null;
            _sleepTimerMessage = null;

            RefreshSleepTimerMenu();
            SetStatus("已取消睡眠定时器");
        }

        private void AskSleepTimerMinutes()
        {
            var text = InputDialog.Show(
                this,
                "睡眠定时器",
                "多少分钟之后停？（正整数；到点会暂停播放，不是退出程序）",
                "20")?.Trim();

            if (text == null) return;

            if (!int.TryParse(text, out var minutes) || minutes <= 0)
            {
                SetStatus("睡眠定时器：分钟数要是正整数（这次没有设）");
                return;
            }

            // 一天以上没有意义（而且乘出来还好算），直接挡住并说明。
            if (minutes > 24 * 60)
            {
                SetStatus("睡眠定时器：最多 1440 分钟（24 小时），这次没有设");
                return;
            }

            StartSleepTimer(SleepTimer.After(Environment.TickCount64, TimeSpan.FromMinutes(minutes)),
                $"睡眠定时器：{minutes} 分钟后停");
        }

        /// <summary>
        /// 界面计时器每一拍问一句：到点了吗。放在这里用现成的 200 ms 计时器（见类注释）。
        /// </summary>
        private void CheckSleepTimer()
        {
            var timer = _sleepTimer;

            if (timer == null || timer.IsEndOfTrack) return;
            if (!timer.IsDue(Environment.TickCount64)) return;

            _sleepTimer = null;
            RefreshSleepTimerMenu();

            if (_engine.State != PlayerState.Playing)
            {
                SetStatus("睡眠定时器到点（当时没有在播，所以什么也没做）");
                return;
            }

            _sleepTimerMessage = "睡眠定时器到点：已暂停（要接着听就按播放）";

            // 先记消息再暂停：暂停会同步触发状态事件，那一支最后会把这条消息写上状态栏。
            _engine.Pause();
            SetStatus(_sleepTimerMessage);
        }

        /// <summary>
        /// 当前这一首自然放完时先问一句：是不是"听完这一首就停"。
        /// </summary>
        /// <returns>处理过了（不要再往下播）返回 <c>true</c>。</returns>
        private bool ApplySleepTimerAtTrackEnd()
        {
            if (_sleepTimer is not { IsEndOfTrack: true }) return false;

            _sleepTimer = null;
            RefreshSleepTimerMenu();

            _sleepTimerMessage = "睡眠定时器：这一首播完了，已停（不会再往下播）";

            CommitHistory();
            SetStatus(_sleepTimerMessage);

            return true;
        }

        /// <summary>状态那一支最后调用：到点那句话压过通用的"已暂停 / 已停止"，只说一次。</summary>
        private bool ApplySleepTimerMessageToStatus()
        {
            if (_sleepTimerMessage is not { Length: > 0 } message) return false;

            _sleepTimerMessage = null;
            SetStatus(message);

            return true;
        }
    }
}
