// 第 8 项与第 22 项的一部分：最近串流地址（1.23.0 F8）。
//
// 第 8 项验纯逻辑与落盘加密（地址里常带账号口令，明文落盘是不能接受的）；
// 第 22 项验菜单：条目数、脱敏、清除、以及"清空要当场落盘"。

using System.Text.Json.Nodes;

namespace SmokeTest
{
    internal static partial class Program
    {
        /// <summary>最近地址：去重 / MRU / 上限 / 脱敏标签 / 落盘加密 / 解不开时只丢那一条。</summary>
        private static bool CheckRecentStreams()
        {
            // ---- ① 纯逻辑 ----
            var raw = new List<string>
            {
                "http://user:pass@host/a.m3u8",
                "http://user:pass@host/a.m3u8",
                "rtsp://host/live",
                @"C:\音乐\本地.mp3",
                string.Empty,
                "https://host/b?token=1"
            };

            var normalized = RecentStreams.Normalize(raw);

            if (normalized.Count != 3)
            {
                Log(8, $"最近地址：归一之后是 {normalized.Count} 条（期望 3：重复只留一条、本地路径与空串丢掉）");
                return false;
            }

            var pushed = RecentStreams.Push(normalized, "http://host/new");

            if (pushed.Count != 4 || pushed[0] != "http://host/new")
            {
                Log(8, $"最近地址：新地址没有排在最前（{string.Join(" | ", pushed)}）");
                return false;
            }

            var many = Enumerable.Range(0, 12).Select(index => $"http://host/{index}").ToList();

            if (RecentStreams.Normalize(many).Count != 10)
            {
                Log(8, $"最近地址：上限不是 10（{RecentStreams.Normalize(many).Count}）");
                return false;
            }

            var label = RecentStreams.Describe("http://user:pass@example.com:8080/a/b.m3u8?token=1");

            if (label.Contains("user", StringComparison.Ordinal) ||
                label.Contains("pass", StringComparison.Ordinal) ||
                label.Contains("token", StringComparison.Ordinal) || label.Contains('?'))
            {
                Log(8, $"最近地址：菜单标签里带着凭据 / 参数（「{label}」）");
                return false;
            }

            if (!label.Contains("example.com", StringComparison.Ordinal) || label.Length > 60)
            {
                Log(8, $"最近地址：菜单标签不合适（「{label}」）");
                return false;
            }

            // ---- ② 落盘加密 ----
            var folder = Path.Combine(AppContext.BaseDirectory, "recent-streams");
            var file = Path.Combine(folder, "settings.json");
            var secretUrl = "http://user:pass@host/secret.m3u8?token=1";

            try
            {
                var settings = new AppSettings { Volume = 42, RecentStreams = new List<string> { secretUrl } };
                settings.SaveTo(file);

                var text = File.ReadAllText(file);

                if (text.Contains("user:pass", StringComparison.Ordinal) || text.Contains("token=1", StringComparison.Ordinal))
                {
                    Log(8, "最近地址：设置文件里是明文地址（期望 dpapi: 密文）");
                    return false;
                }

                if (!text.Contains("dpapi:", StringComparison.Ordinal))
                {
                    Log(8, "最近地址：设置文件里的地址没有用 DPAPI 加密（没找到 dpapi:）");
                    return false;
                }

                var loaded = AppSettings.LoadFrom(file);

                if (loaded.RecentStreams.Count != 1 || loaded.RecentStreams[0] != secretUrl || loaded.Volume != 42)
                {
                    Log(8, $"最近地址：加密保存之后读回来不一致（{loaded.RecentStreams.Count} 条，音量 {loaded.Volume}）");
                    return false;
                }

                // ---- ③ 解不开的那一条丢掉，其余与别的设置都保住 ----
                var node = JsonNode.Parse(settings.ToJson())!.AsObject();
                node["RecentStreams"]!.AsArray().Add("dpapi:AAAA");

                var brokenFile = Path.Combine(folder, "broken.json");
                SafeFile.WriteAllText(brokenFile, node.ToJsonString());

                var broken = AppSettings.LoadFrom(brokenFile);

                if (broken.RecentStreams.Count != 1 || broken.RecentStreams[0] != secretUrl || broken.Volume != 42)
                {
                    Log(8, $"最近地址：解不开的密文没有只丢那一条（剩 {broken.RecentStreams.Count} 条，音量 {broken.Volume}）");
                    return false;
                }

                // ---- ④ 本地路径 / 空串不会从文件里读进来 ----
                var badFile = Path.Combine(folder, "bad.json");
                SafeFile.WriteAllText(badFile, "{\"Volume\":7,\"RecentStreams\":[\"C:\\\\x.mp3\",\"\",\"rtsp://host/ok\"]}");

                var bad = AppSettings.LoadFrom(badFile);

                if (bad.RecentStreams.Count != 1 || bad.RecentStreams[0] != "rtsp://host/ok")
                {
                    Log(8, $"最近地址：本地路径 / 空串也被收下了（{string.Join(" | ", bad.RecentStreams)}）");
                    return false;
                }

                // ---- ⑤ 清除 ----
                if (RecentStreams.Normalize(new[] { secretUrl }).Count == 0)
                {
                    Log(8, "最近地址：清除之前列表本来就是空的");
                    return false;
                }

                var cleared = new AppSettings { RecentStreams = new List<string>() };
                cleared.SaveTo(file);

                if (AppSettings.LoadFrom(file).RecentStreams.Count != 0)
                {
                    Log(8, "最近地址：清空之后又从磁盘读出了条目");
                    return false;
                }

                Log(8, "最近地址的逻辑正确（去重 / MRU / 上限 10 / 标签脱敏 / 落盘 DPAPI 密文 / 解不开只丢那一条 / 本地路径不收）");
                return true;
            }
            finally
            {
                TryDeleteDirectory(folder);
            }
        }

