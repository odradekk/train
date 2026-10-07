// Core 程序集的内部访问边界。
// 测试使用目录内部发布和修订编码验证契约；应用通过公开接口访问 Core。

using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Train.Tests")]
