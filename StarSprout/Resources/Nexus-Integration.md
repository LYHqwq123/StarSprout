# Nexus 浏览器打开与下载（2026-10-14）

0.4.0 按用户选择将社区、作者原站和 Nexus 网页改为系统默认浏览器打开，避免内嵌网页兼容问题。Mod 搜索、API 文件信息、详情与直接 ZIP 下载仍由启动器处理。

## 使用方式

1. 粘贴 Nexus 星露谷 Mod/文件网址或数字 ID，在默认浏览器打开。
2. `Manual Download` 的 ZIP 由浏览器保存，完成后在星芽导入本地 ZIP。
3. 连接本人 API 账号，点击「允许浏览器管理器下载」注册 `nxm://`；原站的 `Mod Manager Download` 可交给星芽下载、校验和缓存。打开对应 Mod 网页时勾选管理器下载后安装，目标游戏保持不变且未运行时可自动安装。
4. 未连接 API 账号的 NXM 会打开同文件的官方手动下载页；Premium API 直下仍遵循官方权限。

## 参考与权限

研究了 [JuniGrid](https://github.com/MLD-yu/JuniGrid) 的公开授权与 NXM 流程，没有复制其实现、客户端 ID 或凭据。SSO 需要 Nexus 发放本应用 ID，见 [官方示例](https://github.com/Nexus-Mods/sso-integration-demo)。没有预置获批 ID，不假称已开通。

[官方 API 政策](https://help.nexusmods.com/article/114-api-acceptable-use-policy) 将 Personal API Key 用途限定在个人使用或测试，面向公众发布应用需由开发者注册自己的应用。登录、下载等待和账户权限遵循原站；星芽不绕过这些要求。

网址入口只接受 Nexus 官方 HTTPS、星露谷路径与合法文件参数。下载缓存不持久化签名 URL、授权 key 或 cookie。网页浏览已从正式用户流程移至默认浏览器；旧的内嵌组件仅保留本地诊断回归代码，不再用于社区链接。

39 项 Nexus 模拟检查通过；没有使用真实账号或付费请求。国内访问情况取决于原站及用户网络。
