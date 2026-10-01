using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace 播放器.Ui
{
    /// <summary>
    /// 一套"要出现在好几个菜单里的项"。
    /// <para>
    /// 一个 <see cref="ToolStripItem"/> 只能有一个父级，所以同一个功能在「视图」菜单、
    /// 右键菜单、托盘菜单里是<b>三套独立的对象</b>。以前每个功能都要自己写三件事：
    /// 建项、按 Tag 递归刷新、再手写一段"把这三四个宿主都刷一遍"。漏掉一处就是
    /// "从某个入口进去勾不对"，而且只有走那个入口才看得见。
    /// </para>
    /// <para>
    /// 这里把后两件事收起来：<see cref="Attach(ToolStripDropDownItem)"/> 把宿主挂进来
    /// （展开 / 弹出前自动刷自己那一份），<see cref="Refresh"/> 刷全部。
    /// "<b>怎么按当前状态刷一项</b>"仍然由各自的功能提供——每套的规则都不一样，不该硬凑成一个通用的。
    /// </para>
    /// </summary>
    public sealed class MenuSection
    {
        private readonly Action<ToolStripMenuItem> _refreshItem;
        private readonly List<ToolStripItemCollection> _hosts = new List<ToolStripItemCollection>();

        public MenuSection(Action<ToolStripMenuItem> refreshItem) =>
            _refreshItem = refreshItem ?? throw new ArgumentNullException(nameof(refreshItem));

        /// <summary>
        /// 挂一个下拉宿主（菜单项 / 下拉按钮）：展开前刷它，<see cref="Refresh"/> 也会刷到它。
        /// </summary>
        public void Attach(ToolStripDropDownItem? host)
        {
            if (host == null) return;

            _hosts.Add(host.DropDownItems);
            host.DropDownOpening += (s, e) => Walk(host.DropDownItems);
        }

        /// <summary>挂一个右键菜单：弹出前刷它，<see cref="Refresh"/> 也会刷到它。</summary>
        public void Attach(ContextMenuStrip? host)
        {
            if (host == null) return;

            _hosts.Add(host.Items);
            host.Opening += (s, e) => Walk(host.Items);
        }

        /// <summary>
        /// 刷所有挂上来的宿主。状态变了（设置改了、开关切了）就调一次，
        /// 不用再记得"哪几个菜单里也有这一套"。
        /// </summary>
        public void Refresh()
        {
            foreach (var host in _hosts) Walk(host);
        }

        /// <summary>递归：子菜单里的项也算这一套（颜色、字体那些子菜单就挂在下层）。</summary>
        private void Walk(ToolStripItemCollection items)
        {
            foreach (ToolStripItem item in items)
            {
                if (item is not ToolStripMenuItem menuItem) continue;

                _refreshItem(menuItem);

                if (menuItem.HasDropDownItems) Walk(menuItem.DropDownItems);
            }
        }
    }
}
