// 冒烟测试的公共 using。原来这些挤在唯一那份 Program.cs 的顶部；
// 拆成多个 partial 文件后放在这里，省得 20 份文件各抄一遍（也免得出现"多余 using"）。
global using System;
global using System.Collections.Generic;
global using System.Drawing;
global using System.IO;
global using System.Linq;
global using System.Reflection;
global using System.Runtime.InteropServices;
global using System.Text;
global using System.Threading;
global using System.Windows.Forms;
global using LibVLCSharp.Shared;
global using 播放器.Core;
global using 播放器.Ui;
