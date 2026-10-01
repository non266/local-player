// 检查点 12：单实例（第二个进程要把已有窗口叫到前面）。

namespace SmokeTest
{
    internal static partial class Program
    {
        // -----------------------------------------------------------------
        // 12) 单实例
        // -----------------------------------------------------------------

        private static bool TestSingleInstance()
        {
            if (!SingleInstance.TryAcquire(out var first) || first == null)
            {
                // 使用者的播放器正开着（互斥体在它手里）。这时绝不能去发消息，
                // 否则会往他正在用的窗口里灌测试路径。跳过即可，不算失败。
                Log(12, "检测到已有实例在运行，跳过单实例检查");
                return true;
            }

            using (first)
            {
                // 同一进程里再抢一次必须失败
                if (SingleInstance.TryAcquire(out var second))
                {
                    second?.Dispose();
                    Log(12, "第二次 TryAcquire 竟然也成功了");
                    return false;
                }

                using var host = new SingleInstanceHostForm { ShowInTaskbar = false, Text = "冒烟测试接收窗口" };
                _ = host.Handle;
                host.Instance = first;

                if (!first.AttachWindow(host.Handle))
                {
                    Log(12, "AttachWindow 失败");
                    return false;
                }

                IReadOnlyList<string> received = null;
                first.ArgumentsReceived += (s, args) => received = args;

                var sent = new[] { @"C:\测试 目录\歌 曲.mp3", @"D:\子目录\b.flac" };

                if (!SingleInstance.NotifyExistingInstance(sent, 2000))
                {
                    Log(12, "NotifyExistingInstance 没能投递到接收窗口");
                    return false;
                }

                if (received == null || received.Count != 2 ||
                    received[0] != sent[0] || received[1] != sent[1])
                {
                    Log(12, "收到的参数与发出的不一致：" + string.Join(" | ", received ?? new List<string>()));
                    return false;
                }

                // 空参数 = 用户又双击了一次程序，也必须能把窗口唤起来
                received = null;

                if (!SingleInstance.NotifyExistingInstance(Array.Empty<string>(), 2000))
                {
                    Log(12, "空参数没能投递");
                    return false;
                }

                if (received == null || received.Count != 0)
                {
                    Log(12, "空参数没有被当成一次「唤醒」");
                    return false;
                }

                Log(12, "单实例转发正常：互斥体互斥、中文带空格路径与空唤醒均可送达");
            }

            // 释放之后应该能重新抢到
            if (!SingleInstance.TryAcquire(out var again) || again == null)
            {
                Log(12, "释放互斥体之后无法重新获取");
                return false;
            }

            again.Dispose();
            return true;
        }

        /// <summary>
        /// 只负责"把收到的消息转给 SingleInstance"的窗口，用来在测试里当接收端。
        /// 主窗体的接线方式与它完全一致。
        /// </summary>
        private sealed class SingleInstanceHostForm : Form
        {
            public SingleInstance Instance { get; set; }

            protected override void WndProc(ref Message m)
            {
                if (Instance != null && Instance.TryHandleMessage(ref m)) return;

                base.WndProc(ref m);
            }
        }
    }
}
