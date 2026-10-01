using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using 播放器.Core;
using 播放器.Ui;

namespace 播放器
{
    /// <summary>
    /// 在线歌词与封面（LrcAPI）。
    /// <para>
    /// 本地那一套找完之后才轮到它：外挂 <c>.lrc</c>、内嵌标签、同名 <c>.txt</c> 都没有歌词，
    /// 或者文件里没有内嵌封面、目录里也没有 <c>cover.jpg</c> 之类，
    /// 才按"曲名 + 专辑 + 歌手"去 <see cref="LrcApi"/> 上要一份。
    /// </para>
    /// <para>
    /// <b>不写盘</b>：取回来的歌词与封面只在本次运行里用（存在内存缓存里，
    /// 换回同一首歌不用重新联网），不会往用户的音乐目录里丢文件。
    /// 这是明确的取舍——想离线复用就自己另存一份 <c>.lrc</c>。
    /// </para>
    /// <para>
    /// 网络请求全部是异步的：切歌时取消上一个还在飞的请求，结果回来时再核对
    /// "这首还是不是当前这首"，避免慢请求把用户已经切走的歌词糊到新歌上。
    /// </para>
    /// </summary>
    public partial class MainForm : IOnlineLookupHost
    {
        /// <summary>菜单项用它认领自己的身份（和桌面歌词那一套同样的做法）。</summary>
        private const string OnlineLookupEnabledTag = "online-lookup-enabled";

        private const string OnlineLookupAddressTag = "online-lookup-address";

        private const string OnlineLookupTokenTag = "online-lookup-token";

        /// <summary>
        /// 在线歌词与封面：会话缓存、要不要联网、取消、请求编排都在它里面
        /// （见 <see cref="OnlineLyricsService"/>）。窗体只负责菜单、对话框和"把结果画出来"。
        /// </summary>
        private readonly OnlineLyricsService _online = new OnlineLyricsService(HasSidecarCover);

        /// <summary>
        /// 只读入口，给冒烟测试用（主工程里有 <c>InternalsVisibleTo("SmokeTest")</c>）。
        /// <para>
        /// 测试原来靠反射去清 <c>_onlineLyricsCache</c> / <c>_onlineCoverCache</c>
        /// （"假装这次运行还没取过"），现在直接调 <c>Clear()</c>。测试只读 / 只清缓存。
        /// </para>
        /// </summary>
        internal OnlineLyricsService Online => _online;

        private ToolStripMenuItem? _menuOnlineLookup;
        private ToolStripMenuItem? _menuOnlineLookupEnabled;
        private ToolStripMenuItem? _menuOnlineLookupLyrics;
        private ToolStripMenuItem? _menuOnlineLookupCover;

        /// <summary>只读入口：视图菜单里的「在线歌词与封面」（给冒烟测试用）。</summary>
        internal ToolStripMenuItem? MenuOnlineLookup => _menuOnlineLookup;

        // =====================================================================
        // 菜单
        // =====================================================================

        private void ConfigureOnlineLookupMenu()
        {
            _menuOnlineLookup = new ToolStripMenuItem("在线歌词与封面(&O)");

            _menuOnlineLookupEnabled = new ToolStripMenuItem("自动获取（本地没有时）")
            {
                CheckOnClick = true,
                Tag = OnlineLookupEnabledTag
            };

            _menuOnlineLookupEnabled.Click +=
                (s, e) => SetOnlineLookupEnabled(_menuOnlineLookupEnabled.Checked);

            _menuOnlineLookupLyrics = new ToolStripMenuItem("获取当前曲目的歌词");
            _menuOnlineLookupLyrics.Click += (s, e) => LookupCurrentTrackOnline(lyrics: true, cover: false);

            _menuOnlineLookupCover = new ToolStripMenuItem("获取当前曲目的封面");
            _menuOnlineLookupCover.Click += (s, e) => LookupCurrentTrackOnline(lyrics: false, cover: true);

            var address = new ToolStripMenuItem("服务地址…") { Tag = OnlineLookupAddressTag };
            address.Click += (s, e) => EditLrcApiBaseUrl();

            // 服务端开了鉴权就必须填这个，否则封面接口一律 401
            var token = new ToolStripMenuItem("鉴权密钥…") { Tag = OnlineLookupTokenTag };
            token.Click += (s, e) => EditLrcApiToken();

            _menuOnlineLookup.DropDownOpening += (s, e) => RefreshOnlineLookupMenu();

            _menuOnlineLookup.DropDownItems.Add(_menuOnlineLookupEnabled);
            _menuOnlineLookup.DropDownItems.Add(new ToolStripSeparator());
            _menuOnlineLookup.DropDownItems.Add(_menuOnlineLookupLyrics);
            _menuOnlineLookup.DropDownItems.Add(_menuOnlineLookupCover);
            _menuOnlineLookup.DropDownItems.Add(new ToolStripSeparator());
            _menuOnlineLookup.DropDownItems.Add(address);
            _menuOnlineLookup.DropDownItems.Add(token);

            InsertOnlineLookupMenu();
            RefreshOnlineLookupMenu();
        }

