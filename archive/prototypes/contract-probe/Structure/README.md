# #292 结构与 core-only 可抛弃原型

在仓库根运行：

```powershell
pwsh -NoProfile -File archive/prototypes/contract-probe/Structure/Run.ps1
```

使用 `global.json` 指定的 SDK 10.0.112。运行器在自己的 `%TEMP%/train-contract-probe-292-<uuid>` 中按显式文件清单复制三个契约、现有核心消费样例及两个原型项目；没有复制真实模块、`bin`、`obj` 或 Godot 状态。目录保留供复核。证据默认写入忽略目录 `TestResults/run-tests/contract-probe-292/structure-core/`，包含输入 SHA256、实际命令、预期/实际退出码、耗时、MSBuild 求值项目引用与解析程序集路径及恢复程序集身份。

结构工具读取实际构建后的三契约 PE AssemblyReference；SDK 随附 Roslyn 从实际程序集读取 public/protected 符号、参数、返回值、可空引用、基类、接口及符号属性，比较本次运行观察到的公开面。这个观察文件不是用户批准的整组基线。MSBuild 解析路径另检查允许项目集合与临时目录边界；框架引用必须由 `Microsoft.NETCore.App` 提供。

故意破坏只在临时副本发生：通过 binary Reference 引入实际编译的 `Train.ForbiddenImplementation`，用内部方法引用其符号，确保违规存在于 PE 中；公开 `ICoreSession.Read()` 与 `ICoreReplay.Read()` 改成必填参数版本，由符号差异及原消费样例编译检查同时检出。正确、破坏、恢复均执行同一工具。真实实现工厂尚不存在于该显式输入，因此未执行工厂调用位置检查，不以名称匹配代替符号检查。

限制：此工具只检查当前三契约与这些原型，尚未接入统一入口，不是生产实现依赖图扫描器。公开面提取是可行性验证，尚未覆盖全部 C# 元数据细节（例如参数属性、自定义修饰符）；语义兼容必须另用文档和共享情景判断。未建立生产程序集工厂基线或四模块生产隔离证据。
