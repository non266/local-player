using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

// 播放历史的落盘：同步写 / 后台写 / 快照序列化。
// 从 Core/PlaybackHistory.cs 按 ARCHITECTURE 17.2 的规矩整块搬过来：成员一个字没动，
// 字段（_dirty / _writeInFlight / BeforeWriteGate）仍留在主文件。

namespace 播放器.Core
{
    public sealed partial class PlaybackHistory
    {
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
    }
}
