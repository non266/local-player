using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using LibVLCSharp.Shared;
using LibVLCSharp.Shared.Structures;

// 引擎的内核那一层：内核参数、按旋转值缓存的内核 + 播放器、换内核与状态重铺。

namespace 播放器.Core
{
    public sealed partial class PlayerEngine
    {
        /// <summary>
        /// 一个内核要带的参数。
        /// <para>
        /// 🔴 <b>画面旋转只能在这里设</b>，这一点是实测出来的（B-2 的前置验证，四组探针）：
        /// <c>transform</c> 是<b>视频输出级</b>的滤镜，它读的是<b>内核</b>的配置——
        /// 媒体级的 <c>:video-filter=transform</c> / <c>:transform-type=…</c> 不论怎么写都不生效
        /// （挂着链子也好、只写类型也好，画面一动不动）；实例级的 <c>--video-filter</c> /
        /// <c>--transform-type</c> 立刻生效，而且<b>媒体级也覆盖不了内核里的类型</b>。
        /// 所以"换了旋转值"就必须"换内核"，见 <see cref="EnsureCore"/>。
        /// </para>
        /// </summary>
        internal static string[] CoreArguments(ScreenRotation rotation)
        {
            var args = new List<string>
            {
                "--no-video-title-show",   // 不在画面上叠加文件名
                "--no-snapshot-preview",   // 截图时不闪一下预览
                "--no-stats",
                "--quiet"
            };

            var type = ScreenRotations.TransformType(rotation);

            if (type != null)
            {
                args.Add("--video-filter=" + ScreenRotations.VideoFilter);
                args.Add("--transform-type=" + type);
            }

            return args.ToArray();
        }

        /// <summary>建一套（内核 + 它自己的播放器）并记进缓存（同一个旋转值只建一次）。</summary>
        private CoreSlot CreateSlot(ScreenRotation rotation)
        {
            var core = new LibVLC(CoreArguments(rotation));
            var slot = new CoreSlot(core, CreatePlayer(core));
            _cores[rotation] = slot;
            return slot;
        }

        /// <summary>在新内核上建一个播放器，作为交换前的准备（状态由 <see cref="EnsureCore"/> 铺回去）。</summary>
        private MediaPlayer CreatePlayer(LibVLC core)
        {
            var player = new MediaPlayer(core);

            player.Opening += (s, e) => { if (!_switchingCore) OnOpening(); };
            player.Playing += (s, e) => { if (!_switchingCore) OnPlaying(); };
            player.Paused += (s, e) => { if (!_switchingCore) SetState(PlayerState.Paused); };
            player.Stopped += (s, e) => { if (!_switchingCore) OnStopped(); };
            player.EndReached += (s, e) => { if (!_switchingCore) OnEndReached(); };
            player.EncounteredError += (s, e) => { if (!_switchingCore) OnEncounteredError(); };
            player.ESAdded += (s, e) => { if (!_switchingCore) RaiseTracksChanged(); };
            player.ESDeleted += (s, e) => { if (!_switchingCore) RaiseTracksChanged(); };

            return player;
        }

        private void OnOpening()
        {
            _endReached = false;
            SetState(PlayerState.Opening);
        }

        private void OnPlaying()
        {
            _endReached = false;
            SetState(PlayerState.Playing);
            RaiseTracksChanged();
        }

