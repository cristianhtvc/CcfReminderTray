# 会议截稿提醒（CcfReminderTray）

一个面向 Windows 11 的开源系统托盘应用。右键托盘图标，可查看 ICML、ICLR、NeurIPS、AAMAS、AAAI、DAI 六个会议的主会常规论文摘要注册与全文提交倒计时；详情窗提供其他投稿节点、来源、原始时间和本地时间。

## 功能

- 后台获取 [ccfddl](https://github.com/ccfddl/ccf-deadlines) 的 `allconf.json`，保留最近一次有效缓存；离线时可继续查看缓存。
- 对 AoE、UTC 偏移和太平洋时区换算，区分已截止、待公布及待核验状态。
- 可设置桌面提醒、提醒阈值和当前用户开机启动；应用未运行时不会提醒。
- 可在详情窗手动修正主会常规摘要注册／全文提交时间，保留修正依据，并在上游时间变化时提示差异。
- 无需 Agent Plan、Claude Code 或任何大模型 API Key 即可运行。

**截止时间来自社区聚合数据，尚未逐项对照当届会议官网。重要投稿安排请以会议官网及官方投稿系统为准。** 应用每个本地日最多发送一条汇总提醒；当天新出现的紧急截止可能因此漏报。

## 从源码运行

需要 Windows 11 与 .NET 10 SDK。在仓库根目录运行：

```powershell
dotnet build CcfReminder.slnx -c Release
dotnet test CcfReminder.slnx -c Release
dotnet run --project src/CcfReminderTray -c Release
```

应用采用 C#、WinForms 和 .NET 10。`packaging/CcfReminderTray.iss` 是 Inno Setup 安装脚本；本仓库不跟踪本机构建产物、缓存或个人设置。

## 发布状态与使用说明

当前源码版本为 **1.0.4**。便携版和安装程序已在开发机完成构建及安装／卸载检查，但**尚未在未安装 .NET 运行时的干净 Windows 11 环境完成验收**。安装包与 exe 也尚未进行代码签名。公开源码不代表这些发布检查已通过。

安装、卸载、通知限制、数据目录与手动修正的详细说明见 [使用文档](packaging/README.md)。第三方许可与数据来源见 [第三方声明](packaging/THIRD-PARTY-NOTICES.txt)。

## 许可

本项目源码采用 [MIT License](LICENSE)。ccfddl 数据和安装工具等第三方内容遵循其各自许可，详见第三方声明。
