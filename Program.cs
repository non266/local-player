using System;
using System.Windows.Forms;
using LibVLCSharp.Shared;
using 播放器.Core;

namespace 播放器
{
    internal static class Program
    {
        /// <summary>应用程序的主入口点。</summary>
        [STAThread]
        private static void Main(string[] args)
        {
            // 日志要在最前面开起来：从"单实例判断"开始，每一步失败用户都可能来问
            // "为什么双击没反应"。落盘到数据目录，到 1 MB 轮转一次（见 AppLog）。
            AppLog.Begin();

            // 单实例判断放在最前面：第二个实例没必要再加载一遍整个播放内核，
            // 把命令行文件交给已经在跑的窗口后立刻退出。
            if (!SingleInstance.TryAcquire(out var singleInstance))
            {
                AppLog.Info("已有实例在运行，把命令行交给它：" + string.Join(" ", args));

                SingleInstance.NotifyExistingInstance(args);
                AppLog.Stop();
                return;
            }

            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            // 未处理异常统一走 AppLog：先写日志（带上下文），再弹一句"详情在哪个文件里"。
            // 以前只弹一个对话框，用户关掉之后什么线索都不剩。
            AppLog.InstallCrashHandlers();

            // 必须在创建 VideoView 之前初始化：这一步负责定位并加载 libvlc 原生库。
            // 注意这里用的是 LibVLCSharp 的 Core，与项目自身的 播放器.Core 命名空间同名。
            try
            {
                LibVLCSharp.Shared.Core.Initialize();
                AppLog.Info("libvlc 初始化成功");
            }
            catch (Exception ex)
            {
                AppLog.Error("无法加载 VLC 播放内核（libvlc）", ex);

                MessageBox.Show(
                    "无法加载 VLC 播放内核（libvlc）。\n\n" +
                    "请确认程序目录下存在 libvlc\\win-x64 文件夹，" +
                    "或者重新执行一次 dotnet restore / 重新生成项目。\n\n" +
                    "详细信息：" + ex.Message + "\n\n" +
                    AppLog.BuildCrashText("日志：", null),
                    "启动失败",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }

            try
            {
                using (singleInstance)
                using (var form = new MainForm(args, singleInstance))
                {
                    Application.Run(form);
                }
            }
            finally
            {
                AppLog.Info("程序退出");
                AppLog.Stop();
            }
        }
    }
}
