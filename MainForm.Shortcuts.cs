using System;
using System.Diagnostics;
using System.Windows.Forms;
using 播放器.Core;
using 播放器.Ui;

namespace 播放器
{
    /// <summary>
    /// 快捷键：主窗口上的所有按键都从 <see cref="ProcessCmdKey"/> 走。
    /// <para>只有"打字的地方"（文本框、搜索框）会让出按键，见 <see cref="IsTypingTarget"/>。</para>
    /// </summary>
    public partial class MainForm : Form
    {
        // =====================================================================
        // 快捷键
        // =====================================================================

        /// <summary>
        /// 给冒烟测试用：把一次按键喂给窗口（等价于真的按下去）。
        /// <para>
        /// 平时按键是 WinForms 调 <see cref="ProcessCmdKey"/> 进来的，而那个方法是
        /// <c>protected override</c>（重写时不能放宽访问级别）。测试要验的是"这一下接的是什么"，
        /// 不想去合成一整条键盘消息，所以留了这个入口。消息为空 = "消息未知"，
        /// 会退回按当前焦点判断（见 <see cref="IsTypingTarget"/>）。
        /// </para>
        /// </summary>
        internal bool ProcessShortcut(Keys keyData)
        {
            var message = new Message();
            return ProcessCmdKey(ref message, keyData);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            // 焦点在文本框里时，单键快捷键不能抢。
            // WinForms 会把同一条 WM_KEYDOWN 既交给文本框、又交给窗体的 ProcessCmdKey，
            // 而这里一旦 return true（"这个键我处理了"），那条按键就被整个吃掉：
            // 字符进不了搜索框，播放控制的动作却照跑。
            // 实测（真按键，见 tools/SmokeTest 第 6 步）：在搜索框里根本打不出空格
            // （"空格分隔多个关键字"这个搜索用法实际上是坏的，反而把播放暂停了），
            // 按 Delete 会删掉播放列表里的选中项。
            // 只放行 Ctrl / Alt 组合键和功能键，它们不会被当成打字内容。
            if (IsTypingTarget(msg) && !IsCommandKeyWhileTyping(keyData))
                return base.ProcessCmdKey(ref msg, keyData);

            switch (keyData)
            {
                case Keys.Space:
                    TogglePlayPause();
                    return true;
                case Keys.Left:
                    SeekRelative(-SeekStepMilliseconds);
                    return true;
                case Keys.Right:
                    SeekRelative(SeekStepMilliseconds);
                    return true;
                case Keys.Up:
                    AdjustVolume(5);
                    return true;
                case Keys.Down:
                    AdjustVolume(-5);
                    return true;
                case Keys.PageUp:
                case Keys.Control | Keys.Left:
                    PlayPrevious();
                    return true;
                case Keys.PageDown:
                case Keys.Control | Keys.Right:
                    PlayNext(true);
                    return true;
                case Keys.M:
                    ToggleMute();
                    return true;
                case Keys.S:
                    StopPlayback();
                    return true;
                case Keys.V:
                    DisableSubtitle();
                    return true;
                case Keys.F9:
                    menuViewPlaylist.Checked = !menuViewPlaylist.Checked;
                    return true;
                case Keys.F11:
                case Keys.F:
                    ToggleFullscreen();
                    return true;
                case Keys.F1:
                    ShowShortcuts();
                    return true;
                case Keys.Delete:
                    RemoveSelectedPlaylistItems();
                    return true;
                case Keys.Control | Keys.O:
                    OpenFilesDialog();
                    return true;
                case Keys.Control | Keys.Shift | Keys.O:
                    OpenFolderDialog();
                    return true;
                case Keys.Control | Keys.U:
                    OpenUrlDialog();
                    return true;
                // Ctrl+S 是"保存这个列表"的直觉动作：存进歌单库（要起名字、能覆盖、能切回来）。
                // 存到任意位置的 m3u 是另一件事（外发、备份），放 Ctrl+Shift+S。
                case Keys.Control | Keys.S:
                    SaveCurrentListAsPlaylist();
                    return true;
                case Keys.Control | Keys.Shift | Keys.S:
                    ExportPlaylistDialog();
                    return true;
                case Keys.Control | Keys.L:
                    OpenPlaylistFileDialog(replace: false);
                    return true;
                case Keys.Control | Keys.T:
                    OpenSubtitleDialog();
                    return true;
                case Keys.Control | Keys.P:
                    TakeSnapshot();
                    return true;
                case Keys.Control | Keys.D:
                    SetDesktopLyricsEnabled(!_settings.DesktopLyricsEnabled);
                    return true;

                // ---- 歌词偏移：Ctrl+] 提前、Ctrl+[ 延后（正负号与 LRC 的 [offset:] 一致）----
                case Keys.Control | Keys.OemCloseBrackets:
                    AdjustLyricsOffset(1);
                    return true;
                case Keys.Control | Keys.OemOpenBrackets:
                    AdjustLyricsOffset(-1);
                    return true;

                // ---- 视频：逐帧与音画同步（与 VLC 的按键习惯保持一致）----
                case Keys.E:
                    StepFrame(true);
                    return true;
                case Keys.Shift | Keys.E:
                    StepFrame(false);
                    return true;
                case Keys.G:
                    StepDelay(audio: true, direction: -1);
                    return true;
                case Keys.H:
                    StepDelay(audio: true, direction: 1);
                    return true;
                case Keys.J:
                    StepDelay(audio: false, direction: -1);
                    return true;
                case Keys.K:
                    StepDelay(audio: false, direction: 1);
                    return true;
                case Keys.Escape:
                case Keys.Enter:
                    if (_isFullscreen)
                    {
                        ExitFullscreen();
                        return true;
                    }
                    break;
            }

            return base.ProcessCmdKey(ref msg, keyData);
        }

        /// <summary>
        /// 这一次按键是不是打在一个文本框（搜索框）里。
        /// <para>
        /// 先看消息本身的落点：真实按键时 <c>ProcessCmdKey</c> 拿到的就是那条原始消息，
        /// 它的窗口句柄就是焦点控件，最可靠。
        /// </para>
        /// <para>
        /// 消息是空的（测试会用反射直接调 <c>ProcessCmdKey</c>）时才退回按当前焦点判断——
        /// 不能无条件混用两种判断：那样在"焦点还留在搜索框上、但按键是发给列表的"这种
        /// 情况下会误判成打字，把快捷键整体挡掉。
        /// </para>
        /// </summary>
        private bool IsTypingTarget(Message msg)
        {
            if (msg.HWnd != IntPtr.Zero)
                return Control.FromHandle(msg.HWnd) is TextBoxBase;

            Control? control = this;

            while (control is ContainerControl container && container.ActiveControl != null)
                control = container.ActiveControl;

            return control is TextBoxBase;
        }

        /// <summary>
        /// 打字时仍然要放行的按键：Ctrl / Alt 组合键，以及 F1~F24。
        /// 其余（字母、空格、方向键、Delete、PgUp / PgDn）都归文本框自己。
        /// </summary>
        private static bool IsCommandKeyWhileTyping(Keys keyData)
        {
            if ((keyData & (Keys.Control | Keys.Alt)) != Keys.None) return true;

            var code = keyData & Keys.KeyCode;
            return code >= Keys.F1 && code <= Keys.F24;
        }
    }
}
