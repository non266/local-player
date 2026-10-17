using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using LibVLCSharp.Shared;
using LibVLCSharp.Shared.Structures;

// 事件收尾：播放器回调、状态广播、UI 线程派发与 Dispose。

namespace 播放器.Core
{
    public sealed partial class PlayerEngine
    {
        private void OnStopped()
        {
            // 自然播放结束会同时触发 EndReached 与 Stopped，此处避免把状态改回 Stopped。
            if (_endReached) return;
            SetState(PlayerState.Stopped);
        }

        private void OnEndReached()
        {
            _endReached = true;
            SetState(PlayerState.Ended);
            Post(() => PlaybackEnded?.Invoke(this, EventArgs.Empty));
        }

        private void OnEncounteredError()
        {
            var detail = _libVlc.LastLibVLCError;
            var message = string.IsNullOrEmpty(detail) ? "无法播放该媒体，可能是格式不受支持或文件已损坏。" : detail;

            SetState(PlayerState.Error);
            Post(() => ErrorOccurred?.Invoke(this, message));
        }

        private void RaiseTracksChanged() => Post(() => TracksChanged?.Invoke(this, EventArgs.Empty));

        internal void SetState(PlayerState state)
        {
            if (_state == state) return;
            _state = state;
            Post(() => StateChanged?.Invoke(this, state));
        }

        /// <summary>
        /// 把回调切换到 UI 线程执行。
        /// <para>
        /// ⚠ 判断"是否已经在 UI 线程上"用的是构造时记下的线程 id，<b>刻意不用
        /// <see cref="ISynchronizeInvoke.InvokeRequired"/></b>：控件在<b>还没创建句柄</b>时
        /// <c>InvokeRequired</c> 会返回 <c>false</c>，于是 libvlc 回调线程上的事件会被
        /// <b>就地执行</b>——那就是在跨线程操作 ToolStrip / ListView 等控件。
        /// </para>
        /// <para>
        /// 这不是理论问题：主窗口的构造函数里就会打开命令行传入的媒体，
        /// 而那一刻句柄可能还没建好，此时 libvlc 的 <c>Opening</c> / <c>Playing</c> 回调
        /// 会直接在原生线程上跑进 <c>UpdateTransportState</c> 之类的地方，
        /// 表现为"偶发"的跨线程异常（冒烟测试实测 30 次里撞到 1 次，
        /// 栈顶是 <c>ToolStripStatusLabel.OnTextChanged</c>）。
        /// </para>
        /// <para>UI 线程还没准备好时先排队，主窗口建好句柄后会调
        /// <see cref="FlushPendingUiActions"/> 补发，事件不会丢。</para>
        /// </summary>
        private void Post(Action action)
        {
            if (_disposed) return;

            if (Environment.CurrentManagedThreadId == _uiThreadId)
            {
                action();
                return;
            }

            try
            {
                _ui.BeginInvoke(action, null);
            }
            catch (ObjectDisposedException)
            {
                // 窗体已经销毁，丢掉即可。
            }
            catch (InvalidOperationException)
            {
                // 句柄还没建好（或正在销毁）：先攒起来，等 UI 线程来取。
                lock (_pendingGate)
                {
                    if (_disposed) return;
                    _pendingUiActions.Enqueue(action);
                }
            }
        }

        /// <summary>
        /// 补发构造期间攒下的事件（窗体建好句柄之后调用，必须在 UI 线程上）。
        /// </summary>
        public void FlushPendingUiActions()
        {
            Action[] pending;

            lock (_pendingGate)
            {
                if (_pendingUiActions.Count == 0) return;

                pending = _pendingUiActions.ToArray();
                _pendingUiActions.Clear();
            }

            // 用普通 for 而不是 foreach：这些动作有可能再次触发 Post，
            // 虽然不会回到这个数组，但显式索引更好读。
            for (var i = 0; i < pending.Length; i++)
            {
                if (_disposed) return;
                pending[i]();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                _mediaPlayer.Stop();
            }
            catch (Exception) { }

            DisposeCurrentMedia();

            // 只释放**当前**这个播放器（和 1.5.0 之前一样），然后释放所有内核。
            // <para>
            // ⚠ 为什么不逐个释放那几个"别的旋转值"的播放器：它们停在"暂停着、画面窗口已经摘下来"
            // 的状态，实测**释放这种播放器偶尔会在 libvlc 里原生崩**（`0xC0000005`，约 20~40 次里 1 次；
            // 先 Stop 再 Dispose 只是把概率压低，没根治）。进程马上就要退出了，显式释放它们没有收益，
            // 所以留给进程退出时由系统回收（最多 5 个，只影响退出的那一瞬）。
            // 内核照旧全部释放：libvlc 的实例是引用计数的，玩家没放不会让实例悬空。
            // </para>
            try
            {
                _mediaPlayer.Dispose();
            }
            catch (Exception) { }

            foreach (var slot in _cores.Values)
            {
                try
                {
                    slot.Core.Dispose();
                }
                catch (Exception) { }
            }

            _cores.Clear();
        }
    }
}
