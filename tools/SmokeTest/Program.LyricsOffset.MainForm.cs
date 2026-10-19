// 检查点 18：歌词时间轴偏移 —— 主窗体上的整条链路（侧栏 / 桌面歌词 / 菜单 / 热键 / 重启）。
//
// 从 Program.LyricsOffset.cs 按 ARCHITECTURE 17.2 的规矩整块搬过来：成员一个字没动，
// 字段一条没搬（这个文件里没有字段）。

namespace SmokeTest
{
    internal static partial class Program
    {
        /// <summary>主窗体上的整条链路：侧栏、桌面歌词、菜单、角落提示、热键、重启。</summary>
        private static bool CheckLyricsOffsetInMainForm()
        {
            var synced = Path.Combine(AppContext.BaseDirectory, "smoke-tags", "sample.flac");

            if (!File.Exists(synced))
            {
                Log(18, "歌词偏移检查：缺少第 9 步生成的样本文件");
                return false;
            }

            // 自己造一个"没有歌词"的真音频，而不是借第 9 步的样本：
            // 那些样本的歌手 / 曲名和第 9 步留下的外挂 .lrc 撞得上名，会被当成"带轴歌词"，
            // 那样这一条就验不到"没有时间轴时不许调"了（数据目录跑完就删，不用额外清理）
            var scratch = Path.Combine(AppSettings.SettingsDirectory, "lyrics-offset");
            TryDeleteDirectory(scratch);
            Directory.CreateDirectory(scratch);

            var noLyrics = Path.Combine(scratch, "no-lyrics.wav");
            WriteWav(noLyrics, seconds: 1, frequency: 440);

            // 历史文件是这一步之前几步共用的，先把这首的偏移清干净
            var history = PlaybackHistory.Load();
            history.RecordLyricsOffset(synced, 0);
            history.Save();

            using var form = new 播放器.MainForm(Array.Empty<string>());
            form.Show();
            PumpMessages(250);

            form.LoadTrackMetadata(synced);

            if (WaitFor(
                    () => SessionOf(form).Lyrics,
                    document => document.IsSynchronized,
                    6) == null)
            {
                Log(18, "歌词偏移检查：等不到这首的带轴歌词");
                return false;
            }

            PumpMessages(200);

            var view = FindByTypeName(form, "LyricsView") as LyricsView;

            if (view == null)
            {
                Log(18, "歌词偏移检查：侧栏里找不到歌词控件");
                return false;
            }

            var shown = view.Document;

            if (shown == null || !shown.IsSynchronized)
            {
                Log(18, "歌词偏移检查：侧栏歌词不是带轴的");
                return false;
            }

            if (shown.Lines[0].Time != TimeSpan.FromSeconds(1) || view.OffsetHint != null)
            {
                Log(18, $"歌词偏移检查：刚载入就被偏移了（第一句 {shown.Lines[0].Time} / 提示 {view.OffsetHint}）");
                return false;
            }

            var menu = form.MenuLyricsOffset;

            if (menu == null)
            {
                Log(18, "歌词偏移检查：视图菜单里没有「歌词偏移」");
                return false;
            }

            // ---- 提前 0.5 秒 ----
            form.AdjustLyricsOffset(1);
            PumpMessages(150);

            if (CurrentOffset(form) != 500)
            {
                Log(18, $"歌词偏移检查：调了一下不是 +0.5 秒（现在是 {CurrentOffset(form)} 毫秒）");
                return false;
            }

            shown = view.Document;

            if (shown == null || shown.Lines[0].Time != TimeSpan.FromMilliseconds(500))
            {
                Log(18, $"歌词偏移检查：侧栏歌词没跟着偏移（第一句 {shown?.Lines[0].Time}）");
                return false;
            }

            if (view.OffsetHint != "+0.5s")
            {
                Log(18, $"歌词偏移检查：歌词页角落没有写出当前偏移（{view.OffsetHint ?? "空"}）");
                return false;
            }

            // 菜单是展开时才刷新的，这里手动刷一次（等于用户点开菜单）
            form.RefreshLyricsOffsetMenu();

            var valueItem = menu.DropDownItems.OfType<ToolStripMenuItem>()
                .FirstOrDefault(i => (i.Tag as string) == "lyrics-offset-value");

            var advanceItem = menu.DropDownItems.OfType<ToolStripMenuItem>()
                .FirstOrDefault(i => (i.Tag as string) == "lyrics-offset-advance");

            if (valueItem == null || !valueItem.Text.Contains("提前 0.5 秒", StringComparison.Ordinal))
            {
                Log(18, $"歌词偏移检查：菜单上没有写出当前偏移（「{valueItem?.Text}」）");
                return false;
            }

            if (advanceItem == null || !advanceItem.Enabled)
            {
                Log(18, "歌词偏移检查：有带轴歌词时「歌词提前」却是灰的");
                return false;
            }

            // 「媒体信息」页里也要有一行（和音画 / 字幕延迟并排）
            var rows = ReadInfoRows(form, synced);

            if (!rows.Any(r => r.Name == "歌词偏移" && r.Value == "提前 0.5 秒"))
            {
                Log(18, $"歌词偏移检查：媒体信息里没有歌词偏移这一行（{string.Join(" / ", rows.Select(r => r.Name + "=" + r.Value))}）");
                return false;
            }

            // ---- 热键：Ctrl+] 提前、Ctrl+[ 延后 ----
            if (!SendShortcut(form, Keys.Control | Keys.OemCloseBrackets) || CurrentOffset(form) != 1000)
            {
                Log(18, $"歌词偏移检查：Ctrl+] 没有让歌词提前（现在是 {CurrentOffset(form)} 毫秒）");
                return false;
            }

            if (!SendShortcut(form, Keys.Control | Keys.OemOpenBrackets) || CurrentOffset(form) != 500)
            {
                Log(18, $"歌词偏移检查：Ctrl+[ 没有让歌词延后（现在是 {CurrentOffset(form)} 毫秒）");
                return false;
            }

            // ---- 桌面歌词必须是同一份时间轴 ----
            form.SetDesktopLyricsEnabled(true, true);
            PumpMessages(300);

            if (form.DesktopLyricsWindow is { } window)
            {
                var desktop = window.Document;

                if (desktop == null || desktop.Lines[0].Time != TimeSpan.FromMilliseconds(500))
                {
                    Log(18, $"歌词偏移检查：桌面歌词用的不是同一份时间轴（第一句 {desktop?.Lines[0].Time}）");
                    return false;
                }

                form.SetDesktopLyricsEnabled(false, false);
                PumpMessages(150);
            }

            // ---- 换到一首没有歌词的歌：偏移跟着文件走，菜单如实说明为什么不能调 ----
            form.LoadTrackMetadata(noLyrics);

            if (WaitFor(
                    () => SessionOf(form).Lyrics,
                    document => document.IsEmpty,
                    6) == null)
            {
                Log(18, "歌词偏移检查：等不到切到没有歌词的那一首");
                return false;
            }

            PumpMessages(250);

            if (CurrentOffset(form) != 0)
            {
                Log(18, $"歌词偏移检查：换歌之后偏移没有跟着那个文件走（现在是 {CurrentOffset(form)} 毫秒）");
                return false;
            }

            form.RefreshLyricsOffsetMenu();

            if (advanceItem.Enabled)
            {
                Log(18, "歌词偏移检查：这首根本没有歌词，居然还能调偏移");
                return false;
            }

            if (valueItem.Text.IndexOf("没有歌词", StringComparison.Ordinal) < 0)
            {
                Log(18, $"歌词偏移检查：菜单没有说清楚为什么不能调（「{valueItem.Text}」）");
                return false;
            }

            // 没有时间轴时按热键也不该改任何东西
            SendShortcut(form, Keys.Control | Keys.OemCloseBrackets);
            PumpMessages(100);

            if (CurrentOffset(form) != 0)
            {
                Log(18, "歌词偏移检查：没有时间轴时按 Ctrl+] 居然把偏移改了");
                return false;
            }

            // ---- 切回带轴的那首：这个文件自己的偏移要回来 ----
            form.LoadTrackMetadata(synced);

            if (WaitFor(
                    () => SessionOf(form).Lyrics,
                    document => document.IsSynchronized,
                    6) == null)
            {
                Log(18, "歌词偏移检查：切回来之后等不到带轴歌词");
                return false;
            }

            PumpMessages(250);

            if (CurrentOffset(form) != 500)
            {
                Log(18, $"歌词偏移检查：切回来没有恢复这个文件的偏移（现在是 {CurrentOffset(form)} 毫秒）");
                return false;
            }

            // ---- 归零：角落提示也要跟着消失 ----
            form.ResetLyricsOffset();
            PumpMessages(150);

            if (CurrentOffset(form) != 0 || view.OffsetHint != null)
            {
                Log(18, $"歌词偏移检查：归零不干净（偏移 {CurrentOffset(form)} / 提示 {view.OffsetHint ?? "空"}）");
                return false;
            }

            // ---- 落盘：重启之后这个文件的偏移自己回来 ----
            form.AdjustLyricsOffset(1);
            PumpMessages(150);

            if (WaitFor(
                    () =>
                    {
                        var disk = PlaybackHistory.Load();
                        return disk.TryGetLyricsOffset(synced, out var milliseconds) && milliseconds == 500
                            ? disk
                            : null;
                    },
                    _ => true,
                    6) == null)
            {
                Log(18, "歌词偏移检查：偏移没有写进 history.json（重启就丢了）");
                return false;
            }

            var onDiskBeforeClose = ReadDiskLyricsOffset(synced);

            form.Close();
            PumpMessages(250);

            var onDiskAfterClose = ReadDiskLyricsOffset(synced);

            using (var reopened = new 播放器.MainForm(Array.Empty<string>()))
            {
                reopened.Show();
                PumpMessages(300);

                reopened.LoadTrackMetadata(synced);

                if (WaitFor(
                        () => SessionOf(reopened).Lyrics,
                        document => document.IsSynchronized,
                        6) == null)
                {
                    Log(18, "歌词偏移检查：重新启动之后等不到带轴歌词");
                    return false;
                }

                PumpMessages(250);

                var restored = SessionOf(reopened).LyricsOffsetMilliseconds;

                if (restored != 500)
                {
                    // 三种"偏移没回来"要分得开，否则下次只能猜：
                    //   ① 磁盘上本来就没了（关窗时被写掉）→ 落盘链路的问题
                    //   ② 磁盘上是 500，但重开的窗体当前根本不是这首 → 会话被别的东西清掉了
                    //   ③ 磁盘上是 500、当前也确实是这首，却读到 0 → 读回来那一步的问题
                    var nowPath = SessionOf(reopened).Path;

                    Log(18, $"歌词偏移检查：重新启动之后这个文件的偏移没有回来（{restored} 毫秒）。"
                            + $"磁盘上：关窗前 {onDiskBeforeClose}、关窗后 {onDiskAfterClose}、"
                            + $"现在 {ReadDiskLyricsOffset(synced)} 毫秒；"
                            + $"重开窗体的当前文件「{nowPath ?? "（没有）"}」"
                            + (string.Equals(nowPath, synced, StringComparison.OrdinalIgnoreCase)
                                ? "（就是这首，读回来那一步的问题）"
                                : "（不是这首：会话被别的东西清掉/换掉了）"));
                    return false;
                }

                reopened.Close();
                PumpMessages(150);
            }

            Log(18, "歌词偏移在主窗体上正常：调一下侧栏歌词立刻跟着走（时间与角落提示都对）、"
                    + "Ctrl+] / Ctrl+[ 能提前与延后、桌面歌词用的是同一份时间轴、媒体信息与菜单都写出当前值，"
                    + "偏移跟着文件走、纯文本歌词时菜单如实禁用且热键不改任何东西，重启后偏移自己回来");
            return true;
        }
    }
}
