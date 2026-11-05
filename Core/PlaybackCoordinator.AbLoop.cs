using System;

namespace 播放器.Core
{
    /// <summary>
    /// 播放业务里的 A-B 循环：两个点、"越过 B 就回 A"、换歌清掉、以及给菜单 / 状态栏看的读数。
    /// <para>
    /// 判定与拒绝理由本来就在 <see cref="AbLoop"/>（纯函数），这里只存"这一首的 A/B 是什么"
    /// 并负责在计时器每一拍决定要不要发 seek。搬出来之前这些字段散在 <c>MainForm.Playback.cs</c>。
    /// </para>
    /// </summary>
    public sealed partial class PlaybackCoordinator
    {
        /// <summary>A 点；<c>null</c> = 没设。与 <see cref="_abLoopEnd"/> 都有值才算生效。</summary>
        private TimeSpan? _abLoopStart;

        private TimeSpan? _abLoopEnd;

        /// <summary>
        /// 这两个点是<b>在哪个文件上</b>设的。
        /// <para>换到别的文件才清：同一首重新起播（音量均衡要重载媒体）不算换歌，
        /// 一个开关把用户刚设的循环点清掉会很难解释。</para>
        /// </summary>
        private string? _abLoopPath;

        /// <summary>
        /// 已经朝 A 点发过 seek、但引擎还没真的回来。
        /// <para>libvlc 的定位是异步的：不记这一笔的话，在引擎时间跌回 B 之前每一拍都会再发一次
        /// seek，画面会一顿一顿地跳。</para>
        /// </summary>
        private bool _abLoopWrapPending;

        /// <summary>A 点（毫秒）；没设为 <c>null</c>。</summary>
        public long? AbLoopStartMilliseconds =>
            _abLoopStart.HasValue ? (long)_abLoopStart.Value.TotalMilliseconds : null;

        /// <summary>B 点（毫秒）；没设为 <c>null</c>。</summary>
        public long? AbLoopEndMilliseconds =>
            _abLoopEnd.HasValue ? (long)_abLoopEnd.Value.TotalMilliseconds : null;

        /// <summary>两个点都设了才算生效。</summary>
        public bool AbLoopActive => _abLoopStart.HasValue && _abLoopEnd.HasValue;

        public bool HasAbLoopStart => _abLoopStart.HasValue;

        public bool HasAbLoopEnd => _abLoopEnd.HasValue;

        /// <summary>
        /// 「把 A 点设在当前时间」：能设返回 <c>null</c>，不能设返回一句原因（由界面显示）。
        /// <para>A 不早于 B 时<b>如实拒绝</b>并说明原因——不偷偷挪成"B 点前面一点"。</para>
        /// </summary>
        public string? SetAbLoopStart(long valueMilliseconds)
        {
            var reject = AbLoop.RejectStart(valueMilliseconds, AbLoopEndMilliseconds);
            if (reject != null) return reject;

            _abLoopStart = TimeSpan.FromMilliseconds(valueMilliseconds);
            _abLoopPath = _engine.CurrentPath;
            _abLoopWrapPending = false;

            return null;
        }

        /// <summary>「把 B 点设在当前时间」：同样如实拒绝 B 不晚于 A 的情况。</summary>
        public string? SetAbLoopEnd(long valueMilliseconds)
        {
            var reject = AbLoop.RejectEnd(valueMilliseconds, AbLoopStartMilliseconds);
            if (reject != null) return reject;

            _abLoopEnd = TimeSpan.FromMilliseconds(valueMilliseconds);
            _abLoopPath = _engine.CurrentPath;

            return null;
        }

        /// <summary>用户清掉两个点（菜单「清除 A-B」）。</summary>
        public void ClearAbLoop() => ClearAbLoopCore();

        /// <summary>
        /// 界面计时器每一拍问一句：越过 B 点了吗。越了就跳回 A 点。
        /// <returns>该显示成什么时间（刚跳回 A 点时就是 A 点）。</returns>
        /// </summary>
        public long ApplyAbLoop(long timeMilliseconds)
        {
            var start = AbLoopStartMilliseconds;
            var end = AbLoopEndMilliseconds;

            if (!AbLoop.ShouldWrap(timeMilliseconds, start, end))
            {
                _abLoopWrapPending = false;
                return timeMilliseconds;
            }

            // 已经发过 seek、引擎还没回来：这期间不再重复发。
            if (_abLoopWrapPending) return timeMilliseconds;

            _abLoopWrapPending = true;
            _engine.SeekTo(start!.Value);

            return start.Value;
        }

        /// <summary>换到别的文件就把循环点清掉；<paramref name="path"/> 是即将播放的文件。</summary>
        public void ClearAbLoopForOtherFile(string? path)
        {
            if (!_abLoopStart.HasValue && !_abLoopEnd.HasValue) return;

            if (_abLoopPath != null && string.Equals(_abLoopPath, path, StringComparison.OrdinalIgnoreCase))
                return;

            ClearAbLoopCore();
        }

        /// <summary>把两个点丢掉（用户清除 / 换歌 / 停止都走这里）。</summary>
        private void ClearAbLoopCore()
        {
            _abLoopStart = null;
            _abLoopEnd = null;
            _abLoopPath = null;
            _abLoopWrapPending = false;
        }
    }
}
