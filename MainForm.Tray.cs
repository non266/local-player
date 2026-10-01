using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using 播放器.Core;

namespace 播放器
{
    /// <summary>
    /// 系统托盘图标与单实例转发。
    /// <para>
    /// 托盘菜单可以直接控制播放；第二个实例启动时把自己的命令行文件
    /// 交给正在运行的窗口，窗口会被自动恢复到前台并开始播放。
    /// </para>
    /// </summary>
    public partial class MainForm
    {
        /// <summary><see cref="NotifyIcon.Text"/> 最长 63 个字符，超长会抛异常。</summary>
        private const int MaximumTrayTextLength = 63;

        private NotifyIcon? _trayIcon;
        private ContextMenuStrip? _trayMenu;
        private SingleInstance? _singleInstance;

        /// <summary>只读入口：托盘菜单本身（给冒烟测试用）。</summary>
        internal ContextMenuStrip? TrayMenu => _trayMenu;

        /// <summary>真正要退出（而不是缩到托盘）。</summary>
        private bool _exiting;

        // =====================================================================
        // 托盘
        // =====================================================================

        private void InitializeTray()
        {
            _trayMenu = new ContextMenuStrip { ShowImageMargin = false };

            _trayMenu.Items.Add(CreateTrayItem("播放 / 暂停", TogglePlayPause));
            _trayMenu.Items.Add(CreateTrayItem("停止", StopPlayback));
            _trayMenu.Items.Add(CreateTrayItem("上一首", PlayPrevious));
            _trayMenu.Items.Add(CreateTrayItem("下一首", () => PlayNext(true)));
            _trayMenu.Items.Add(new ToolStripSeparator());
            _trayMenu.Items.Add(CreateTrayItem("显示主界面", RestoreFromTray));

            // 主界面缩进托盘时也能开关桌面歌词，所以托盘里必须有这一项。
            // 而且托盘这一份必须是**完整的一套**：歌词锁定之后在鼠标眼里就不存在了，
            // 右键菜单点不到，而主界面可能正缩在托盘里看片——这时这里是唯一的出口。
            _trayMenu.Items.Add(new ToolStripSeparator());

            _trayDesktopLyrics = new ToolStripMenuItem("桌面歌词(&D)");

            // 每次展开都对齐一遍：勾选状态、外观那一项上的当前字号 / 浓度
            _trayDesktopLyrics.DropDownOpening += (s, e) => SyncDesktopLyricsChecks();

            _trayDesktopLyricsEnabled = new ToolStripMenuItem("启用桌面歌词")
            {
                CheckOnClick = true
            };
            _trayDesktopLyricsEnabled.Click +=
                (s, e) => SetDesktopLyricsEnabled(_trayDesktopLyricsEnabled.Checked);

            _trayDesktopLyrics.DropDownItems.Add(_trayDesktopLyricsEnabled);
            _trayDesktopLyrics.DropDownItems.Add(new ToolStripSeparator());
            BuildDesktopLyricsOptions(_trayDesktopLyrics.DropDownItems, includeClose: false);

            // 这一份也交给同一套去刷：托盘、视图菜单、桌面歌词右键三处是三个独立对象，
            // 挂进来之后 SyncDesktopLyricsChecks() 一次就能全刷到
            _desktopLyricsSection?.Attach(_trayDesktopLyrics);

            _trayMenu.Items.Add(_trayDesktopLyrics);

            // 歌词偏移也要在托盘里有一份，理由和桌面歌词那一段一样：
            // 桌面歌词一锁定就在鼠标眼里不存在了（右键菜单点不到），而主界面可能正缩在托盘里，
            // 那时想"歌词慢半拍"就没地方调了。这一份和视图菜单 / 歌词右键 / 桌面歌词右键共用同一套项。
            _trayLyricsOffset = new ToolStripMenuItem("歌词偏移(&Y)");
            BuildLyricsOffsetItems(_trayLyricsOffset.DropDownItems);
            _lyricsOffsetSection?.Attach(_trayLyricsOffset);
            _trayMenu.Items.Add(_trayLyricsOffset);

            _trayMenu.Items.Add(new ToolStripSeparator());
            _trayMenu.Items.Add(CreateTrayItem("退出", ExitApplication));

            _trayIcon = new NotifyIcon
            {
                Text = "全能本地播放器",
                ContextMenuStrip = _trayMenu,
                Visible = true,
                Icon = Icon
            };

            _trayIcon.DoubleClick += (s, e) => RestoreFromTray();

            // 托盘项是在菜单项之后建的，勾选状态在这里补一次
            SyncDesktopLyricsChecks();

            UpdateTrayText();
        }

