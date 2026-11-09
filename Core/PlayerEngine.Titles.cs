using System;

// 标题（DVD 的 title）与菜单导航：都直接落在 MediaPlayer 上，出错时安静地当"没有"。

namespace 播放器.Core
{
    public sealed partial class PlayerEngine
    {
        /// <summary>
        /// 标题数；没有媒体时为 0。
        /// <para>
        /// DVD 上它包含菜单那个 title（<c>dvd://</c> 时正片从 1 开始）；普通文件一般是 1。
        /// </para>
        /// </summary>
        public int TitleCount
        {
            get
            {
                try { return Math.Max(0, _mediaPlayer.TitleCount); }
                catch (Exception) { return 0; }
            }
        }

        /// <summary>当前标题号；没有媒体时为 <c>-1</c>。</summary>
        public int Title
        {
            get
            {
                try { return _mediaPlayer.Title; }
                catch (Exception) { return -1; }
            }
            set
            {
                try { _mediaPlayer.Title = Math.Max(0, value); }
                catch (Exception) { }
            }
        }

        /// <summary>某个标题下的章节数（DVD 每个标题的章数不一定一样，所以不能只看总数）。</summary>
        public int ChapterCountForTitle(int title)
        {
            try { return Math.Max(0, _mediaPlayer.ChapterCountForTitle(title)); }
            catch (Exception) { return 0; }
        }

        /// <summary>DVD 菜单导航：上 / 下 / 左 / 右 / 确定 / 回菜单根。</summary>
        public void Navigate(DvdMenuAction action)
        {
            try { _mediaPlayer.Navigate(DvdMenu.ModeOf(action)); }
            catch (Exception) { }
        }
    }
}
