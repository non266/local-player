using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using 播放器.Ui;

namespace 播放器.Core
{
    /// <summary>
    /// 持久化的用户设置，保存在 <c>%AppData%\播放器\settings.json</c>。
    /// </summary>
    public sealed class AppSettings
    {
        private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Converters = { new JsonStringEnumConverter() }
        };

        // ---- 播放相关 ---------------------------------------------------------

        /// <summary>音量 0~100。</summary>
        public int Volume { get; set; } = 80;

        /// <summary>是否静音。</summary>
        public bool Muted { get; set; }

        /// <summary>播放速率。</summary>
        public float Rate { get; set; } = 1.0f;

        /// <summary>循环模式。</summary>
        public RepeatMode RepeatMode { get; set; } = RepeatMode.None;

        /// <summary>是否随机播放。</summary>
        public bool Shuffle { get; set; }

        /// <summary>画面宽高比；空表示默认。</summary>
        public string AspectRatio { get; set; } = string.Empty;

        // ---- 界面相关 ---------------------------------------------------------

        /// <summary>界面主题。</summary>
        public ThemeId Theme { get; set; } = ThemeId.Auto;

        /// <summary>是否记住每个文件的播放进度，下次打开自动续播。</summary>
        public bool ResumePlayback { get; set; } = true;

        /// <summary>
        /// 启动时要不要把上次退出时的播放列表恢复出来。<b>默认关：打开程序就是一张空列表。</b>
        /// <para>
        /// 关掉时这一次启动算<b>全新的一次</b>：列表、当前项、以及"当前歌单"那层关联都不恢复
        /// （见 <see cref="ForgetSession"/>）。<b>歌单文件一个字节都不动</b>——
        /// 想接着听上次那份，在「文件 → 播放列表」里点一下就能载入回来。
        /// </para>
        /// <para>
        /// 关掉<b>不影响退出时的记录</b>：上次的列表照样写进设置。这样用户把开关打开之后，
        /// 拿到的就是"上一次关闭时的那份列表"，而不是几周前残留的一份旧数据。
        /// </para>
        /// </summary>
        public bool RestoreLastPlaylist { get; set; }

        /// <summary>是否注册全局媒体键（程序在后台也能用 ⏯⏭⏮ 控制）。</summary>
        public bool GlobalMediaKeys { get; set; } = true;

        /// <summary>
        /// 本地找不到歌词 / 封面时，是否去 LrcAPI 上要一份（自动 + 菜单手动都能触发）。
        /// <para>要联网，所以给一个显式开关，默认开。</para>
        /// </summary>
        public bool OnlineLookup { get; set; } = true;

        /// <summary>
        /// LrcAPI 服务地址，**必须手动填**（默认空）。
        /// <para>
        /// 故意不给默认值：这是要联网的第三方服务，让程序自己挑一个地址发出去
        /// 是很不礼貌的事；用户填了哪个才用哪个（自建实例、局域网地址都行）。
        /// 没填时"在线歌词与封面"会提示去菜单里填。
        /// </para>
        /// </summary>
        public string LrcApiBaseUrl { get; set; } = string.Empty;

        /// <summary>
        /// LrcAPI 的鉴权密钥（可选，同样手动填）。
        /// <para>
        /// 服务端开了鉴权就必须填，否则封面接口会返回 401；
        /// 它只作为 <c>Authorization</c> 请求头发给用户自己配的那台服务，
        /// <b>不会</b>跟着封面图片的地址发给第三方主机（见 <c>LrcApi.ShouldSendToken</c>）。
        /// </para>
        /// </summary>
        public string LrcApiToken { get; set; } = string.Empty;

        /// <summary>播放列表的排序依据。</summary>
        public PlaylistSortField SortField { get; set; } = PlaylistSortField.AddedOrder;

        /// <summary>播放列表排序是否降序。</summary>
        public bool SortDescending { get; set; }

        /// <summary>是否显示播放列表面板。</summary>
        public bool ShowPlaylist { get; set; } = true;

