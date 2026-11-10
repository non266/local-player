using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Forms;
using 播放器.Core;

// 扫描（拖进来的文件夹 / 网络位置）的界面与生命周期（1.17.0）。
//
// 以前这一段只有"丢到后台、回来加进列表"：没有进度，也没有停的手段。离线的 NAS 目录
// 可以在后台跑几分钟，状态栏一直停在"正在扫描…"。现在多了三件事：
//   ① 状态栏里的「取消扫描」链接（只在扫描期间可见）；
//   ② 已找到多少条（在界面计时器里读一次共享计数，不跨线程 Post）；
//   ③ 取消之后已经扫到的条目照常加入，并说清楚"只加了前 N 项"。

namespace 播放器
{
    public partial class MainForm
    {
        /// <summary>一批扫描的把手。取消时整批一起停。</summary>
        internal sealed class ScanBatch
        {
            internal ScanBatch(CancellationTokenSource tokenSource)
            {
                TokenSource = tokenSource;
            }

            internal CancellationTokenSource TokenSource { get; }

            internal CancellationToken Token => TokenSource.Token;
        }

        private ToolStripStatusLabel? _lblScanCancel;

        /// <summary>在飞的批次（取消时逐个 Cancel；收尾时摘掉）。都在界面线程上改。</summary>
        private readonly List<CancellationTokenSource> _scanCancellations = new();

        /// <summary>在飞几批、已经找到多少条：都在后台线程上写，所以走 Interlocked。</summary>
        private int _scanInFlight;
        private int _scanFound;

        /// <summary>用户点过「取消扫描」（等批次真的收尾之后才复位）。</summary>
        private bool _scanCancelRequested;

        /// <summary>正在扫描（冒烟测试用）。</summary>
        internal bool IsScanning => Volatile.Read(ref _scanInFlight) > 0;

        /// <summary>已经找到多少条（冒烟测试用）。</summary>
        internal int ScanFoundCount => Volatile.Read(ref _scanFound);

        /// <summary>「取消扫描」链接现在可不可见（冒烟测试用）。</summary>
        internal bool ScanCancelVisible => _lblScanCancel?.Visible == true;

        /// <summary>「取消扫描」链接现在的文字（冒烟测试用）。</summary>
        internal string? ScanCancelText => _lblScanCancel?.Text;

        /// <summary>
        /// 建状态栏那个链接。**不写进 Designer**：`MainForm.Designer.cs` 是手改的，
        /// 每改一次都容易漂，所以这一项在代码里插进去（插在状态文字之后、弹簧之前）。
        /// </summary>
        private void InitScanUi()
        {
            if (_lblScanCancel != null) return;

            var label = new ToolStripStatusLabel
            {
                Name = "lblScanCancel",
                Text = "取消扫描",
                IsLink = true,
                Visible = false
            };

            label.Click += (s, e) => CancelScans();

            var after = statusStripMain.Items.IndexOf(lblStatus);
            statusStripMain.Items.Insert(after < 0 ? statusStripMain.Items.Count : after + 1, label);

            _lblScanCancel = label;

            // 换主题时只遍历"当时已经在"的项（见 MainForm.Theme.cs），所以先摆好再上色。
            label.ForeColor = statusStripMain.ForeColor;
        }

        /// <summary>开始一批扫描：登记一个取消源，并把"已找到"清零（只在没有别的批次时清）。</summary>
        private ScanBatch BeginScan()
        {
            var batch = new ScanBatch(new CancellationTokenSource());

            _scanCancellations.Add(batch.TokenSource);

            if (Interlocked.Increment(ref _scanInFlight) == 1)
                Interlocked.Exchange(ref _scanFound, 0);

            return batch;
        }

        /// <summary>一批扫描收尾（界面线程）：摘掉取消源、释放它，并在最后一批结束时复位界面状态。</summary>
        private void EndScan(ScanBatch batch)
        {
            _scanCancellations.Remove(batch.TokenSource);
            batch.TokenSource.Dispose();

            if (Interlocked.Decrement(ref _scanInFlight) <= 0)
            {
                Interlocked.Exchange(ref _scanInFlight, 0);
                _scanCancelRequested = false;

                if (_lblScanCancel != null) _lblScanCancel.Visible = false;
            }
        }

        /// <summary>后台线程找到一个条目时调一次（只动计数，不碰控件）。</summary>
        private void NoteScanFound() => Interlocked.Increment(ref _scanFound);

        /// <summary>用户点了「取消扫描」：把在飞的批次全部取消，并立刻说一句。</summary>
        internal void CancelScans()
        {
            if (!IsScanning) return;

            _scanCancelRequested = true;

            foreach (var source in _scanCancellations.ToArray())
            {
                try
                {
                    source.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // 刚好在收尾：这一批已经结束了，没什么可取消的。
                }
            }

            if (_lblScanCancel != null) _lblScanCancel.Text = "正在取消扫描…";

            SetStatus("正在取消扫描…");
        }

        /// <summary>
        /// 界面计时器每拍刷一次链接文字。**不跨线程 BeginInvoke**：
        /// 每找到一个文件就 Post 一次的话，扫得越快（本地大目录）界面越卡。
        /// </summary>
        private void UpdateScanProgress()
        {
            var label = _lblScanCancel;
            if (label == null) return;

            if (!IsScanning)
            {
                if (label.Visible) label.Visible = false;
                return;
            }

            var found = ScanFoundCount;

            label.Text = _scanCancelRequested
                ? "正在取消扫描…"
                : found > 0
                    ? $"取消扫描 · 已找到 {found:N0} 项"
                    : "取消扫描";

            if (!label.Visible) label.Visible = true;
        }

        /// <summary>
        /// 取消之后的状态栏：说清"加了多少"与"剩下的没扫"，不要假装扫完了。
        /// <para>
        /// 刻意**不报"还有几个目录没扫"**：`Folders` 记的是"碰过的目录"（取消时那一只也在里面），
        /// 拿它当"没扫完的个数"会多报一个。要报就必须在扫描里另记"完成了几只"，不值得。
        /// </para>
        /// </summary>
        private static string DescribeCancelledScan(int addedCount, NetworkPathImportResult imported)
        {
            var parts = new List<string> { "已取消扫描" };

            if (addedCount > 0) parts.Add($"已加入前 {addedCount} 项");
            else parts.Add("一条都没扫到");

            if (imported.Truncated) parts.Add("这一批也触到了扫描上限，只扫了一部分");

            return string.Join("：", parts);
        }
    }
}
