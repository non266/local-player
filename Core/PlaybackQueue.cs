using System;
using System.Collections.Generic;

namespace 播放器.Core
{
    /// <summary>入队的结果（界面按它给一句人话）。</summary>
    public enum QueueAddResult
    {
        /// <summary>进队了。</summary>
        Added,

        /// <summary>已经在队里（不重复排）。</summary>
        AlreadyQueued,

        /// <summary>队列满了。</summary>
        Full,

        /// <summary>路径不成立（空串之类），没收。</summary>
        Rejected
    }

    /// <summary>
    /// 「下一首播放」队列：临时插播用的队伍。
    /// <para>
    /// <b>纯逻辑，只认路径</b>：它不碰播放列表、也不落盘——队列是"这一次听的时候想插播什么"，
    /// 关掉程序就没了才对（下次打开时那几首多半已经不想插播了）。
    /// 入队时去重、有条数上限（再多就不是"插播"而是"重排整个列表"了）。
    /// </para>
    /// <para>
    /// 顺序就是<b>入队顺序</b>：后来的排在队尾，播完一首再从队首取下一条。
    /// </para>
    /// </summary>
    public sealed class PlaybackQueue
    {
        /// <summary>队里最多几条。</summary>
        public const int Capacity = 20;

        private readonly List<string> _paths = new List<string>();

        /// <summary>队里现在有几条。</summary>
        public int Count => _paths.Count;

        /// <summary>队里的路径，从队首到队尾（只读，给界面标记用）。</summary>
        public IReadOnlyList<string> Paths => _paths;

        /// <summary>入队一条。</summary>
        public QueueAddResult Enqueue(string path)
        {
            if (string.IsNullOrEmpty(path)) return QueueAddResult.Rejected;
            if (Contains(path)) return QueueAddResult.AlreadyQueued;
            if (_paths.Count >= Capacity) return QueueAddResult.Full;

            _paths.Add(path);
            return QueueAddResult.Added;
        }

        /// <summary>这条在不在队里。</summary>
        public bool Contains(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;

            foreach (var queued in _paths)
            {
                if (string.Equals(queued, path, StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        /// <summary>取队首并把它移出队列；队空返回 <c>null</c>。</summary>
        public string? Dequeue()
        {
            if (_paths.Count == 0) return null;

            var path = _paths[0];
            _paths.RemoveAt(0);
            return path;
        }

        /// <summary>把某一条移出队列（例如它已经被手动播到了）。返回是否真的移掉了一条。</summary>
        public bool Remove(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;

            for (var i = 0; i < _paths.Count; i++)
            {
                if (!string.Equals(_paths[i], path, StringComparison.OrdinalIgnoreCase)) continue;

                _paths.RemoveAt(i);
                return true;
            }

            return false;
        }

        /// <summary>清空。</summary>
        public void Clear() => _paths.Clear();
    }
}
