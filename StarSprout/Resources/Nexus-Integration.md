# Nexus 官方 API 浏览与下载（2026-10-14）

0.4.2 使用官方 v2 GraphQL 实现启动器原生目录、全站名称搜索、分页、详情和文件列表，替代网页内嵌。社区原站及可选下载授权页面使用系统默认浏览器。

## 公开浏览

- API：`POST https://api.nexusmods.com/v2/graphql`，见 [官方 GraphQL 文档](https://graphql.nexusmods.com/#query-mods)。
- 最新发布、最近更新及按下载数排序的热门列表用 `mods` 的 `count` / `offset` 分页；`totalCount` 来自服务端，不用局部筛选冒充全站搜索。
- 星露谷域名固定为 `stardewvalley`，游戏 ID 为 `1303`，返回值再次检查游戏；名称搜索使用经真实请求核实的 `nameStemmed MATCHES` 与相关性排序。
- 公开详情和 `modFiles` 可匿名读取，包括描述、文件名、版本、类别和大小；在本机 API 检查中已实际验证，不要求用户先提供密钥。
- 可输入数字 Mod ID 或合法星露谷 Mod URL。正文按文本显示，接口不向任意第三方域发送密钥。

## 下载权限

Premium 连接本人 API Key 后，可直接获取下载链接并由启动器下载、校验 ZIP、缓存与安装。普通账户需要原站生成授权的 NXM `key` 与 `expires`；软件内显示「官网授权下载」「授权并安装」及首次 NXM 注册入口，授权页使用默认浏览器。没有绕过登录、等待或账户权限。

公开 API 元数据不代表可匿名下载所有文件。RAR、ARCHIVED 和 DELETED 文件不会被当作可自动安装的 ZIP。下载器仍进行路径、格式及大小校验，游戏运行中禁止安装；授权期间更换游戏目录则只缓存。

权限依据：[Nexus 官方 API 客户端文档](https://github.com/Nexus-Mods/node-nexus-api/blob/master/docs/classes/_nexus_.nexus.md)，明确普通账户直接获取链接需要网站生成的临时授权。参考 [官方 Vortex Nexus 客户端](https://github.com/Nexus-Mods/Vortex/blob/master/packages/game-extension-test/src/nexusClient.ts)。[API 使用政策](https://help.nexusmods.com/article/114-api-acceptable-use-policy) 仍适用；SSO 需要注册本应用 ID，没有复用他人的 ID 或凭据。

## 验证

NexusChecks 63 项模拟检查通过；含真实匿名目录、全站名称搜索、详情与文件列表共 67 项通过。没有使用真实账号、API Key 或付费请求，也没有在实际游戏目录安装文件。
