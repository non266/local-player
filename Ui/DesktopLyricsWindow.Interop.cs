using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using 播放器.Core;

// 原生这一层：窗口样式常量、BLENDFUNCTION、P/Invoke 与「锁定时鼠标穿透」的落地。

namespace 播放器.Ui
{
    internal sealed partial class DesktopLyricsWindow
    {
        // ---- 原生常量 ---------------------------------------------------------

        private const int GwlExStyle = -20;
        private const int WsExLayered = 0x00080000;
        private const int WsExTransparent = 0x00000020;
        private const int WsExToolWindow = 0x00000080;
        private const int WsExNoActivate = 0x08000000;

        private const int WmNcHitTest = 0x0084;
        private const int HtTransparent = -1;

        /// <summary>窗口被激活时（<c>WM_ACTIVATE</c>）。</summary>
        private const int WmActivate = 0x0006;

        /// <summary><c>WM_ACTIVATE</c> 的 wParam：不是点击引起的激活（要还前台）。</summary>
        private const int WaActive = 1;

        /// <summary>鼠标在窗口里移动（只有落在"字"的像素上才会收到）。</summary>
        private const int WmMouseMove = 0x0200;

        /// <summary>检查"鼠标还在不在歌词上"的间隔（毫秒）。</summary>
        private const int HoverCheckInterval = 120;

        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoActivate = 0x0010;

        private static readonly IntPtr HwndTopMost = new IntPtr(-1);

        private const int UlwAlpha = 0x00000002;
        private const byte AcSrcOver = 0x00;
        private const byte AcSrcAlpha = 0x01;

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct BlendFunction
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
        private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr GetWindowLong64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLong64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(
            IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int width, int height, uint flags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UpdateLayeredWindow(
            IntPtr hWnd,
            IntPtr hdcDst,
            ref Point pptDst,
            ref Size psize,
            IntPtr hdcSrc,
            ref Point pptSrc,
            int crKey,
            ref BlendFunction pblend,
            int dwFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hDC);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hDC);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        // ---- 原生样式 ---------------------------------------------------------

        private static IntPtr ReadExtendedStyle(IntPtr handle) =>
            IntPtr.Size == 8
                ? GetWindowLong64(handle, GwlExStyle)
                : (IntPtr)GetWindowLong32(handle, GwlExStyle);

        private static void WriteExtendedStyle(IntPtr handle, IntPtr style)
        {
            if (IntPtr.Size == 8) SetWindowLong64(handle, GwlExStyle, style);
            else SetWindowLong32(handle, GwlExStyle, style.ToInt32());
        }

        /// <summary>
        /// 锁定状态落到窗口样式上。
        /// <para>
        /// 背景透明之后，<b>透明的地方本来就已经点得到了</b>（分层窗口按 alpha 做命中测试），
        /// 所以这里要管的是"连字也不让点"：锁定时把整个窗口对鼠标透明。
        /// </para>
        /// <para>
        /// 两条路一起走：
        /// <list type="number">
        ///   <item><c>WS_EX_TRANSPARENT</c>：系统在做命中测试时会跳过这个窗口，
        ///   点击直接落到下面的程序上（跨进程有效），这是主要机制。</item>
        ///   <item><c>WM_NCHITTEST</c> 回 <c>HTTRANSPARENT</c>：见 <see cref="WndProc"/>，
        ///   作为兜底——文档里这条路只对<b>同一个线程</b>的窗口继续往下传。</item>
        /// </list>
        /// </para>
        /// </summary>
        private void ApplyMouseTransparency()
        {
            if (!IsHandleCreated) return;

            var style = ReadExtendedStyle(Handle).ToInt64();

            style |= WsExLayered;

            if (_locked) style |= WsExTransparent;
            else style &= ~(long)WsExTransparent;

            WriteExtendedStyle(Handle, new IntPtr(style));
        }
    }
}
