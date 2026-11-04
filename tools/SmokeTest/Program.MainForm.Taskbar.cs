// 检查点 6 的一部分：任务栏缩略图按钮（1.11.0 B11）。

namespace SmokeTest
{
    internal static partial class Program
    {
        /// <summary>
        /// 鼠标悬停任务栏图标时，预览面板上那排「上一个 / 播放暂停 / 下一个 / 停止」。
        /// <para>
        /// 这套按钮从 1.0 起就有代码，却<b>从来没出现过</b>：以前在窗口可见之前就调
        /// <c>ThumbBarAddButtons</c>，Explorer 收下调用照样返回 <c>S_OK</c>，代码就把"已挂上"
        /// 置了真，之后再也不会重试（1.11.0 修的就是这个）。
        /// 冒烟能验的是这条时序不变式 + 点击能控制播放 + 没媒体时会置灰；
        /// <b>系统界面上到底画没画出来只能人眼看</b>——README 与 CHANGELOG 里都写着。
        /// </para>
        /// </summary>
        private static bool CheckTaskbarThumbnailButtons(Form form)
        {
            var main = (播放器.MainForm)form;
            var buttons = main.TaskbarButtons;

            if (buttons == null)
            {
                Log(6, "任务栏按钮：窗口显示之后按钮对象都没建出来");
                return false;
            }

            if (!buttons.IsActive)
            {
                Log(6, "任务栏按钮：窗口显示之后仍然没有挂上（ThumbBarAddButtons 没成功）");
                return false;
            }

            if (buttons.ButtonCount != 4)
            {
                Log(6, $"任务栏按钮：只有 {buttons.ButtonCount} 个按钮（缺停止）");
                return false;
            }

            if (buttons.AddsWhileHidden != 0)
            {
                Log(6, "任务栏按钮：窗口还没显示就调了 ThumbBarAddButtons"
                        + "（Explorer 会收下，但按钮不会出现——1.0～1.10 就是这个毛病）");
                return false;
            }

            if (buttons.HiddenSkips == 0)
            {
                Log(6, "任务栏按钮：可见性守卫没有生效（启动时一次都没跳过，说明注册仍然发生在窗口显示之前）");
                return false;
            }

            // ---- 点击：喂一条和系统完全一样的 WM_COMMAND ----
            var wasPlaying = main.Engine.IsPlaying;

            SendThumbButtonClick(main, TaskbarThumbnailButtons.ButtonPlayPause);
            PumpMessages(500);

            if (main.Engine.IsPlaying == wasPlaying)
            {
                Log(6, "任务栏按钮：点了播放 / 暂停没有反应");
                return false;
            }

            SendThumbButtonClick(main, TaskbarThumbnailButtons.ButtonPlayPause);
            PumpMessages(500);

            if (main.Engine.IsPlaying != wasPlaying)
            {
                Log(6, "任务栏按钮：再点一次播放 / 暂停没有切回去");
                return false;
            }

            SendThumbButtonClick(main, TaskbarThumbnailButtons.ButtonStop);
            PumpMessages(500);

            if (main.Engine.HasMedia)
            {
                Log(6, "任务栏按钮：点了停止之后还有媒体（停止没接线）");
                return false;
            }

            // ---- 没有媒体时：播放 / 停止要置灰；列表里还有条目，上一个 / 下一个仍可用 ----
            main.RefreshTransportState();

            var flags = buttons.LastFlags;

            if (flags.Length != 4)
            {
                Log(6, $"任务栏按钮：推给任务栏的按钮有 {flags.Length} 个（应该 4 个）");
                return false;
            }

            if ((flags[1] & 0x1) == 0 || (flags[3] & 0x1) == 0)
            {
                Log(6, "任务栏按钮：没有媒体时播放 / 停止没有置灰（没有推 THBF_DISABLED）");
                return false;
            }

            if ((flags[0] & 0x1) != 0 || (flags[2] & 0x1) != 0)
            {
                Log(6, "任务栏按钮：列表里还有条目时上一个 / 下一个不该置灰");
                return false;
            }

            // 把播放恢复回去：后面的检查（进度条拖动、搜索打字…）都假设有一首正在播。
            SendThumbButtonClick(main, TaskbarThumbnailButtons.ButtonPlayPause);

            if (!PumpUntil(() => main.Engine.HasMedia && main.Engine.IsPlaying, 15000))
            {
                Log(6, "任务栏按钮：停止之后再用那个按钮没能把播放恢复起来");
                return false;
            }

            Log(6, "任务栏缩略图按钮正常：窗口显示后才挂上 4 个（含停止），窗口没显示时一次都没注册，"
                    + "点播放 / 暂停与停止都能作用到引擎，没媒体时播放 / 停止置灰、上一个 / 下一个跟着列表走");
            return true;
        }

        /// <summary>把一条 THBN_CLICKED 的 WM_COMMAND 喂给窗口（等价于系统点了那个按钮）。</summary>
        private static void SendThumbButtonClick(播放器.MainForm form, int buttonId)
        {
            const int thbnClicked = 0x1800;

            var wParam = new IntPtr(((long)thbnClicked << 16) | (uint)buttonId);
            var message = Message.Create(form.Handle, TaskbarThumbnailButtons.WmCommand, wParam, IntPtr.Zero);

            form.ProcessWindowMessage(ref message);
        }
    }
}
