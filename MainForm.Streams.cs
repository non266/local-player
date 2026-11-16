using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using 播放器.Core;

namespace 播放器
{
    /// <summary>最近打开过的网络串流地址（1.23.0 F8）。</summary>
    public partial class MainForm
    {
        private ToolStripMenuItem? _menuRecentStreams;

        /// <summary>「文件 → 最近地址」那一项（运行时插进去的；冒烟要按它）。</summary>
        internal ToolStripMenuItem? RecentStreamsMenu => _menuRecentStreams;

        /// <summary>把「最近地址」子菜单插到「打开网络串流…」后面（不改 Designer）。</summary>
        internal void ConfigureRecentStreamsMenu()
        {
            if (_menuRecentStreams != null) return;

            var menu = new ToolStripMenuItem("最近地址(&R)");
            menu.DropDownOpening += (s, e) => RebuildRecentStreamsMenu();

            _menuRecentStreams = menu;

            var index = menuFile.DropDownItems.IndexOf(menuFileOpenUrl);

            menuFile.DropDownItems.Insert(Math.Max(0, index + 1), menu);
        }

        /// <summary>
        /// 按当前设置重建「最近地址」：空时是灰的「（暂无）」，否则最新在前（&1～&9）、
        /// 末尾是「清除最近地址」。菜单里只显示 <see cref="RecentStreams.Describe"/> 的短标签——
        /// 地址里的账号口令不能摆在屏幕上。
        /// </summary>
        internal void RebuildRecentStreamsMenu()
        {
            if (_menuRecentStreams == null) return;

            ClearMenuItems(_menuRecentStreams.DropDownItems);

            var recent = RecentStreams.Normalize(_settings.RecentStreams);
            _settings.RecentStreams = recent;

            if (recent.Count == 0)
            {
                _menuRecentStreams.DropDownItems.Add(
                    new ToolStripMenuItem("（暂无记录）") { Enabled = false });
                _menuRecentStreams.Enabled = false;
                return;
            }

            _menuRecentStreams.Enabled = true;

            for (var i = 0; i < recent.Count; i++)
            {
                var url = recent[i];
                var label = RecentStreams.Describe(url);
                var text = i < 9 ? $"&{i + 1} {label}" : label;

                var item = new ToolStripMenuItem(text) { Tag = url, ToolTipText = label };
                item.Click += (s, e) => OpenRecentStream((string)item.Tag!);

                _menuRecentStreams.DropDownItems.Add(item);
            }

            _menuRecentStreams.DropDownItems.Add(new ToolStripSeparator());

            var clearItem = new ToolStripMenuItem("清除最近地址(&C)");
            clearItem.Click += (s, e) => ClearRecentStreams();
            _menuRecentStreams.DropDownItems.Add(clearItem);
        }

        /// <summary>记一条"打开过的串流"（去重、置顶、截到 10 条），并重建菜单。</summary>
        internal void RememberRecentStream(string url)
        {
            _settings.RecentStreams = RecentStreams.Push(_settings.RecentStreams, url);
            RebuildRecentStreamsMenu();
        }

        private void OpenRecentStream(string url)
        {
            RememberRecentStream(url);
            AddPathsWithFolders(new[] { url }, AddPlayback.Always);
        }

        /// <summary>
        /// 清空最近地址。<b>当场落盘</b>：这是个隐私动作，不能等退出时才写
        /// （别的设置是"改一下记在心里、退出时写一次"，这里特意不一样）。
        /// </summary>
        private void ClearRecentStreams()
        {
            _settings.RecentStreams = new List<string>();
            RebuildRecentStreamsMenu();

            try
            {
                _settings.Save();
            }
            catch (Exception ex)
            {
                ShowError("保存设置失败", ex);
            }

            SetStatus("已清除最近打开的串流地址");
        }
    }
}
