# 体素引擎公开契约

本页落实[体素引擎层公开契约：场景、拾取与编辑会话](https://github.com/odradekk/train/issues/289)的决定。业务与表现规则仍见[场景](../design/01-voxel-engine-and-scenes.md)、[渲染](../design/07-rendering.md)、[编辑器](../design/15-design-editor-and-tools.md)；公共语义、身份与提交沿用[公共约定](conventions.md)、[跨层协议](transactions.md)和[编辑协议](editing.md)。

- [scenes-status.md](scenes-status.md)：当前实现状态。
- [scenes-session.md](scenes-session.md)：公开面与责任；可选流式记录；坐标与身份；世界场景会话。
- [scenes-display.md](scenes-display.md)：显示更新与资源；网格、模型与效果数据；拾取与命中有效性。
- [scenes-editing.md](scenes-editing.md)：设计编辑会话；预览、提交与撤销；校验与保存完成；只读设计预览；开发模板预览。
- [scenes-validation.md](scenes-validation.md)：失败、线程与验证；本票验证记录。
