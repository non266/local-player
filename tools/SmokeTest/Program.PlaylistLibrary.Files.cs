// 歌单库 —— 磁盘上的文件（一个歌单一个 m3u8、目录布局、坏文件与导入导出）。
//
// 从 Program.PlaylistLibrary.cs 按 ARCHITECTURE 17.2 的规矩整块搬过来：成员一个字没动。


namespace SmokeTest
{
    internal static partial class Program
    {
        /// <summary>磁盘上的那一份：存 / 列 / 载 / 改名 / 删 / 导入。</summary>
        private static bool CheckPlaylistLibraryFiles(string root, List<string> media)
        {
            var items = media.Select(path => new PlaylistItem(path)).ToList();

            // ---- 保存 ----
            PlaylistLibrary.Save("测试歌单", items);

            var saved = PlaylistLibrary.List().FirstOrDefault(p => p.Name == "测试歌单");

            if (saved == null)
            {
                Log(17, "歌单库检查：保存之后在库里列不出来");
                return false;
            }

            if (!File.Exists(saved.FilePath) || Path.GetExtension(saved.FilePath) != ".m3u8")
            {
                Log(17, $"歌单库检查：歌单文件不对（{saved.FilePath}）");
                return false;
            }

            var expectedFolder = Path.Combine(AppSettings.SettingsDirectory, PlaylistLibrary.FolderName);

            if (!saved.FilePath.StartsWith(expectedFolder, StringComparison.OrdinalIgnoreCase))
            {
                Log(17, $"歌单库检查：歌单没有存在数据目录下（{saved.FilePath}，期望在 {expectedFolder} 下）");
                return false;
            }

            if (saved.TrackCount != media.Count)
            {
                Log(17, $"歌单库检查：条目数不对（列出 {saved.TrackCount}，实际 {media.Count}）");
                return false;
            }

            // ---- 载入：顺序也要一样（歌单的顺序就是用户排的顺序）----
            var loaded = PlaylistLibrary.Load("测试歌单");

            if (!loaded.Ok || loaded.Tracks.Count != media.Count || loaded.Missing != 0)
            {
                Log(17, $"歌单库检查：载入不对（成功 {loaded.Ok}、{loaded.Tracks.Count} 项、缺 {loaded.Missing} 项：{loaded.Error}）");
                return false;
            }

            for (var i = 0; i < media.Count; i++)
            {
                if (string.Equals(loaded.Tracks[i], media[i], StringComparison.OrdinalIgnoreCase)) continue;

                Log(17, $"歌单库检查：载入后第 {i + 1} 项变成了「{loaded.Tracks[i]}」（应当是「{media[i]}」）");
                return false;
            }

            // 名字不区分大小写也算同一份（免得同一个歌单出现两个大小写不同的文件）
            if (!PlaylistLibrary.Exists("测试歌单".ToUpperInvariant()))
            {
                Log(17, "歌单库检查：按名字找歌单时没有忽略大小写");
                return false;
            }

            // ---- 文件已经不在了：必须计数上报，而不是像"加载播放列表"那样悄悄丢掉 ----
            PlaylistFile.Save(
                Path.Combine(PlaylistLibrary.EnsureDirectory(), "缺了几首.m3u8"),
                new List<PlaylistItem>
                {
                    new PlaylistItem(media[0]),
                    new PlaylistItem(Path.Combine(root, "早就删了.mp3")),
                    new PlaylistItem(media[1])
                });

            var partial = PlaylistLibrary.Load("缺了几首");

            if (!partial.Ok || partial.Tracks.Count != 2 || partial.Total != 3 || partial.Missing != 1)
            {
                Log(17, "歌单库检查：文件不在的条目没有被单独计数（"
                        + $"可用 {partial.Tracks.Count}、总计 {partial.Total}、缺 {partial.Missing}）");
                return false;
            }

            // ---- 自然排序：库里真的按数值排 ----
            PlaylistLibrary.Save("歌单2", items.Take(1).ToList());
            PlaylistLibrary.Save("歌单10", items.Take(2).ToList());

            var names = PlaylistLibrary.List().Select(p => p.Name).ToList();
            var two = names.IndexOf("歌单2");
            var ten = names.IndexOf("歌单10");

            if (two < 0 || ten < 0 || two > ten)
            {
                Log(17, $"歌单库检查：列出来的顺序不是自然顺序（{string.Join(" / ", names)}）");
                return false;
            }

            // ---- 重命名 ----
            if (!PlaylistLibrary.Rename("测试歌单", "改名之后", out var actual, out var error) || actual != "改名之后")
            {
                Log(17, $"歌单库检查：改名失败（{error}）");
                return false;
            }

            if (PlaylistLibrary.Exists("测试歌单") || !PlaylistLibrary.Exists("改名之后"))
            {
                Log(17, "歌单库检查：改名之后旧名字还在或新名字找不到");
                return false;
            }

            if (PlaylistLibrary.Load("改名之后").Tracks.Count != media.Count)
            {
                Log(17, "歌单库检查：改名把内容弄丢了");
                return false;
            }

            // 改成已有的名字 / 非法名字都要被挡住
            if (PlaylistLibrary.Rename("改名之后", "歌单2", out _, out var clash) || clash.Length == 0)
            {
                Log(17, "歌单库检查：改成已有的名字没有被挡下");
                return false;
            }

            if (PlaylistLibrary.Rename("改名之后", "a/b", out _, out _))
            {
                Log(17, "歌单库检查：改名时非法字符没有被挡下");
                return false;
            }

            // 只改大小写也是改名：文件系统上本来就是同一个文件，
            // 不能报"已经有一个叫 MIX 的歌单了"
            PlaylistLibrary.Save("Mix", items.Take(1).ToList());

            if (!PlaylistLibrary.Rename("Mix", "MIX", out var upper, out error) || upper != "MIX")
            {
                Log(17, $"歌单库检查：只改大小写的改名失败了（{error}）");
                return false;
            }

            if (!PlaylistLibrary.Exists("MIX") ||
                PlaylistLibrary.List().Count(p => string.Equals(p.Name, "MIX", StringComparison.OrdinalIgnoreCase)) != 1)
            {
                Log(17, "歌单库检查：只改大小写的改名把歌单弄丢了，或者弄出了两份");
                return false;
            }

            PlaylistLibrary.Delete("MIX", out _);

            // ---- 导入：外面的 m3u 往往是相对路径，拷进来必须还能用 ----
            var external = Path.Combine(root, "外部歌单.m3u8");

            // 故意存成相对路径（媒体就在同一个目录里，PlaylistFile 会写相对路径）
            PlaylistFile.Save(external, items);

            var rawText = File.ReadAllText(external);
            if (!rawText.Contains("track1.mp3", StringComparison.OrdinalIgnoreCase))
            {
                Log(17, "歌单库检查：外部歌单没有写成相对路径，这一段验不到东西");
                return false;
            }

            if (!PlaylistLibrary.Import(external, false, out var imported, out error) || imported != "外部歌单")
            {
                Log(17, $"歌单库检查：导入失败（{error}）");
                return false;
            }

            var importedTracks = PlaylistLibrary.Load("外部歌单");

            if (!importedTracks.Ok || importedTracks.Tracks.Count != media.Count || importedTracks.Missing != 0)
            {
                Log(17, "歌单库检查：相对路径的歌单导入之后找不到文件了（"
                        + $"可用 {importedTracks.Tracks.Count}、缺 {importedTracks.Missing}）");
                return false;
            }

            // 同一个名字再导入一次：不带覆盖要失败并说明
            if (PlaylistLibrary.Import(external, false, out _, out var again) || again.Length == 0)
            {
                Log(17, "歌单库检查：同名导入没有被挡下");
                return false;
            }

            // 覆盖导入可以
            if (!PlaylistLibrary.Import(external, true, out _, out error))
            {
                Log(17, $"歌单库检查：覆盖导入失败（{error}）");
                return false;
            }

            // 不是歌单的文件 / 不存在的文件都要被挡住
            var notPlaylist = Path.Combine(root, "track1.mp3");

            if (PlaylistLibrary.Import(notPlaylist, false, out _, out _) ||
                PlaylistLibrary.Import(Path.Combine(root, "根本没有.m3u8"), false, out _, out _))
            {
                Log(17, "歌单库检查：非歌单文件或不存在的东西被导入成功了");
                return false;
            }

            // ---- 新建空歌单 ----
            var fresh = PlaylistLibrary.Create("新建的空歌单");

            if (!File.Exists(fresh.FilePath) || !PlaylistLibrary.Exists("新建的空歌单"))
            {
                Log(17, "歌单库检查：新建空歌单之后磁盘上没有这个文件");
                return false;
            }

            if (fresh.TrackCount != 0 || PlaylistLibrary.Load("新建的空歌单").Ok)
            {
                Log(17, "歌单库检查：新建出来的歌单不是空的");
                return false;
            }

            if (PlaylistLibrary.List().First(p => p.Name == "新建的空歌单").TrackCount != 0)
            {
                Log(17, "歌单库检查：空歌单在列表里的曲目数不是 0");
                return false;
            }

            // 同名新建要抛错（绝不覆盖别人的歌单）
            try
            {
                PlaylistLibrary.Create("新建的空歌单");
                Log(17, "歌单库检查：同名新建没有报错，把已有歌单覆盖了");
                return false;
            }
            catch (InvalidOperationException)
            {
                // 正是期望的
            }

            if (PlaylistLibrary.Delete("新建的空歌单", out error) == false)
            {
                Log(17, $"歌单库检查：空歌单删不掉（{error}）");
                return false;
            }

            // ---- 删除 ----
            if (!PlaylistLibrary.Delete("改名之后", out error))
            {
                Log(17, $"歌单库检查：删除失败（{error}）");
                return false;
            }

            if (PlaylistLibrary.Exists("改名之后"))
            {
                Log(17, "歌单库检查：删除之后歌单还在");
                return false;
            }

            // 删歌单不许动媒体文件
            if (!File.Exists(media[0]))
            {
                Log(17, "歌单库检查：删歌单把媒体文件也删了");
                return false;
            }

            // 留一份给后面两步用（主窗体与歌单窗口都要有歌单可看）
            PlaylistLibrary.Save("给主窗体看的歌单", items);

            Log(17, $"歌单库文件正常：存在 {expectedFolder} 下的 m3u8 文件里，"
                    + "顺序原样保留、缺文件的条目单独计数（缺 1 项）、新建（同名不覆盖）/ 改名 / 删除 / "
                    + "导入（相对路径也认得）都对，媒体文件毫发无损");
            return true;
        }
    }
}
