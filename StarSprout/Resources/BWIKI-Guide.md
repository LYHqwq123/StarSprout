# BWIKI 图文攻略接入

- 内容来源：https://wiki.biligame.com/stardewvalley/
- 固定 API：https://wiki.biligame.com/stardewvalley/api.php
- 使用 MediaWiki action=query 搜索和查询修订信息，action=parse 获取用户选择的正文与目录；无需密钥。不请求用户账号信息，不绕过站点访问限制。
- 接口文档：https://www.mediawiki.org/wiki/API:Parsing_wikitext
- 来源内容说明：https://wiki.biligame.com/stardewvalley/帮助:编辑入门

正文由启动器重新排版，仅保留阅读所需的文字、标题、表格、列表、链接和图片；不运行原站脚本、加载站点广告或嵌入整站登录页。图片仅允许 BWIKI 星露谷图片目录下的公开光栅图片，经本地代理按需读取、校验格式和大小后缓存。原站内部词条链接在阅读器内打开；原文按钮通过默认浏览器打开。

收藏、最近阅读、正文缓存与图片缓存位于 StarSproutData/Guides/。离线模式不发出攻略或图片联网请求，未缓存文章会明确提示；未加载图片需要联网后才能补全。缓存仅限用户实际请求内容，不进行整站批量下载。

正文通常遵循 CC BY-NC-SA 3.0：https://creativecommons.org/licenses/by-nc-sa/3.0/deed.zh-hans 。阅读页保留来源、原文与历史入口、许可，注明排版简化。游戏画面、地图及其他素材以原站特殊标注为准。攻略内容与图片不随启动器或源码包分发，不适用软件的 MIT 许可。

检查覆盖接口参数、分页与错误、缓存和离线行为、收藏与历史、图片来源与格式，以及不可信 HTML 的安全排版。真实联网检查仅查询公开百科，不涉及游戏文件或用户密钥。
