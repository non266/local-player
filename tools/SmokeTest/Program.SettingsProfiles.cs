// 检查点 20：设置方案（多套设置：存几套、切过去、只拿其中几组）。

using System.Text.Json.Nodes;

namespace SmokeTest
{
    internal static partial class Program
    {
        // -----------------------------------------------------------------
        // 20) 设置方案（多套设置）
        //
        // 这一项验三件事：
        //   1. 每一项设置都恰好属于一组（漏了 → 载入方案时那一项永远过不来；重复 → 勾一组会连带改别的组）；
        //   2. 方案库在磁盘上是什么样（存 / 列 / 读回来一模一样、改名、删除、导入导出、坏文件要挡住）；
        //   3. 主窗体上的联动（当前方案、改动自动写回、只勾一组时别的组不动、改名停用删除时关联跟着走）。
        // -----------------------------------------------------------------

        private static bool TestSettingsProfiles()
        {
            var root = Path.Combine(AppContext.BaseDirectory, "smoke-profiles");
            TryDeleteDirectory(root);
            Directory.CreateDirectory(root);

            try
            {
                if (!CheckSettingsProfileSections()) return false;
                if (!CheckSettingsProfileLibrary(root)) return false;
                if (!CheckSettingsProfilesInMainForm()) return false;
                if (!CheckSettingsLoadDialog()) return false;
            }
            finally
            {
                TryDeleteDirectory(root);
            }

            Log(20, "设置方案正常：设置的每一项都恰好归进一组（漏一个或重一个都会红），"
                    + "一份方案就是一个 json 文件（存下来再读回来一模一样），"
                    + "改名 / 删除 / 导入 / 导出都在、坏文件会被挡住并如实标出来，"
                    + "载入时可以只勾几组（没勾的原样不动），当前方案会自动写回、"
                    + "改名 / 停用 / 删除之后关联都跟着走，勾选窗口默认全勾、一组都不勾就点不动");
            return true;
        }

        /// <summary>
        /// 一份设置"规范化之后再序列化"的样子。
        /// <para>
        /// 存盘、读回、合并都会走一遍规范化（补协议头、去尾斜杠、把可空字段补回默认值……），
        /// 所以"存下来再读回来一模一样"要跟这个比，而不是跟用户手上那份对象的原始 JSON 比。
        /// </para>
        /// </summary>
        private static string Canonical(AppSettings settings) =>
            AppSettings.FromJson(settings.ToJson()).ToJson();

        /// <summary>两份设置 JSON 的第一处差别（只报一处，够定位就行）；一样时返回 <c>null</c>。</summary>
        private static string? FirstSettingsDifference(string left, string right)
        {
            var a = JsonNode.Parse(left)?.AsObject();
            var b = JsonNode.Parse(right)?.AsObject();

            if (a == null || b == null) return "有一边解析不出 JSON";

            foreach (var key in a.Select(pair => pair.Key)
                         .Concat(b.Select(pair => pair.Key))
                         .Distinct(StringComparer.Ordinal))
            {
                var mine = a.TryGetPropertyValue(key, out var leftValue) ? leftValue?.ToJsonString() : "（没有这一项）";
                var theirs = b.TryGetPropertyValue(key, out var rightValue) ? rightValue?.ToJsonString() : "（没有这一项）";

                if (!string.Equals(mine, theirs, StringComparison.Ordinal))
                    return $"{key}：{mine} ↔ {theirs}";
            }

            return null;
        }

