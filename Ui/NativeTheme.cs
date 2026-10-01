using System;
using 播放器.Core;
using System.Runtime.InteropServices;

namespace 播放器.Ui
{
    /// <summary>
    /// 让 Windows 原生控件跟着主题走。
    /// <para>
    /// WinForms 里有两块东西是画不动的：<c>ListView</c> 的滚动条与表头、以及窗口标题栏。
    /// 它们由系统主题引擎绘制，只能通过 <c>uxtheme</c> / <c>dwmapi</c> 告诉系统切换成深色版本。
    /// 所有调用都做了容错：系统不支持时静默降级，不影响功能。
    /// </para>
    /// </summary>
    internal static class NativeTheme
    {
        private const int LvmGetHeader = 0x101F;

        // DWMWA_USE_IMMERSIVE_DARK_MODE：Windows 10 2004（build 19041）起为 20，更早的版本是 19。
        private const int DwmwaUseImmersiveDarkMode = 20;
        private const int DwmwaUseImmersiveDarkModeLegacy = 19;

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int SetWindowTheme(IntPtr hWnd, string? pszSubAppName, string? pszSubIdList);

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        /// <summary>把窗口标题栏切成深色 / 浅色。</summary>
        public static void ApplyTitleBar(IntPtr handle, bool dark)
        {
            if (handle == IntPtr.Zero) return;

            try
            {
                var value = dark ? 1 : 0;
                if (DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref value, sizeof(int)) != 0)
                    DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeLegacy, ref value, sizeof(int));
            }
            catch (Exception ex)
            {
// 旧系统不支持，忽略。
                AppLog.Swallowed("旧系统不支持，忽略。", ex);
            }
        }

        /// <summary>让资源管理器风格的原生控件（滚动条等）切换深色版本。</summary>
        public static void ApplyExplorer(IntPtr handle, bool dark)
        {
            if (handle == IntPtr.Zero) return;

            try
            {
                SetWindowTheme(handle, dark ? "DarkMode_Explorer" : "Explorer", null);
            }
            catch (Exception ex)
            {
// 忽略。
                AppLog.Swallowed("忽略。", ex);
            }
        }

        /// <summary>
        /// 处理列表视图：滚动条走 DarkMode_Explorer，表头需要单独拿到它的窗口句柄，
        /// 用 DarkMode_ItemsView 才会跟着变深。
        /// </summary>
        public static void ApplyListView(IntPtr listViewHandle, bool dark)
        {
            ApplyExplorer(listViewHandle, dark);

            if (listViewHandle == IntPtr.Zero) return;

            try
            {
                var header = SendMessage(listViewHandle, LvmGetHeader, IntPtr.Zero, IntPtr.Zero);
                if (header == IntPtr.Zero) return;

                SetWindowTheme(header, dark ? "DarkMode_ItemsView" : "ItemsView", null);
            }
            catch (Exception ex)
            {
// 忽略。
                AppLog.Swallowed("忽略。", ex);
            }
        }
    }
}
