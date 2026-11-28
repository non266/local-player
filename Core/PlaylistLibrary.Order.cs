using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

// 歌单顺序文件（一行一个名字）：读、写、换位、改名时跟着换。

namespace 播放器.Core
{
    public static partial class PlaylistLibrary
    {
        /// <summary>歌单顺序文件的完整路径。</summary>
        public static string OrderFilePath => Path.Combine(Directory, OrderFileName);
        // ---- 顺序（用户在歌单窗口里拖出来的那个顺序） ---------------------------

        /// <summary>
        /// 把顺序文件里的名字排到前面，其余的跟在后面（保持传进来的自然顺序）。
        /// <para>
        /// 顺序文件里<b>已经不存在的名字直接忽略</b>：删掉歌单、改名字都不会让它变成
        /// "必须定期清理的垃圾"，只会让那份歌单回到后面那一堆里。
        /// </para>
        /// </summary>
        private static List<SavedPlaylist> ApplyOrder(List<SavedPlaylist> playlists)
        {
            var order = ReadOrder();
            if (order.Count == 0) return playlists;

            var rest = new List<SavedPlaylist>(playlists);
            var ordered = new List<SavedPlaylist>(playlists.Count);

            foreach (var name in order)
            {
                var index = rest.FindIndex(p => string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase));
                if (index < 0) continue;

                ordered.Add(rest[index]);
                rest.RemoveAt(index);
            }

            ordered.AddRange(rest);
            return ordered;
        }

        /// <summary>读顺序文件（一行一个名字，空行忽略）；没有 / 读不出来时返回空表。</summary>
        private static List<string> ReadOrder()
        {
            var names = new List<string>();

            try
            {
                var path = OrderFilePath;
                if (!File.Exists(path)) return names;

                foreach (var line in File.ReadAllLines(path))
                {
                    var name = line.Trim();
                    if (name.Length > 0) names.Add(name);
                }
            }
            catch (Exception)
            {
                // 读不出来就当"还没排过"：顺序是锦上添花，不该因为它挡住歌单列表。
                return new List<string>();
            }

            return names;
        }

        /// <summary>
        /// 把 <paramref name="names"/> 记成歌单顺序（写进顺序文件）。
        /// <para>只写<b>现在真的存在</b>的名字，重复的只留一次——顺序文件里不该留下不存在的名字。</para>
        /// </summary>
        public static bool SetOrder(IReadOnlyList<string> names, out string error)
        {
            error = string.Empty;
            if (names == null) throw new ArgumentNullException(nameof(names));

            var existing = new HashSet<string>(
                List().Select(playlist => playlist.Name), StringComparer.CurrentCultureIgnoreCase);

            var lines = new List<string>();
            var seen = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);

            foreach (var name in names)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (!existing.Contains(name)) continue;
                if (!seen.Add(name)) continue;

