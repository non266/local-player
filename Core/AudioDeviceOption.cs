using System;

namespace 播放器.Core
{
    /// <summary>
    /// 一个可选的音频输出设备。
    /// <para>
    /// <see cref="DeviceId"/> 为 <c>null</c> 表示"系统默认设备"；
    /// <see cref="Module"/> 是枚举出该设备的 libvlc 输出模块（Windows 上是 <c>mmdevice</c>），
    /// 为 <c>null</c> 表示交给 libvlc 用当前模块处理。
    /// </para>
    /// </summary>
    public sealed class AudioDeviceOption
    {
        public AudioDeviceOption(string? deviceId, string description, string? module = null)
        {
            DeviceId = deviceId;
            Description = description;
            Module = module;
        }

        /// <summary>设备标识；<c>null</c> 表示系统默认设备。</summary>
        public string? DeviceId { get; }

        /// <summary>显示给用户的设备名。</summary>
        public string Description { get; }

        /// <summary>枚举出这个设备的输出模块名，可能为 <c>null</c>。</summary>
        public string? Module { get; }

        /// <summary>是否是"系统默认设备"。</summary>
        public bool IsDefault => string.IsNullOrEmpty(DeviceId);

        /// <summary>两个设备是否指向同一个标识。</summary>
        public bool Matches(string? deviceId) =>
            string.Equals(DeviceId ?? string.Empty, deviceId ?? string.Empty, StringComparison.Ordinal);

        public override string ToString() => Description;
    }
}
