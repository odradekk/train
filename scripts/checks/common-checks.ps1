# 统一入口与结构检查共用的构建失败分类和生成源码例外。
# 源码编译错误不能被同时出现的还原告警遮蔽。
function Get-BuildFailureKind([string] $Output) {
    if ($Output -match 'error CS\d+') { return 'failed' }
    if ($Output -match 'error (NU\d+|NETSDK1045|NETSDK1141|MSB4236|MSB4019)' -or
        $Output -match 'compatible .NET SDK was not found|No .NET SDKs were found') {
        return 'blocked'
    }
    return 'failed'
}

# MSTest SDK 生成的入口来自 NuGet 缓存，允许编译进测试项目。
function Test-TestSdkCompileSource([string] $Path) {
    return $Path -match '[\\/]microsoft\.net\.test\.sdk[\\/][^\\/]+[\\/]build[\\/]netcoreapp3\.1[\\/]Microsoft\.NET\.Test\.Sdk\.Program\.cs$'
}
