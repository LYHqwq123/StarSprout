# 离线安装启动器运行环境

双击根目录 `StarSprout.exe` 会先检测 .NET 10 Desktop Runtime x64 和 WebView2 Runtime，缺少时从微软官方下载、校验并安装，再打开主界面。.NET 安装可能出现 Windows 管理员授权窗口。

在联网电脑上运行以下 PowerShell 命令，可先下载两个完整的离线运行库安装程序：

```powershell
Start-Process -FilePath '.\StarSprout.exe' -ArgumentList '--download-offline' -Wait
```

下载完成后，将整个启动器目录（包括 `runtimes`）复制到离线电脑，再双击根目录 `StarSprout.exe`。此操作只下载微软运行库，不包含游戏、SMAPI 或模组。

离线运行库需保持微软原始签名。不要单独复制 `app/StarSprout.App.exe`，主程序依赖前置启动程序检测环境。
