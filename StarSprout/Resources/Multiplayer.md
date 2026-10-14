# 星芽联机来源与使用

- 底层核心：EasyTier v2.6.4，LGPL-3.0，Copyright EasyTier contributors。
- 核心发行页：https://github.com/EasyTier/EasyTier/releases/tag/v2.6.4
- 对应源码：https://github.com/EasyTier/EasyTier/archive/refs/tags/v2.6.4.zip
- 公开节点说明：https://doc.easytier.cn/guide/network/networking-without-public-ip.html
- 免虚拟网卡说明：https://doc.easytier.cn/guide/network/no-root.html
- UDP 转发说明：https://easytier.cn/guide/network/port-forward.html
- 陶瓦交互参考：https://github.com/burningtnt/Terracotta

星芽独立编写房间、邀请码、启动与界面控制，采用未修改的 EasyTier 命令行程序；没有使用陶瓦的 Minecraft 扫描器、游戏握手、客户端身份或邀请码。星芽 SG1 房间码只在相同协议的星芽客户端之间使用。公开节点用于发现和必要时的中转，并不保证任何网络下都能直连。

首次用户明确点击才下载核心，仅保留 core.exe、cli.exe、许可和元数据。官方 ZIP 为 32,657,720 字节，SHA256 为 27af91e270e554709b048bd32327fefd2dfce5062ae1e8701af7550c6f525f84；程序内同时固定两个 EXE 的哈希。启动之前再次验证，不从本地可修改元数据中信任程序哈希。不安装 wintun、WinDivert、Windows 服务或自动防火墙规则。

核心以 no-TUN 运行，管理端口仅绑定回环地址；房主入站只允许游戏 UDP 24642，访客以本机回环 UDP 转发连接房主，不导出局域网、不更改系统 DNS 或默认路由、不启用 UPnP。房间名和口令分别随机生成，口令仅传给子进程环境，不进入命令行或配置文件；显示日志前脱敏。邀请码包含加入凭据，请仅分享给参与者。

房主先在星芽创建房间，然后在游戏内合作模式主持农场，准备足够小屋。房客在星芽输入同一个 SG1 邀请码，发现房主后在游戏内加入局域网/IP：127.0.0.1，端口 UDP 24642。双方需要兼容版本与模组组合。退出房间或关闭启动器会终止本启动器拥有的联机核心，不结束游戏或其他 EasyTier 实例。

检查包括实际官方核心 ZIP 安装、邀请码输入与状态解析、假的进程边界协调、两个真实核心的纯回环 UDP 请求/响应。不把进程启动或公共节点连接当作玩家已连接；没有真实跨地域双人游戏验证。相关脚本为 verification/easytier-local-udp.ps1；使用独立临时目录和回环监听，关闭 STUN、UPnP 与打洞，不访问公共房间。
