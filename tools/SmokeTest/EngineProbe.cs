// 诊断探针：只给"偶发红"抓现场用，不参与任何断言。
//
// 为什么要有它：B5 那两条偶发红
//   · `右键点进度条把播放位置拽回去了（40691 → 550 ms）`
//   · `按住拖到进度条外面之后不跟手了（500 → 离开后 0 → 再移动 0）`
// 两次都是"引擎时间突然回到 0 附近"，但"回到 0"至少有四种来源：
//   ① 有人重载了媒体（PlayIndex / 换歌）② 内核被换掉（旋转、音频设备变化）
//   ③ 播到结尾又从头开始 ④ 真的被 seek 了
// 只看失败那一句的两个数字分不出来。探针把中间那段曲线与引擎事件一起留下来，
// 失败时由调用方写进 smoke-journal.txt。
//
// 探针只读公开属性、只追加日志，**不改任何判据**——它自己红不红跟它无关。

using System.Diagnostics;

namespace SmokeTest
{
    /// <summary>
    /// 每 50 ms 采一次 <see cref="PlayerEngine"/> 的时间 / 在播 / 时长 / 列表当前项，
    /// 同时订阅引擎事件（状态变化 / 播放结束 / 错误 / 内核重建 / 轨道变化）。
    /// </summary>
    internal sealed class EngineProbe : IDisposable
    {
        private const int IntervalMs = 50;

        /// <summary>只留最近 20 秒（50 ms × 400），一次拖动的窗口绰绰有余。</summary>
        private const int KeepSamples = 400;

        private readonly PlayerEngine _engine;
        private readonly Playlist _playlist;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly object _gate = new object();
        private readonly List<Sample> _samples = new List<Sample>();
        private readonly List<string> _events = new List<string>();
        private readonly Thread _thread;

        private volatile bool _stop;
        private EventHandler<PlayerState>? _onState;
        private EventHandler? _onEnded;
        private EventHandler<string>? _onError;
        private EventHandler? _onCoreRebuilt;
        private EventHandler? _onTracks;

        private readonly struct Sample
        {
            public Sample(long ms, long time, bool playing, long length, int index)
            {
                Ms = ms;
                Time = time;
                Playing = playing;
                Length = length;
                Index = index;
            }

            public long Ms { get; }
            public long Time { get; }
            public bool Playing { get; }
            public long Length { get; }
            public int Index { get; }
        }

        public EngineProbe(PlayerEngine engine, Playlist playlist)
        {
            _engine = engine;
            _playlist = playlist;

            _onState = (_, state) => Event("状态 " + state);
            _onEnded = (_, _) => Event("播放结束");
            _onError = (_, message) => Event("错误 " + message);
            _onCoreRebuilt = (_, _) => Event("内核重建");
            _onTracks = (_, _) => Event("轨道变化");

            engine.StateChanged += _onState;
            engine.PlaybackEnded += _onEnded;
            engine.ErrorOccurred += _onError;
            engine.CoreRebuilt += _onCoreRebuilt;
            engine.TracksChanged += _onTracks;

            _thread = new Thread(Loop) { IsBackground = true, Name = "smoke-engine-probe" };
            _thread.Start();
        }

        private void Event(string text)
        {
            lock (_gate)
            {
                _events.Add(_clock.ElapsedMilliseconds + "ms " + text);
            }
        }

        private void Loop()
        {
            while (!_stop)
            {
                Thread.Sleep(IntervalMs);

                long time;
                long length;
                bool playing;

                try
                {
                    time = _engine.Time;
                    length = _engine.Length;
                    playing = _engine.IsPlaying;
                }
                catch (Exception)
                {
                    // 引擎被 Dispose 的那一拍：停就对了，别把异常抛到后台线程上
                    return;
                }

                int index;
                try
                {
                    index = _playlist.CurrentIndex;
                }
                catch (Exception)
                {
                    index = -2;
                }

                lock (_gate)
                {
                    _samples.Add(new Sample(_clock.ElapsedMilliseconds, time, playing, length, index));
                    if (_samples.Count > KeepSamples) _samples.RemoveAt(0);
                }
            }
        }

        /// <summary>
        /// 把现场压成几行：时间区间与回退次数、在播/列表当前项的变化、引擎事件、最后几个采样点。
        /// 只有失败分支会调它，所以日志不会被正常跑撑大。
        /// </summary>
        public string Dump()
        {
            List<Sample> samples;
            List<string> events;

            lock (_gate)
            {
                samples = new List<Sample>(_samples);
                events = new List<string>(_events);
            }

            if (samples.Count == 0) return "探针：没有采到样本";

            var min = samples[0].Time;
            var max = samples[0].Time;
            var resets = 0;
            var playingChanges = 0;
            var indexChanges = 0;
            var lastReset = "";
            var previous = samples[0];

            foreach (var sample in samples.Skip(1))
            {
                if (sample.Time < min) min = sample.Time;
                if (sample.Time > max) max = sample.Time;

                // 回退超过 2 秒才算"重置"，避免把 seek 之后正常的抖动算进去
                if (sample.Time < previous.Time - 2000)
                {
                    resets++;
                    lastReset = $"{previous.Time} → {sample.Time} @ {sample.Ms}ms";
                }

                if (sample.Playing != previous.Playing) playingChanges++;
                if (sample.Index != previous.Index) indexChanges++;

                previous = sample;
            }

            var last = samples[samples.Count - 1];
            var sb = new StringBuilder();

            sb.Append("探针：").Append(samples.Count).Append(" 个采样（每 ").Append(IntervalMs)
              .Append(" ms）· Time ").Append(min).Append("..").Append(max)
              .Append(" · 回退 ").Append(resets).Append(" 次");

            if (lastReset.Length > 0) sb.Append("（最后一次 ").Append(lastReset).Append('）');

            sb.Append(" · 在播 ").Append(last.Playing ? "是" : "否")
              .Append(" · 时长 ").Append(last.Length)
              .Append(" · 列表当前 ").Append(last.Index)
              .Append(" · 在播变化 ").Append(playingChanges).Append(" 次")
              .Append(" · 列表项变化 ").Append(indexChanges).Append(" 次");

            if (events.Count > 0)
            {
                sb.AppendLine().Append("  引擎事件：").Append(string.Join(" | ", events));
            }

            var tail = samples.Skip(Math.Max(0, samples.Count - 12)).ToList();
            sb.AppendLine().Append("  末 12 点：")
              .Append(string.Join(" ", tail.Select(s => s.Time + "@" + s.Ms)));

            return sb.ToString();
        }

        public void Dispose()
        {
            _stop = true;

            try
            {
                _thread.Join(1000);
            }
            catch (Exception)
            {
                // 线程已经退出或根本起不来，都不影响结论
            }

            if (_onState != null) _engine.StateChanged -= _onState;
            if (_onEnded != null) _engine.PlaybackEnded -= _onEnded;
            if (_onError != null) _engine.ErrorOccurred -= _onError;
            if (_onCoreRebuilt != null) _engine.CoreRebuilt -= _onCoreRebuilt;
            if (_onTracks != null) _engine.TracksChanged -= _onTracks;
        }
    }
}
