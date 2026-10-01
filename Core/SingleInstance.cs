using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace 播放器.Core
{
    /// <summary>
    /// 单实例控制：用命名互斥体保证同一登录会话里只有一个 播放器 在运行。
    /// <para>
    /// 第二次启动的程序把自己的命令行文件参数转交给已在运行的实例，然后立刻退出；
    /// 已运行的实例收到参数后把自己提到前台，并播放 / 添加这些文件。
    /// 没有带文件参数也没关系——那就只把已有窗口提到前台。
    /// </para>
    /// <para>
    /// 进程间通信只用 Win32 消息：命名管道、内存映射文件、套接字这些都会被
    /// 受限沙箱挡掉，而 <c>WM_COPYDATA</c> 由 USER32 直接完成跨进程拷贝，
    /// 不依赖任何额外的内核对象。
    /// </para>
    /// </summary>
    public sealed class SingleInstance : IDisposable
    {
        /// <summary>
        /// 互斥体名字，前缀 <c>Local\</c> 表示"当前登录会话"。
        /// <para>
        /// 桌面程序该用 Local 而不是 Global：同一台机器上不同用户（或不同远程会话）
        /// 各开一个窗口是合理的；放进 <c>Global\</c> 反而会让第二个用户根本起不来。
        /// </para>
        /// </summary>
        private const string MutexName = @"Local\播放器.SingleInstance";

        /// <summary>
        /// 用来定位对方窗口的窗口属性名。
        /// <para>带 GUID 是为了不跟别的程序、别的版本撞名。</para>
        /// </summary>
        private const string InstanceKey = "播放器.SingleInstance.{4F1C2E90-6B3D-4A57-9E21-7C0D5B8A3E64}";

        /// <summary>
        /// 系统定义的 <c>WM_COPYDATA</c>。
        /// <para>
        /// ⚠ 必须用这个固定值，<b>不能</b>自己 <c>RegisterWindowMessage</c> 一个自定义消息来代替：
        /// 只有真正的 <c>WM_COPYDATA</c> 才会被系统做跨进程编组。
        /// 用自定义消息的话，接收方拿到的只是发送方地址空间里的裸指针，
        /// 一读就访问冲突（实测退出码 0xC0000005，排查了很久）。
        /// </para>
        /// </summary>
        private const int WmCopyData = 0x004A;

        /// <summary>COPYDATASTRUCT 的 <c>dwData</c> 魔数，'SCRP'。用来认出"这条消息是自己人发的"。</summary>
        private static readonly UIntPtr CopyDataMagic = new UIntPtr(0x53435250);

        /// <summary>负载长度上限（字节）。防止恶意或损坏的发送方让我们一次性分配巨量内存。</summary>
        private const int MaximumPayloadBytes = 1024 * 1024;

        /// <summary>找不到接收窗口时的重试间隔（毫秒）。</summary>
        private const int RetryIntervalMilliseconds = 100;

        private const uint SmtoAbortIfHung = 0x0002;

        /// <summary>用来探活的消息：不需要跨进程编组，最安全。</summary>
        private const int WmNull = 0x0000;

        private Mutex? _mutex;
        private IntPtr _windowHandle;
        private bool _windowPropertySet;
        private bool _disposed;

        private SingleInstance(Mutex mutex) => _mutex = mutex;

        /// <summary>
        /// 尝试成为唯一实例；已经有实例在运行时返回 <c>false</c>。
        /// </summary>
        public static bool TryAcquire(out SingleInstance? instance)
        {
            instance = null;

            try
            {
                // createdNew 是判断依据：名字已被占用说明别人正拿着互斥体。
                // 上一个实例崩溃时系统会回收它的句柄、销毁这个内核对象，
                // 所以那种情况下 createdNew 仍然是 true——崩溃不会把程序卡在启动阶段。
                var mutex = new Mutex(initiallyOwned: true, name: MutexName, createdNew: out var createdNew);

                if (!createdNew)
                {
                    // 没拿到所有权，这个句柄是多余的，立刻回收。
                    mutex.Dispose();
                    return false;
                }

                instance = new SingleInstance(mutex);
                return true;
            }
            catch (Exception)
            {
                // 名字非法、权限不足等等。宁可放行多开一个窗口，也不要让程序起不来。
                return false;
            }
        }

        /// <summary>把主窗口句柄登记进实例，收到消息时用它把窗口提到前台。</summary>
        public bool AttachWindow(IntPtr handle)
        {
            if (_disposed || handle == IntPtr.Zero) return false;
            if (_windowPropertySet && _windowHandle == handle) return true;

            // 换窗口时先摘掉旧标记，免得留下一个再也不响应的死句柄。
            DetachWindowProperty();

            try
            {
                // 在窗口上打一个私有属性当作"我就是接收者"的标记，发送方靠它认人。
                _windowHandle = handle;
                _windowPropertySet = SetPropW(handle, InstanceKey, new IntPtr(1));

                if (!_windowPropertySet) _windowHandle = IntPtr.Zero;

                return _windowPropertySet;
            }
            catch (Exception)
            {
                _windowHandle = IntPtr.Zero;
                _windowPropertySet = false;
                return false;
            }
        }

        /// <summary>
        /// 在窗体的 <c>WndProc</c> 里调用；返回 <c>true</c> 表示消息已处理完。
        /// <para>
        /// 必须在 <c>base.WndProc</c> <b>之前</b>调用：<c>lParam</c> 指向的是
        /// 发送方进程里的数据（由系统临时映射过来），只有在本函数返回前才有效。
        /// </para>
        /// </summary>
        public bool TryHandleMessage(ref Message message)
        {
            if (_disposed) return false;

            // 只认系统的 WM_COPYDATA：系统的跨进程编组只对这一个消息号生效。
            if (message.Msg != WmCopyData) return false;

            if (message.LParam == IntPtr.Zero) return false;

            IReadOnlyList<string> arguments;

            try
            {
                // lParam 指向发送方进程里的 COPYDATASTRUCT，由系统临时映射进本进程。
                // 这里刻意用 Marshal.Read* 逐个字段读，而不是 PtrToStructure<T>：
                // 后者在这个位置会直接触发访问冲突（实测 0xC0000005）。
                var dwData = unchecked((ulong)Marshal.ReadInt64(message.LParam, 0));
                var cbData = unchecked((uint)Marshal.ReadInt64(message.LParam, 8));
                var lpData = Marshal.ReadIntPtr(message.LParam, 16);


                // 魔数不符 = 不是本程序发的。WM_COPYDATA 谁都能往我们窗口里灌，
                // 认不出就当作没看见，也不回填 Result，让它走默认处理。
                if (dwData != CopyDataMagic.ToUInt64()) return false;

                arguments = ReadPayload(cbData, lpData);

                // 明确回填非 0，告诉发送方参数已经收到，它可以放心退出了。
                message.Result = new IntPtr(1);
            }
            catch (Exception)
            {
                // 对方给的数据坏了：当作没收到。绝不把异常漏回调用方的 WndProc，
                // 那会变成"未处理异常"对话框。
                return false;
            }


            try
            {
                ArgumentsReceived?.Invoke(this, arguments);
            }
            catch (Exception ex)
            {
// 订阅者自己的异常不该让这条消息的处理路径变成崩溃点。
                AppLog.Swallowed("订阅者自己的异常不该让这条消息的处理路径变成崩溃点。", ex);
            }

            return true;
        }

        /// <summary>另一个实例传来了命令行参数（也可能是空列表，表示只是又启动了一次）。</summary>
        /// <remarks>在 UI 线程上触发，订阅者可以直接操作控件。</remarks>
        public event EventHandler<IReadOnlyList<string>>? ArgumentsReceived;

        /// <summary>把参数发给已在运行的实例；没有实例或超时返回 <c>false</c>。</summary>
        public static bool NotifyExistingInstance(IReadOnlyList<string> arguments, int timeoutMilliseconds = 2000)
        {
            if (arguments == null) throw new ArgumentNullException(nameof(arguments));

            try
            {
                // 先序列化再找窗口，保证 AllocHGlobal 出来的内存在任何分支下都会被释放。
                var payload = MarshalPayload(arguments, out var length);
                return SendPayload(payload, length, timeoutMilliseconds);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static IReadOnlyList<string> ReadPayload(uint cbData, IntPtr lpData)
        {
            if (cbData == 0 || lpData == IntPtr.Zero) return Array.Empty<string>();
            if (cbData > MaximumPayloadBytes) return Array.Empty<string>();

            var bytes = new byte[cbData];
            Marshal.Copy(lpData, bytes, 0, bytes.Length);

            return SplitArguments(Encoding.UTF8.GetString(bytes));
        }

        /// <summary>
        /// 把参数压成 UTF-8 字节数组放到非托管堆上。
        /// <para>
        /// 分隔符用 <c>'\n'</c>：Windows 文件名里不可能出现这个字符，
        /// 所以它是天然的、不会切错的分隔符。
        /// </para>
        /// </summary>
        private static IntPtr MarshalPayload(IReadOnlyList<string> arguments, out int length)
        {
            var builder = new StringBuilder();

            foreach (var argument in arguments)
            {
                if (string.IsNullOrEmpty(argument)) continue;

                if (builder.Length > 0) builder.Append('\n');
                builder.Append(argument);
            }

            var bytes = Encoding.UTF8.GetBytes(builder.ToString());
            length = bytes.Length;

            // 分配在非托管堆：地址不会被 GC 搬动，正好可以交给 WM_COPYDATA。
            // 空负载也要给一个有效地址，AllocHGlobal(0) 的行为在各版本上并不一致。
            var buffer = Marshal.AllocHGlobal(Math.Max(1, bytes.Length));

            if (bytes.Length > 0) Marshal.Copy(bytes, 0, buffer, bytes.Length);

            return buffer;
        }

        /// <summary>
        /// 填写 <c>COPYDATASTRUCT</c> 并用 <c>SendMessageTimeoutW</c> 发出；
        /// 调用方负责释放 <paramref name="payload"/>。
        /// </summary>
        private static bool SendPayload(IntPtr payload, int length, int timeoutMilliseconds)
        {
            var timeout = timeoutMilliseconds <= 0 ? 2000 : timeoutMilliseconds;
            var startedAt = Environment.TickCount64;


            var header = Marshal.AllocHGlobal(Marshal.SizeOf<COPYDATASTRUCT>());

            try
            {
                Marshal.StructureToPtr(
                    new COPYDATASTRUCT
                    {
                        dwData = CopyDataMagic,
                        cbData = new UIntPtr((uint)length),
                        lpData = payload
                    },
                    header,
                    fDeleteOld: false);


                while (true)
                {
                    var windows = FindReceiverWindows();

                    foreach (var window in windows)
                    {
                        // ⚠ 这里的两次调用缺一不可，原因很反直觉：
                        // SendMessageTimeout 不会为 WM_COPYDATA 做跨进程编组——接收方拿到的
                        // 会是发送方地址空间里的裸指针，一读就访问冲突（实测退出码 0xC0000005）。
                        // 所以真正的数据只能用 SendMessage 发；而 SendMessage 是同步的，
                        // 对方卡死就会把我们一直挂住。折中办法是先用不需要编组的 WM_NULL
                        // 探一次活，确认对方还在响应，再发数据。
                        var alive = SendMessageTimeoutW(
                            window, WmNull, IntPtr.Zero, IntPtr.Zero, SmtoAbortIfHung, (uint)timeout, out _);

                        if (alive == IntPtr.Zero) continue;

                        SendMessageW(window, WmCopyData, IntPtr.Zero, header);
                        return true;
                    }

                    // 找不到窗口，很可能是第一个实例"已经拿到互斥体、但窗口句柄还没建好"
                    // 的那几十毫秒。直接放弃会让参数永久丢失，所以短睡后重试。
                    if (Environment.TickCount64 - startedAt >= timeout) return false;

                    Thread.Sleep(RetryIntervalMilliseconds);
                }
            }
            finally
            {
                // 成功、失败、抛异常，两块非托管内存都要归还。
                Marshal.FreeHGlobal(header);
                Marshal.FreeHGlobal(payload);
            }
        }

        /// <summary>
        /// 枚举所有顶层窗口，挑出带了我们私有属性的那个。
        /// <para>
        /// 刻意不用 <c>FindWindow</c>：标题栏会随当前曲目变化（"播放器 - 某首歌"），
        /// 拿标题当查找键，一换歌就失效。窗口属性挂在 HWND 上，与标题无关，
        /// 而且只有本程序会写，语义上就是"这是单实例接收窗口"。
        /// </para>
        /// </summary>
        private static List<IntPtr> FindReceiverWindows()
        {
            var collector = new WindowCollector();
            var handle = GCHandle.Alloc(collector);

            try
            {
                // 枚举期间回调委托必须活着，被 GC 回收会让系统跳到野地址。
                // collector 已经 pin 住，委托实例是它的字段，所以一并保住了。
                EnumWindows(collector.Callback, IntPtr.Zero);
            }
            catch (Exception ex)
            {
// 枚举失败按"没找到"处理，交给上层重试。
                AppLog.Swallowed("枚举失败按'没找到'处理，交给上层重试。", ex);
            }
            finally
            {
                handle.Free();
            }

            return collector.Found;
        }

        /// <summary>把 <c>'\n'</c> 分隔的负载还原成参数列表。</summary>
        private static IReadOnlyList<string> SplitArguments(string? payload)
        {
            if (string.IsNullOrEmpty(payload)) return Array.Empty<string>();

            var parts = payload.Split('\n');
            var result = new List<string>(parts.Length);

            foreach (var raw in parts)
            {
                // 顺手去掉可能存在的 '\r'，否则路径末尾会多一个字符导致找不到文件。
                var line = raw.TrimEnd('\r');
                if (line.Length == 0) continue;

                result.Add(line);
            }

            return result;
        }

        private void DetachWindowProperty()
        {
            if (!_windowPropertySet) return;

            try
            {
                RemovePropW(_windowHandle, InstanceKey);
            }
            catch (Exception ex)
            {
// 窗口可能已经销毁，失败无所谓。
                AppLog.Swallowed("窗口可能已经销毁，失败无所谓。", ex);
            }

            _windowPropertySet = false;
            _windowHandle = IntPtr.Zero;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // 先摘窗口属性再放互斥体：反过来的话，另一进程可能在
            // "互斥体已空闲、属性还在"的间隙里抢到互斥体，把消息发给正在关闭的我们。
            DetachWindowProperty();

            var mutex = _mutex;
            _mutex = null;

            if (mutex == null) return;

            try
            {
                mutex.ReleaseMutex();
            }
            catch (Exception ex)
            {
// 没持有（或已被遗弃）时 ReleaseMutex 会抛，忽略。
                AppLog.Swallowed("没持有（或已被遗弃）时 ReleaseMutex 会抛，忽略。", ex);
            }

            try
            {
                mutex.Dispose();
            }
            catch (Exception ex)
            {
// 忽略。
                AppLog.Swallowed("忽略。", ex);
            }
        }

        /// <summary><c>EnumWindows</c> 的回调载体：把命中的 HWND 收进 <see cref="Found"/>。</summary>
        private sealed class WindowCollector
        {
            private readonly EnumWindowsProc _callback;

            public WindowCollector() => _callback = OnWindow;

            public List<IntPtr> Found { get; } = new List<IntPtr>();

            public EnumWindowsProc Callback => _callback;

            private bool OnWindow(IntPtr handle, IntPtr lParam)
            {
                try
                {
                    if (GetPropW(handle, InstanceKey) != IntPtr.Zero) Found.Add(handle);
                }
                catch (Exception ex)
                {
// 单个窗口查属性失败不能中断整轮枚举。
                    AppLog.Swallowed("单个窗口查属性失败不能中断整轮枚举。", ex);
                }

                return true;
            }
        }

        // -----------------------------------------------------------------
        // Win32 互操作。函数名保留 W 后缀，和真实导出名一致，便于对照 MSDN。
        // -----------------------------------------------------------------

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private delegate bool EnumWindowsProc(IntPtr handle, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct COPYDATASTRUCT
        {
            /// <summary>自定义标识，这里放魔数。</summary>
            public UIntPtr dwData;

            /// <summary>负载字节数。</summary>
            public UIntPtr cbData;

            /// <summary>指向负载的指针（位于发送方进程的地址空间）。</summary>
            public IntPtr lpData;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetPropW(IntPtr hWnd, string lpString, IntPtr hData);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetPropW(IntPtr hWnd, string lpString);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr RemovePropW(IntPtr hWnd, string lpString);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SendMessageW(
            IntPtr hWnd,
            int msg,
            IntPtr wParam,
            IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SendMessageTimeoutW(
            IntPtr hWnd,
            int msg,
            IntPtr wParam,
            IntPtr lParam,
            uint fuFlags,
            uint uTimeout,
            out UIntPtr lpdwResult);
    }
}
