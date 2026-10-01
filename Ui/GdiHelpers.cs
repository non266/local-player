using System.Drawing;
using System.Drawing.Drawing2D;

namespace 播放器.Ui
{
    /// <summary>几个自绘控件共用的绘图小工具。</summary>
    internal static class GdiHelpers
    {
        /// <summary>构造一个圆角矩形路径。</summary>
        public static GraphicsPath RoundedRect(RectangleF bounds, float radius)
        {
            var path = new GraphicsPath();

            // 半径不能超过短边的一半，否则弧会互相吃掉。
            radius = System.Math.Max(0f, System.Math.Min(radius, System.Math.Min(bounds.Width, bounds.Height) / 2f));
            if (radius <= 0.1f)
            {
                path.AddRectangle(bounds);
                return path;
            }

            var diameter = radius * 2f;

            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180f, 90f);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270f, 90f);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0f, 90f);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90f, 90f);
            path.CloseFigure();

            return path;
        }
    }
}
