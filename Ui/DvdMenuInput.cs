using System;
using System.Windows.Forms;
using 播放器.Core;

namespace 播放器.Ui
{
    /// <summary>
    /// 把按键翻成 DVD 菜单动作（只认方向键与回车）。
    /// <para>
    /// 单独放一个纯函数的原因：这几条映射是"菜单里方向键到底去哪"的唯一定义，
    /// 界面上（<c>MainForm.ProcessCmdKey</c>）只管"菜单在屏上就叫它"。
    /// </para>
    /// <para>
    /// Esc 不在这里：全屏时它是"退出全屏"（多年习惯），拿它当"回菜单根"会让全屏里退不出来。
    /// 要回菜单根走「播放 → 光盘 → 回到菜单」或画面右键菜单。
    /// </para>
    /// </summary>
    public static class DvdMenuInput
    {
        /// <summary>这个键是不是菜单动作；不是就返回 <c>null</c>。</summary>
        public static DvdMenuAction? For(Keys key) => key switch
        {
            Keys.Up => DvdMenuAction.Up,
            Keys.Down => DvdMenuAction.Down,
            Keys.Left => DvdMenuAction.Left,
            Keys.Right => DvdMenuAction.Right,
            Keys.Enter => DvdMenuAction.Activate,
            _ => null
        };
    }
}
