# 社区模组接口核实（2026-10-13）

## 3DM Mod站

公开前端 `https://mod.3dmgame.com/StardewValley` 使用以下匿名请求，已实测，不需要登录或 API Key：

```http
POST https://mod.3dmgame.com/api/search/getModlist
Content-Type: application/json

{"page":1,"pageSize":24,"search":"","original":0,"time":0,"order":0,"gameType":0,"gameId":[10]}
```

星露谷的实际 `game_id` 是 `10`。用户提供的旧路径 `/stardewvalley/133` 中 `133` 不是游戏 ID，不能用于 `gameId` 参数。

详情：`GET https://mod.3dmgame.com/api/mods/257914`，其中 `data.mods_resource[].mods_resource_url` 包含公开附件。服务仅将 `https://dmod.3dmgame.com/static/upload/*.zip` 或主站相同路径的 ZIP 标为可直接安装。网盘、网页、RAR、Nexus 页面不会作为 ZIP 安装；下载后的文件仍交由 ContentService 校验和安全导入。

实测样本：`https://dmod.3dmgame.com/static/upload/resource2/202608/_20260827201441_67918.zip`，HTTP 200，Content-Type `application/zip`，73,683 字节；含 `DailyScreenshot/manifest.json`、DLL 和 i18n。仅下载到研究目录，没有安装到用户游戏。

用户截图资源为 `258808`：`AloeMilkfish ZH(2026.9.9)`。其公开附件 `https://dmod.3dmgame.com/static/upload/resource2/202609/_20260910002643_34982.zip` 返回 HTTP 200、`application/zip`，2,547 字节。ZIP 没有 `manifest.json`，只含 `[CP] AloeMilkfish/code/locations.json`、`i18n/default.json`、`i18n/zh.json`，属于汉化覆盖补丁。0.3.1 可缓存这类数据补丁并自动匹配已有原版目录，备份后应用；没有原版时提示缺少原版且保留缓存，不能把补丁冒充完整 Mod。

0.3.1 列表可直接点「下载安装」，先从详情自动选择首个公开 ZIP，再下载、验证与安装，不打开原网页。完整 ZIP 与数据补丁均经过路径、大小与格式检查。已对完整 DailyScreenshot 样本及隔离 AloeMilkfish 目录验证安装；没有修改用户游戏。

这是网站自己使用的公开网页接口，不代表官方承诺稳定的第三方 API。代码为登录限制、重定向、HTML 响应和接口失效提供原站浏览提示。

## 小火星露谷社区

主站公开页面的 Flarum 配置明确给出 `apiUrl`：`https://svmbbs.smallfire.cn/api`。已实测匿名：

- `GET https://svmbbs.smallfire.cn/api/discussions?page%5Blimit%5D=24&page%5Boffset%5D=0`
- `GET https://svmbbs.smallfire.cn/api/discussions/114`

可在启动器中读取帖子列表、作者和首帖详情。样本 [CJB 传送点汉化](https://svmbbs.smallfire.cn/d/114) 的模组模板引用 Nexus 的 `DownloadWebUrl`、Mod ID、File ID，未提供独立 ZIP 镜像。服务保留 Nexus 页面链接，仅供站内浏览；没有绕过 Nexus 账号或下载权限。

## 星露谷小站

[原站](https://esous.cn/) 及 [模组帖子样本](https://esous.cn/thread-77.htm) 使用 Xiuno 社区页面。未核实可靠的匿名模组检索与完整 ZIP 下载 API。服务不编造 API，保留内嵌原站浏览。

## 验证

`StarSprout/verification/CommunityChecks` 的 36 项模拟检查通过，包括游戏筛选、下载格式识别、纯文本内容、URL 校验、JSON 契约、响应上限、重定向和权限限制、离线阻断。

`--live` 的 4 项真实匿名检查通过：3DM 搜索、3DM 附件详情、小火列表、小火帖子及 Nexus 权限限制。