        /// <summary>是否显示右侧侧栏（歌词 / 封面 / 媒体信息）。</summary>
        public bool ShowSidebar { get; set; } = true;

        /// <summary>侧栏宽度；0 表示尚未保存过，由程序按默认宽度推算。</summary>
        public int SidebarWidth { get; set; }

        /// <summary>侧栏当前显示的是哪一页。</summary>
        public SidebarTab SidebarPage { get; set; } = SidebarTab.Lyrics;

        /// <summary>侧栏是否已分离为悬浮窗（不再停靠在主窗口里）。</summary>
        public bool SidebarFloating { get; set; }

        /// <summary>播放列表是否已分离为独立窗口。</summary>
        public bool PlaylistFloating { get; set; }

        /// <summary>悬浮侧栏窗口的位置与大小；宽高为 0 表示尚未保存过。</summary>
        public int SidebarWindowX { get; set; }

        public int SidebarWindowY { get; set; }

        public int SidebarWindowWidth { get; set; }

        public int SidebarWindowHeight { get; set; }

        /// <summary>独立播放列表窗口的位置与大小；宽高为 0 表示尚未保存过。</summary>
        public int PlaylistWindowX { get; set; }

        public int PlaylistWindowY { get; set; }

        public int PlaylistWindowWidth { get; set; }

        public int PlaylistWindowHeight { get; set; }

        /// <summary>
        /// 悬浮侧栏吸附在主窗口的哪条边上。
        /// <para>吸附之后窗口位置不再由 X/Y 决定，而是跟着主窗口走，所以要单独记。</para>
        /// </summary>
        public WindowSnapEdge SidebarSnap { get; set; } = WindowSnapEdge.None;

        /// <summary>侧栏吸附时，沿贴合边方向相对主窗口左上角的偏移（像素）。</summary>
        public int SidebarSnapOffset { get; set; }

        /// <summary>独立播放列表窗口吸附在主窗口的哪条边上。</summary>
        public WindowSnapEdge PlaylistSnap { get; set; } = WindowSnapEdge.None;

        /// <summary>播放列表吸附时，沿贴合边方向相对主窗口左上角的偏移（像素）。</summary>
        public int PlaylistSnapOffset { get; set; }

        /// <summary>关闭窗口时是缩到系统托盘，还是直接退出。</summary>
        public bool MinimizeToTray { get; set; }

        // ---- 桌面歌词 ---------------------------------------------------------

        /// <summary>是否显示桌面歌词悬浮窗。</summary>
        public bool DesktopLyricsEnabled { get; set; }

        /// <summary>桌面歌词窗口的位置；<see cref="DesktopLyricsPlaced"/> 为 false 时无效。</summary>
        public int DesktopLyricsX { get; set; }

        public int DesktopLyricsY { get; set; }

        /// <summary>
        /// 是否已经记过桌面歌词的位置。
        /// <para>
        /// 不能拿"X 和 Y 都是 0"当没记过：屏幕左上角是一个完全合理的摆放位置。
        /// </para>
        /// </summary>
        public bool DesktopLyricsPlaced { get; set; }

        /// <summary>桌面歌词主句字号（磅），12~96。</summary>
        public int DesktopLyricsFontSize { get; set; } = DesktopLyricsDefaults.FontSize;

        /// <summary>桌面歌词整窗不透明度（百分比），20~100。</summary>
        public int DesktopLyricsOpacity { get; set; } = DesktopLyricsDefaults.Opacity;

        /// <summary>桌面歌词是否锁定（锁定后对鼠标透明，点得到下面的程序）。</summary>
        public bool DesktopLyricsLocked { get; set; }

        /// <summary>桌面歌词是否显示下一句。</summary>
        public bool DesktopLyricsShowNext { get; set; } = true;

