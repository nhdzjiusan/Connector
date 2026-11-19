# 外设电量

Windows 10 2004 及更新版本的 x64 托盘应用。独立发布的可执行文件可直接双击运行。

[下载 Windows x64 免安装版](https://github.com/nhdzjiusan/Connector/releases/tag/peripheral-power-v1.1.0)

- 每台成功读到电量的外设显示一个百分比图标。鼠标悬停可看型号和完整电量，点击打开详情与设置。
- 刷新间隔可选 5–15 分钟，默认 10 分钟。可随时手动刷新，接收器插拔和系统从睡眠恢复也会触发刷新。
- 勾选「登录时启动」后，Windows 登录时会静默启动。请从计划长期保留的位置运行程序，再开启这项设置。
- 读不到电量的外设不占托盘位置。所有设备都没有读数时，保留应用入口，方便打开设置和手动重试。
- Windows 可能把图标归入任务栏「^」的折叠区。可在 Windows 的任务栏设置中显示这些图标。

## 液态玻璃弹窗

弹窗跟随 Windows 应用深浅色主题。设备数据与背景分层绘制，接电参考值会直接标在设备名称下方。

![浅色与深色弹窗，示例设备数据](glass-preview.png)

[开发设计规范](液态玻璃设计规范.md)

## 兼容范围

内置 2.4 GHz 接收器协议为 MCHOSE G3 V2、G87 V2 和 G9 Pro。蓝牙设备需要支持标准 BLE Battery Service，并提供可识别的鼠标、键盘或耳机类型。休眠、关机、超出接收范围或使用未支持协议的设备可能不返回电量。

已在实机上读到 G3 V2、G87 V2 和 G9 Pro 的电量。G9 Pro 接电时，接收器可能持续回传插线前的百分比，卡片会标记「已接电源 · 参考电量」。换用其他 2.4 GHz 接收器时，可在 UsbBattery.cs 中加入对应的 USB 标识和电量读取协议。

## 构建

在 Windows 安装 .NET 10 SDK，在项目目录运行以下命令。

    dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true

可在构建目录运行下面的命令生成一次设备读取结果。

    PeripheralPower.exe --scan scan.json

USB HID 访问使用 [HidSharp](https://www.nuget.org/packages/HidSharp)。
G3 V2 和 G9 Pro 的协议参考 [OpenMHub](https://github.com/brayanspagnol/OpenMHub)，G87 V2 的协议参考 [mchose-battery](https://github.com/Tensyd/mchose-battery)。
