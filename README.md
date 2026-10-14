# 星芽启动器 · StarSprout 0.4.2

Windows x64 星露谷物语与 SMAPI 启动器。界面参考 Axolotl，采用圆润的毛玻璃侧栏、卡片与柔和背景，使用原创绿色萌芽图标；窗口顶部可拖动，右上角提供最大化/还原。界面与后端独立编写。

作者：[LYHqwq123](https://github.com/LYHqwq123)。源码采用 MIT 许可，致谢见 [THIRD-PARTY.md](THIRD-PARTY.md)。

## 打开与游戏目录

1. 完整解压 Windows 运行包，双击根目录 `StarSprout.exe`。前置程序使用 .NET Framework 4.7.2，适用于 Windows 10/11。
2. 程序先检测 .NET 10 Desktop Runtime x64 与 Microsoft Edge WebView2 Runtime；缺少时从微软官方下载并验证签名，安装成功后进入主界面。.NET 安装可能弹出系统管理员授权。
3. 在游戏库中选择包含 `Stardew Valley.exe` 的游戏目录，选择原版或 SMAPI 启动。设置页可通过中国国内或官方线路准备 SMAPI 安装包，运行解压目录中的 `install on Windows.bat` 并按提示安装。关闭启动器会保留正在运行的游戏。

目录结构为根目录 `StarSprout.exe`、`StarSprout.exe.config`、`app/StarSprout.App.exe` 和 `runtimes/`。不要单独移动主程序。个人设置、缓存、备份位于根目录 `StarSproutData/`；运行包与源码包不包含这些数据。

## 首页与 SMAPI 国内线路

首页可单独更换游戏图片或恢复默认，不影响启动器背景。月份日历只记录经本启动器成功启动的日期，包括独立版/离线启动；日期以启动时的主题强调色圈出，同日显示累计次数，可切换月份。旧版使用记录无法补回，不会仅因打开页面就生成记录。

设置页默认提供 SMAPI 4.5.2 的中国国内线路（3DM 公开附件），并保留同版本官方 GitHub 回退。国内包已完整核实为 41,889,142 字节，与官方安装包 SHA-256 相同；下载后再次校验并安全解压，不自动执行脚本。此版要求游戏 1.6.14 或更高版本，下载器不会将旧包冒充最新版。详见 [下载来源与校验](StarSprout/Resources/SMAPI-Download.md)。

## 模组、整合包与离线

外观页可切换浅色、深色、OLED 或跟随系统，选择强调色及自定义本地 PNG/JPEG/BMP 背景；可调整模糊、可见度、卡片透明度、叠加色与图片适应方式，重启后保留设置。背景复制到启动器数据目录并转为安全 PNG，原图保留。

Mod 管理支持 ZIP 导入、明确的启用/禁用按钮、多选批量启停、全部停用、搜索、重复 ID 与依赖检查。开始排查时保存原组合，之后可以恢复；记录按游戏目录隔离，重启后可恢复。启停只调整文件夹名，不修改模组清单；游戏运行中暂停安装与启停。资源下载和缓存安装会自动识别同 ID 更新：先完整备份并校验，保留配置、原目录及停用状态；相同或较新版本保留，批次失败回滚。同名而不同 ID 的目录会停止，手动 ZIP 导入保留禁止覆盖行为。存档页提供 ZIP 备份。

Mod 行提供「删除」：确认名称和目录后，直接永久删除该 Mod 的所有文件，不生成备份、不留隔离副本，也不移入回收站。删除只接受当前列表的精确 Mod 目录，不接受父目录或外部路径；先检查链接、只读和文件占用。更新 Mod 时的自动备份独立保留。旧版本已经生成的副本不会自动清理。

资源中心提供作者公开发布源下载、本地缓存与软件内详情。中文社区入口包括 [3DM](https://mod.3dmgame.com/stardewvalley/133)、[小火社区](https://svmbbs.smallfire.cn/) 与 [ESOUS](https://esous.cn/)。社区、作者原站及 Nexus 网页统一使用系统默认浏览器打开。3DM 公开 ZIP 与作者/API 直链在启动器中直接下载、校验、缓存与安装；浏览器手动下载的 ZIP 使用「导入本地 ZIP」安装。网页、网盘分享、RAR/7z/EXE 不会被当成 Mod ZIP 安装。

社区目录接入 3DM 官方前端公开调用的检索/详情接口，以及小火社区的公开 Flarum 帖子接口，可在软件内搜索、翻页和查看详情。3DM 列表直接提供「下载」「下载安装」：自动解析首个公开 ZIP、下载、校验、缓存、安装并刷新 Mod 列表，无需打开网页或手动移动文件。首次选择一次游戏目录即可，运行中允许缓存下载。小火帖子的原站/网盘/Nexus 链接按其授权要求在默认浏览器中处理。ESOUS 暂未核实到可调用的公开下载 API，保留默认浏览器原站入口。第三方接口可能调整，遇到失败可使用原站入口或已有缓存。

订阅文件示例（将占位网址替换为维护者真实发布地址）：

```json
{"items":[{"id":"community-pack","name":"我的整合包","author":"社区维护者","kind":"pack","version":"1.0.0","description":"说明兼容的游戏和 SMAPI 版本","sourceUrl":"https://example.org/pack","downloadUrl":"https://example.org/pack.zip","sha256":"填写ZIP的64位SHA256"}]}
```

完整 Mod ZIP 需要各自的 `manifest.json`；仅含数据的汉化补丁可自动匹配已安装原版的同名 Mod 目录，完整备份后应用，并保留原版清单、配置和停用状态。截图中的 AloeMilkfish ZH 资源属于此类补丁，需要先有原版；缺少原版时保留缓存并明确提示。无清单补丁不接受 DLL/EXE/脚本。备份位于 `StarSproutData/ModBackups/`，保留恢复信息。已下载资源可导出为星芽离线包，在另一台电脑导入并安装；资源包不包含游戏本体或账户密钥。

离线页单独保存独立版游戏目录，直接启动本地程序，不需要 Steam/Xbox 账户。打开离线开关后，Mod 安装目标切换到该独立目录，并暂停在线下载与远程 AI。已有独立版文件夹可直接导入。[GOG](https://www.gog.com/en/game/stardew_valley) 提供购买后的 DRM-free 离线安装版本。目前未核实到官方授权的中国国内游戏本体直链，程序未内置第三方游戏本体下载源。

启动器运行库的离线准备方法见 `runtimes/README.md`。

## Nexus Mods

资源中心的 Nexus 目录直接调用官方 API，在软件内浏览最新发布、最近更新、热门下载，进行全站 Mod 名称搜索和分页。无需 API Key 即可读取公开描述和完整文件列表。输入 Mod ID 或粘贴 N 网链接，也会在软件中读取详情，不打开嵌入网页。浏览器入口作为可选备用；3DM、小火等社区原站链接继续使用默认浏览器。

Premium 账户连接本人的 API Key 后，可在文件列表中直接下载 ZIP 或下载安装。普通账户连接 API Key 并注册 NXM 入口后，点击「官网授权下载」或「授权并安装」，由原站生成临时授权，再交给星芽下载；此权限步骤仍需浏览器。文件列表会直接提示首次注册 NXM；游戏目录改变或游戏正在运行时只缓存。RAR、已归档或删除的文件不提供自动安装。未配置 API Key 的 NXM 打开官方手动下载页，普通浏览器下载的 ZIP 可导入。API Key 使用当前 Windows 账户的 DPAPI 加密保存。

SSO 登录流程已实现，但启用前需要 Nexus 审批的本应用 Application ID，不能复用其他启动器的身份。没有该 ID 时使用 Personal API Key 与在默认浏览器官网登录下载。遵循 [Nexus API 政策](https://help.nexusmods.com/article/114-api-acceptable-use-policy)，不会绕过下载权限。

## AI 日志诊断

AI 页面支持保存多家服务连接，各连接独立保存加密密钥，可切换服务与模型，按需获取服务商模型列表；也可手填模型名。内置 OpenAI、DeepSeek、通义千问、Moonshot、智谱、豆包、硅基流动、Ollama、Claude、Gemini 和自定义连接预设。Claude/Gemini 使用各自协议，其余预设使用 Chat Completions 兼容接口。实际可用模型由服务商账户与接口决定，不内置可能过时的模型清单。

启动器先做本地依赖和清单检查，按内置 ERROR/WARN、缺失依赖、版本不兼容、重复模组、异常和补丁冲突关键词提取日志上下文、去重，再生成脱敏快照。默认输入预算 12000 字符，可调整预算或补充关键词；字符预算与 token 估计用于减少发送量，实际计费由服务商的 tokenizer 决定。

先检查脱敏预览，再同意发送并分析。AI 会依据版本、UniqueID、关键日志和本地发现给出建议，不会自动改动模组。密钥不出现在前端状态或导出包中；日志发送前遮盖个人路径与常见敏感信息。本机 HTTP 需手动允许，远程 API 使用 HTTPS。保存配置和打开页面不会自动调用收费模型。

## 构建与检查

需要 .NET 10 SDK、Visual Studio Build Tools 与 .NET Framework 4.7.2 Targeting Pack。源码包解压后执行：

```powershell
.\StarSprout\tools\Build-Release.ps1
```

构建脚本输出 Windows 运行包、源码包和 SHA-256 文件至 `StarSprout/dist/`。可通过 `-Packages` 指定 NuGet 缓存。

`StarSprout/verification/` 中包含隔离检查与前端检查。`--smoke 输出目录` 渲染九个页面并检查最大化/还原；`--drag-test 输出目录` 验证顶部实际鼠标拖动；`--browser-test 输出目录` 用本地模拟社区站验证原生 ZIP 下载、登录弹窗和安装到隔离测试目录；`--appearance-test 输出目录` 用隔离配置验证各主题及背景图。检查不会启动游戏。前置程序提供 `--check-only --report=文件`、`--dry-run-plan --simulate-missing --report=文件` 及 `--self-test`。

已验证构建、页面渲染、真实窗口拖动与模拟接口。未使用真实 Nexus/AI 密钥进行收费或账户请求，也未在测试中启动游戏；运行库缺失安装流程通过模拟计划校验，本机已有所需运行库。

界面布局参考 [Axolotl Launcher](https://github.com/Mystic-Stars/Axolotl)，Nexus 流程参考研究 [JuniGrid](https://github.com/MLD-yu/JuniGrid)，未复制其品牌、源码、客户端 ID 或凭据。依赖与许可见 [THIRD-PARTY.md](THIRD-PARTY.md)。

## 发布到 GitHub

源码包包含许可证、更新记录和 Windows 构建工作流。先构建并验证发布包，再运行 `StarSprout/tools/Publish-GitHub.ps1`。默认目标为 `LYHqwq123/StarSprout`，使用 Git Credential Manager 中当前作者账户的凭据；本机未登录时可加 `-Login`，仅在 GitHub 官方设备登录页面完成授权，不需要把密钥发给任何人。脚本校验 SHA-256、推送干净源码、创建草稿 Release，上传运行包/源码包/校验文件后才转为正式发布；不会上传个人设置或缓存。