        /// <summary>
        /// 桌面歌词第二行在有翻译时显示翻译（双语歌词：同一时间戳的原文 + 译文）。
        /// <para>
        /// 默认开：双语歌词里"原文 + 译文"才是完整的一句。关掉它，第二行就总是"下一句"
        /// （<see cref="DesktopLyricsShowNext"/> 是第二行的总开关）。
        /// </para>
        /// </summary>
        public bool DesktopLyricsShowTranslation { get; set; } = true;

        /// <summary>
        /// 桌面歌词下面是否显示控制条（播放 / 暂停、上一首、下一首、停止）。
        /// <para>默认开。锁定（鼠标穿透）时控制条一律不显示——那时候它点不到，画出来只会误导。</para>
        /// </summary>
        public bool DesktopLyricsShowControls { get; set; } = true;

        /// <summary>桌面歌词的文字配色。</summary>
        public DesktopLyricsColor DesktopLyricsColor { get; set; } = DesktopLyricsColor.Theme;

        /// <summary>
        /// 歌词字体族；空表示用内置默认字体。
        /// <para>侧栏歌词页与桌面歌词共用这一项——用户心里的"歌词"就是一处设置。</para>
        /// </summary>
        public string LyricsFontFamily { get; set; } = string.Empty;

        // ---- 声音相关 ---------------------------------------------------------

        /// <summary>均衡器设置（开关、前置放大、各频段增益）。</summary>
        public EqualizerState Equalizer { get; set; } = new EqualizerState();

        // ---- 视频相关 ---------------------------------------------------------

        /// <summary>
        /// 去隔行模式；空表示关闭。
        /// <para>可用值：<c>auto</c>、<c>blend</c>、<c>bob</c>、<c>linear</c>、<c>x</c>、
        /// <c>yadif</c>、<c>yadif2x</c>、<c>phosphor</c>、<c>ivtc</c>。</para>
        /// </summary>
        public string Deinterlace { get; set; } = string.Empty;

        /// <summary>选定的音频输出设备标识；空表示系统默认设备。</summary>
        public string AudioDeviceId { get; set; } = string.Empty;

        /// <summary>选定设备所属的 libvlc 输出模块；空表示交给 libvlc 用当前模块。</summary>
        public string AudioDeviceModule { get; set; } = string.Empty;

        /// <summary>窗口是否置顶。</summary>
        public bool AlwaysOnTop { get; set; }

        /// <summary>窗口位置与大小；0 表示尚未保存过。</summary>
        public int WindowX { get; set; }

        public int WindowY { get; set; }

        public int WindowWidth { get; set; }

        public int WindowHeight { get; set; }

        public bool WindowMaximized { get; set; }

        /// <summary>主分隔条的当前位置；0 表示尚未保存过，由程序按默认宽度推算。</summary>
        public int SplitterDistance { get; set; }

        // ---- 会话相关 ---------------------------------------------------------

        /// <summary>上次打开文件所在目录。</summary>
        public string LastDirectory { get; set; } = string.Empty;

        /// <summary>
        /// 是否把诊断日志写进 <c>数据目录\播放器.log</c>（默认开）。
        /// <para>
        /// 关掉之后只保留内存里最近若干条（「帮助 → 查看日志」仍能看），磁盘上不留记录。
        /// 日志里会出现文件路径，所以给一个开关；文件到 1 MB 会自动轮转一次，不会无限长大。
        /// </para>
        /// </summary>
        public bool DiagnosticLog { get; set; } = true;

        /// <summary>
        /// 当前播放列表对应歌单库里的哪一份（歌单名，不含扩展名）。
        /// <para>
        /// 空表示"当前列表还没存成歌单"，或者对应的歌单已经被改名 / 删掉了。
        /// 它只用来显示"当前歌单：XXX"和提供「覆盖保存 / 重新载入」的快捷入口——
        /// 列表内容本身仍然只在用户点保存时才写回歌单（不会自动改用户的歌单）。
        /// </para>
        /// </summary>
        public string CurrentPlaylistName { get; set; } = string.Empty;