        /// <summary>
        /// 把内核换成带 <paramref name="rotation"/> 的那一套（同一个就什么都不做）。
        /// <para>
        /// 1.5.0 起：每个旋转值缓存的是**一套**（内核 + 它自己的播放器），换内核就是把
        /// <see cref="_mediaPlayer"/> 指到那一套的播放器上，**不再每次新建 / 释放播放器**——
        /// 那是 M8 那个原生崩的根：实测每换一次泄漏约 170 个句柄 / 35 个线程（一个 libvlc
        /// 播放器的解码线程数），换到 240 次左右进程就崩。
        /// </para>
        /// <para>
        /// 旧播放器（上一个内核那一套的）只 <c>Stop()</c>，**不释放**：它下次切回去还要用。
        /// 释放统一放在 <see cref="Dispose"/> 里（先播放器、后内核）。
        /// </para>
        /// </summary>
        private void EnsureCore(ScreenRotation rotation)
        {
            if (rotation == _coreRotation) return;

            var reused = _cores.TryGetValue(rotation, out var slot);

            if (!reused) slot = CreateSlot(rotation);

            // 换内核这一段**偶发原生崩**（M8：`0xC0000005`，整个进程直接没）。
            // 崩的时候产品日志就是唯一的现场，所以这里每一步都留一行——
            // 日志停在哪一行，就说明崩在它后面那一步（旋转是用户偶尔才做一次的动作，
            // 一次多七八行日志不碍事；平时也只有这一段会打这么多）。
            //
            // 2026-10-15 靠这几行定到的位置：**`old.Stop()`**，而且崩的那一次永远是
            // "切到另一首 / 第一次**复用**缓存里的内核"；后来用"连续换内核"压测（60 ms 一次）
            // 量出真正的机制是**每次换内核泄漏一个播放器的原生线程与句柄**（见 CoreSlot 的注释）。
            AppLog.Info($"换内核：目标「{ScreenRotations.Describe(rotation)}」，"
                        + (reused ? "复用缓存里的那一套" : "新建一套（内核 + 播放器）")
                        + $"（缓存里现在有 {_cores.Count} 套）");

            var old = _mediaPlayer;

            _switchingCore = true;

            try
            {
                AppLog.Info("换内核：① 放掉当前媒体");
                DisposeCurrentMedia();

                // ⚠ 这里**故意不是 Stop()**（1.5.0 改的，M8 的根因）：
                // 实测（"连续换内核"探针 + 每轮记资源）每次 `MediaPlayer.Stop()` 都会漏掉
                // 约 150 个句柄 / 35 个 OS 线程——正好是一个 libvlc 输入 + 解码 + 视频输出的
                // 线程组，而且过多久都不还回来；`SetPause(true)` **一个都不漏**
                // （换 42 次之后线程数还是平的：537 → 533）。
                // 攒到上千线程之后，某次 libvlc 调用就变成 `0xC0000005`——那就是 M8。
                // 旧播放器本来也不用真停：它属于它那个内核，下次切回去还要接着用（见 CoreSlot）。
                AppLog.Info("换内核：② 暂停旧播放器（不 Stop：Stop 会漏一整套原生线程；也不释放，它属于它那个内核）");

                var paused = false;

                try
                {
                    if (old.CanPause)
                    {
                        old.SetPause(true);
                        paused = true;
                    }
                }
                catch (Exception ex) { AppLog.Swallowed("换内核之前暂停旧播放器失败。", ex); }

                // 还在打开（CanPause 还是假）这种少见情况退回 Stop：宁可漏一次，也不能让
                // 两个播放器同时出声。
                if (!paused)
                {
                    try { old.Stop(); }
                    catch (Exception ex) { AppLog.Swallowed("换内核之前停旧播放器失败。", ex); }
                }

                _libVlc = slot!.Core;
                _coreRotation = rotation;

                AppLog.Info("换内核：③ 切到那一套自己的播放器并铺回状态");
                _mediaPlayer = slot.Player;

                ApplyPlayerState(_mediaPlayer);
            }
            finally
            {
                _switchingCore = false;
            }

            // 界面必须在这里把 VideoView 改挂到新的 Player 上
            AppLog.Info("换内核：④ 通知界面把窗口改挂过去");
            CoreRebuilt?.Invoke(this, EventArgs.Empty);

            AppLog.Info("换内核：完成（旧播放器留着，切回去时复用）");
        }

        /// <summary>把"用户那一套"铺到一个（可能是全新的）播放器上。</summary>
        private void ApplyPlayerState(MediaPlayer player)
        {
            player.Volume = Math.Clamp(_desiredVolume, 0, 100);
            player.Mute = _desiredMuted;
            player.SetRate(Math.Clamp(_desiredRate, 0.25f, 4.0f));

            if (_equalizerState != null) ApplyEqualizerTo(player, _equalizerState);
        }
    }
}
