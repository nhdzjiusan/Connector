# 声明式 HID 电量规则

规则只描述本机电量报文。JSON 文件可包含一个规则，或以 `{"schemaVersion":1,"rules":[...]}` 包装多个规则。省略版本时按 1 处理。[用户规则 JSON Schema](../HidRules/hid-rule.schema.json) 可供编辑器校验。运行时还会校验偏移、掩码、报文上限、覆盖冲突和设备描述符。下面是离线回放使用的模拟规则。

```json
{
  "id": "example-battery",
  "name": "Example battery",
  "kind": "mouse",
  "match": {
    "vid": 4660, "pids": [22136], "usagePage": 65281, "usage": 1,
    "deviceGroup": "example-battery", "transport": "wired", "priority": 100,
    "minInput": 3, "minOutput": 4
  },
  "query": {
    "mode": "output_then_input", "length": 4, "bytes": {"0": 0, "1": 17},
    "checksum": {"kind": "sum8", "start": 0, "end": 3, "index": 3}
  },
  "response": {
    "minLength": 3, "maxReads": 3,
    "expect": [[{"index": 0, "equal": 0}]],
    "asleepWhen": [[{"index": 2, "equal": 255}]]
  },
  "parse": {
    "level": {"field": {"index": 1, "mask": 127}, "decode": "percent"},
    "defaultPower": "discharging",
    "states": [
      {"power": "charging", "when": [[{"index": 2, "mask": 16, "shift": 4, "equal": 1}]]}
    ]
  }
}
```

- 所有下标从 0 开始。第 0 字节始终是 Report ID，无编号报文使用 0。VID、PID、Usage、掩码与字节值均用 JSON 十进制整数。稀疏查询帧按 `length` 补零。
- `output_then_input` 和 `feature_set_get` 需要长度和稀疏字节。`feature_get_only` 需要显式 `reportId`。`passive_listen` 只读 Input，无查询字段。
- 校验和支持 `none`、`xor`、`sum8`、`sub_from_const_8` 和 `crc8`。`start` 包含，`end` 排除，结果位置 `index` 在区间外。`sub_from_const_8` 使用 `constant` 减去字节和，结果按 8 位截断。`crc8` 按高位优先处理，可配置 `polynomial`、`initial` 和 `xorOut`。
- `expect`、`asleepWhen`、`invalidWhen` 和状态的 `when` 使用同一种谓词结构。组内全部条件须满足，多个组命中一个即可。通过长度、`expect` 和响应校验和后，`asleepWhen` 结束本轮查询，`invalidWhen` 跳过该帧。解码超过 0–100 或查表未命中的帧也会跳过，仍受 `maxReads` 限制。
- 电量字段支持 1 或 2 字节，默认小端。先应用 `mask` 再右移 `shift`。`scale` 用 `raw * factor + offset`，`lookup` 使用 `values` 中的原始值到百分比映射。`voltage_mv` 可先缩放成毫伏，再按 `points` 中的 `millivolts` 和 `percent` 分段线性插值，区间外取端点。百分比四舍五入，域外值视为无效。
- 状态依次匹配 `states`，首项命中生效。支持 `discharging`、`charging`、`full`、`external_reference`、`unknown`。`isReference` 可标注充电时仍为参考电量。默认状态是 `unknown`。

## 加载与安全范围

启动时从内嵌资源加载默认规则，再按文件名排序读取 `%LOCALAPPDATA%\PeripheralPower\rules\*.json`。通过校验的文件按 `id` 覆盖，在后文件优先。覆盖内置规则时须保留 `kind` 与完整 `match`。新增规则使用自己的 `deviceGroup`。文件校验失败时跳过整文件并记录错误，先前有效的规则继续生效。如果设备描述符无法支持覆盖规则的查询，则在收发前使用对应的内置规则。`builtinDriver` 仅供固定内置驱动使用，与 `query`、`response`、`parse` 互斥。

外部规则需要确切的 VID 和 PID。Output 与 Feature 模式还要求精确的 Usage 和厂商 Usage Page `0xFF01–0xFFFF`。可用 `match.interface.inputReportId`、`outputReportId`、`featureReportId` 缩小集合范围。仅 `passive_listen` 可以省略 Usage Page 和 Usage，此时要求精确的 Input Report ID，并且同一 VID/PID 只有一个可确认的集合命中。标准键鼠等系统页面、通用厂商页面 `0xFF00`，以及已知 HalfKay、BootloadHID 和 QMK HID 引导程序标识会被拒绝。厂商自定义代码的含义由设备固件决定，仅使用已确认用于电量读取的命令。[Windows HID 顶层集合](https://learn.microsoft.com/en-us/windows-hardware/drivers/hid/hidclass-hardware-ids-for-top-level-collections) · [HalfKay 标识](https://www.pjrc.com/teensy/check_halfkay.html) · [QMK 引导程序标识](https://github.com/qmk/qmk_firmware/blob/master/docs/driver_installation_zadig.md)

USB 扫描只打开匹配到该厂商集合的 HID 路径。外部规则的写入报文还必须匹配该集合声明的 Report ID 和长度。每轮沿用三秒 USB 预算，同一设备路径通过互斥锁串行访问。

同一 `deviceGroup` 的新读数按 `priority` 取最高值，G3 V2 有线读数优先。有线无结果时可使用同组无线读数。组仍存在但未取得有效读数时，面板保留上次电量和采样时间。内置规则还包括 G87 V2 和 G9 Pro 接收器。G3 V2 接收器的内置驱动先确认设备可用，再执行电量规则。

## 离线回放

创建一个纯文本帧文件，每行写一帧脱敏后的十六进制内容，例如 `00 28 10`。可用空格分隔字节，也可使用 `#` 添加注释。

```powershell
dotnet run -c Release --project tests/peripheral-power/PeripheralPower.Check.csproj -- --hid-rule rule.json --id example-battery --frames frames.txt
```

命令显示查询帧、每帧的过滤结果、解析到的百分比与供电状态。省略应用参数则运行完整回归。

## 从诊断提取规则字段

运行 `PeripheralPower.exe --scan scan.json` 手动生成本地报告。报告保留 `readings`、`details`、`partial`，新增独立的诊断 `schemaVersion` 与 `hid`。BLE 读数在报告内使用临时编号。程序运行中的设备标识和合并方式继续沿用原逻辑。

- `hid.endpoints` 中的 `vid`、`pid`、`collections[].usages` 对应 `match`。集合的 `reports` 列出各类 Report ID，端点的 `inputLength`、`outputLength`、`featureLength` 记录可取得的最大报文长度。未匹配设备只采集这些枚举信息。
- `hid.exchanges` 按 `endpointId` 和 `phase` 关联同一交换。`query` 是模板生成的报文，`send` 是补齐后尝试写入的报文，`receive` 是原始回复及过滤原因，`outcome` 是该端点的最终处理结果。采集达到上限时会标记 `captureLimited`，被裁短的报文另有 `truncated`。
- 报告不导出 Windows 设备路径和序列号字段。设备显示名及厂商回复仍可能包含自定义名称或标识，分享报告前请脱敏。

可用报告里的端点编号直接回放，下面以端点 7 为例。自定义规则将 `builtin` 换成规则文件路径。缺少完整交换或接收帧被截断时，工具会拒绝回放。

```powershell
dotnet run -c Release --project tests/peripheral-power/PeripheralPower.Check.csproj -- --hid-rule builtin --id mchose-g9-pro-receiver --diagnostic scan.json --endpoint 7
```
