using System;
using System.Drawing;
using System.Windows.Forms;
using 播放器.Ui;

namespace 播放器
{
    /// <summary>
    /// 文件页的"分离 / 归位"：与侧栏、播放列表共用 <c>MainForm.Floating.cs</c> 的窗口与吸附机制。
    /// <para>
    /// 与另外两个不同的只有一处：分离之后侧栏里要留一块「已分离」占位（控件只有一个，
    /// 不能同时出现在两个地方），所以这里多一步 <see cref="SidebarPanel.DetachFileBrowser"/>。
    /// </para>
    /// </summary>
    public partial class MainForm
    {
        private const int DefaultFileBrowserWindowWidth = 420;
        private const int DefaultFileBrowserWindowHeight = 560;
        private const int MinimumFileBrowserWindowWidth = 280;
        private const int MinimumFileBrowserWindowHeight = 240;

        /// <summary>分离 / 归位文件浏览器。</summary>
        /// <param name="revealInSidebar">
        /// 归位时要不要顺便把侧栏打开并切到文件页。用户点菜单 / 关窗口时为 true；
        /// 载入设置方案时为 false——不能因为归位就推翻方案里的"侧栏收起"。
        /// </param>
        private void SetFileBrowserFloating(bool floating, bool revealInSidebar = true)
        {
            if (_settings.FileBrowserFloating == floating &&
                (floating ? _fileBrowserWindow != null : _fileBrowserWindow == null))
            {
                UpdateFileBrowserMenuState();
                return;
            }

            _settings.FileBrowserFloating = floating;

            if (_menuViewFileBrowserFloating != null && _menuViewFileBrowserFloating.Checked != floating)
                _menuViewFileBrowserFloating.Checked = floating;

            if (floating) DetachFileBrowser();
            else DockFileBrowser(revealInSidebar);

            UpdateFileBrowserMenuState();

            SetStatus(floating ? "文件浏览器已分离为独立窗口" : "文件浏览器已归位");
        }

        private void DetachFileBrowser()
        {
            if (_fileBrowserWindow != null) return;

            var view = sidebarPanel.DetachFileBrowser();

            var minimum = new Size(
                (int)Math.Round(MinimumFileBrowserWindowWidth * UiScale),
                (int)Math.Round(MinimumFileBrowserWindowHeight * UiScale));

            _fileBrowserWindow = CreateFloatingWindow(
                "播放器 · 文件浏览器",
                _settings.FileBrowserWindowX,
                _settings.FileBrowserWindowY,
                _settings.FileBrowserWindowWidth,
                _settings.FileBrowserWindowHeight,
                new Size(DefaultFileBrowserWindowWidth, DefaultFileBrowserWindowHeight),
                minimum);

            _fileBrowserWindow.FormClosing += OnFileBrowserWindowClosing;
            _fileBrowserWindow.Controls.Add(view);

            view.Dock = DockStyle.Fill;
            view.Visible = true;

            _fileBrowserSnap = _settings.FileBrowserSnap;
            _fileBrowserSnapOffset = _settings.FileBrowserSnapOffset;
            AttachSnapHandlers(_fileBrowserWindow);

            _fileBrowserWindow.Show(this);

            ApplyFloatingWindowTheme(_fileBrowserWindow);
            view.ApplyNativeTheme(_palette.IsDark);

            // 位置要在显示之后才最终确定：上次是吸附状态的话，这里按主窗口当前的位置重新贴回去。
            LayoutAttachedWindows();
        }

        private void DockFileBrowser(bool revealInSidebar)
        {
            var window = _fileBrowserWindow;
            if (window == null) return;

            RememberFloatingBounds(
                window,
                (x, y, w, h) =>
                {
                    _settings.FileBrowserWindowX = x;
                    _settings.FileBrowserWindowY = y;
                    _settings.FileBrowserWindowWidth = w;
                    _settings.FileBrowserWindowHeight = h;
                },
                edge => _settings.FileBrowserSnap = edge,
                _fileBrowserSnap);

            ForgetSnap(FloatingPanel.FileBrowser);

            _fileBrowserWindow = null;
            window.FormClosing -= OnFileBrowserWindowClosing;
            DetachSnapHandlers(window);

            window.Controls.Remove(sidebarPanel.FileBrowser);
            window.Hide();
            window.Dispose();

            sidebarPanel.AttachFileBrowser(sidebarPanel.FileBrowser);

            if (revealInSidebar)
            {
                // 用户刚点的是"放回侧栏"，不能落在收起的侧栏里。
                _settings.ShowSidebar = true;
                ApplySidebarVisibility(true);
                sidebarPanel.ActiveTab = SidebarTab.Files;
            }

            UpdateFileBrowserMenuState();
        }

        private void OnFileBrowserWindowClosing(object? sender, FormClosingEventArgs e)
        {
            // 只有用户自己点 ✕ 才归位；主窗口关闭时（FormOwnerClosing）要放行。
            if (e.CloseReason != CloseReason.UserClosing) return;

            e.Cancel = true;
            SetFileBrowserFloating(false);
        }
    }
}