        /// <summary>菜单：条目插在「打开网络串流」之后、只显示脱敏标签、清除会当场落盘。</summary>
        private static bool CheckRecentStreamsMenu(播放器.MainForm form)
        {
            var menu = form.RecentStreamsMenu;

            if (menu == null)
            {
                Log(22, "最近地址：文件菜单里没有「最近地址」这一项");
                return false;
            }

            var fileMenu = FindTopMenuItem(form, "文件");

            if (fileMenu == null || !fileMenu.DropDownItems.Contains(menu))
            {
                Log(22, "最近地址：这一项不在「文件」菜单里");
                return false;
            }

            form.Settings.RecentStreams = new List<string>
            {
                "http://user:pass@host/one.m3u8?token=1",
                "rtsp://host/two"
            };

            // 落盘一次：模拟"这些地址是以前退出时写下的"。这样"清除只清内存"的变体才验得出来
            // （否则磁盘上本来就是空的，漏掉 Save 也看不出来）。
            form.Settings.Save();

            form.RebuildRecentStreamsMenu();

            // 2 条 + 分隔线 + 清除项
            if (menu.DropDownItems.Count != 4)
            {
                Log(22, $"最近地址：菜单条目数是 {menu.DropDownItems.Count}（期望 4：2 条 + 分隔线 + 清除）");
                return false;
            }

            var first = menu.DropDownItems[0] as ToolStripMenuItem;

            if (first == null)
            {
                Log(22, "最近地址：第一条菜单项不是普通条目");
                return false;
            }

            if (first.Tag is not string firstUrl || firstUrl != "http://user:pass@host/one.m3u8?token=1")
            {
                Log(22, "最近地址：第一条的 Tag 不是最新那条地址");
                return false;
            }

            if (first.Text.Contains("user:pass", StringComparison.Ordinal) ||
                first.Text.Contains("token", StringComparison.Ordinal))
            {
                Log(22, $"最近地址：菜单里直接摆出了凭据（「{first.Text}」）");
                return false;
            }

            var clear = menu.DropDownItems.OfType<ToolStripMenuItem>().LastOrDefault();

            if (clear == null || !clear.Text.Contains("清除", StringComparison.Ordinal))
            {
                Log(22, "最近地址：菜单最后一项不是「清除最近地址」");
                return false;
            }

            clear.PerformClick();

            if (form.Settings.RecentStreams.Count != 0)
            {
                Log(22, "最近地址：点了清除之后设置里还有条目");
                return false;
            }

            if (menu.Enabled || menu.DropDownItems.Count != 1)
            {
                Log(22, "最近地址：清空之后菜单没有变成「（暂无记录）」的灰项");
                return false;
            }

            // 清除要当场落盘：重新读设置文件也不该有地址
            var reloaded = AppSettings.LoadFrom(AppSettings.SettingsFilePath);

            if (reloaded.RecentStreams.Count != 0)
            {
                Log(22, "最近地址：清除只清了内存，磁盘上还留着（隐私动作必须当场落盘）");
                return false;
            }

            Log(22, "最近地址菜单正确（插在打开串流之后、只显示脱敏标签、清除当场落盘）");
            return true;
        }
    }
}
