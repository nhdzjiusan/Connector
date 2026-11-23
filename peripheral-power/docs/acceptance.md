# 1.2.0 发布验收

下列步骤用于复测。文末单列本轮实际覆盖范围。除工作流外，路径默认相对 peripheral-power/。

| 项 | 文件或函数及动机 | 手工验证 |
|---:|---|---|
| 1 | PeripheralPower.csproj 显式声明 exe 元数据，便于追溯下载文件 | 在属性→详细信息核对版本 1.2.0.0、产品和公司 |
| 2 | 根 .github/workflows/peripheral-power-release.yml 用标签验证版本、构建并更新发布附件 | 检查新标签产出带版本号的 exe 与 .sha256；重跑同一标签应替换同名附件 |
| 3 | 根 LICENSE 覆盖原创代码，NOTICE 保留 HidSharp Apache-2.0 署名和原始许可 | 对照 NuGet 2.6.4 的 LICENSE.txt 与项目内文件 |
| 4 | 两级 README 给出文件校验方法，便于发现下载损坏 | 用 certutil 计算 SHA256，对比附件第一列 |
| 5 | Program.Main、OfferRestart、Diagnostics.ErrorLog 记录界面与致命异常；单次提示提供人工重启 | 在测试构建中分别从按钮、后台线程抛出异常，检查日志、恢复入口、点击后只启动一个实例；错误文件接近 1 MiB 时产生 .1 |
| 6 | Settings.RepairStartup 只修复已存在的本程序 Run 项，避免移动后失效 | 开自启、退出、移动 exe、启动两次；注册表应改为新路径，只有首次提示 |
| 7 | PowerContext 提供两种菜单退出方式，面板在已开启自启时确认 | 分别退出后查看当前用户 Run 项，再测试取消确认 |
| 8 | PowerContext.Merge 按来源与设备 ID 保存读数，完整枚举配合拓扑事件确认移除 | 成功读取后使设备休眠，应保留卡片及旧时间；拔接收器后卡片消失 |
| 9 | UsbBattery.ReadAsync 将枚举和读取纳入三秒预算，使用按路径命名的互斥锁避免重叠写 | 让一台设备沉睡、另一台在线，同时刷新和运行 --scan；正常设备仍有结果，超时有 partial 标记 |
| 10 | ScanToFile 先检查目标目录，错误走 stderr 和日志 | 指定不存在的父目录，退出码 1，目标目录没有伪 JSON |
| 11 | Program.Main 单独处理帮助、版本和未知参数 | PowerShell 用 `& .\PeripheralPower.exe --version | Out-String` 等待 WinExe；检查帮助、未知项退出码 2 |
| 12 | UsbBattery.HeadsetSample 将状态 0 保留为未知供电参考；ReadMouse 的暂不可用状态交给缓存与有线端点 | 用真机对比插拔线、休眠、充满时的 --scan 和面板；不凭接收器在线判断充电 |
| 13 | BatteryPanel.SetData 用 ID 复用 DeviceCard，拓扑变化才重新布局 | 五行以上滚动到末尾，刷新相同设备；焦点、滚动和卡片应保留 |
| 14 | BatteryPanel 光斑局部失效、系统高对比度与减少动画，降低重绘负担 | 切主题、关系统动画，移动鼠标；在高对比度下检查文字可读和光斑停止 |
| 15 | PowerContext.UpdateFallback 缓存 TrayArt.MakeReading 的 Icon，退出统一释放 | 缩小任务栏空白区域，反复刷新相同读数，观察 GDI 数量稳定，再退出 |
| 16 | BluetoothBattery.ReadDeviceAsync 对 Battery Level 先缓存后实读，Generic 图标接纳未知外观 | 接入有标准电池服务的未分类 BLE 外设；在电台不可用时检查旧读数 |
| 17 | UsbBattery.LoadProtocols 读取可选 JSON 白名单，复用限定协议 | 在隔离配置目录试无文件、无效类型、不合法 Usage 和已知兼容别名；无效配置应记错并回退 |
| 18 | PowerContext.NotifyLow 与设置区开关按阈值去重，避免重复通知 | 在测试数据中依次提供 21、20、19、10、10、5%；应提示三次，关开关后停止 |
| 19 | Settings 保存 JSON，临时文件原子替换；Run 项作为实际自启来源 | 仅保留 interval.txt，设为 12；启动、改为 6、重启，确认 JSON 及面板一致 |
| 20 | 设计规范加入 125%/175%，docs/light.png、dark.png 单独展示两种主题 | 按设计规范逐项复测 100/125/150/175/200%，特别关注滚动、跨屏和 checkbox |
| 21 | 根 README 说明工具边界、系统、隐私、构建贡献和许可 | 从首页找到两张图、验证命令、下载入口及测试命令 |
| 22 | 子目录 README 及 overflow.svg 说明托盘、卸载、解压、签名和误报排查 | 从折叠区打开面板，验证日志位置、exe 签名；按具体哈希查杀软报告 |
| 23 | 子目录 README 兼容矩阵区分实测、已实现与设备限制 | 对照连接方式及 --scan 检查读数；标准 BLE 不代表任意 2.4G 接收器 |
| 24 | 根 CHANGELOG.md 与 .github/ISSUE_TEMPLATE/bug.yml 固化变更和故障证据 | 查看 1.1.0 起的条目，检查模板必填项与脱敏提示 |

### 本轮验证范围

Windows x64 实际构建并运行了单文件、CLI、真实接收器扫描，以及合并与卡片复用回归检查。深浅色图片由真实窗体使用示例数据生成。此前已实读三款迈从设备；本轮对照时接收器在线而设备没有返回有效百分比，新旧版本均如此。物理插线供电切换、独立 BLE 硬件、不同显示器 DPI、登录周期、系统通知和致命故障恢复仍需按表实测。GitHub 托管发布以正式标签运行记录为准；VirusTotal 以相同文件哈希的实际报告为准。
