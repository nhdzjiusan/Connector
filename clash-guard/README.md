# Clash Guard

Windows 上的 Clash Verge / mihomo 节点守护脚本。需要 Node.js 24，无需安装 npm 依赖。[下载打包版](https://github.com/nhdzjiusan/Connector/releases/tag/clash-guard-v1.0.0)

脚本一次运行只检查一轮。它管理「节点选择」组，候选限于名称以 🇸🇬 开头的实际代理节点。当前新加坡节点连续两次测速失败时，切换到可用的新加坡节点。全部不可用时，若组里有 DIRECT 则切到直连，之后继续检查新加坡节点是否恢复。在全局模式下，脚本确认切换后也会尝试同步 GLOBAL。

## 使用

1. 确认 Clash Verge 的 mihomo 外部控制器在本机运行。脚本默认地址为 127.0.0.1:9097，代理组名为「节点选择」。如需调整，请修改脚本中的 CFG。
2. 若控制器设置了密钥，将实际值存于运行脚本的进程或当前 Windows 用户的 CLASH_GUARD_SECRET 环境变量。压缩包和源码均不含密钥。
3. 先在脚本目录运行下列命令。试跑会读取控制器和测速，保留当前代理选择。

       node .\clash-guard.js --dry-run --verbose

4. 正常检查一轮使用：

       node .\clash-guard.js

需要持续守护时，可在 Windows 任务计划程序里添加任务，让它用同一 Windows 账号每 3 分钟运行一轮。动作的程序填 node.exe 的实际路径，参数填 clash-guard.js 的完整路径并加引号。启用「如果任务已在运行，则不启动新实例」。任务运行时会在脚本所在目录写入日志和状态，请选一个当前账号可写入的位置。

## 临时暂停

下面的 PowerShell 命令会暂停 30 分钟。时间到后下一轮自动恢复，也可以提前移走暂停文件。

    @{ until = [DateTimeOffset]::UtcNow.AddMinutes(30).ToUnixTimeMilliseconds() } |
        ConvertTo-Json -Compress |
        Set-Content -LiteralPath '.\暂停-clash-guard.json' -Encoding utf8
