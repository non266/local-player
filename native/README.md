# native\ —— 随包分发的原生文件

这里放的是**不属于任何 NuGet 包、但发行版必须有**的原生文件。
构建时由 `播放器.csproj` 里的 `CopyDiscPlugins` target 复制进输出目录的
`libvlc\win-x64\plugins\access\`（`dotnet publish` 之后同样复制），打包脚本整目录打包，
所以 `dist\` 里的便携版 / 安装程序会自动带上它们。

> ⚠ 这些是二进制，不要手改、不要用别的版本替换。换版本时把下面的哈希一起更新。

## vlc-3.0.24\ —— DVD 访问插件

| 文件 | 字节数 | SHA256 |
| --- | --- | --- |
| `libdvdnav_plugin.dll` | 288,192 | `23D3A2C7DE5C495B193672CB7B66920CECE4FEA8517271C03A72DE3E9102C050` |
| `libdvdread_plugin.dll` | 222,144 | `0C17C6D629317626CD1FDA87EF13B80122C2C1C82FCDBFE7F72AF06F9C215287` |

- **来源**：VLC 3.0.24 官方 win64 构建
  <https://download.videolan.org/pub/videolan/vlc/3.0.24/win64/vlc-3.0.24-win64.7z>
  （压缩包 SHA256 `1ED59C09152E78AFF84663FA76EFAB26057716576DB74332858810E5EF54AE1F`），
  从包里的 `vlc-3.0.24\plugins\access\` 原样取出，未改名、未重打包。
- **为什么需要**：本项目用的 `VideoLAN.LibVLC.Windows` 3.0.24 包**没有**这两个插件
  （包里的 `plugins\access` 有 `libcdda` / `liblibbluray` / `libvcd`，唯独没有 dvdread / dvdnav）。
  少了它们，`dvd:///L:` 与 `dvdsimple:///L:` 都只会得到 `Your input can't be opened`，
  「打开光盘」里 DVD-Video 就走不通。实测把这两个文件放进同一套 libvlc 之后，
  `dvdsimple:///L:` 正常播放（详见 `docs/1.11.0计划.md` 第 1 节）。
- **版本必须配套**：插件与 `libvlccore.dll` 同属 3.0 系列、且与包里的 libvlc **同为 3.0.24**；
  升级 `VideoLAN.LibVLC.Windows` 时要同步换这两个文件并重算哈希。
- **许可证**：VLC 的插件，GPLv2+（与包里已有的三百多个插件同一性质）。
  它只是被 libvlc 在运行时加载，没有链接进本项目的代码；随 libvlc 一起分发。
- **不包含 `libdvdcss.dll`**：CSS 加密的 DVD 需要它，VLC 官方包也不带（法律原因）。
  用户自己放一个到 `libvlc\win-x64\` 下可用；这一点在 README 的「已知限制」里写明。

## 蓝光

包里的 `liblibbluray_plugin.dll` 是 NuGet 包自带的，**没有**往这里加东西。
1.11.0 只做到"识别出 `BDMV` 并如实说暂不支持"：加密蓝光需要 AACS 密钥库，
本机也没有蓝光设备，没有真机验证过的代码不写进发行版。
