using System;
using System.Collections.Generic;
using System.Windows.Forms;
using 播放器.Core;
using 播放器.Ui;

namespace 播放器
{
    /// <summary>
    /// 主窗口的主题部分：把一套 <see cref="ThemePalette"/> 应用到所有控件上。
    /// <para>
    /// 这里刻意用"显式逐控件赋值"而不是递归遍历控件树：控件数量不多，
    /// 每个控件的角色一目了然，改起来也不会误伤。
    /// </para>
    /// </summary>
    public partial class MainForm
    {
        private ThemePalette _palette = Themes.Light;
        private MediaIconSet? _icons;

        // 换主题时旧图标要延迟释放：ToolStrip 可能还在用它们重绘。
        private readonly List<MediaIconSet> _retiredIcons = new List<MediaIconSet>();
        private bool _themeReady;

        /// <summary>应用主题：配色 + 自绘渲染器 + 图标 + 原生深色处理。</summary>
        private void ApplyTheme(ThemeId id)
        {
            _settings.Theme = id;
            _palette = Themes.Resolve(id);

            ApplyPalette(_palette);
            ApplyToolStripRenderer(_palette);
            ApplyIcons(_palette);
            ApplyNativeTheme(_palette);
            UpdateThemeMenuChecks(id);
            UpdateTaskbarIcons();

            // 列表里"正在播放/无法播放"的文字颜色也来自调色板，需要重画一遍。
            RefreshPlaylistView();
        }

        private void ApplyPalette(ThemePalette p)
        {
            BackColor = p.WindowBack;

            splitMain.BackColor = p.SplitterBack;
            splitMain.Panel1.BackColor = p.VideoBack;
            splitMain.Panel2.BackColor = p.PanelBack;

            // 视频区内部的分隔条（左视频、右侧栏）与承载播放列表的面板
            splitVideo.BackColor = p.SplitterBack;
            splitVideo.Panel1.BackColor = p.VideoBack;
            splitVideo.Panel2.BackColor = p.PanelBack;
            playlistHost.BackColor = p.PanelBack;

            sidebarPanel.ApplyPalette(p);
            // 分离出去的悬浮窗也要跟着换配色
            ApplyThemeToFloatingWindows();
            // 桌面歌词的"跟随主题"配色取自强调色，同样要更新
            _desktopLyrics?.SetPalette(p);
            // 视频区
            videoView.BackColor = p.VideoBack;
            lblPlaceholder.BackColor = p.VideoBack;
            lblPlaceholder.ForeColor = p.PlaceholderFore;

            // 播放列表
            lblPlaylistHeader.BackColor = p.PanelBack;
            lblPlaylistHeader.ForeColor = p.MenuFore;
            pnlPlaylistSearch.BackColor = p.PanelBack;
            txtPlaylistSearch.BackColor = p.ListBack;
            txtPlaylistSearch.ForeColor = p.ListFore;
            listViewPlaylist.BackColor = p.ListBack;
            listViewPlaylist.ForeColor = p.ListFore;

            // 传送栏
            pnlTransport.BackColor = p.TransportBack;
            foreach (var label in new[] { lblCurrentTime, lblTotalTime, lblVolumeCaption, lblVolumeValue })
                label.ForeColor = p.TransportFore;

            foreach (var slider in new[] { trackSeek, trackVolume })
            {
                slider.BackColor = p.TransportBack;
                slider.TrackColor = p.TrackBack;
                slider.FillColor = p.Accent;
                slider.ThumbColor = p.Accent;
                slider.Invalidate();
            }

            // 菜单 / 工具栏 / 状态栏 / 播放列表工具条
            foreach (var strip in new ToolStrip[] { menuStripMain, toolStripMain, statusStripMain, toolStripPlaylist })
            {
                strip.BackColor = p.MenuBack;
                strip.ForeColor = p.MenuFore;
            }

            foreach (ToolStripItem item in statusStripMain.Items)
                item.ForeColor = p.MenuFore;
        }

        private void ApplyToolStripRenderer(ThemePalette p)
        {
            var renderer = new ThemedToolStripRenderer(p);

            menuStripMain.Renderer = renderer;
            toolStripMain.Renderer = renderer;
            statusStripMain.Renderer = renderer;
            toolStripPlaylist.Renderer = renderer;
            contextMenuPlaylist.Renderer = renderer;

            if (_lyricsContextMenu != null) _lyricsContextMenu.Renderer = renderer;
            if (_desktopLyricsMenu != null) _desktopLyricsMenu.Renderer = renderer;
            if (_trayMenu != null) _trayMenu.Renderer = renderer;
        }

        private void ApplyIcons(ThemePalette p)
        {
            var icons = new MediaIconSet(p.IconInk, p.Accent);

            tsbOpen.Image = icons.OpenFile;
            tsbOpenFolder.Image = icons.OpenFolder;
            tsbPlay.Image = icons.Play;
            tsbPause.Image = icons.Pause;
            tsbStop.Image = icons.Stop;
            tsbPrev.Image = icons.Previous;
            tsbNext.Image = icons.Next;
            tsbRewind.Image = icons.Rewind;
            tsbForward.Image = icons.Forward;
            tsbSnapshot.Image = icons.Snapshot;
            tsbTogglePlaylist.Image = icons.Playlist;

            if (_icons != null)
                _retiredIcons.Add(_icons);

            _icons = icons;

            try
            {
                Icon = AppIcons.For(p.Accent);
            }
            catch (Exception ex)
            {
// 生成图标失败不影响使用。
                AppLog.Swallowed("生成图标失败不影响使用。", ex);
            }

            // 托盘图标跟着主题换色
            UpdateTrayIcon();
        }

        /// <summary>标题栏和列表滚动条由系统绘制，只能通过原生 API 切深色。</summary>
        private void ApplyNativeTheme(ThemePalette p)
        {
            if (!IsHandleCreated) return;

            NativeTheme.ApplyTitleBar(Handle, p.IsDark);
            NativeTheme.ApplyListView(listViewPlaylist.Handle, p.IsDark);
            listViewPlaylist.Invalidate();

            // 侧栏里的媒体信息也是个 ListView，滚动条同样要切深色
            sidebarPanel.ApplyNativeTheme(p.IsDark);

            // 分离出去的两个窗口也各自有标题栏
            if (_sidebarWindow is { IsHandleCreated: true })
                NativeTheme.ApplyTitleBar(_sidebarWindow.Handle, p.IsDark);

            if (_playlistWindow is { IsHandleCreated: true })
                NativeTheme.ApplyTitleBar(_playlistWindow.Handle, p.IsDark);
        }

        private void UpdateThemeMenuChecks(ThemeId id)
        {
            menuThemeAuto.Checked = id == ThemeId.Auto;
            menuThemeLight.Checked = id == ThemeId.Light;
            menuThemeDark.Checked = id == ThemeId.Dark;
            menuThemeOled.Checked = id == ThemeId.Oled;
            menuThemeVlc.Checked = id == ThemeId.Vlc;
        }

        // 注意：OnHandleCreated 与 WndProc 统一放在 MainForm.cs，
        // 因为它们还要处理全局媒体键和任务栏按钮，不只是主题。

        private void DisposeThemeResources()
        {
            foreach (var icons in _retiredIcons)
                icons.Dispose();

            _retiredIcons.Clear();

            _icons?.Dispose();
            _icons = null;

            // 程序图标是按主题色缓存的，底层 HICON 不受 GC 管理，由这里统一销毁。
            AppIcons.ReleaseHandles();
        }
    }
}
