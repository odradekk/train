# Godot 契约调用原型

本目录是 #292 的可抛弃原型。隔离目录只复制三个契约项目、现有 Godot/Scenes 消费样例和本目录 harness，重新构建。它执行原样的 `ResourceConsumer`，不修改生产代码或契约；**不等于尚未迁移的生产 Godot 模块通过隔离验证**。

在仓库根目录运行（Windows、PowerShell 7、锁定 .NET SDK、Godot .NET 4.7.2 和 Windows 导出模板）：

```powershell
pwsh -NoProfile -File archive/prototypes/contract-probe/Godot/run.ps1 -OutputDirectory TestResults/run-tests/contract-probe-292/godot/new-run
```

输出目录必须不存在。脚本保留输入 SHA256 清单、命令和构建／导入／源运行／导出／导出运行日志；每个引擎运行必须完成断言并打印 `PROBE_SUCCESS`。Godot 导出可能报错却退出 0，因此额外拒绝导出日志中的 `ERROR:`。运行期 APPDATA 只定向到本轮输出，导出准备保留原 APPDATA 以读取模板。

原型生成 2×2 PNG 和单三角形 GLB 测试夹具，使用 `importer="keep"`。真实 Windows 导出运行用 `FileAccess` 从 PCK 中 `res://` 读取原字节，核对固定 SHA256；再写入独立 `user://mods` 并核对同字节。真实引擎检查 PNG 元数据、`ContentAsset` 经现有样例建立纹理、GLB 缓冲解码和临时场景释放、单 mesh/surface 的米制顶点及包围盒、无动画／骨骼／相机／灯光、损坏 PNG/GLB 拒绝、SceneMesh 上传后的坐标／绕序／法线，以及资源准备失败时保留上一完整纹理。

Godot 为平面模型 AABB 增加约 `1e-5` 厚度，ArrayMesh 上传法线有压缩误差，断言按这两个真实引擎行为使用容差。损坏夹具产生的预期原生日志错误不等于断言失败。

GLB 也先包装为 `ContentAsset`，元数据明确为固定夹具；冻结后故意把实际 `user://mods` 源文件改成损坏字节。仅本原型实现的最小模型适配从 `asset.Bytes` 解码，核对修订仍匹配原字节、模型原米制顶点仍正确，证明不重读已经变化的来源。现有 `ResourceConsumer` 没有模型创建方法，未修改该样例。

仍待生产实现验证：完整 `IAssetInspector` 元数据与允许／拒绝结构矩阵（透明模式、形变、外部图片、超格等）、整帧 Base/绑定校验及整组资源原子安装、共享资源退役、材料其他通道、原点／相机／拾取／暂停特效、真实渲染画面与性能。这里的失败保留检查只是最小准备后交换资源，不能代替生产帧安装器；headless 数据断言不能证明画面观感。
