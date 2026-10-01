using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using 播放器.Core;

namespace 播放器.Ui
{
    /// <summary>
    /// 已导入字体的管理对话框：看清楚每个字体长什么样、删掉不要的。
    /// <para>
    /// 之前导入是"只进不出"的：菜单里只有「导入字体文件…」，导错了就永久多一行，
    /// 想卸载只能退出程序、自己去数据目录里删文件。这个对话框补上删除入口，
    /// 顺带解决另一个问题——<b>只看字体族名完全看不出字形</b>
    /// （尤其是艺术字、手写体），所以列表里每一行都用它自己的字体来画，
    /// 下面还有一行大号样例。
    /// </para>
    /// </summary>
    internal sealed class FontManagerDialog : Form
    {
        private const int DesignWidth = 560;

        private readonly Action<IWin32Window> _importRequest;
        private readonly ListBox _list = new ListBox();
        private readonly TextBox _sample = new TextBox();
        private readonly Label _detail = new Label();
        private readonly Button _importButton = new Button();
        private readonly Button _deleteButton = new Button();
        private readonly Button _okButton = new Button();
        private readonly Button _cancelButton = new Button();

        private readonly List<(Control Control, Rectangle DesignBounds)> _layout =
            new List<(Control, Rectangle)>();

        private string? _selectedFamily;
        private string? _selectedFile;
        private Font? _sampleFont;
        private float _sampleFontSize;
        private string? _sampleFontFamily;

        internal FontManagerDialog(string? currentFamily, Action<IWin32Window> importRequest)
        {
            _importRequest = importRequest;
            _selectedFamily = currentFamily;

            Text = "已导入的字体";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            Font = UiFonts.Sidebar;
            DoubleBuffered = true;

            BuildLayout();
            ApplyDpiLayout();
            ReloadList();
        }

        private float DpiScale => DeviceDpi / 96f;

        private int Scaled(int value) => (int)Math.Round(value * DpiScale);

