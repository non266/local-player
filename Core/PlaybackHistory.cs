using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace 播放器.Core
{
    /// <summary>单个文件的播放记录。</summary>
    public sealed class PlaybackRecord
    {
        public string FilePath { get; set; } = string.Empty;

        public double PositionSeconds { get; set; }

        public double DurationSeconds { get; set; }

        public DateTime LastPlayedUtc { get; set; }

        /// <summary>这个文件上次用的音频延迟（毫秒）——同一个片源每次都要重调很烦，所以记住。</summary>
        public long AudioDelayMilliseconds { get; set; }

        /// <summary>这个文件上次用的字幕延迟（毫秒）。</summary>
        public long SubtitleDelayMilliseconds { get; set; }

        /// <summary>
        /// 这个文件上次用的歌词偏移（毫秒，正值 = 歌词提前）。
        /// <para>和音画 / 字幕延迟一样按文件记：同一首歌的不同版本、不同来源的歌词，
        /// 差得往往不是同一个数。</para>
        /// </summary>
        public long LyricsOffsetMilliseconds { get; set; }

        /// <summary>
        /// 这个文件上次用的画面旋转 / 翻转（<see cref="ScreenRotations.ToStorage"/> 写的那串字；
        /// 空 = 不旋转）。
        /// <para><b>只增字段</b>：老 <c>history.json</c> 里没有它，读出来就是空串 = 不旋转，
        /// 不影响别的记录。</para>
        /// </summary>
        public string Rotation { get; set; } = string.Empty;
    }

    /// <summary>
    /// 播放历史：记住每个文件播到哪儿，并据此提供"最近播放"列表。
    /// <para>
    /// 单独存一个 <c>history.json</c>，不塞进 settings.json —— 记录会随播放不断增长，
    /// 混在一起会让设置文件变得又大又难读。
    /// </para>
    /// </summary>
    public sealed class PlaybackHistory
    {
        /// <summary>刚开头几秒不值得续播，否则每次打开都像卡住。</summary>
        private const int MinimumResumeSeconds = 15;

        /// <summary>距离结尾这么近就当作已经看完，下次从头播。</summary>
        private const int TailGuardSeconds = 20;

        /// <summary>最多保留多少条记录（超出后淘汰最旧的）。</summary>
        private const int MaximumRecords = 2000;

        /// <summary>"最近播放"菜单里显示多少条。</summary>
        public const int RecentCount = 15;

        private readonly Dictionary<string, PlaybackRecord> _records =
            new Dictionary<string, PlaybackRecord>(StringComparer.OrdinalIgnoreCase);

        private bool _dirty;

        /// <summary>是否已有一个后台写入在跑，避免每次计时都排队一次。</summary>
        private int _writeInFlight;

        /// <summary>退出时那条同步写最多等在飞写入多久（毫秒）；等不到也照写。</summary>
        private const int SaveWriteWaitMilliseconds = 2000;

        /// <summary>
        /// 测试用的门闩：<b>真正写文件之前</b>会调它一次（生产代码从不设置它）。
        /// <para>
        /// 冒烟靠它在"写入已经排上、还没落盘"的那一小段里塞进一次新的改动，
        /// 检查"写到不脏为止"是真的（第 8 步 <c>CheckHistoryWriteBehind</c>）。
        /// 没有这个门闩，那一小段时序在测试里根本摆不出来。
        /// </para>
        /// </summary>
        internal Action? BeforeWriteGate { get; set; }

        /// <summary>
        /// 读取历史失败时留下的说明，正常时为 <c>null</c>。
        /// <para>以前这里损坏是"悄悄从空的开始"，用户不会知道所有续播位置都没了。</para>
        /// </summary>
        public string? LoadWarning { get; private set; }

        /// <summary>历史文件路径。</summary>
        public static string FilePath =>
            Path.Combine(AppSettings.SettingsDirectory, "history.json");

        /// <summary>
        /// 最近播放过的文件，按时间由新到旧。
        /// <para>
        /// 跳过"没播过、只是被记下了某个调整"的空壳记录：调一下歌词偏移也会给文件建一条记录，
        /// 让它混进"最近播放"里就成了凭空多出来的条目。
        /// </para>
        /// </summary>
        public IReadOnlyList<string> Recent =>
            _records.Values
                .Where(r => r.LastPlayedUtc != default)
                .OrderByDescending(r => r.LastPlayedUtc)
                .Take(RecentCount)
                .Select(r => r.FilePath)
                .ToList();

        /// <summary>已记录的文件数量。</summary>
        public int Count => _records.Count;

        /// <summary>
        /// 记录一次播放位置。只更新内存，落盘由 <see cref="Save"/> 负责。
        /// </summary>
        public void Record(string filePath, TimeSpan position, TimeSpan duration)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return;

            // 网络流没有稳定标识，记了也没意义。
            if (MediaFormats.IsStreamUri(filePath)) return;

            if (!_records.TryGetValue(filePath, out var record))
            {
                record = new PlaybackRecord { FilePath = filePath };
                _records[filePath] = record;
            }

            record.PositionSeconds = Math.Max(0, position.TotalSeconds);
            if (duration > TimeSpan.Zero)
                record.DurationSeconds = duration.TotalSeconds;

            // DateTime.UtcNow 的精度可能只有十几毫秒，连续记录会撞上同一时刻，
            // 导致"最近播放"排序不稳定。这里强制单调递增。
            var now = DateTime.UtcNow;
            if (record.LastPlayedUtc >= now)
                now = record.LastPlayedUtc.AddMilliseconds(1);

            record.LastPlayedUtc = now;
            _dirty = true;
        }

        /// <summary>
        /// 取出可用的续播位置。位置太靠前、太靠后，或没记录过，都返回 false。
        /// </summary>
        public bool TryGetResumePosition(string filePath, out TimeSpan position)
        {
            position = TimeSpan.Zero;
            if (string.IsNullOrWhiteSpace(filePath)) return false;
            if (!_records.TryGetValue(filePath, out var record)) return false;

            var seconds = record.PositionSeconds;
            if (seconds < MinimumResumeSeconds) return false;

            // 已经播到结尾附近：当作看完，下次从头开始。
            if (record.DurationSeconds > 0 &&
                seconds > record.DurationSeconds - TailGuardSeconds)
                return false;

            position = TimeSpan.FromSeconds(seconds);
            return true;
        }

        /// <summary>取某个文件上次播放的时间；没有记录返回 <c>null</c>。</summary>
        public DateTime? GetLastPlayed(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return null;
            return _records.TryGetValue(filePath, out var record) ? record.LastPlayedUtc : null;
        }

        /// <summary>
        /// 记住某个文件的音画 / 字幕延迟。
        /// <para>刻意不改 <see cref="PlaybackRecord.LastPlayedUtc"/>：调延迟不等于播过这个文件，
        /// 否则"最近播放"的顺序会被调参操作搅乱。</para>
        /// </summary>
        public void RecordDelays(string filePath, long audioDelayMilliseconds, long subtitleDelayMilliseconds)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return;
            if (MediaFormats.IsStreamUri(filePath)) return;

            if (!_records.TryGetValue(filePath, out var record))
            {
                record = new PlaybackRecord { FilePath = filePath };
                _records[filePath] = record;
            }

            if (record.AudioDelayMilliseconds == audioDelayMilliseconds &&
                record.SubtitleDelayMilliseconds == subtitleDelayMilliseconds)
                return;

            record.AudioDelayMilliseconds = audioDelayMilliseconds;
            record.SubtitleDelayMilliseconds = subtitleDelayMilliseconds;
            _dirty = true;
        }

        /// <summary>取某个文件上次用的延迟；没有记录时返回 false 并给出 0。</summary>
        public bool TryGetDelays(
            string filePath, out long audioDelayMilliseconds, out long subtitleDelayMilliseconds)
        {
            audioDelayMilliseconds = 0;
            subtitleDelayMilliseconds = 0;

            if (string.IsNullOrWhiteSpace(filePath)) return false;
            if (!_records.TryGetValue(filePath, out var record)) return false;

            audioDelayMilliseconds = record.AudioDelayMilliseconds;
            subtitleDelayMilliseconds = record.SubtitleDelayMilliseconds;

            return audioDelayMilliseconds != 0 || subtitleDelayMilliseconds != 0;
        }

        /// <summary>
        /// 记住某个文件的歌词偏移（毫秒，正值 = 歌词提前）。
        /// <para>和 <see cref="RecordDelays"/> 一样刻意不动 <see cref="PlaybackRecord.LastPlayedUtc"/>：
        /// 调歌词不影响"最近播放"的顺序。</para>
        /// </summary>
        public void RecordLyricsOffset(string filePath, long milliseconds)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return;
            if (MediaFormats.IsStreamUri(filePath)) return;

            if (!_records.TryGetValue(filePath, out var record))
            {
                record = new PlaybackRecord { FilePath = filePath };
                _records[filePath] = record;
            }

            if (record.LyricsOffsetMilliseconds == milliseconds) return;

            record.LyricsOffsetMilliseconds = milliseconds;
            _dirty = true;
        }

        /// <summary>取某个文件上次用的歌词偏移；没有记录或为 0 时返回 false。</summary>
        public bool TryGetLyricsOffset(string filePath, out long milliseconds)
        {
            milliseconds = 0;

            if (string.IsNullOrWhiteSpace(filePath)) return false;
            if (!_records.TryGetValue(filePath, out var record)) return false;

            milliseconds = record.LyricsOffsetMilliseconds;
            return milliseconds != 0;
        }

        /// <summary>
        /// 记住某个文件的画面旋转 / 翻转。
        /// <para>和 <see cref="RecordDelays"/> 一样刻意不动 <see cref="PlaybackRecord.LastPlayedUtc"/>：
        /// 转个画面不等于"重新看过这个文件"，否则"最近播放"的顺序会被它搅乱。</para>
        /// </summary>
        public void RecordRotation(string filePath, ScreenRotation rotation)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return;
            if (MediaFormats.IsStreamUri(filePath)) return;

            if (!_records.TryGetValue(filePath, out var record))
            {
                record = new PlaybackRecord { FilePath = filePath };
                _records[filePath] = record;
            }

            var value = ScreenRotations.ToStorage(rotation);
            if (string.Equals(record.Rotation, value, StringComparison.Ordinal)) return;

            record.Rotation = value;
            _dirty = true;
        }

        /// <summary>取某个文件上次用的画面旋转；没记过时给"不旋转"。</summary>
        public bool TryGetRotation(string filePath, out ScreenRotation rotation)
        {
            rotation = ScreenRotation.None;

            if (string.IsNullOrWhiteSpace(filePath)) return false;
            if (!_records.TryGetValue(filePath, out var record)) return false;

            rotation = ScreenRotations.Parse(record.Rotation);
            return rotation != ScreenRotation.None;
        }

        /// <summary>只清掉播放进度，保留"最近播放"。</summary>
        public void ClearPositions()
        {
            foreach (var record in _records.Values)
            {
                record.PositionSeconds = 0;
                record.DurationSeconds = 0;
            }

            _dirty = true;
        }

        /// <summary>
        /// 清空"最近播放"与续播进度。
        /// <para>
        /// <b>但保留按文件记下的那些调整</b>（音画 / 字幕延迟、歌词偏移）：那些是用户一项项调出来的，
        /// 不是历史记录，跟着"最近播放"一起被清掉属于无声的数据丢失。
        /// 只为这类调整而建的空壳记录会保留（它们已经不会出现在最近播放里，见 <see cref="Recent"/>）。
        /// </para>
        /// </summary>
        public void Clear()
        {
            if (_records.Count == 0) return;

            var keep = new List<PlaybackRecord>();

            foreach (var record in _records.Values)
            {
                if (record.AudioDelayMilliseconds == 0 &&
                    record.SubtitleDelayMilliseconds == 0 &&
                    record.LyricsOffsetMilliseconds == 0)
                {
                    continue;
                }

                record.PositionSeconds = 0;
                record.DurationSeconds = 0;
                record.LastPlayedUtc = default;

                keep.Add(record);
            }

            _records.Clear();

            foreach (var record in keep)
                _records[record.FilePath] = record;

            _dirty = true;
        }

        /// <summary>移除单条记录。</summary>
        public void Forget(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return;
            if (!_records.Remove(filePath)) return;

            _dirty = true;
        }

        /// <summary>
        /// 把内存里的历史写到磁盘（没有变化就跳过）。用于退出等必须落地的场合。
        /// <para>
        /// <b>先等在飞的那次后台写入落地</b>：它带着的是<b>更旧</b>的快照，要是它落在这次同步写之后，
        /// 就把新状态盖回去了（"调完立刻关程序，调整丢了"就是这么来的）。
        /// 等不到（比如磁盘卡住）也照写——宁可交错，也不能因为等而完全不写。
        /// </para>
        /// </summary>
        public void Save()
        {
            if (!_dirty) return;

            var deadline = Environment.TickCount64 + SaveWriteWaitMilliseconds;

            while (Volatile.Read(ref _writeInFlight) != 0 && Environment.TickCount64 < deadline)
                Thread.Sleep(5);

            SaveNow();
        }

        /// <summary>真正落盘的那一步（两个入口共用）。</summary>
        private void SaveNow()
        {
            try
            {
                // 测试用的门闩：让冒烟能把一次写入"按住"，摆出"写入在飞时又改了"的时序。
                BeforeWriteGate?.Invoke();

                Prune();
                SafeFile.WriteAllText(FilePath, SerializeSnapshot());
                _dirty = false;
            }
            catch (Exception ex)
            {
// 历史写不进去不应影响播放。
                AppLog.Swallowed("历史写不进去不应影响播放。", ex);
            }
        }

        /// <summary>
        /// 把历史写到磁盘，但<b>序列化在调用线程上做完、写文件丢给线程池</b>。
        /// <para>
        /// 播放时每 20 秒就要落一次盘，最多 2000 条记录序列化成缩进 JSON 是几百 KB。
        /// 整个写过程放在 UI 线程上会周期性地顿一下，所以这里只留"取快照"在 UI 线程。
        /// </para>
        /// <para>
        /// 返回值说明<b>这次调用有没有把写入排上</b>：<c>true</c> = 已经是新的（本来就不脏）
        /// 或者已经排了一次写入；<c>false</c> = 已经有写入在飞、<b>这次没排上</b>。
        /// </para>
        /// <para>
        /// ⚠ 早退的这一支不能只指望"下一次调用"：关掉「记住播放进度」之后，
        /// 按文件的调整就只剩这一条落盘路径了。需要"一次都不许丢"的调用方（<see cref="PerFileAdjustments"/>）
        /// 拿到 <c>false</c> 之后要在后台补一次——但<b>必须是那个持有这次改动的实例来补</b>：
        /// 历史是"一个实例一份内存副本、共写同一个文件"，让写入任务自己去复查 <c>_dirty</c> 而重写，
        /// 会把<b>别的</b>实例的旧快照也冲出去，反而盖掉更新的记录（本轮踩过，见冒烟第 8 步）。
        /// </para>
        /// </summary>
        public bool SaveInBackground()
        {
            if (!_dirty) return true;

            // 0 → 1 成功才算抢到写入权，避免两次写入重叠。
            if (Interlocked.CompareExchange(ref _writeInFlight, 1, 0) != 0) return false;

            string json;
            try
            {
                Prune();
                json = SerializeSnapshot();
                _dirty = false;
            }
            catch (Exception)
            {
                Interlocked.Exchange(ref _writeInFlight, 0);
                return false;
            }

            var path = FilePath;
            Task.Run(() =>
            {
                try
                {
                    // 测试用的门闩（见 SaveNow 里的说明）。
                    BeforeWriteGate?.Invoke();

                    SafeFile.WriteAllText(path, json);
                }
                catch (Exception)
                {
                    // 忽略：下一次计时会再试。
                    _dirty = true;
                }
                finally
                {
                    Interlocked.Exchange(ref _writeInFlight, 0);
                }
            });

            return true;
        }

        private string SerializeSnapshot()
        {
            var payload = _records.Values
                .OrderByDescending(r => r.LastPlayedUtc)
                .ToList();

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };

            return JsonSerializer.Serialize(payload, options);
        }

        /// <summary>从磁盘读取历史；文件缺失或损坏时返回空历史。</summary>
        public static PlaybackHistory Load()
        {
            var history = new PlaybackHistory();

            var path = FilePath;
            if (!File.Exists(path)) return history;

            try
            {
                var json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json)) return history;

                var payload = JsonSerializer.Deserialize<List<PlaybackRecord>>(json);
                if (payload == null) return history;

                foreach (var record in payload)
                {
                    if (string.IsNullOrWhiteSpace(record.FilePath)) continue;
                    history._records[record.FilePath] = record;
                }
            }
            catch (Exception)
            {
                // 历史损坏就从空的开始，但先留一份备份并告诉用户，
                // 否则"续播位置全没了"这件事是完全无声的。
                history._records.Clear();

                var backup = SafeFile.TryBackupCorrupt(path);
                history.LoadWarning = backup == null
                    ? "播放历史已损坏，本次从空白开始（原文件无法备份）。"
                    : "播放历史已损坏，已备份为 " + Path.GetFileName(backup) + "，本次从空白开始。";
            }

            return history;
        }

        /// <summary>超出上限时淘汰最旧的记录。</summary>
        private void Prune()
        {
            if (_records.Count <= MaximumRecords) return;

            var stale = _records.Values
                .OrderByDescending(r => r.LastPlayedUtc)
                .Skip(MaximumRecords)
                .Select(r => r.FilePath)
                .ToList();

            foreach (var path in stale)
                _records.Remove(path);
        }
    }
}
