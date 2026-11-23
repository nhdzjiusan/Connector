# 声明式 HID 电量规则

规则只描述本机电量报文。JSON 文件可包含一个规则，或以 `{"rules":[...]}` 包装多个规则。下面是离线回放使用的模拟规则。

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
- `expect`、`asleepWhen` 和状态的 `when` 使用同一种谓词结构。组内全部条件须满足，多个组命中一个即可。过滤顺序为长度、`expect`、响应校验和、`asleepWhen`、解析。响应读数由 `maxReads` 限制。
- 电量字段支持 1 或 2 字节，默认小端。先应用 `mask` 再右移 `shift`。`scale` 用 `raw * factor + offset`，`lookup` 使用 `values` 中的原始值到百分比映射。`voltage_mv` 可先缩放成毫伏，再按 `points` 中的 `millivolts` 和 `percent` 分段线性插值，区间外取端点。百分比四舍五入，域外值视为无效。
- 状态依次匹配 `states`，首项命中生效。支持 `discharging`、`charging`、`full`、`external_reference`、`unknown`。`isReference` 可标注充电时仍为参考电量。默认状态是 `unknown`。

## 加载与安全范围

启动时从内嵌资源加载默认规则，再按文件名排序读取 `%LOCALAPPDATA%\PeripheralPower\rules\*.json`。通过校验的文件按 `id` 覆盖，在后文件优先。校验失败时记录错误并保留已有规则。`builtinDriver` 仅供固定内置驱动使用，与 `query`、`response`、`parse` 互斥。

外部规则需要确切的 VID、PID、Usage 和厂商 Usage Page `0xFF01–0xFFFF`。标准键鼠等系统页面、通用厂商页面 `0xFF00` 与已知 HalfKay 标识 `16C0:0478` 会被拒绝。厂商自定义代码的含义由设备固件决定，仅使用已确认用于电量读取的命令。[Windows HID 顶层集合](https://learn.microsoft.com/en-us/windows-hardware/drivers/hid/hidclass-hardware-ids-for-top-level-collections) · [HalfKay 标识](https://www.pjrc.com/teensy/check_halfkay.html)

## 离线回放

创建一个纯文本帧文件，每行写一帧脱敏后的十六进制内容，例如 `00 28 10`。可用空格分隔字节，也可使用 `#` 添加注释。

```powershell
dotnet run -c Release --project tests/peripheral-power/PeripheralPower.Check.csproj -- --hid-rule rule.json --id example-battery --frames frames.txt
```

命令显示查询帧、每帧的过滤结果、解析到的百分比与供电状态。省略应用参数则运行完整回归。
