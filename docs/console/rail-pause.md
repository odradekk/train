# `rail pause`：暂停主机自动推进

[返回命令索引](README.md) · [场景前提与参数](README.md#使用前提与通用参数) · [结果与错误处理](README.md#结果与错误处理)

```text
rail pause
```

参数：无。暂停只挡住 Godot Host 按显示帧折算的自动积分，不进入权威快照比较核心。成功输出 `Rail Paused step <n>.`。已经暂停时仍报告 `Paused`。

暂停不是停车：不关闭牵引、不清零速度，也不取消待执行修改；要停车用 [`rail stop`](rail-stop.md)。暂停期间提交的当前步修改立即在当前边界按接受顺序执行，未来步修改照常排队。暂停期间渲染、相机和查询仍可用。显式 [`rail step`](rail-step.md) 仍会推进权威步。恢复见 [`rail resume`](rail-resume.md)。场景快捷键 `P` 在切片 4 切换暂停/恢复，不是本命令的别名实现。
