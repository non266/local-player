using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using 播放器.Core;

// 摆位与尺寸：摆到保存的位置 / 居中、内容变化后重排、锚点与工作区钳制。

namespace 播放器.Ui
{
    internal sealed partial class DesktopLyricsWindow
    {
        /// <summary>
        /// 摆到保存过的位置。
        /// <para>
        /// 摆完一定要再钳一次：上次退出到现在，显示器可能被拔掉、分辨率或缩放可能变了，
        /// 而这是一个没有边框的窗口，跑到可视区外面就等于消失了。
        /// </para>
        /// </summary>
        public void MoveTo(Point location)
        {
            Location = location;
            ClampToWorkingArea();

            // 这次位置不是用户拖出来的（来自设置恢复 / 设置方案切换 / 居中），
            // 所以内容随后变化时不要以中心为锚——否则"上次放在这儿"会被
            // 半屏的宽度差重新居中、当场挪走（冒烟第 15 步实测 1218 → 368）。
            _anchoredByContent = false;
        }

        /// <summary>
        /// 把窗口摆到所在屏幕的下方居中——第一次开启时的默认位置。
        /// <para>第一次开时窗口还没摆过，位置是 (0,0)，所以这里拿到的是主屏幕。</para>
        /// </summary>
        public void CenterOnScreen()
        {
            var area = Screen.FromControl(this).WorkingArea;

            Location = new Point(
                area.Left + (area.Width - Width) / 2,
                area.Bottom - Height - (int)Math.Round(area.Height * 0.1));

            ClampToWorkingArea();
        }


        /// <summary>重新排版：算出两行文字、量出需要的客户区大小、按需伸缩窗口，然后重画。</summary>
        private void ApplyContent()
        {
            BuildTexts();

            var maximum = MaximumContentWidth;

            var current = Measure(_currentText, MainFont, maximum);
            var next = _hasNext ? Measure(_nextText, NextFont, maximum) : SizeF.Empty;

            _currentHeight = current.Height;
            _nextHeight = next.Height;

            var contentWidth = Math.Max(current.Width, next.Width);

            var width = Math.Max(MinimumBoxWidth, (int)Math.Ceiling(contentWidth + PaddingX * 2 + WrapSlack));
            var height = Math.Max(1, (int)Math.Ceiling(ContentHeight + PaddingY * 2 + ControlsTotalHeight));

            // 控制条常驻，所以窗口至少要能放下它：歌词很短时（比如只有 "♪"）也要撑到这个宽度，
            // 否则四个按钮会被裁掉一半。
            if (ControlsVisible) width = Math.Max(width, ControlsTotalWidth + PaddingX * 2);

            if (ClientSize.Width != width || ClientSize.Height != height)
                ResizeKeepingCenter(new Size(width, height));

            Render();
        }

        /// <summary>
        /// 改尺寸时怎么锚。
        /// <para>
        /// 用户<b>没动过</b>它时（位置来自设置恢复、设置方案、或首次居中）锚<b>左上角</b>：
        /// 那时"上次放在这儿"是用户的意图，内容一变就重新居中会把它挪走半屏。
        /// 用户<b>拖过</b>之后锚<b>水平中心</b>：歌词换一句就变宽变窄，锚在左边窗口会左右乱跳，
        /// 锚在中心看起来才是"在屏幕那个位置"。
        /// </para>
        /// </summary>
        private void ResizeKeepingCenter(Size size)
        {
            var anchor = _anchoredByContent ? Left + Width / 2 : Left;

            ClientSize = size;
            Location = new Point(_anchoredByContent ? anchor - Width / 2 : anchor, Top);

            ClampToWorkingArea();
        }

        /// <summary>把窗口整体挪回工作区内——边框都没了，拖出去就再也找不回来。</summary>
        private void ClampToWorkingArea()
        {
            var probe = new Rectangle(Location, Size);
            var area = Screen.FromRectangle(probe).WorkingArea;

            Location = new Point(
                Math.Max(Math.Min(Left, area.Right - Width), area.Left),
                Math.Max(Math.Min(Top, area.Bottom - Height), area.Top));
        }
    }
}