        /// <summary>插在「桌面歌词」之后：都属于"内容从哪来"这一类。</summary>
        private void InsertOnlineLookupMenu()
        {
            if (_menuOnlineLookup == null) return;

            var index = _menuDesktopLyrics == null ? -1 : menuView.DropDownItems.IndexOf(_menuDesktopLyrics);

            if (index < 0) menuView.DropDownItems.Add(_menuOnlineLookup);
            else menuView.DropDownItems.Insert(index + 1, _menuOnlineLookup);
        }

        internal void RefreshOnlineLookupMenu()
        {
            var configured = LrcApi.LooksLikeBaseUrl(_settings.LrcApiBaseUrl);
            var hasMedia = !string.IsNullOrEmpty(_track.Path);
            var isVideo = hasMedia && IsVideoMedia(_track.Path);

            if (_menuOnlineLookupEnabled != null)
            {
                _menuOnlineLookupEnabled.Checked = _settings.OnlineLookup;

                // 为什么没得自动查，直接写在菜单项上
                _menuOnlineLookupEnabled.Text = isVideo
                    ? "自动获取（视频不查歌词与封面）"
                    : configured
                        ? "自动获取（本地没有时）"
                        : "自动获取（先在下面填服务地址）";
            }

            // 视频连手动都不查：电影名搜出来的歌词多半是另一首歌的
            var canLookup = hasMedia && !isVideo;

            if (_menuOnlineLookupLyrics != null) _menuOnlineLookupLyrics.Enabled = canLookup;
            if (_menuOnlineLookupCover != null) _menuOnlineLookupCover.Enabled = canLookup;

            foreach (var item in _menuOnlineLookup?.DropDownItems.OfType<ToolStripMenuItem>() ?? Enumerable.Empty<ToolStripMenuItem>())
            {
                var tag = item.Tag as string;

                if (tag == OnlineLookupAddressTag)
                {
                    // 地址直接写在菜单上：一眼能看到自己填的是什么、填没填
                    item.Text = configured
                        ? "服务地址：" + LrcApi.NormalizeBaseUrl(_settings.LrcApiBaseUrl) + "…"
                        : "服务地址：未填写（点这里填）…";
                }
                else if (tag == OnlineLookupTokenTag)
                {
                    // 密钥只显示"填没填"，绝不回显内容
                    var hasToken = LrcApi.NormalizeToken(_settings.LrcApiToken) != null;

                    item.Text = hasToken ? "鉴权密钥：已设置（点这里改）…" : "鉴权密钥：未设置（需要鉴权的服务必填）…";
                }
            }
        }

        private void SetOnlineLookupEnabled(bool enabled)
        {
            if (enabled && !LrcApi.LooksLikeBaseUrl(_settings.LrcApiBaseUrl))
            {
                // 开了自动获取却没有地址 → 直接把填地址的对话框弹出来。
                // 让用户自己按了确定才算数：程序不会替他挑一个服务。
                SetStatus("先填一个 LrcApi 服务地址，才能在线获取歌词与封面");
                EditLrcApiBaseUrl();

                if (!LrcApi.LooksLikeBaseUrl(_settings.LrcApiBaseUrl))
                {
                    _settings.OnlineLookup = false;
                    RefreshOnlineLookupMenu();
                    SetStatus("已取消：在线歌词与封面需要先填服务地址");
                    return;
                }
            }

            _settings.OnlineLookup = enabled;
            RefreshOnlineLookupMenu();

            if (!enabled)
            {
                _online.Cancel();
                SetStatus("已关闭在线歌词与封面");
                return;
            }

            SetStatus("已开启在线歌词与封面（本地没有时自动到 " + LrcApi.NormalizeBaseUrl(_settings.LrcApiBaseUrl) + " 查）");

            // 打开开关就顺手给当前这首补一次
            LookupCurrentTrackOnline(lyrics: true, cover: true, silent: true);
        }