        /// <summary>分组清单本身：覆盖全、不重复，以及"按组合并"的语义。</summary>
        private static bool CheckSettingsProfileSections()
        {
            var uncovered = SettingsProfile.UncoveredProperties();

            if (uncovered.Count > 0)
            {
                Log(20, "设置分组：有设置项没被归进任何一组（" + string.Join(" / ", uncovered) + "）");
                return false;
            }

            var unknown = SettingsProfile.UnknownProperties();

            if (unknown.Count > 0)
            {
                Log(20, "设置分组：组里写了设置中根本没有的名字（" + string.Join(" / ", unknown) + "）");
                return false;
            }

            var owner = new Dictionary<string, SettingsSection>(StringComparer.Ordinal);

            foreach (var section in SettingsProfile.All)
            {
                var properties = SettingsProfile.Properties(section);

                if (properties.Count == 0)
                {
                    Log(20, $"设置分组：「{SettingsProfile.Name(section)}」一项都没有");
                    return false;
                }

                foreach (var name in properties)
                {
                    if (owner.TryGetValue(name, out var first))
                    {
                        Log(20, $"{name} 同时属于「{SettingsProfile.Name(first)}」和"
                                + $"「{SettingsProfile.Name(section)}」——勾一组会连带改另一组");
                        return false;
                    }

                    owner[name] = section;
                }
            }

            // ---- 只勾一组时，别的组必须原样不动 ----
            var current = new AppSettings
            {
                Volume = 10,
                Rate = 0.5f,
                Muted = true,
                AspectRatio = "4:3",
                Theme = ThemeId.Light,
                Deinterlace = "bob"
            };

            var incoming = new AppSettings
            {
                Volume = 90,
                Rate = 2.0f,
                Muted = false,
                AspectRatio = "16:9",
                Theme = ThemeId.Dark,
                Deinterlace = "yadif"
            };

            var playbackOnly = SettingsProfile.Merge(current, incoming, new[] { SettingsSection.Playback });

            if (playbackOnly.Volume != 90 || playbackOnly.Rate != 2.0f ||
                playbackOnly.Muted || playbackOnly.AspectRatio != "16:9")
            {
                Log(20, "设置合并：勾了「播放」却没把这一组拿过来（音量 "
                        + $"{playbackOnly.Volume}、速度 {playbackOnly.Rate}、静音 {playbackOnly.Muted}、"
                        + $"画面比例「{playbackOnly.AspectRatio}」）");
                return false;
            }

            if (playbackOnly.Theme != ThemeId.Light || playbackOnly.Deinterlace != "bob")
            {
                Log(20, "设置合并：只勾了「播放」，界面与音视频输出却被一起改了");
                return false;
            }

            // ---- 一组都不勾 = 什么都不变；全勾 = 就是方案里那一份 ----
            var nothingChanged = FirstSettingsDifference(
                SettingsProfile.Merge(current, incoming, Array.Empty<SettingsSection>()).ToJson(), Canonical(current));

            if (nothingChanged != null)
            {
                Log(20, "设置合并：一组都没勾，设置却变了（" + nothingChanged + "）");
                return false;
            }

            var allMerged = FirstSettingsDifference(
                SettingsProfile.Merge(current, incoming, SettingsProfile.All).ToJson(), Canonical(incoming));

            if (allMerged != null)
            {
                Log(20, "设置合并：整份载入之后和方案文件不一致（" + allMerged + "）");
                return false;
            }

            // ---- "与当前不同"的项数（勾选框上就写它，数错了等于骗人）----
            var playbackDiff = SettingsProfile.CountDifferences(current, incoming, SettingsSection.Playback);

            if (playbackDiff != 4)
            {
                Log(20, $"设置合并：「播放」与当前不同的项数不对（{playbackDiff}，期望 4）");
                return false;
            }

            var outputDiff = SettingsProfile.CountDifferences(current, incoming, SettingsSection.Output);

            if (outputDiff != 1)
            {
                Log(20, $"设置合并：「音视频输出」与当前不同的项数不对（{outputDiff}，期望 1）");
                return false;
            }

            if (SettingsProfile.CountDifferences(current, incoming, SettingsSection.Session) != 0)
            {
                Log(20, "设置合并：「会话」两边一模一样，却报出有区别");
                return false;
            }

            Log(20, $"设置分组正常：{SettingsProfile.All.Count} 组一共 {owner.Count} 项设置各有归属、"
                    + "不重不漏，只勾一组时别的组原样不动、一组不勾什么都不变、整份载入与方案文件一致，"
                    + "「与当前不同」的项数也数得对");
            return true;
        }

