// 字体 —— 管理对话框（导入字体对话框的列表、预览、选中效果与 DPI 下的排版）。
//
// 从 Program.Fonts.cs 按 ARCHITECTURE 17.2 的规矩整块搬过来：成员一个字没动。

namespace SmokeTest
{
    internal static partial class Program
    {
        /// <summary>
        /// 字体管理对话框：列表里每一行都该用"它自己的字体"画出来（这是唯一看得见字形的地方），
        /// 并且要能选中、能删除。
        /// </summary>
        private static bool CheckFontManagerDialog(Form form, string importedFamily)
        {
            var dialog = new FontManagerDialog(importedFamily, w => { });

            using (dialog)
            {
                dialog.StartPosition = FormStartPosition.Manual;
                dialog.Location = new Point(80, 80);
                dialog.Show();
                PumpMessages(150);

                var list = dialog.Controls.OfType<ListBox>().FirstOrDefault();

                if (list == null)
                {
                    Log(15, "字体管理对话框检查：找不到字体列表");
                    return false;
                }

                if (list.DrawMode != DrawMode.OwnerDrawFixed)
                {
                    Log(15, "字体管理对话框检查：列表不是自绘的，看不出字体的字形");
                    return false;
                }

                var names = list.Items.Cast<object>().Select(o => o?.ToString()).ToList();
                var files = UiFonts.ImportedFiles;

                if (names.Count != files.Count)
                {
                    Log(15, $"字体管理对话框检查：列表里 {names.Count} 项，字体库里 {files.Count} 个文件");
                    return false;
                }

                var wanted = UiFonts.FileForFamily(importedFamily);

                if (wanted == null || !names.Contains(wanted))
                {
                    Log(15, $"字体管理对话框检查：列表里没有「{importedFamily}」所在的文件"
                            + $"（列表里是：{string.Join(" / ", names)}）");
                    return false;
                }

                // 选中之后：下面那行大号样例要用选中的字体
                list.SelectedItem = wanted;
                PumpMessages(120);

                var sample = dialog.Controls.OfType<TextBox>().FirstOrDefault();

                if (sample == null || sample.Font == null)
                {
                    Log(15, "字体管理对话框检查：找不到样例文字框");
                    return false;
                }

                if (!string.Equals(sample.Font.FontFamily.Name, importedFamily, StringComparison.OrdinalIgnoreCase))
                {
                    Log(15, $"字体管理对话框检查：样例没有用选中的字体"
                            + $"（现在是「{sample.Font.FontFamily.Name}」）");
                    return false;
                }

                var scale = dialog.DeviceDpi / 96f;
                var worstBottom = 0;

                foreach (Control control in dialog.Controls)
                {
                    worstBottom = Math.Max(worstBottom, control.Bottom);

                    if (control.Right > dialog.ClientSize.Width || control.Bottom > dialog.ClientSize.Height)
                    {
                        Log(15, $"字体管理对话框在 {scale:0.##} 倍缩放下有控件越界："
                                + $"{control.GetType().Name} {control.Bounds} 超出客户区 {dialog.ClientSize}");
                        return false;
                    }
                }

                var itemHeight = list.ItemHeight;
                var deviceDpi = dialog.DeviceDpi;

                dialog.Close();

                Log(15, $"字体管理对话框正常：{names.Count} 个导入字体、行高 {itemHeight}（自绘预览）、"
                        + $"选中的字体用在样例上，DPI {deviceDpi} 下无越界（最大下边界 {worstBottom}）");
            }

            return true;
        }
    }
}