                lines.Add(name);
            }

            return WriteOrder(lines, out error);
        }

        /// <summary>
        /// 把某一份歌单挪到第 <paramref name="targetIndex"/> 位（拖放落下去时做的事），并把新顺序记进顺序文件。
        /// <para><paramref name="targetIndex"/> 是<b>挪完之后的最终下标</b>（0 = 排在第一个）。</para>
        /// </summary>
        public static bool Move(string name, int targetIndex, out string error)
        {
            error = string.Empty;

            var names = List().Select(playlist => playlist.Name).ToList();
            var from = names.FindIndex(
                candidate => string.Equals(candidate, name, StringComparison.CurrentCultureIgnoreCase));

            if (from < 0)
            {
                error = $"歌单库里没有「{name}」。";
                return false;
            }

            if (names.Count < 2) return true;      // 只有一份，没什么可挪的

            targetIndex = Math.Clamp(targetIndex, 0, names.Count - 1);
            if (targetIndex == from) return true;

            var moved = names[from];
            names.RemoveAt(from);
            names.Insert(targetIndex, moved);

            return SetOrder(names, out error);
        }

        /// <summary>
        /// 把一组歌单当成一个块，按<b>移动前</b>的坐标插到 <paramref name="insertBefore"/> 之前
        /// （1.33.0 的歌单窗口多选拖动）。
        /// <para>
        /// 语义与 `Playlist.MoveMany` 一致：块内相对顺序不变；非连续选中会跨过中间未选中的
        /// 歌单；落点在选中块范围（含首尾）内时不动。整批只写一次顺序文件。
        /// 任何一份名字已经不在库里就整批失败，不允许移动一半。
        /// </para>
        /// <para><paramref name="moved"/> 是移动后的新索引（升序）；no-op 时为空表。</para>
        /// </summary>
        public static bool MoveMany(
            IReadOnlyList<string> names,
            int insertBefore,
            out IReadOnlyList<int> moved,
            out string error)
        {
            if (names == null) throw new ArgumentNullException(nameof(names));

            moved = Array.Empty<int>();
            error = string.Empty;

            var all = List().Select(playlist => playlist.Name).ToList();

            var wanted = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);

            foreach (var name in names)
            {
                if (!string.IsNullOrWhiteSpace(name)) wanted.Add(name);
            }

            if (wanted.Count == 0)
            {
                error = "没有选中要移动的歌单。";
                return false;
            }

            var selected = new List<int>(wanted.Count);

            foreach (var name in wanted)
            {
                var index = all.FindIndex(
                    candidate => string.Equals(candidate, name, StringComparison.CurrentCultureIgnoreCase));

                if (index < 0)
                {
                    error = $"歌单库里没有「{name}」。";
                    return false;
                }

                selected.Add(index);
            }

            selected.Sort();

            // 全选没有可移动的东西；和主列表一样直接 no-op。
            if (selected.Count == all.Count) return true;

            insertBefore = Math.Clamp(insertBefore, 0, all.Count);

            // 落点在选中块范围内（含首尾边界）不动：块已经在那儿了。
            if (insertBefore >= selected[0] && insertBefore <= selected[^1] + 1) return true;

            var block = selected.Select(index => all[index]).ToList();
            var selectedSet = new HashSet<int>(selected);
            var rest = all.Where((name, index) => !selectedSet.Contains(index)).ToList();

            var removedBefore = selected.Count(index => index < insertBefore);
            var at = Math.Clamp(insertBefore - removedBefore, 0, rest.Count);

            rest.InsertRange(at, block);

            if (!SetOrder(rest, out error)) return false;

            var result = new List<int>(block.Count);

            for (var i = 0; i < block.Count; i++) result.Add(at + i);

            moved = result;
            return true;
        }

        /// <summary>写顺序文件（原子写）；失败返回 false 并给出理由。</summary>
        private static bool WriteOrder(List<string> names, out string error)
        {
            error = string.Empty;

            try
            {
                EnsureDirectory();

                // 末尾补一个换行：用记事本打开时最后一行不会和"文件末尾"粘在一起。
                var text = names.Count == 0
                    ? string.Empty
                    : string.Join(Environment.NewLine, names) + Environment.NewLine;

                SafeFile.WriteAllText(OrderFilePath, text);
                return true;
            }
            catch (Exception ex)
            {
                error = "记顺序失败：" + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 顺序文件里把旧名字换成新名字。
        /// <para>
        /// 改名之后那份歌单<b>不该掉到"没排过"的那一堆里</b>——用户拖出来的位置还是个位置。
        /// 顺序文件坏了 / 写不进去也不该让改名失败，所以这里只记一条日志、不改返回值。
        /// </para>
        /// </summary>
        private static void ReplaceOrderName(string oldName, string newName)
        {
            var order = ReadOrder();
            if (order.Count == 0) return;

            var changed = false;

            for (var i = 0; i < order.Count; i++)
            {
                if (!string.Equals(order[i], oldName, StringComparison.CurrentCultureIgnoreCase)) continue;

                order[i] = newName;
                changed = true;
            }

            if (changed && !WriteOrder(order, out var error)) AppLog.Info("歌单顺序文件没更新成：" + error);
        }
    }
}
