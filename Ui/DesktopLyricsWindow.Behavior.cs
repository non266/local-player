using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using 播放器.Core;

// 行为：不抢焦点、拖入字体、悬停背景、鼠标拖动与收尾。

namespace 播放器.Ui
{
    internal sealed partial class DesktopLyricsWindow
    {
        /// <summary>
        /// 用户往歌词窗口上拖了字体文件。
        /// <para>
        /// 窗口自己不认得怎么导入（那要写进数据目录、还要刷菜单和两处歌词），
        /// 所以只把路径抛给主窗体，由它走"和菜单导入完全相同"的那条路。
        /// </para>
        /// <para>⚠ 锁定时窗口是鼠标穿透的（<c>WS_EX_TRANSPARENT</c>），拖不进来——这是预期的。</para>
        /// </summary>
        public event Action<string[]>? FontFilesDropped;

        private static string[] FontFilesIn(IDataObject? data)
        {
            if (data == null || !data.GetDataPresent(DataFormats.FileDrop)) return Array.Empty<string>();

            if (data.GetData(DataFormats.FileDrop) is not string[] paths) return Array.Empty<string>();

            return paths.Where(Core.FontLibrary.IsSupported).ToArray();
        }

        private void OnFontDragEnter(object? sender, DragEventArgs e) =>
            e.Effect = FontFilesIn(e.Data).Length > 0 ? DragDropEffects.Copy : DragDropEffects.None;

        private void OnFontDragDrop(object? sender, DragEventArgs e)
        {
            var paths = FontFilesIn(e.Data);
            if (paths.Length == 0) return;

            FontFilesDropped?.Invoke(paths);
        }

        /// <summary>用 Show() 显示时不激活自己：全屏看视频时弹出来绝不能把焦点抢走。</summary>
        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;

                // LAYERED 是逐像素透明（UpdateLayeredWindow）的前提；
                // TOOLWINDOW：不出现在 Alt+Tab 里（ShowInTaskbar=false 只管任务栏）；
                // NOACTIVATE：点了也不激活，不打断下面那个程序。
                parameters.ExStyle |= WsExLayered | WsExToolWindow | WsExNoActivate;