        private void EditLrcApiBaseUrl()
        {
            var current = LrcApi.NormalizeBaseUrl(_settings.LrcApiBaseUrl);

            var entered = InputDialog.Show(
                this,
                "LrcApi 服务地址",
                "自己跑一个 LrcAPI（或用它提供的服务），把地址填在这里，例如：\n"
                + "    http://127.0.0.1:28883\n"
                + "    https://api.lrc.cx\n"
                + "程序不会替你选服务，填哪个就用哪个：",
                current);

            if (entered == null) return;

            var normalized = LrcApi.NormalizeBaseUrl(entered);

            if (normalized.Length == 0)
            {
                _settings.LrcApiBaseUrl = string.Empty;
                RefreshOnlineLookupMenu();
                SetStatus("已清空歌词服务地址（在线获取需要先填一个）");
                return;
            }

            if (!LrcApi.LooksLikeBaseUrl(normalized))
            {
                RefreshOnlineLookupMenu();
                SetStatus("这个地址看起来不对（要 http:// 或 https:// 开头）：" + normalized);
                return;
            }

            _settings.LrcApiBaseUrl = normalized;
            RefreshOnlineLookupMenu();

            SetStatus("歌词服务地址：" + normalized);

            // 刚填好地址，顺手按当前曲目试一次，省得用户再点一次菜单
            if (_settings.OnlineLookup) LookupCurrentTrackOnline(lyrics: true, cover: true, silent: true);
        }

        /// <summary>
        /// 填 / 改 / 清空 LrcApi 的鉴权密钥。
        /// <para>
        /// 密钥是"服务端开了鉴权才需要"的，所以这里不强制：留空就等于不带 Authorization 头。
        /// 输入框用掩码显示，状态栏也只说"已设置 / 已清空"，不回显内容。
        /// </para>
        /// </summary>
        private void EditLrcApiToken()
        {
            var current = LrcApi.NormalizeToken(_settings.LrcApiToken) ?? string.Empty;

            var entered = InputDialog.Show(
                this,
                "LrcApi 鉴权密钥",
                "服务端开了鉴权时，把密钥填在这里（会作为 Authorization 请求头发给这台服务）。\n"
                + "留空表示不用鉴权。密钥只发给上面填的那个地址，不会跟着封面图片发到别处：\n",
                current,
                mask: true);

            if (entered == null) return;

            _settings.LrcApiToken = LrcApi.NormalizeToken(entered) ?? string.Empty;
            RefreshOnlineLookupMenu();

            SetStatus(_settings.LrcApiToken.Length == 0
                ? "已清空 LrcApi 鉴权密钥（需要鉴权的服务会取不到封面）"
                : "已设置 LrcApi 鉴权密钥（不显示内容）");

            if (_settings.OnlineLookup) LookupCurrentTrackOnline(lyrics: false, cover: true, silent: true);
        }

        // =====================================================================
        // 触发
        // =====================================================================

        /// <summary>
        /// 切歌之后决定要不要联网：只在"本地真的没有"时才去要。
        /// <para>缺不缺由 <see cref="OnlineLyricsService.Decide"/> 判断（歌词 / 封面各算各的）。</para>
        /// </summary>
        private void QueueOnlineLookup(string? path, MediaTags tags, LyricsDocument lyrics)
        {
            if (!_settings.OnlineLookup) return;
            if (string.IsNullOrEmpty(path)) return;

            // 视频不去要歌词与封面：拿"某某电影.2019.1080p"这种名字去歌词服务里搜，
            // 搜回来的多半是一首同名的歌，歌词和画面八竿子打不着；
            // 封面同理（画面本身就是封面）。本地已有的 .lrc / 内嵌封面照旧会用。
            if (IsVideoMedia(path)) return;

            // 网络串流没有可靠的曲名可搜；本地文件才走这条路
            if (!File.Exists(path)) return;

            var need = _online.Decide(path, tags, lyrics);

            if (!need.Any)
            {
                // 缓存里有：直接补上，别为了已经有的东西再联网
                if (lyrics.IsEmpty && _online.TryGetLyrics(path, out var cachedLyrics))
                    ApplyOnlineLyrics(path, cachedLyrics!, "在线歌词（本次运行已取过）");

                if (!tags.HasCoverArt && _online.TryGetCover(path, out var cachedCover, out var cachedMime))
                    ApplyOnlineCover(path, cachedCover!, cachedMime ?? string.Empty, "在线封面（本次运行已取过）");

                return;
            }

            _ = RunOnlineLookupAsync(path, tags, need.Lyrics, need.Cover, silent: true);
        }

        /// <summary>菜单里的"获取当前曲目的歌词 / 封面"。</summary>
        internal void LookupCurrentTrackOnline(bool lyrics, bool cover, bool silent = false)
        {
            var path = _track.Path;

            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                SetStatus("当前没有可查询的本地曲目");
                return;
            }

            if (IsVideoMedia(path))
            {
                SetStatus("当前是视频，不查歌词与封面");
                return;
            }