        /// <summary>
        /// 打开字体管理对话框。返回用户选中的字体族；取消时返回 <c>null</c>。
        /// <para><paramref name="importRequest"/> 是"导入字体文件…"按钮要做的事（由主窗体提供，
        /// 拖放导入走的是同一份逻辑）。</para>
        /// </summary>
        public static string? Show(
            IWin32Window owner, ThemePalette palette, string? currentFamily, Action<IWin32Window> importRequest)
        {
            using var dialog = new FontManagerDialog(currentFamily, importRequest);

            dialog.ApplyPalette(palette);

            return dialog.ShowDialog(owner) == DialogResult.OK ? dialog._selectedFamily : null;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyDpiLayout();
        }

        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);
            ApplyDpiLayout();
        }

        // ---- 布局 -------------------------------------------------------------

        private void Place(Control control, int x, int y, int width, int height)
        {
            var bounds = control.AutoSize ? new Rectangle(x, y, 0, 0) : new Rectangle(x, y, width, height);

            _layout.Add((control, bounds));
            control.Bounds = bounds;
            Controls.Add(control);
        }

        private void ApplyDpiLayout()
        {
            var bottom = 0;

            foreach (var (control, design) in _layout)
            {
                var x = (int)Math.Round(design.X * DpiScale);
                var y = (int)Math.Round(design.Y * DpiScale);

                if (control.AutoSize)
                {
                    control.Location = new Point(x, y);
                }
                else
                {
                    control.Bounds = new Rectangle(
                        x, y,
                        Math.Max(1, (int)Math.Round(design.Width * DpiScale)),
                        Math.Max(1, (int)Math.Round(design.Height * DpiScale)));
                }

                bottom = Math.Max(bottom, control.Bottom);
            }

            ClientSize = new Size(Scaled(DesignWidth), Math.Max(Scaled(160), bottom + Scaled(16)));

            // 行高也要跟着 DPI 走，否则 125% 下每一行都会被压扁成原来的 80%
            _list.ItemHeight = Scaled(30);
        }

        private void BuildLayout()
        {
            var hint = new Label
            {
                Text = "一行一个字体文件，用该文件里的字体画出它的名字——字体族名看不出字形，看字才知道。"
            };

            Place(hint, 14, 12, 0, 0);

            _list.DrawMode = DrawMode.OwnerDrawFixed;
            _list.IntegralHeight = false;
            _list.ItemHeight = Scaled(30);
            _list.BorderStyle = BorderStyle.FixedSingle;
            _list.DrawItem += OnDrawItem;
            _list.SelectedIndexChanged += (s, e) => OnSelectionChanged();
            _list.DoubleClick += (s, e) => AcceptIfSelected();
            Place(_list, 14, 40, DesignWidth - 28, 190);

            _sample.Multiline = true;
            _sample.ReadOnly = true;
            _sample.BorderStyle = BorderStyle.FixedSingle;
            _sample.ScrollBars = ScrollBars.Vertical;
            _sample.Text = SampleText;
            Place(_sample, 14, 240, DesignWidth - 28, 84);

            _detail.AutoSize = true;
            Place(_detail, 14, 332, 0, 0);

            _importButton.Text = "导入字体文件…";
            _importButton.Click += (s, e) => ImportRequested();
            Place(_importButton, 14, 362, 130, 30);

            _deleteButton.Text = "删除选中字体";
            _deleteButton.Click += (s, e) => DeleteSelected();
            Place(_deleteButton, 152, 362, 130, 30);

            _okButton.Text = "使用选中字体";
            _okButton.DialogResult = DialogResult.OK;
            Place(_okButton, 350, 362, 118, 30);

            _cancelButton.Text = "关闭";
            _cancelButton.DialogResult = DialogResult.Cancel;
            Place(_cancelButton, 476, 362, 70, 30);

            AcceptButton = _okButton;
            CancelButton = _cancelButton;
        }

        /// <summary>样例文字：中英数都有，一眼能看出"这个字体有没有中文字形"。</summary>
        private const string SampleText = "桌面歌词示例 Desktop Lyrics 123 永和九年，岁在癸丑";

        // ---- 列表 -------------------------------------------------------------

        /// <summary>
        /// 已导入字体文件的管理对话框。
        /// <para>
        /// 列表是<b>一行一个字体文件</b>（不是一行一个字体族）：同一个族名完全可能来自多个文件
        /// （同一个字体导入两次就会），按族名删有歧义。每行用该文件里的字体画出它的族名，
        /// 下面还有一行大号样例——只看族名是看不出字形的。
        /// </para>
        /// </summary>
        private void ReloadList(string? selectFile = null)
        {
            var files = UiFonts.ImportedFiles;

            _list.BeginUpdate();

            try
            {
                _list.Items.Clear();

                foreach (var file in files)
                    _list.Items.Add(file);

                var wanted = selectFile ?? _selectedFile;

                if (wanted == null && _selectedFamily != null)
                    wanted = UiFonts.FileForFamily(_selectedFamily);

                var index = -1;

                if (wanted != null)
                {
                    for (var i = 0; i < files.Count; i++)
                    {
                        if (!string.Equals(files[i], wanted, StringComparison.OrdinalIgnoreCase)) continue;

                        index = i;
                        break;
                    }
                }

                if (index >= 0) _list.SelectedIndex = index;
                else if (_list.Items.Count > 0) _list.SelectedIndex = 0;
            }
            finally
            {
                _list.EndUpdate();
            }

            OnSelectionChanged();
        }

        private void OnDrawItem(object? sender, DrawItemEventArgs e)
        {
            e.DrawBackground();

            if (e.Index < 0 || e.Index >= _list.Items.Count) return;

            var file = _list.Items[e.Index]?.ToString() ?? string.Empty;
            var families = UiFonts.FamiliesInFile(file);

            var family = families.Count > 0 ? families[0] : null;
            var caption = family ?? "（这个文件里没有能用的字体）";

            // 族名用它自己的字体画：这就是"预览"。
            // 每次绘制都建一个 Font 再释放（不是漏，是释放掉），因为每一行用的字体都不一样；
            // 不这么做的代价是对每个导入字体长期各留一个 GDI 句柄。
            using var font = UiFonts.Resolve(family, UiFonts.Sidebar.SizeInPoints, FontStyle.Regular);
            using var nameBrush = new SolidBrush(e.ForeColor);
            using var fileBrush = new SolidBrush(Color.FromArgb(150, e.ForeColor));

            var bounds = e.Bounds;
            var nameWidth = Math.Max(40, bounds.Width - Scaled(190));

            e.Graphics.DrawString(
                caption,
                font,
                nameBrush,
                new RectangleF(bounds.X + 4, bounds.Y + 2, nameWidth, bounds.Height - 4),
                new StringFormat(StringFormat.GenericTypographic)
                {
                    Trimming = StringTrimming.EllipsisCharacter,
                    FormatFlags = StringFormatFlags.NoWrap,
                    LineAlignment = StringAlignment.Center
                });

            e.Graphics.DrawString(
                Path.GetFileName(file),
                UiFonts.Sidebar,
                fileBrush,
                new RectangleF(bounds.Right - Scaled(185), bounds.Y + 2, Scaled(180), bounds.Height - 4),
                new StringFormat(StringFormat.GenericTypographic)
                {
                    Alignment = StringAlignment.Far,
                    Trimming = StringTrimming.EllipsisPath,
                    FormatFlags = StringFormatFlags.NoWrap,
                    LineAlignment = StringAlignment.Center
                });

            e.DrawFocusRectangle();
        }

        private void OnSelectionChanged()
        {
            _selectedFile = _list.SelectedItem?.ToString();

            var families = _selectedFile == null ? Array.Empty<string>() : UiFonts.FamiliesInFile(_selectedFile);
            _selectedFamily = families.Count > 0 ? families[0] : null;

            _detail.Text = _selectedFile == null
                ? "还没有导入任何字体。"
                : families.Count == 0
                    ? $"文件：{Path.GetFileName(_selectedFile)}（读不出字体）"
                    : $"文件：{Path.GetFileName(_selectedFile)}"
                      + (families.Count > 1 ? $"（这个文件里有 {families.Count} 个字体族：{string.Join(" / ", families)}）" : string.Empty);

            _deleteButton.Enabled = _selectedFile != null;

            RebuildSampleFont();
            _sample.Invalidate();
        }

        private void RebuildSampleFont()
        {
            var size = 20f;

            if (_sampleFont != null &&
                Math.Abs(_sampleFontSize - size) < 0.01f &&
                string.Equals(_sampleFontFamily, _selectedFamily, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _sampleFont?.Dispose();
            _sampleFont = UiFonts.Resolve(_selectedFamily, size, FontStyle.Regular);
            _sampleFontSize = size;
            _sampleFontFamily = _selectedFamily;

            _sample.Font = _sampleFont;
        }

        // ---- 操作 -------------------------------------------------------------

        private void ImportRequested()
        {
            // 把"自己"当 owner 传给文件对话框：否则它会躲在模态的管理对话框后面
            _importRequest(this);

            // 导入完可能多了好几个文件，直接选中最后一个（新导进来的那个）
            var before = _list.Items.Count;
            ReloadList(_selectedFile);

            if (_list.Items.Count > before && _list.Items.Count > 0)
                _list.SelectedIndex = _list.Items.Count - 1;
        }

        private void DeleteSelected()
        {
            var file = _list.SelectedItem?.ToString();
            if (string.IsNullOrEmpty(file)) return;

            var families = UiFonts.FamiliesInFile(file);

            var what = families.Count > 0
                ? $"「{string.Join(" / ", families)}」（{Path.GetFileName(file)}）"
                : $"文件 {Path.GetFileName(file)}";

            var answer = MessageBox.Show(
                this,
                $"要删除导入的字体{what}吗？\n\n"
                + "（原文件不会动，只是从字体库里移除。已经用上它的地方会自动退回默认字体。）",
                "删除导入的字体",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (answer != DialogResult.Yes) return;

            if (!UiFonts.TryRemoveFontFile(file, out var error))
            {
                MessageBox.Show(this, error ?? "删除失败", "删除导入的字体", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // 删掉的可能正是当前在用的那一个：让调用方把它退回默认字体
            if (families.Any(f => string.Equals(f, _selectedFamily, StringComparison.OrdinalIgnoreCase)))
                _selectedFamily = string.Empty;

            ReloadList();
        }

        private void AcceptIfSelected()
        {
            if (_list.SelectedItem == null) return;

            DialogResult = DialogResult.OK;
            Close();
        }

        // ---- 主题 -------------------------------------------------------------

        private void ApplyPalette(ThemePalette palette)
        {
            BackColor = palette.WindowBack;
            ForeColor = palette.ListFore;

            _list.BackColor = palette.ListBack;
            _list.ForeColor = palette.ListFore;

            _sample.BackColor = palette.ListBack;
            _sample.ForeColor = palette.ListFore;

            foreach (Control control in Controls)
            {
                switch (control)
                {
                    case Button button:
                        button.FlatStyle = FlatStyle.Flat;
                        button.BackColor = palette.MenuBack;
                        button.ForeColor = palette.MenuFore;
                        button.FlatAppearance.BorderColor = palette.MenuBorder;
                        break;

                    case Label label:
                        label.ForeColor = palette.ListFore;
                        break;
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _sampleFont?.Dispose();
                _sampleFont = null;
            }

            base.Dispose(disposing);
        }
    }
}
