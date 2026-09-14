# `rail resume`：恢复 1× 主机推进

[返回命令索引](README.md) · [场景前提与参数](README.md#使用前提与通用参数) · [结果与错误处理](README.md#结果与错误处理)

```text
rail resume
```

参数：无。恢复后 Host 仍按固定 60 步/秒折算显示帧，不开放倍速。成功输出 `Rail Resumed step <n>.`。未暂停时仍报告 `Resumed`。

恢复不改变已提交姿态、速度或步号。同一按步输入在 30/60/144 FPS 显示调度下权威结果相同。
