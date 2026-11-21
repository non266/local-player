using System;
using System.Drawing;
using System.Windows.Forms;
using 播放器.Core;

namespace 播放器.Ui
{
    /// <summary>
    /// 文件页左边的文件夹树（1.28.0）：<b>只负责控件外观与键盘行为</b>，懒加载、同步、
    /// 代际号都在 <see cref="FileBrowserView"/> 的 Tree partial 里。
    /// <para>
    /// 用系统 <see cref="TreeView"/> 而不是自绘：树要的是展开 / 折叠 / 键盘 / 无障碍这一整套，
    /// 自绘只会把 WinForms 已经做好的事重做一遍；深色主题靠 <see cref="NativeTheme.ApplyExplorer"/>
    /// 把滚动条与展开箭头切过去，文字与背景走调色板。
    /// </para>
    /// </summary>
    internal sealed class FolderTree : TreeView
    {
        internal FolderTree()
        {
            BorderStyle = BorderStyle.None;
            HideSelection = false;
            FullRowSelect = true;
            ShowLines = true;
            ShowPlusMinus = true;
            ShowRootLines = true;
            ShowNodeToolTips = true;
            TabStop = true;

            AccessibleName = "文件夹树";
            AccessibleDescription = "浏览磁盘与文件夹；点一个文件夹就在右边列出来";
        }

        /// <summary>行高与 DPI 有关，由视图统一按 DPI 设置。</summary>
        internal void SetMetrics(int itemHeight)
        {
            ItemHeight = Math.Max(12, itemHeight);
        }

        /// <summary>跟着主题换颜色；深色时让系统把滚动条 / 展开箭头也切过去。</summary>
        internal void ApplyPalette(ThemePalette palette, bool dark)
        {
            BackColor = palette.ListBack;
            ForeColor = palette.ListFore;

            if (!IsHandleCreated) return;

            NativeTheme.ApplyExplorer(Handle, dark);
            Invalidate();
        }
    }
}