        /// <summary>
        /// 当前设置方案的名字（空 = 没在用方案，只有 settings.json 这一份）。
        /// <para>
        /// 用法和 <see cref="CurrentPlaylistName"/> 一样：只记"现在用的是哪一份"，
        /// 载入 / 另存为 / 停用方案时由界面写这里；内容本身在设置方案库
        /// （<c>数据目录\设置方案\名字.json</c>）里。
        /// </para>
        /// </summary>
        public string CurrentSettingsProfileName { get; set; } = string.Empty;

        /// <summary>上次退出时的播放列表。</summary>
        public List<string> LastPlaylist { get; set; } = new List<string>();

        /// <summary>上次退出时选中的条目索引。</summary>
        public int LastPlaylistIndex { get; set; } = -1;

        /// <summary>
        /// 把"上次会话"的那几项状态清成"全新一次运行"：列表、当前项、当前歌单关联。
        /// <para>
        /// 只动<b>内存里这份设置</b>，一个文件都不碰：磁盘上的歌单文件照旧，
        /// 退出时的记录也照旧（<c>SaveSession</c> 会按当时的真实情况重新写）。
        /// </para>
        /// <para>
        /// "当前歌单"这层关联也要清，是因为它单独留着会骗人：一张空列表挂着歌单名，
        /// 往里加两首再点「覆盖保存」，那份歌单就被两首替换掉了。
        /// 清掉之后状态是诚实的——"还没有对应歌单"，替换前该问的都会问。
        /// </para>
        /// </summary>
        public void ForgetSession()
        {
            LastPlaylist = new List<string>();
            LastPlaylistIndex = -1;
            CurrentPlaylistName = string.Empty;
        }