                return parameters;
            }
        }

        /// <summary>
        /// 显示窗口，并保证不夺走前台。
        /// <para>
        /// 只靠 <c>WS_EX_NOACTIVATE</c> + <see cref="ShowWithoutActivation"/> <b>不够</b>：
        /// 本机实测这个窗口被显示出来的时候，前台窗口常常就变成了它，
        /// 而"全屏看视频时弹出歌词"最不能接受的就是把焦点抢走——
        /// 抢走之后全屏视频收不到键盘、游戏里的按键也会丢。
        /// </para>
        /// <para>
        /// 拦截放在 <see cref="WndProc"/> 里（收到激活消息就还给窗口构造时记下的那个窗口）。
        /// 不能靠时序：实测 <c>Show()</c> 返回之后激活还可能补上来，
        /// 在 <c>Show()</c> 后面顺着写一句 <c>SetForegroundWindow</c> 是拦不住的。
        /// </para>
        /// </summary>
        public void ShowWithoutTakingFocus()
        {
            Show();
            BringToTop();
            GiveBackForeground(force: false);

            // 激活可能比 Show() 晚一步生效（实测确实如此），所以等消息循环转一圈再核对一次。
            // 少了这一手，就只能在"Show() 返回那一瞬间"判断有没有被抢，晚了就漏掉。
            try
            {
                BeginInvoke(new Action(() => GiveBackForeground(force: false)));
            }
            catch (Exception ex)
            {
// 窗口正在销毁，忽略
                AppLog.Swallowed("窗口正在销毁，忽略", ex);
            }
        }

        /// <summary>如果前台被我们抢走了，就还给显示之前那个窗口。</summary>
        /// <param name="force">
        /// <c>true</c> 表示"我正在被激活，无条件还回去"：在 <c>WM_ACTIVATE</c> 处理中
        /// <see cref="GetForegroundWindow"/> 可能还没更新，靠它判断会漏。
        /// <c>false</c> 是兜底路径，只在确实被抢走时才还。
        /// </param>
        private void GiveBackForeground(bool force)
        {
            var target = _focusReturnTarget;

            if (target == IntPtr.Zero || target == Handle) return;
            if (!IsHandleCreated || IsDisposed) return;
            if (!force && GetForegroundWindow() != Handle) return;

            SetForegroundWindow(target);
        }

        /// <summary>
        /// 把它重新提到所有置顶窗口之上。
        /// <para>
        /// 主窗口进全屏时会自己变成置顶窗口，两个置顶窗口之间是按激活顺序排的，
        /// 后置顶的主窗口会盖在上面——桌面歌词这时就看不见了。
        /// 所以进全屏、以及主窗口切"窗口置顶"之后，都要显式再提一次。
        /// </para>
        /// <para><c>SWP_NOACTIVATE</c> 不能省：提层级的同时绝不能把焦点从视频上抢走。</para>
        /// </summary>
        public void BringToTop()
        {
            if (!IsHandleCreated) return;

            SetWindowPos(Handle, HwndTopMost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);

            ApplyMouseTransparency();
            ApplyContent();
        }

        protected override void WndProc(ref Message m)
        {
            // 锁定时连非客户区命中测试都直接让路。
            if (_locked && m.Msg == WmNcHitTest)
            {
                m.Result = (IntPtr)HtTransparent;
                return;
            }

            // 鼠标消息只会在"有像素"的地方送到这个窗口，所以它只能当个"立刻反应"的加速器，
            // 真正判断在不在歌词上的是 UpdateHover（计时器里按窗口矩形算）。
            if (m.Msg == WmMouseMove) UpdateHover();

            base.WndProc(ref m);

            // 有人把前台切到我们身上了：立刻还回去。
            // 这个窗口的意义是"只是显示"，任何时候都不该成为用户正在操作的那个窗口。
            // 有人把前台切到我们身上了：把"不是用户点出来的那种"还回去。
            // ⚠ 这里必须区分 wParam：
            //   WA_ACTIVE(1)      —— Show() 之后系统补上来的激活，要还回去（不然弹歌词会抢焦点）
            //   WA_CLICKACTIVE(2) —— 用户<b>点</b>出来的激活，绝不能还！
            // 实测：连点击引起的激活也一并还回去的话，那次左键"按下"会被整个取消，
            // 窗口从此点不动、拖不动（消息、抬起都到，唯独按下不来）。
            if (m.Msg == WmActivate && m.WParam.ToInt64() == WaActive)
                GiveBackForeground(force: true);
        }

        /// <summary>
        /// 鼠标进到歌词上了：给一块背景显示出来。
        /// <para>
        /// 背景是全透明的，用户平时只看到一串悬空的字，看不出哪里可以抓；
        /// 所以鼠标停上来要铺一块背景。
        /// </para>
        /// </summary>
        private void BeginHover()
        {
            if (!_hovering)
            {
                _hovering = true;
                Render();
            }
        }

        /// <summary>
        /// 按真实光标位置更新"鼠标在不在歌词上"。
        /// <para>
        /// ⚠ 这里<b>不能</b>等系统的鼠标消息：命中区域是逐像素的，
        /// 背景透明时光标只要落在字之间的空隙上，窗口根本收不到任何消息——
        /// 于是"把鼠标移到歌词上"这件事永远没人知道，只有正正好戳中某个笔画才有反应，
        /// 用户的感觉就是"移动不了"。所以由一个计时器按窗口矩形主动问光标在哪。
        /// </para>
        /// <para>
        /// 同理，判断"离开"也不能靠 <c>WM_MOUSELEAVE</c>：逐像素命中测试下，
        /// 光标一落到透明像素上系统就认为"已经离开窗口"，那块背景会刚出现就被收掉。
        /// </para>
        /// </summary>
        private void UpdateHover()
        {
            // 锁定的意思是"这个窗口在鼠标眼里不存在"，那就不该有任何提示
            if (_locked)
            {
                ClearHoveredControl();
                EndHover();
                return;
            }

            // 拖动过程中光标可能被拖到窗口外面，但这时背景要留着（用户正抓着它）
            if (_dragging) return;

            // 按"窗口矩形"判断而不是按像素：矩形之内都算还在歌词上，
            // 这样两块文字之间的空隙、以及字的描边之外那点边距，都算在里面。
            var client = PointToClient(Cursor.Position);
            var inside = ClientRectangle.Contains(client);

            // 按钮高亮要跟着光标走，所以放在 inside 的早退之前——
            // 在两个按钮之间移动时 inside 一直是 true，早退的话高亮就永远不动了。
            UpdateHoveredControl(client);

            if (inside == _hovering) return;

            _hovering = inside;
            Render();
        }

        /// <summary>光标已经不在歌词上了：把背景收掉。</summary>
        private void EndHover()
        {
            if (!_hovering) return;

            _hovering = false;
            Render();
        }

        private void OnHoverTimerTick(object? sender, EventArgs e) => UpdateHover();

        /// <summary>窗口显示 / 隐藏时开关轮询：藏起来的时候没必要一直问光标在哪。</summary>
        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);

            if (Visible)
            {
                if (!_hoverTimer.Enabled) _hoverTimer.Start();
                UpdateHover();
            }
            else
            {
                _hoverTimer.Stop();
                EndHover();
            }
        }

        // ---- 拖动 -------------------------------------------------------------

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);

            // 记下按下的位置：双击那道"是不是落在按钮上"的判断用它，而不是实时光标位置
            _lastMousePoint = e.Location;

            if (_locked || e.Button != MouseButtons.Left) return;

            // 落在控制条按钮上就是"按按钮"，不是拖动：
            // 拖动是桌面歌词唯一的移动方式，不能被按钮吃掉；反过来点按钮也不该把窗口拖走。
            if (TryPressControl(e.Location)) return;

            _dragging = true;
            _dragOrigin = PointToScreen(e.Location);
            _dragWindowOrigin = Location;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            // 拖动中窗口跟着光标跑，按钮的"悬停"没有意义；不拖动时让高亮跟手，不等计时器节拍
            if (!_dragging) UpdateHoveredControl(e.Location);

            if (!_dragging) return;

            // 用"窗口当初的位置 + 鼠标位移"来算，而不是累加每次的增量：
            // 累加会把每一次取整误差留下来，拖久了窗口会和光标脱节。
            var now = PointToScreen(e.Location);

            // 用户真的动过它了：之后内容引起的伸缩以中心为锚（见 ResizeKeepingCenter）
            _anchoredByContent = true;

            Location = new Point(
                _dragWindowOrigin.X + now.X - _dragOrigin.X,
                _dragWindowOrigin.Y + now.Y - _dragOrigin.Y);

            ClampToWorkingArea();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && _pressedControl >= 0) ReleaseControl(e.Location);

            _dragging = false;
            base.OnMouseUp(e);

            // 拖出去再松手时，光标已经不在窗口里，别等下一个计时器节拍
            UpdateHover();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _hoverTimer.Stop();
                _hoverTimer.Dispose();

                DisposeFonts();

                _surface?.Dispose();
                _surface = null;

                _measure?.Dispose();
                _measure = null;
                _probe?.Dispose();
                _probe = null;

                _format.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