        /// <summary>方案库在磁盘上的那条真相：名字、存读往返、改名、删除、导入导出、坏文件。</summary>
        private static bool CheckSettingsProfileLibrary(string root)
        {
            // ---- 名字规则（和歌单共用一套）----
            if (SettingsLibrary.TryNormalizeName("有/斜杠", out _, out var slashError) ||
                slashError.IndexOf('/') < 0)
            {
                Log(20, $"方案名规则：带斜杠的名字没被挡住或没说清是哪个字符（「{slashError}」）");
                return false;
            }

            if (SettingsLibrary.TryNormalizeName("CON", out _, out _))
            {
                Log(20, "方案名规则：Windows 保留名 CON 被放行了");
                return false;
            }

            if (!SettingsLibrary.TryNormalizeName("工作 用的...", out var trimmed, out _) || trimmed != "工作 用的")
            {
                Log(20, $"方案名规则：结尾的点号和空格没被收拾干净（「{trimmed}」）");
                return false;
            }

            // ---- 存下来 → 列出来 → 读回来必须一模一样 ----
            var source = new AppSettings
            {
                Volume = 33,
                Theme = ThemeId.Oled,
                AspectRatio = "16:10",
                Deinterlace = "yadif2x"
            };

            source.LrcApiBaseUrl = "https://lrc.example.com/";
            source.DesktopLyricsFontSize = 42;
            source.LastPlaylist.Add("x.mp3");

            var saved = SettingsLibrary.Save("工作", source);

            if (saved.Name != "工作" || !SettingsLibrary.Exists("工作"))
            {
                Log(20, "方案库：保存之后磁盘上没有这份方案");
                return false;
            }

            if (!SettingsLibrary.TryLoad("工作", out var loaded, out var loadError))
            {
                Log(20, "方案库：读不回来：" + loadError);
                return false;
            }

            var lost = FirstSettingsDifference(loaded.ToJson(), Canonical(source));

            if (lost != null)
            {
                Log(20, "方案库：存下来再读回来，设置和原来对不上（" + lost + "）");
                return false;
            }

            // 地址落盘时会规范化（去掉结尾的斜杠），所以比的是规范化之后的样子
            if (loaded.LrcApiBaseUrl != "https://lrc.example.com" || loaded.DesktopLyricsFontSize != 42)
            {
                Log(20, $"方案库：读回来的个别字段不对（地址「{loaded.LrcApiBaseUrl}」、"
                        + $"桌面歌词字号 {loaded.DesktopLyricsFontSize}）");
                return false;
            }

            // ---- 列出来按自然顺序：方案2 在 方案10 前面 ----
            SettingsLibrary.Save("方案10", new AppSettings());
            SettingsLibrary.Save("方案2", new AppSettings());

            var names = SettingsLibrary.List().Select(p => p.Name).ToList();
            var index2 = names.IndexOf("方案2");
            var index10 = names.IndexOf("方案10");

            if (index2 < 0 || index10 < 0 || index2 > index10)
            {
                Log(20, $"方案库：列出来的顺序不是自然顺序（{string.Join(" / ", names)}）");
                return false;
            }

            // ---- 改名：普通改名，以及只改大小写（文件系统上本来就是同一个文件）----
            if (!SettingsLibrary.Rename("方案2", "方案二", out var renamed, out var renameError) ||
                renamed != "方案二" || SettingsLibrary.Exists("方案2"))
            {
                Log(20, "方案库：改名失败或旧名字还在：" + renameError);
                return false;
            }

            SettingsLibrary.Save("mix", new AppSettings());

            if (!SettingsLibrary.Rename("mix", "MIX", out var cased, out var caseError) || cased != "MIX")
            {
                Log(20, "方案库：只改大小写的改名被拒了：" + caseError);
                return false;
            }

            // ---- 导入：收进方案库，源文件不动；坏文件必须被挡住 ----
            var external = Path.Combine(root, "外部设置.json");
            File.WriteAllText(external, new AppSettings { Volume = 71 }.ToJson());

            if (!SettingsLibrary.Import(external, false, out var importedName, out var importError) ||
                importedName != "外部设置")
            {
                Log(20, "方案库：导入失败：" + importError);
                return false;
            }

            if (!File.Exists(external))
            {
                Log(20, "方案库：导入把源文件弄丢了");
                return false;
            }

            if (!SettingsLibrary.TryLoad("外部设置", out var imported, out _) || imported.Volume != 71)
            {
                Log(20, "方案库：导入进来的方案内容不对");
                return false;
            }

            var garbage = Path.Combine(root, "坏文件.json");
            File.WriteAllText(garbage, "{ 这不是一份设置 }");

            if (SettingsLibrary.Import(garbage, false, out _, out var garbageError) || garbageError.Length == 0)
            {
                Log(20, "方案库：坏文件被当成方案收进来了");
                return false;
            }

            // ---- 库里本来就有个坏文件：要列出来（用户得看得到才好删），读的时候要说明白 ----
            File.Copy(garbage, Path.Combine(SettingsLibrary.Directory, "坏方案.json"), true);

            var broken = SettingsLibrary.List().FirstOrDefault(p => p.Name == "坏方案");

            if (broken == null || broken.Readable)
            {
                Log(20, "方案库：坏文件没有被标成「读不出来」");
                return false;
            }

            if (SettingsLibrary.TryLoad("坏方案", out _, out var brokenError) || brokenError.Length == 0)
            {
                Log(20, "方案库：坏文件居然被当成一份有效设置读了回来");
                return false;
            }

            if (SettingsLibrary.Describe(broken).IndexOf("读不出来", StringComparison.Ordinal) < 0)
            {
                Log(20, $"方案库：坏文件的描述里没写清楚（「{SettingsLibrary.Describe(broken)}」）");
                return false;
            }

            // ---- 导出：写到指定文件，内容与当前设置一致 ----
            var exported = Path.Combine(root, "导出.json");

            if (!SettingsLibrary.ExportTo(source, exported, out var exportError) || !File.Exists(exported))
            {
                Log(20, "方案库：导出失败：" + exportError);
                return false;
            }

            var exportedDifference = FirstSettingsDifference(
                AppSettings.LoadFrom(exported).ToJson(), Canonical(source));

            if (exportedDifference != null)
            {
                Log(20, "方案库：导出的文件内容和当前设置对不上（" + exportedDifference + "）");
                return false;
            }

            // ---- 删除：只删这一个文件 ----
            if (!SettingsLibrary.Delete("方案二", out var deleteError) || SettingsLibrary.Exists("方案二"))
            {
                Log(20, "方案库：删除失败：" + deleteError);
                return false;
            }

            Log(20, "方案库文件正常：一个方案一个 json 文件（存在 数据目录\\设置方案 下），"
                    + "存下来再读回来一模一样、列表按自然顺序（方案2 在 方案10 前面）、"
                    + "改名（含只改大小写）/ 删除 / 导入（源文件不动）/ 导出都在，"
                    + "坏文件不会被收进来、库里已有的坏文件会被标成「读不出来」");
            return true;
        }


    }
}