        private static ToolStripMenuItem CreateTrayItem(string text, Action action)
        {
            var item = new ToolStripMenuItem(text);
            item.Click += (s, e) => action();
            return item;
        }

        /// <summary>托盘提示文字跟着当前曲目走。</summary>
        private void UpdateTrayText()
        {
            if (_trayIcon == null) return;

            var index = _playlist.CurrentIndex;
            var name = index >= 0 && index < _playlist.Count ? _playlist.Items[index].DisplayName : null;

            var text = string.IsNullOrEmpty(name) ? "全能本地播放器" : "全能本地播放器 · " + name;

            _trayIcon.Text = text.Length > MaximumTrayTextLength
                ? text[..(MaximumTrayTextLength - 3)] + "..."
                : text;
        }

        /// <summary>主题换了以后托盘图标也要跟着换。</summary>
        private void UpdateTrayIcon()
        {
            if (_trayIcon == null) return;

            try
            {
                _trayIcon.Icon = Icon;
            }
            catch (Exception ex)
            {
// 图标生成失败不影响使用
                AppLog.Swallowed("图标生成失败不影响使用", ex);
            }
        }

        private void RestoreFromTray()
        {
            if (!Visible) Show();

            if (WindowState == FormWindowState.Minimized)
                WindowState = FormWindowState.Normal;

            Activate();
            BringToFront();
        }

        private void HideToTray()
        {
            // 只 Hide()，刻意不动 ShowInTaskbar：
            // 改这个属性会让 WinForms 销毁并重建窗口句柄，而全局媒体键、任务栏缩略图按钮、
            // 单实例用的窗口属性、视频输出窗口全都挂在原句柄上，会一起失效。
            // 窗口隐藏后任务栏按钮本来就会自己消失。
            Hide();
        }

        private void ExitApplication()
        {
            _exiting = true;
            Close();
        }

        /// <summary>这次关闭是不是应该收进托盘而不是退出。</summary>
        private bool ShouldHideToTray(FormClosingEventArgs e) =>
            !_exiting && _settings.MinimizeToTray && e.CloseReason == CloseReason.UserClosing;

        private void DisposeTray()
        {
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
                _trayIcon = null;
            }

            _trayMenu?.Dispose();
            _trayMenu = null;
        }

        // =====================================================================
        // 单实例
        // =====================================================================

        private void AttachSingleInstance(SingleInstance? instance)
        {
            _singleInstance = instance;
            if (_singleInstance == null) return;

            _singleInstance.ArgumentsReceived += OnArgumentsReceived;
        }

        /// <summary>另一个实例传来了文件：恢复窗口并播放。</summary>
        private void OnArgumentsReceived(object? sender, IReadOnlyList<string> arguments)
        {
            RestoreFromTray();

            var files = arguments
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Select(a => a.Trim('"'))
                .Where(a => !a.StartsWith("-", StringComparison.Ordinal))
                .ToList();

            if (files.Count == 0) return;

            RememberDirectory(files.FirstOrDefault(File.Exists));

            // 用户明确指定了要放的东西，直接切过去（而不是只入列表）。
            AddPathsWithFolders(files, AddPlayback.Always);
        }

        private void DisposeSingleInstance()
        {
            if (_singleInstance == null) return;

            _singleInstance.ArgumentsReceived -= OnArgumentsReceived;
            _singleInstance.Dispose();
            _singleInstance = null;
        }
    }
}