            _ = RunOnlineLookupAsync(path, _track.Tags, lyrics, cover, silent);
        }

        /// <summary>
        /// 这个是视频吗（视频不查歌词与封面，自动与手动都不查）。
        /// <para>按扩展名判断：只有真的"视频文件"才算，网络串流判断不了，仍按音频对待。</para>
        /// </summary>
        private static bool IsVideoMedia(string? path) => MediaFormats.IsVideoFile(path);

        /// <summary>
        /// 真的去要一次。编排在 <see cref="OnlineLyricsService.LookupAsync"/> 里，
        /// 这里只负责把"这次查什么、按什么查"和当前的地址 / 密钥交过去。
        /// </summary>
        private Task RunOnlineLookupAsync(string path, MediaTags tags, bool lyrics, bool cover, bool silent) =>
            _online.LookupAsync(
                new OnlineLookupRequest(
                    path,
                    FirstNonEmpty(tags.Title, Path.GetFileNameWithoutExtension(path)),
                    tags.Album,
                    tags.Artist,
                    lyrics,
                    cover,
                    silent),
                // 地址 + 鉴权密钥一起带上（密钥只发给用户自己配的那台服务）
                new LrcApi.Endpoint(_settings.LrcApiBaseUrl, _settings.LrcApiToken),
                this);

        // =====================================================================
        // 把结果画出来（IOnlineLookupHost 的实现：服务拿到的结果从这里落地）
        // =====================================================================

        bool IOnlineLookupHost.IsAlive => !IsDisposed && IsHandleCreated;

        void IOnlineLookupHost.Post(Action action) => RunOnUiThread(action);

        void IOnlineLookupHost.Status(string message) => SetStatus(message);

        void IOnlineLookupHost.ApplyLyrics(string path, LyricsDocument document, string message) =>
            ApplyOnlineLyrics(path, document, message);

        void IOnlineLookupHost.ApplyCover(string path, byte[] data, string mime, string message) =>
            ApplyOnlineCover(path, data, mime, message);

        private void ApplyOnlineLyrics(string path, LyricsDocument document, string message)
        {
            // 结局回来时这首可能已经不是当前曲目了（用户切歌很快）
            if (!string.Equals(path, _track.Path, StringComparison.OrdinalIgnoreCase)) return;

            _track.SetLyrics(document);

            sidebarPanel.ShowTrack(
                _track.Path, _track.Tags, _track.ShiftedLyrics,
                BuildInfoRows(_track.Path, _track.Tags));

            UpdateLyricsPosition();
            RefreshDesktopLyricsContent();

            SetStatus($"{message}：{document.Lines.Count} 行来自 LrcApi");
        }

        private void ApplyOnlineCover(string path, byte[] data, string mime, string message)
        {
            if (!string.Equals(path, _track.Path, StringComparison.OrdinalIgnoreCase)) return;

            // 塞进当前的标签里：侧栏封面页与「媒体信息」都是从这里取的，
            // 这样"这张封面是哪来的"只有一处来源，不用给封面单开一条链路。
            _track.ApplyCover(data, mime);

            ApplyMetadata(_track.Path, _track.Tags, _track.Lyrics, allowOnlineLookup: false);
            SetStatus($"{message}：{FormatSize(data.Length)}");
        }

        /// <summary>封面大小：不到 1 KB 时说字节数，别显示成"0 KB"。</summary>
        private static string FormatSize(int bytes) =>
            bytes < 1024 ? bytes + " 字节" : bytes / 1024 + " KB";

        /// <summary>
        /// 目录里有没有外挂封面（<c>cover.jpg</c> 之类）。
        /// <para>这一步要看文件系统，由窗体告诉 <see cref="OnlineLyricsService"/>：
        /// 那边的判断是"缓存里有没有"，这边是"磁盘上有没有"，两边都不越界。</para>
        /// </summary>
        private static bool HasSidecarCover(string path)
        {
            try
            {
                var folder = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return false;

                return CoverView.HasSidecarCover(folder);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 把一段回调送回 UI 线程。
        /// <para>
        /// 正常路径上 <c>await</c> 的续体已经回到 UI 线程了，但测试（以及任何没有消息泵的场合）
        /// 未必装了同步上下文，所以这里再兜一道：<c>InvokeRequired</c> 时才 <c>BeginInvoke</c>。
        /// </para>
        /// </summary>
        private void RunOnUiThread(Action action)
        {
            if (IsDisposed || !IsHandleCreated) return;

            if (InvokeRequired)
            {
                try
                {
                    BeginInvoke(action);
                }
                catch (Exception ex)
                {
// 窗体正在销毁
                    AppLog.Swallowed("窗体正在销毁", ex);
                }

                return;
            }

            action();
        }
    }
}
