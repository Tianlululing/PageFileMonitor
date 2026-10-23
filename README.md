# PageFileMonitor · 页面文件监控

一个 Windows 桌面小工具，实时显示 **页面文件（pagefile.sys）占用、系统提交内存、以及页面文件所在卷的磁盘读写吞吐**，并附带一个提交内存压力测试按钮。

用 C# / .NET 8 WinForms 写成，全部逻辑在单个 `Program.cs` 里，无第三方依赖。

## 功能

- **页面文件总量与已用**：多卷合计，带进度条和占用百分比
- **提交内存**：当前提交量 / 提交上限
- **分卷显示**：每个含页面文件的卷显示实时读/写速率与本次运行累计字节，并标注所属物理磁盘号
- **多卷细节**：卷数大于 1 或存在未生效配置时，每个卷下方多一行，列出该卷各页面文件的已用/总计
- **未生效配置检测**：读取注册表里配置的页面文件，把「已配置但需要重启才生效」的项单独标出来
- **提交内存压力测试**：申请指定大小的提交内存，60 秒后自动释放，也可手动立即释放
- **DPI 适配**：按系统 DPI 对 96 DPI 设计坐标等比缩放
- **窗口自适应**：高度随卷数量动态调整

## 运行要求

| 项目 | 要求 |
|---|---|
| 系统 | Windows 10 / 11 x64 |
| 运行时 | 无需安装，发布版自带 .NET 8 运行时（自包含单文件） |
| 权限 | 普通用户即可；仅做性能计数器与注册表读取，不需要管理员 |

## 使用

双击 `PageFileMonitor.exe`，界面每秒刷新一次。

**提交内存压力测试**：在「大小 (MB)」填入数量（1 ~ 65536，默认 1024），点「申请提交内存」。程序会真正 `VirtualAlloc` 出这么多提交内存，状态栏显示倒计时；再点一次「立即释放」或等满 60 秒会自动释放。这个功能用于观察提交内存上涨时页面文件使用量的变化，**不要在内存紧张的机器上填大数值**。

## 实现要点

程序直接调用 Win32 API，没有引入外部库：

- **枚举生效中的页面文件**：`psapi.dll!EnumPageFilesW`。注意返回的 `TotalSize` / `TotalInUse` 单位是**内存页**（通常 4 KB），需要乘以 `GetPerformanceInfo` 给出的 `PageSize` 才换算成字节
- **性能计数器**：PDH 的 `\LogicalDisk(*)\Disk Read Bytes/sec`、`\Disk Write Bytes/sec` 和 `\Paging File(*)\% Usage`
  - `% Usage` 返回的是 **0..100 的百分数**（0.99 表示 0.99%），取用时必须除以 100，否则已用量会放大 100 倍
  - 计数器数组用 `PdhGetFormattedCounterArray` 一次性取回，避免逐个实例查询
- **未生效的页面文件**：`HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PagingFiles`，类型 `REG_MULTI_SZ`，每条形如 `<路径> <初始MB> <最大MB>`
- **卷 → 物理磁盘号**：`CreateFile("\\.\F:")` + `IOCTL_STORAGE_GET_DEVICE_NUMBER`，结果按盘符缓存，失败则不缓存以便下轮重试
- **采样策略**：每秒只采一次，单次采样异常静默跳过（下个周期自动重试），不会让界面卡死；只有卷/文件行集合发生变化时才重建控件
- **累计读写**：按采样间隔对速率做积分得到的「本次运行累计」，不是磁盘的 Lifetime 计数

## 构建

```bat
build.cmd
```

等价于：

```bat
dotnet publish -c Release -o publish -p:IncludeNativeLibrariesForSelfExtract=true
```

产物是 `publish\PageFileMonitor.exe`，单文件、自包含、开启压缩，约 65 MB。

## 目录结构

```
PageFileMonitor/
├── Program.cs               # 全部代码：WinForms 界面 + Win32 互操作 + 采样逻辑
├── PageFileMonitor.csproj    # net8.0-windows / WinForms / 单文件自包含
├── build.cmd                # 一键发布
├── smoke.ps1                # 冒烟测试：启动 exe、等待、PrintWindow 截图、正常关闭
├── bin/  obj/  publish/     # 构建产物（已在 .gitignore 中排除）
```

`smoke.ps1` 使用相对脚本自身的路径，先执行 `build.cmd` 再运行；它会把窗口截图保存为同目录的 `window.png`，用于无人值守地确认「程序能启动、能画出窗口、能正常退出」。

## 已知限制

- 只统计**有页面文件的卷**，没有页面文件的磁盘不会出现在列表里
- 压力测试受系统提交上限约束，申请过大时会失败并显示 Win32 错误码
- 未做代码签名，首次运行可能触发 SmartScreen 提示（选择「更多信息 → 仍要运行」即可）
