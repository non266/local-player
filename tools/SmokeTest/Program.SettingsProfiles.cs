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

        /// <summary>主窗体上的联动：当前方案、改动自动写回、只勾一组、改名停用删除。</summary>
        private static bool CheckSettingsProfilesInMainForm()
        {
            // 方案里存着的音量（关窗那一刻的值），第二个窗体拿它验"只勾一组"
            var profileVolume = 0;

            using (var form = new 播放器.MainForm(Array.Empty<string>()))
            {
                form.Show();
                PumpMessages(250);

                var fileMenu = form.MenuFile;
                var settingsMenu = form.MenuFileSettings;

                if (fileMenu == null || settingsMenu == null)
                {
                    Log(20, "设置方案检查：文件菜单里没有「设置方案」");
                    return false;
                }

                var topLevel = fileMenu.DropDownItems.OfType<ToolStripMenuItem>()
                    .Select(i => i.Text).ToList();

                if (!topLevel.Any(t => t.Contains("设置方案(&G)", StringComparison.Ordinal)))
                {
                    Log(20, $"设置方案检查：文件菜单里没有「设置方案(&G)」（{string.Join(" / ", topLevel)}）");
                    return false;
                }

                form.RebuildSettingsProfileMenu(settingsMenu.DropDownItems);

                var texts = settingsMenu.DropDownItems.OfType<ToolStripMenuItem>()
                    .Select(i => i.Text).ToList();

                if (!texts.Any(t => t.Contains("保存当前设置为方案", StringComparison.Ordinal)) ||
                    !texts.Any(t => t.Contains("从文件导入方案", StringComparison.Ordinal)) ||
                    !texts.Any(t => t.Contains("导出当前设置到文件", StringComparison.Ordinal)) ||
                    !texts.Any(t => t.Contains("打开方案文件夹", StringComparison.Ordinal)))
                {
                    Log(20, $"设置方案检查：菜单里的几项不全（{string.Join(" / ", texts)}）");
                    return false;
                }

                // ---- 存一份 → 成为当前方案 → 菜单里标出来 ----
                if (!form.SaveCurrentSettingsToProfile("窗体方案", "已保存方案"))
                {
                    Log(20, "设置方案检查：保存当前设置为方案失败");
                    return false;
                }

                if (form.CurrentSettingsProfileName != "窗体方案" || !SettingsLibrary.Exists("窗体方案"))
                {
                    Log(20, $"设置方案检查：保存之后没有成为当前方案（「{form.CurrentSettingsProfileName}」）");
                    return false;
                }

                form.RebuildSettingsProfileMenu(settingsMenu.DropDownItems);

                var entries = settingsMenu.DropDownItems.OfType<ToolStripMenuItem>().ToList();
                var row = entries.FirstOrDefault(i => i.Text.StartsWith("窗体方案（", StringComparison.Ordinal));

                if (row == null || !row.Checked)
                {
                    Log(20, $"设置方案检查：菜单里没有把当前方案标出来（{string.Join(" / ", entries.Select(i => i.Text))}）");
                    return false;
                }

                if (!entries.Any(i => i.Text.Contains("当前方案：「窗体方案」", StringComparison.Ordinal)) ||
                    !entries.Any(i => i.Text.Contains("停用当前方案", StringComparison.Ordinal)))
                {
                    Log(20, $"设置方案检查：当前方案那几项不全（{string.Join(" / ", entries.Select(i => i.Text))}）");
                    return false;
                }

                // ---- 改动要自动写回：设置是退出时落盘的，方案文件跟着一起写 ----
                // 音量走界面那条路（关窗时以滑块为准，直接改设置对象会被它盖掉），
                // 主题不看界面，直接改设置对象。
                var savedVolume = SettingsOf(form).Volume;

                if (!SendShortcut(form, Keys.Up) || SettingsOf(form).Volume == savedVolume)
                {
                    Log(20, "设置方案检查：按音量加键没有改到设置里的音量");
                    return false;
                }

                var changedVolume = SettingsOf(form).Volume;

                SettingsOf(form).Theme = ThemeId.Vlc;
                PumpMessages(150);

                form.Close();
                PumpMessages(250);

                if (!SettingsLibrary.TryLoad("窗体方案", out var writtenBack, out var writeError))
                {
                    Log(20, "设置方案检查：关掉之后读不回方案文件：" + writeError);
                    return false;
                }

                if (writtenBack.Volume != changedVolume || writtenBack.Theme != ThemeId.Vlc)
                {
                    Log(20, "设置方案检查：改动没有自动写回方案文件（音量 "
                            + $"{writtenBack.Volume}，期望 {changedVolume}；主题 {writtenBack.Theme}，期望 {ThemeId.Vlc}）");
                    return false;
                }

                profileVolume = writtenBack.Volume;
            }

            using (var second = new 播放器.MainForm(Array.Empty<string>()))
            {
                second.Show();
                PumpMessages(300);

                // ---- 整份载入：方案里的值都该跟着来（先做这一条，趁方案文件还是刚存下的样子）----
                SettingsOf(second).Volume = 3;
                SettingsOf(second).Theme = ThemeId.Light;
                PumpMessages(150);

                if (!second.LoadSettingsProfile("窗体方案", SettingsProfile.All))
                {
                    Log(20, "设置方案检查：整份载入失败");
                    return false;
                }

                PumpMessages(200);

                if (SettingsOf(second).Volume != profileVolume || SettingsOf(second).Theme != ThemeId.Vlc)
                {
                    Log(20, "设置方案检查：整份载入没有把方案里的值带过来（音量 "
                            + $"{SettingsOf(second).Volume}，期望 {profileVolume}；"
                            + $"主题 {SettingsOf(second).Theme}，期望 {ThemeId.Vlc}）");
                    return false;
                }

                // ---- 只勾一组：没勾的那些原样不动 ----
                SettingsOf(second).Volume = 4;
                SettingsOf(second).Theme = ThemeId.Oled;
                PumpMessages(150);

                if (!second.LoadSettingsProfile("窗体方案", new[] { SettingsSection.Playback }))
                {
                    Log(20, "设置方案检查：只载入「播放」那一组失败");
                    return false;
                }

                PumpMessages(200);

                if (SettingsOf(second).Volume != profileVolume)
                {
                    Log(20, "设置方案检查：勾了「播放」却没把音量带过来（"
                            + $"{SettingsOf(second).Volume}，期望方案里的 {profileVolume}）");
                    return false;
                }

                if (SettingsOf(second).Theme != ThemeId.Oled)
                {
                    Log(20, $"设置方案检查：只勾了「播放」，主题却被一起改了（{SettingsOf(second).Theme}）");
                    return false;
                }

                if (second.CurrentSettingsProfileName != "窗体方案")
                {
                    Log(20, "设置方案检查：载入之后没有把「当前方案」接上");
                    return false;
                }

                // ---- 改名：当前方案的关联跟着走 ----
                if (!second.RenameCurrentSettingsProfile("窗体方案改") ||
                    second.CurrentSettingsProfileName != "窗体方案改")
                {
                    Log(20, $"设置方案检查：重命名之后关联没跟着走（「{second.CurrentSettingsProfileName}」）");
                    return false;
                }

                // ---- 停用：设置留着，方案文件也留着 ----
                second.DeactivateSettingsProfile();

                if (second.CurrentSettingsProfileName.Length != 0)
                {
                    Log(20, "设置方案检查：停用之后还挂着当前方案");
                    return false;
                }

                if (!SettingsLibrary.Exists("窗体方案改"))
                {
                    Log(20, "设置方案检查：停用方案把方案文件删掉了（不该动它）");
                    return false;
                }

                // ---- 删除当前方案：设置还在，只是不再对应方案 ----
                if (!second.LoadSettingsProfile("窗体方案改", SettingsProfile.All))
                {
                    Log(20, "设置方案检查：重新载入方案失败");
                    return false;
                }

                if (!second.DeleteSettingsProfile("窗体方案改", confirm: false))
                {
                    Log(20, "设置方案检查：删除当前方案失败");
                    return false;
                }

                if (second.CurrentSettingsProfileName.Length != 0 || SettingsLibrary.Exists("窗体方案改"))
                {
                    Log(20, "设置方案检查：删掉当前方案之后关联还挂着");
                    return false;
                }

                // ---- 导出（不弹对话框那条路）----
                var exported = Path.Combine(AppContext.BaseDirectory, "smoke-profiles", "导出设置.json");

                if (!second.ExportSettingsToFile(exported) || !File.Exists(exported))
                {
                    Log(20, "设置方案检查：导出当前设置失败");
                    return false;
                }

                if (AppSettings.LoadFrom(exported).Volume != SettingsOf(second).Volume)
                {
                    Log(20, "设置方案检查：导出的内容和当前设置对不上");
                    return false;
                }

                second.Close();
                PumpMessages(150);
            }

            Log(20, "设置方案与主窗体联动正常：文件菜单里有「设置方案」，保存之后成为当前方案并在菜单里标出来，"
                    + "改动随设置一起自动写回方案文件（关窗之后文件里就是新值），"
                    + "载入时可以只勾几组（没勾的保持原样），改名 / 停用 / 删除之后关联都跟着走、"
                    + "方案文件该留的留着");
            return true;
        }

        /// <summary>勾选窗口：一次列出全部分组、默认全勾、写得清"与当前不同"、控件不越界。</summary>
        private static bool CheckSettingsLoadDialog()
        {
            var current = new AppSettings { Volume = 10, Theme = ThemeId.Light };
            var incoming = new AppSettings { Volume = 90, Theme = ThemeId.Dark, Deinterlace = "bob" };

            using var dialog = new SettingsLoadDialog(current, incoming, "外观方案");

            dialog.StartPosition = FormStartPosition.Manual;
            dialog.Location = new Point(40, 40);
            dialog.Show();
            PumpMessages(200);

            var list = dialog.Controls.OfType<ListView>().FirstOrDefault();

            if (list == null || !list.CheckBoxes || list.Items.Count != SettingsProfile.All.Count)
            {
                Log(20, "载入对话框：分组清单不对（"
                        + (list == null ? "找不到清单" : $"{list.Items.Count} 行、勾选框 {list.CheckBoxes}")
                        + "）");
                return false;
            }

            if (dialog.SelectedSections.Count != SettingsProfile.All.Count || !dialog.CanLoad)
            {
                Log(20, $"载入对话框：默认没有全勾上（勾了 {dialog.SelectedSections.Count} 组）");
                return false;
            }

            // 「与当前不同」那一列：播放组只差音量一项（主题不在这一组里）
            var playbackRow = list.Items.Cast<ListViewItem>()
                .FirstOrDefault(i => i.Text == SettingsProfile.Name(SettingsSection.Playback));

            if (playbackRow == null || playbackRow.SubItems[2].Text != "1 项")
            {
                Log(20, $"载入对话框：「播放」那一组的差异数不对（「{playbackRow?.SubItems[2].Text}」）");
                return false;
            }

            var sessionRow = list.Items.Cast<ListViewItem>()
                .FirstOrDefault(i => i.Text == SettingsProfile.Name(SettingsSection.Session));

            if (sessionRow == null || sessionRow.SubItems[2].Text != "没有区别")
            {
                Log(20, $"载入对话框：「会话」两边一样却报了差异（「{sessionRow?.SubItems[2].Text}」）");
                return false;
            }

            // 控件不许越界（高 DPI 下最容易出问题）
            var scale = dialog.DeviceDpi / 96f;

            foreach (Control control in dialog.Controls)
            {
                if (control.Right <= dialog.ClientSize.Width && control.Bottom <= dialog.ClientSize.Height)
                    continue;

                Log(20, $"载入对话框在 {scale:0.##} 倍缩放下有控件越界："
                        + $"{control.GetType().Name} {control.Bounds} 超出客户区 {dialog.ClientSize}");
                return false;
            }

            // 取消一组：结果里就没有它了
            dialog.SetChecked(SettingsSection.Playback, false);

            if (dialog.SelectedSections.Count != SettingsProfile.All.Count - 1 ||
                dialog.SelectedSections.Contains(SettingsSection.Playback))
            {
                Log(20, "载入对话框：取消一组之后交回去的结果不对");
                return false;
            }

            // 一组都不勾：载入按钮点不动，硬点也不生效
            dialog.SetAllChecked(false);

            if (dialog.CanLoad || dialog.SelectedSections.Count != 0)
            {
                Log(20, "载入对话框：一组都没勾时「载入」还是亮的");
                return false;
            }

            dialog.LoadSelected();

            if (dialog.DialogResult == DialogResult.OK)
            {
                Log(20, "载入对话框：一组都没勾，点「载入」居然生效了");
                return false;
            }

            // 全勾上再点：正常关掉并交出全部分组
            // （注意：非模态 Show 出来的窗口，Close() 会把它释放掉，
            //  所以"交回了什么"要在点之前看，点之后只能看 DialogResult）
            dialog.SetAllChecked(true);

            if (!dialog.CanLoad || dialog.SelectedSections.Count != SettingsProfile.All.Count)
            {
                Log(20, $"载入对话框：全勾上之后状态不对（勾了 {dialog.SelectedSections.Count} 组、"
                        + $"「载入」{(dialog.CanLoad ? "能点" : "是灰的")}）");
                return false;
            }

            dialog.LoadSelected();

            if (dialog.DialogResult != DialogResult.OK)
            {
                Log(20, "载入对话框：全勾上之后点「载入」没有生效");
                return false;
            }

            Log(20, $"载入对话框正常：一次列出全部 {SettingsProfile.All.Count} 组、默认全勾，"
                    + "每行写明「与当前不同」的项数（没区别的写「没有区别」），"
                    + "一组都不勾时「载入」点不动且硬点无效，DPI 缩放下控件没有越界");
            return true;
        }
    }
}
