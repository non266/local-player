using System;
using System.Collections.Generic;

namespace 播放器.Core
{
    /// <summary>
    /// 随机播放的"洗牌袋"：**一轮之内每首恰好播一次**，一轮完了重新洗。
    /// <para>
    /// 为什么要换掉"纯随机"：以前是"在全部下标里均匀取一个、只要不等于当前这个"，
    /// 于是同一首可能在别的歌还没轮到之前先播第二遍（列表越长越明显）——
    /// 这就是用户说的"随机播放老是重复"。洗牌袋是绝大多数播放器说的"随机播放"。
    /// </para>
    /// <para>
    /// 一轮的定义：**从"正在播的这一首"之后开始算**——它在这一轮里已经播过了，所以新的一轮
    /// 把它排除在外（不然一轮里会出现两次同一首，或者漏掉一首）。这也是"换一轮不会立刻
    /// 重复上一首"的实现方式：上一首就是当前这首，而它被排除了。
    /// </para>
    /// <para>
    /// 抽成纯逻辑（随机源从外面传进来）是为了能断言：给定种子，结果完全确定，
    /// 于是"一轮是排列""一轮不含当前这首""单曲不卡死"都能直接验。
    /// </para>
    /// </summary>
    public sealed class ShuffleBag
    {
        private readonly Random _random;

        /// <summary>这一轮还没播过的下标（洗好的顺序）。</summary>
        private int[] _remaining = Array.Empty<int>();

        private int _next;

        public ShuffleBag(Random? random = null) => _random = random ?? new Random();

        /// <summary>列表里一共有几首（上一次 <see cref="Reset"/> 传进来的数）。</summary>
        public int Count { get; private set; }

        /// <summary>
        /// 按新的条目数重洗一遍（列表载入 / 增删条目之后调用）。
        /// <para>只改"洗牌的池子"，不影响正在播的那一首。</para>
        /// </summary>
        public void Reset(int count)
        {
            Count = Math.Max(0, count);
            _remaining = Array.Empty<int>();
            _next = 0;
        }

        /// <summary>
        /// 下一个该播的下标。一轮之内不会重复；一轮取完之后自动重洗，
        /// 而新的一轮**不含** <paramref name="currentIndex"/>（它这一轮已经播过了）。
        /// </summary>
        /// <param name="currentIndex">正在播的那个下标。</param>
        public int Next(int currentIndex)
        {
            if (Count <= 0) return 0;
            if (Count == 1) return 0;

            if (_next >= _remaining.Length) Refill(currentIndex);

            return _remaining[_next++];
        }

        private void Refill(int currentIndex)
        {
            var remaining = new List<int>(Count);

            for (var i = 0; i < Count; i++)
            {
                // 正在播的这一首这一轮已经播过了，新的一轮从它后面开始排
                if (i != currentIndex) remaining.Add(i);
            }

            // Fisher-Yates：从后往前，每一步和前面（含自己）任取一个交换
            for (var i = remaining.Count - 1; i > 0; i--)
            {
                var j = _random.Next(i + 1);
                (remaining[i], remaining[j]) = (remaining[j], remaining[i]);
            }

            _remaining = remaining.ToArray();
            _next = 0;
        }
    }
}
