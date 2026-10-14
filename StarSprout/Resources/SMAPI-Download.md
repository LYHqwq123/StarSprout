# SMAPI 国内下载线路

核实日期：2026-10-14。

星芽提供国内社区线路与官方 GitHub 线路，均固定为经过核实的 SMAPI 4.5.2 安装包，要求《星露谷物语》1.6.14 或更新版本。界面必须显示版本及要求，不能把这一固定版本称为永远最新。国内线路是社区转载，不能标为 SMAPI 官方中国镜像。

## 已核实来源

| 线路 | 页面 | ZIP |
| --- | --- | --- |
| 国内 · 3DM | https://mod.3dmgame.com/mod/255905 | https://dmod.3dmgame.com/static/upload/resource2/202607/_20260708111457_68634.zip |
| 官方 · GitHub | https://github.com/Pathoschild/SMAPI/releases/tag/4.5.2 | https://github.com/Pathoschild/SMAPI/releases/download/4.5.2/SMAPI-4.5.2-installer.zip |

3DM 页面自用公开接口 `GET https://mod.3dmgame.com/api/mods/255905` 返回 `game_id=10`、`mods_version=4.5.2`，附件名称为 `SMAPI 4.5.2 4.5.2.zip`。实际附件响应为 HTTP 200、`application/zip`、41,889,142 字节，支持 HTTP Range。不使用匿名代理或假造镜像域名。

已完整读取国内附件并核对：SHA256 与下述官方普通安装包一致；实际包的 Windows 安装脚本及内部安装数据已在隔离目录成功解压，未运行安装器。此项验证针对核实当日的文件，程序每次下载仍重新校验。

官方版本和附件信息通过 [Pathoschild 官方 GitHub Release API](https://api.github.com/repos/Pathoschild/SMAPI/releases/latest) 核实；普通安装包的官方 `digest` 是：

```text
sha256:dd01ddca7b566bfe0d3b3d2d03833496abc56c53da976241f2ab443f5484acc4
```

来源和兼容要求还可在 [SMAPI 官方网站](https://smapi.io/) 与 [官方发布记录](https://github.com/Pathoschild/SMAPI/blob/develop/docs/release-notes.md) 查看。[上游源码与许可](https://github.com/Pathoschild/SMAPI) 归 Pathoschild/SMAPI 项目所有。星芽不捆绑 SMAPI 安装器或游戏文件，安装包由用户操作时从所选来源下载。

## 下载行为

1. 优先使用用户选择的线路；默认国内 · 3DM。
2. 下载到星芽数据目录中的 `Smapi/Downloads`，逐字节计算 SHA256，必须与上述同版本官方值相同。ZIP 内容变化或下载页伪装将被拒绝。
3. 国内网络请求失败时回退到相同版本、相同 SHA256 的官方线路。文件校验失败会明确报错，不会把不一致文件当安装器打开。
4. 验证后在独立目录安全解压，拒绝路径越界、符号链接、重复路径及异常体积，并要求存在 Windows 安装脚本与内部安装文件。
5. 展示安装包所在文件夹及安装说明。星芽不静默执行 `.bat`/`.exe`，不把 SMAPI 安装器放到 `Mods`，也不自行覆盖游戏文件。

已验证的 ZIP 可在离线模式下重新准备；离线缓存仍要重新核对官方 SHA256。用户需运行包内 `install on Windows.bat` 完成 SMAPI 本身的安装。更老的游戏版本应按上游兼容说明选择对应旧版，不自动降级。

## 集成契约与验证

`SmapiDownloadService.GetState()` 返回两条线路及兼容要求；`DownloadAsync("domestic" | "github", ct)` 返回 ZIP 路径、解压目录、Windows 安装脚本路径、校验值和实际线路。`Progress` 提供下载、回退、校验、解压和完成阶段。调用方可打开返回的目录，不能据此自动执行脚本。

`verification/SmapiChecks` 使用隔离临时目录和模拟 HTTP 覆盖下载、缓存、哈希、回退、取消与安全解压；`--sample <path>` 可对实际国内 ZIP 再核对官方哈希并验证完整包提取。所有验证均不执行下载的代码、不更改实际游戏目录。

本次验证结果：26 项隔离服务检查通过，3 项实际国内 ZIP 检查通过。