        /// <summary>
        /// 数据目录（设置与播放历史都放这里）。
        /// <para>
        /// 默认是 <c>%AppData%\播放器</c>；如果设置了环境变量 <c>播放器_DATA_DIR</c>，
        /// 则改用它——便携安装可以用这个把数据放在程序目录旁边，
        /// 自动化测试也用它在临时目录里跑，避免碰到使用者的真实配置。
        /// </para>
        /// </summary>
        public static string SettingsDirectory
        {
            get
            {
                var custom = Environment.GetEnvironmentVariable("播放器_DATA_DIR");
                if (!string.IsNullOrWhiteSpace(custom))
                    return custom;

                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "播放器");
            }
        }

        /// <summary>设置文件的完整路径。</summary>
        public static string SettingsFilePath => Path.Combine(SettingsDirectory, "settings.json");

        /// <summary>读取默认位置的设置；文件缺失或损坏时返回默认值。</summary>
        public static AppSettings Load() => LoadFrom(SettingsFilePath);

        /// <summary>写入默认位置的设置；失败时静默忽略（例如目录只读）。</summary>
        public void Save() => SaveTo(SettingsFilePath);

        /// <summary>
        /// 读取设置失败时留下的说明（文件损坏已备份等），正常读取时为 <c>null</c>。
        /// <para>调用方可以把它显示一次，免得用户只看到"设置全没了"却不知道发生了什么。</para>
        /// </summary>
        [JsonIgnore]
        public string? LoadWarning { get; private set; }

        /// <summary>从指定文件读取设置。文件缺失、为空或格式损坏时返回默认值。</summary>
        public static AppSettings LoadFrom(string filePath)
        {
            if (!File.Exists(filePath)) return new AppSettings();

            try
            {
                var json = File.ReadAllText(filePath);
                if (string.IsNullOrWhiteSpace(json)) return new AppSettings();
                if (TryFromJson(json, out var settings)) return settings;

                // 整份读不出来时先试试"能救几项算几项"：
                // 坏了一项就把整份设置清零，代价太大——窗口尺寸、主题、当前歌单、播放列表全没了。
                if (TrySalvage(json, out var kept, out var lost, out var restUnreadable) is { } salvaged)
                {
                    var backupPath = SafeFile.TryBackupCorrupt(filePath);

                    salvaged.LoadWarning = (backupPath == null
                            ? "设置文件有一处读不出来："
                            : $"设置文件有一处读不出来，原文件已备份为 {Path.GetFileName(backupPath)}：")
                        + $"保住了 {kept} 项"
                        + (lost.Count > 0 ? $"，这些用了默认值（{string.Join("、", lost)}）" : string.Empty)
                        + (restUnreadable ? "，文件从某一处起读不下去、后面那些项也用了默认值" : string.Empty)
                        + "。";

                    AppLog.Warn(salvaged.LoadWarning);
                    return salvaged;
                }
            }
            catch (Exception ex)
            {
// 落到下面的备份逻辑。
                AppLog.Swallowed("落到下面的备份逻辑。", ex);
            }

            // 一项都救不回来时不要只是"悄悄退回默认值"：把坏文件留一份，并告诉用户。
            var backup = SafeFile.TryBackupCorrupt(filePath);
            return new AppSettings
            {
                LoadWarning = backup == null
                    ? "设置文件已损坏，本次使用默认设置（原文件无法备份）。"
                    : "设置文件已损坏，已备份为 " + Path.GetFileName(backup) + "，本次使用默认设置。"
            };
        }

        /// <summary>设置文件里认得的属性名（用来分辨"救回来一项"和"忽略一个不认识的字段"）。</summary>
        private static HashSet<string>? _knownPropertyNames;

        private static HashSet<string> KnownPropertyNames =>
            _knownPropertyNames ??= new HashSet<string>(
                (JsonNode.Parse(new AppSettings().ToJson())?.AsObject() ?? new JsonObject())
                .Select(pair => pair.Key),
                StringComparer.Ordinal);

        /// <summary>
        /// 从一份读不出来的设置里把还能用的项尽量捞出来。
        /// <para>
        /// 做法是<b>逐项试</b>：把每个属性的原始 JSON 片段单独反序列化一遍，
        /// 成功的留在结果里、失败的把名字记下来。用 <see cref="Utf8JsonReader"/> 一项一项读，
        /// 所以<b>语法坏了也能救出前半部分</b>——读到坏的地方就停，并用
        /// <paramref name="restUnreadable"/> 说明"后面的确实没办法了"。
        /// </para>
        /// <para>返回 <c>null</c> 表示一项都没救回来（这时调用方照旧整份退回默认值并备份）。</para>
        /// </summary>
        internal static AppSettings? TrySalvage(
            string json, out int kept, out IReadOnlyList<string> lost, out bool restUnreadable)
        {
            kept = 0;
            restUnreadable = false;

            var lostNames = new List<string>();
            lost = lostNames;

            var bytes = Encoding.UTF8.GetBytes(json);

            var options = new JsonReaderOptions
            {
                // 手改过的文件常常是"多一个逗号"或"加了注释"，这两种先宽容一点
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            };

            var reader = new Utf8JsonReader(bytes, options);
            var recovered = new JsonObject();
            var current = string.Empty;

            try
            {
                if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;

                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndObject) break;

                    if (reader.TokenType != JsonTokenType.PropertyName)
                    {
                        // 结构已经乱了（比如少了个大括号），后面不再猜
                        restUnreadable = true;
                        break;
                    }

                    current = reader.GetString() ?? string.Empty;

                    // 先前进到"值"上：Skip 只跳过<b>当前 token 的子节点</b>，
                    // 停在属性名上它什么也不做（这一步漏了的话取到的片段是空的）
                    if (!reader.Read())
                    {
                        restUnreadable = true;
                        break;
                    }

                    var valueStart = (int)reader.TokenStartIndex;

                    // 取值：Skip 会把整个值（含子对象 / 数组）一起跳过；跳不过去就说明这里是坏的
                    reader.Skip();

                    var raw = Encoding.UTF8.GetString(bytes, valueStart, (int)reader.BytesConsumed - valueStart);

                    if (!KnownPropertyNames.Contains(current)) continue;   // 不认识的字段：正常忽略

                    if (TryRecoverProperty(current, raw) is { } value)
                    {
                        recovered[current] = value;
                        continue;
                    }

                    lostNames.Add(current);
                }
            }
            catch (JsonException)
            {
                // 文件在这一项上坏了：这一项和后面的都没了
                restUnreadable = true;

                if (current.Length > 0 && KnownPropertyNames.Contains(current) && !lostNames.Contains(current))
                    lostNames.Add(current);
            }

            if (recovered.Count == 0) return null;

            if (!TryFromJson(recovered.ToJsonString(), out var result)) return null;

            kept = recovered.Count;
            return result;
        }

        /// <summary>把单个属性的原始 JSON 片段试着重出来；重不出来返回 <c>null</c>。</summary>
        private static JsonNode? TryRecoverProperty(string name, string rawValue)
        {
            // 名字来自文件本身，用 JsonEncodedText 转义一下再拼，免得名字里有引号把片段拼坏。
            // 注意 Encode 出来的**不含**两侧引号，要自己加上。
            var fragment = "{\"" + JsonEncodedText.Encode(name).ToString() + "\":" + rawValue + "}";

            if (!TryFromJson(fragment, out var single)) return null;

            var node = JsonNode.Parse(single.ToJson());

            return node is JsonObject obj && obj.TryGetPropertyValue(name, out var value)
                ? value?.DeepClone()
                : null;
        }

        /// <summary>把设置写入指定文件，目录不存在时自动创建。</summary>
        public void SaveTo(string filePath)
        {
            // 走临时文件 + 改名覆盖，避免写到一半崩溃留下半截 JSON。
            SafeFile.WriteAllText(filePath, ToJson());
        }

        /// <summary>序列化为 JSON 文本。</summary>
        public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);

        /// <summary>从 JSON 文本反序列化；内容为空或无效时返回默认值。</summary>
        public static AppSettings FromJson(string json) =>
            TryFromJson(json, out var settings) ? settings : new AppSettings();

        /// <summary>
        /// 尝试反序列化。<paramref name="json"/> 为空或格式损坏时返回 <c>false</c>。
        /// <para>与 <see cref="FromJson"/> 的区别是调用方<b>能分辨"读失败"和"确实没有设置"</b>，
        /// 因此可以决定要不要备份坏文件、要不要提示用户。</para>
        /// </summary>
        public static bool TryFromJson(string json, out AppSettings settings)
        {
            settings = new AppSettings();

            if (string.IsNullOrWhiteSpace(json)) return false;

            try
            {
                var parsed = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions);
                if (parsed == null) return false;

                // 设置文件是纯文本，用户可能会手改（也可能被别的版本写坏）。
                // 这里把可空字段补回默认值，避免读到一个 null 就在启动时崩掉。
                parsed.LastDirectory ??= string.Empty;
                parsed.AspectRatio ??= string.Empty;
                parsed.LastPlaylist ??= new List<string>();
                parsed.CurrentPlaylistName = (parsed.CurrentPlaylistName ?? string.Empty).Trim();
                parsed.CurrentSettingsProfileName = (parsed.CurrentSettingsProfileName ?? string.Empty).Trim();
                parsed.AudioDeviceId ??= string.Empty;
                parsed.AudioDeviceModule ??= string.Empty;

                parsed.Equalizer ??= new EqualizerState();
                parsed.Equalizer.PresetName ??= EqualizerCatalog.FlatPresetName;
                parsed.Equalizer.Amps ??= new float[EqualizerCatalog.BandCount];

                parsed.Deinterlace ??= string.Empty;
                parsed.LyricsFontFamily ??= string.Empty;

                // 只是把地址收拾干净（补协议头、去尾斜杠），没填就还是空——
                // 这里不会替用户填一个默认服务地址
                parsed.LrcApiBaseUrl = LrcApi.NormalizeBaseUrl(parsed.LrcApiBaseUrl);
                parsed.LrcApiToken = LrcApi.NormalizeToken(parsed.LrcApiToken) ?? string.Empty;

                settings = parsed;
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }
    }
}
