// 视频 —— 分割与吸附（侧栏与视频面板之间的分割条、拖动与吸附判据）。
//
// 从 Program.Video.cs 按 ARCHITECTURE 17.2 的规矩整块搬过来：成员一个字没动。

namespace SmokeTest
{
    internal static partial class Program
    {
        /// <summary>
        /// 检查侧栏与播放列表是不是真的各自独立：互不牵连，而且都能脱离主窗口再归位。
        /// <para>
        /// 以前两者挤在同一个面板里，关掉播放列表会把侧栏一起收走——那是个实打实的耦合缺陷，
        /// 所以这里专门盯着它。
        /// </para>
        /// </summary>
        private static bool CheckSeparation(Form form)
        {
            var splitVideo = Find(form, "splitVideo") as SplitContainer;
            var splitMain = Find(form, "splitMain") as SplitContainer;
            var sidebar = Find(form, "sidebarPanel");
            var playlistHost = Find(form, "playlistHost");

            if (splitVideo == null || splitMain == null || sidebar == null || playlistHost == null)
            {
                Log(11, "分离检查：找不到分隔条或承载面板");
                return false;
            }

            var showPlaylist = FindViewMenuItem(form, "显示播放列表");
            var showSidebar = FindViewMenuItem(form, "显示侧栏");
            var floatSidebar = FindViewMenuItem(form, "侧栏分离为悬浮窗");
            var floatPlaylist = FindViewMenuItem(form, "播放列表分离为独立窗口");

            if (showPlaylist == null || showSidebar == null || floatSidebar == null || floatPlaylist == null)
            {
                Log(11, "分离检查：「视图」菜单里缺少相关菜单项");
                return false;
            }

            // ---- 1) 关掉播放列表，不应该连带收起侧栏 ----
            SetMenuChecked(showPlaylist, false);
            PumpMessages(250);

            if (splitVideo.Panel2Collapsed)
            {
                Log(11, "分离检查：关掉播放列表把侧栏一起收起来了（两者仍然耦合）");
                return false;
            }

            SetMenuChecked(showPlaylist, true);
            PumpMessages(250);

            // ---- 2) 关掉侧栏，不应该连带收起播放列表 ----
            SetMenuChecked(showSidebar, false);
            PumpMessages(250);

            if (splitMain.Panel2Collapsed)
            {
                Log(11, "分离检查：关掉侧栏把播放列表一起收起来了");
                return false;
            }

            SetMenuChecked(showSidebar, true);
            PumpMessages(250);

            // ---- 3) 侧栏分离为悬浮窗，再归位 ----
            SetMenuChecked(floatSidebar, true);
            PumpMessages(400);

            var sidebarForm = sidebar.FindForm();
            if (sidebarForm == null || ReferenceEquals(sidebarForm, form) || !sidebarForm.Visible)
            {
                Log(11, "分离检查：侧栏没有搬进独立的悬浮窗");
                return false;
            }

            if (!splitVideo.Panel2Collapsed)
            {
                Log(11, "分离检查：侧栏分离之后原停靠位置没有收起来");
                return false;
            }

            SetMenuChecked(floatSidebar, false);
            PumpMessages(400);

            if (!ReferenceEquals(sidebar.Parent, splitVideo.Panel2))
            {
                Log(11, "分离检查：侧栏没有归位到原来的分隔条");
                return false;
            }

            // ---- 4) 播放列表分离为独立窗口，再归位 ----
            SetMenuChecked(floatPlaylist, true);
            PumpMessages(400);

            var playlistForm = playlistHost.FindForm();
            if (playlistForm == null || ReferenceEquals(playlistForm, form) || !playlistForm.Visible)
            {
                Log(11, "分离检查：播放列表没有搬进独立窗口");
                return false;
            }

            SetMenuChecked(floatPlaylist, false);
            PumpMessages(400);

            if (!ReferenceEquals(playlistHost.Parent, splitMain.Panel2))
            {
                Log(11, "分离检查：播放列表没有归位到原来的面板");
                return false;
            }

            // ---- 5) 把两者都留在分离状态：吸附检查需要它，收尾的设置检查也需要 ----
            SetMenuChecked(floatSidebar, true);
            PumpMessages(300);
            SetMenuChecked(floatPlaylist, true);
            PumpMessages(400);

            // ---- 6) 吸附到主窗口边上：贴齐、跟着走、拖开脱离 ----
            if (!CheckSnapping(form, sidebar)) return false;

            Log(11, "侧栏与播放列表相互独立，且都能分离为独立窗口再归位");
            return true;
        }

