using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using LibVLCSharp.Shared;
using LibVLCSharp.Shared.Structures;

// 章节跳转与截图。

namespace 播放器.Core
{
    public sealed partial class PlayerEngine
    {
        // ---- 章节 -------------------------------------------------------------

        /// <summary>当前媒体的章节数；没有媒体或不支持章节时为 0。</summary>
        public int ChapterCount
        {
            get
            {
                try
                {
                    // libvlc 在没有媒体时返回 -1 表示出错；对调用方来说"没有章节"就是 0。
                    return Math.Max(0, _mediaPlayer.ChapterCount);
                }
                catch (Exception)
                {
                    return 0;
                }
            }
        }

        /// <summary>当前章节序号；无章节时为 <c>-1</c>。</summary>
        public int CurrentChapter
        {
            get
            {
                try { return _mediaPlayer.Chapter; }
                catch (Exception) { return -1; }
            }
        }

        /// <summary>当前标题下的全部章节。</summary>
        public IReadOnlyList<ChapterDescription> GetChapters()
        {
            try { return _mediaPlayer.FullChapterDescriptions(-1) ?? Array.Empty<ChapterDescription>(); }
            catch (Exception) { return Array.Empty<ChapterDescription>(); }
        }

        /// <summary>跳到指定章节。</summary>
        public void SetChapter(int index)
        {
            try { _mediaPlayer.Chapter = index; }
            catch (Exception) { }
        }

        /// <summary>跳到下一章。</summary>
        public void NextChapter()
        {
            try { _mediaPlayer.NextChapter(); }
            catch (Exception) { }
        }

        /// <summary>跳到上一章。</summary>
        public void PreviousChapter()
        {
            try { _mediaPlayer.PreviousChapter(); }
            catch (Exception) { }
        }

        // ---- 截图 -------------------------------------------------------------

        /// <summary>把当前画面保存为图片，返回是否成功。暂停状态下也可以截。</summary>
        public bool TakeSnapshot(string filePath)
        {
            // 不要求正在播放：暂停时截图是常见需求（VLC 也支持），只要媒体已经打开。
            if (_currentMedia == null) return false;

            try
            {
                var folder = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
                return _mediaPlayer.TakeSnapshot(0, filePath, 0, 0);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
