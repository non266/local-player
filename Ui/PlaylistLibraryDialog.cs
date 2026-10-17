using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using 播放器.Core;

namespace 播放器.Ui
{
    /// <summary>
    /// 歌单窗口：把歌单库里的<b>全部歌单一次列出来</b>，挑一个载入，
    /// 或者就地改名 / 删除 / 把外面的 m3u 拖进来。
    /// <para>
    /// 菜单里只列得出前二十个，而且菜单里没法"看清楚了再选"。这里是主入口：
    /// 名字、有多少项、什么时候改的，一行看清。
    /// </para>
    /// <para>
    /// 载入动作<b>不在这里执行</b>：窗口只负责选择，退出后由主窗体去替换播放列表
    /// （那要停播、要问"当前列表有未保存的改动"，都是主窗体的活）。
    /// 改名和删除是磁盘上的操作，就地做完。
    /// </para>
    /// </summary>
    internal sealed partial class PlaylistLibraryDialog : Form
    {
        private const int DesignWidth = 700;

        /// <summary>窗口关掉之后交给主窗体的结果。</summary>
        internal sealed class Selection
        {
            /// <summary>要载入的歌单名；<c>null</c> 表示用户在窗口里没选载入。</summary>
            public string? LoadName { get; set; }

            /// <summary>true = 追加到当前列表，false = 替换。</summary>
            public bool Append { get; set; }

            /// <summary>刚新建的空歌单名；主窗体据此问一句要不要切过去。</summary>
            public string? NewName { get; set; }

            /// <summary>歌单库有没有被改过（新建 / 导入 / 改名 / 删除）。</summary>
            public bool Changed { get; set; }

            /// <summary>窗口关闭时"当前歌单"叫什么；被删掉时为 <c>null</c>。</summary>
            public string? CurrentName { get; set; }
        }

        private readonly ListView _list = new ListView();
        private readonly Label _hint = new Label();
        private readonly Label _detail = new Label();
        private readonly Button _loadButton = new Button();
        private readonly Button _appendButton = new Button();
        private readonly Button _newButton = new Button();
        private readonly Button _renameButton = new Button();
        private readonly Button _batchButton = new Button();
        private readonly Button _deleteButton = new Button();
        private readonly Button _folderButton = new Button();
        private readonly Button _closeButton = new Button();

        /// <summary>正在拖自己的行（调整歌单顺序）——用来和"从外面拖 m3u 进来"区分开。</summary>
        private bool _draggingRow;

        private readonly Selection _result;

        /// <summary>只读入口：窗口关掉之后带出去的选择（给冒烟测试用）。</summary>
        internal Selection Result => _result;

        private readonly Font _currentFont;

        private List<SavedPlaylist> _playlists = new List<SavedPlaylist>();

        /// <summary>上一次操作的说明（导入结果之类），选中别的歌单时清掉。</summary>
        private string? _note;

        internal PlaylistLibraryDialog(string? currentName)
        {
            _result = new Selection { CurrentName = currentName };

            Text = "我的歌单";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            Font = UiFonts.Sidebar;
            DoubleBuffered = true;
            AllowDrop = true;

            _currentFont = new Font(UiFonts.Sidebar, FontStyle.Bold);

            BuildLayout();
            ApplyDpiLayout();
            Reload();
        }

        private float DpiScale => DeviceDpi / 96f;

        private int Scaled(int value) => (int)Math.Round(value * DpiScale);

        /// <summary>
        /// 打开歌单窗口。返回的一律是当前状态（不是 <c>null</c>）：
        /// 用户可能在窗口里改了名就关掉，主窗体的"当前歌单"必须跟着更新。
        /// </summary>
        public static Selection Show(IWin32Window owner, ThemePalette palette, string? currentName)
        {
            using var dialog = new PlaylistLibraryDialog(currentName);

            dialog.ApplyPalette(palette);
            dialog.ShowDialog(owner);

            return dialog._result;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyDpiLayout();
        }

        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);
            ApplyDpiLayout();
        }

        // ---- 主题 -------------------------------------------------------------

        private void ApplyPalette(ThemePalette palette)
        {
            BackColor = palette.WindowBack;
            ForeColor = palette.ListFore;

            _list.BackColor = palette.ListBack;
            _list.ForeColor = palette.ListFore;

            foreach (Control control in Controls)
            {
                switch (control)
                {
                    case Button button:
                        button.FlatStyle = FlatStyle.Flat;
                        button.BackColor = palette.MenuBack;
                        button.ForeColor = palette.MenuFore;
                        button.FlatAppearance.BorderColor = palette.MenuBorder;
                        break;

                    case Label label:
                        label.ForeColor = palette.ListFore;
                        break;
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _currentFont.Dispose();

            base.Dispose(disposing);
        }
    }
}