        /// <summary>
        /// 悬浮窗吸附在主窗口边上。
        /// <para>
        /// 验三件事，缺一不可：
        /// <list type="number">
        ///   <item>拖到主窗口边缘附近会<b>贴齐</b>（左边界正好等于主窗口右边界）；</item>
        ///   <item>贴住之后移动主窗口，它<b>跟着走</b>（这才是"吸附"而不是"碰巧对齐"）；</item>
        ///   <item>拖开足够远会<b>脱离</b>，不再被拽回去。</item>
        /// </list>
        /// 另外顺带验下边贴齐（说明不只支持左右两侧）。
        /// </para>
        /// <para>调用时两者都处于分离状态；结束时把侧栏留在"吸附在右侧"。</para>
        /// </summary>
        /// <param name="form">主窗口。</param>
        /// <param name="sidebar">
        /// 侧栏控件。<b>必须由调用方在它还没被搬走时先取好引用</b>——
        /// 分离之后它的父级是悬浮窗（不是主窗口的子孙），
        /// 这时候再用 <c>Find(form, "sidebarPanel")</c> 是找不到它的。
        /// </param>
        private static bool CheckSnapping(Form form, Control sidebar)
        {
            var sidebarForm = sidebar?.FindForm();

            if (sidebarForm == null || ReferenceEquals(sidebarForm, form))
            {
                Log(11, "吸附检查：此刻侧栏不在悬浮窗状态");
                return false;
            }

            string Edge() => ((播放器.MainForm)form).SidebarSnap.ToString();

            // ---- 1) 先拖远：应当是未吸附状态 ----
            sidebarForm.Location = new Point(form.Left - sidebarForm.Width - 260, form.Top + 60);
            PumpMessages(250);

            if (Edge() != "None")
            {
                Log(11, $"吸附检查：拖远之后仍然是「{Edge()}」");
                return false;
            }

            // ---- 2) 拖到主窗口右侧附近：应当贴齐 ----
            sidebarForm.Location = new Point(form.Right + 8, form.Top + 90);
            PumpMessages(250);

            if (Edge() != "Right")
            {
                Log(11, $"吸附检查：拖到右侧附近没有被吸附（当前「{Edge()}」）");
                return false;
            }

            if (sidebarForm.Left != form.Right)
            {
                Log(11, $"吸附检查：吸附后没有贴齐（窗口左边界 {sidebarForm.Left}，主窗口右边界 {form.Right}）");
                return false;
            }

            // ---- 3) 移动主窗口：悬浮窗要跟着走 ----
            var before = sidebarForm.Location;
            var mainBefore = form.Location;
            const int deltaX = -60;
            const int deltaY = 40;

            form.Location = new Point(mainBefore.X + deltaX, mainBefore.Y + deltaY);
            PumpMessages(300);

            if (sidebarForm.Left != form.Right)
            {
                Log(11, $"吸附检查：主窗口移动后没有保持贴齐（{sidebarForm.Left} vs {form.Right}）");
                return false;
            }

            if (sidebarForm.Top != before.Y + deltaY)
            {
                Log(11, $"吸附检查：主窗口移动后悬浮窗没有跟着走"
                        + $"（上边界 {sidebarForm.Top}，期望 {before.Y + deltaY}）");
                return false;
            }

            // ---- 4) 拖开：应当脱离 ----
            sidebarForm.Location = new Point(form.Left - sidebarForm.Width - 200, form.Top + 60);
            PumpMessages(250);

            if (Edge() != "None")
            {
                Log(11, $"吸附检查：拖开之后没有脱离（当前「{Edge()}」）");
                return false;
            }

            // ---- 5) 下边也要能贴（说明不是只支持左右） ----
            // 先把窗口改矮，否则贴到主窗口下边会被工作区下沿钳回来，测不出贴齐。
            sidebarForm.Size = new Size(300, 170);
            PumpMessages(200);

            sidebarForm.Location = new Point(form.Left + 80, form.Bottom + 6);
            PumpMessages(250);

            if (Edge() != "Bottom")
            {
                Log(11, $"吸附检查：拖到下方附近没有被吸附（当前「{Edge()}」）");
                return false;
            }

            if (sidebarForm.Top != form.Bottom)
            {
                Log(11, $"吸附检查：下方吸附没有贴齐（窗口上边界 {sidebarForm.Top}，主窗口下边界 {form.Bottom}）");
                return false;
            }

            // ---- 6) 最后留在"吸附在右侧"，用来验证这个状态能存进设置 ----
            sidebarForm.Location = new Point(form.Right + 4, form.Top + 120);
            PumpMessages(250);

            if (Edge() != "Right" || sidebarForm.Left != form.Right)
            {
                Log(11, $"吸附检查：收尾时没能回到右侧吸附（「{Edge()}」，左边界 {sidebarForm.Left}）");
                return false;
            }

            Log(11, "悬浮窗吸附正常：贴齐、跟着主窗口移动、拖开脱离，左右下三边都能贴");
            return true;
        }
    }
}
