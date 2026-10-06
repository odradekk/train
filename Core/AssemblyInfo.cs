// Core 程序集的内部访问边界。
// 主程序集中的会话适配和测试仍使用目录内部发布、修订编码；旧会话退役后收回对应访问。

using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Train")]
[assembly: InternalsVisibleTo("Train.Tests")]
