// Train.Core.Simulation 提供不依赖引擎的通用确定性整数混合算法。
// 此文件只处理显式传入的 64 位值，不保存随机状态或定义具体用途的随机键。

namespace Train.Core.Simulation;

/// <summary>对显式输入执行无状态的 SplitMix64 变换，生成确定的 64 位混合值。</summary>
/// <remarks>
/// 每次调用先加固定增量，再执行异或移位和乘法；相当于参考生成器从输入状态前进一步的输出，但不保存该状态。
/// 算法常量按参考实现固定，所有加法和乘法按模 2^64 溢出，不是玩法调参值。
/// </remarks>
/// <example>输入 0 得到 <c>0xe220a8397b1dcdaf</c>；见 <c>SplitMix64Tests.Mix_matches_reference_vectors</c>。</example>
/// <seealso href="https://prng.di.unimi.it/splitmix64.c"/>
public static class SplitMix64
{
    /// <summary>参考算法在变换前加到输入的固定 64 位增量。</summary>
    private const ulong Increment = 0x9E3779B97F4A7C15UL;

    /// <summary>第一次异或移位后的固定乘法因子。</summary>
    private const ulong FirstMultiplier = 0xBF58476D1CE4E5B9UL;

    /// <summary>第二次异或移位后的固定乘法因子。</summary>
    private const ulong SecondMultiplier = 0x94D049BB133111EBUL;

    /// <summary>第一次异或前右移的位数。</summary>
    private const int FirstShift = 30;

    /// <summary>第二次异或前右移的位数。</summary>
    private const int SecondShift = 27;

    /// <summary>返回结果前最后一次异或所用的右移位数。</summary>
    private const int FinalShift = 31;

    /// <summary>将一个无符号 64 位输入确定地混合为一个无符号 64 位输出。</summary>
    /// <param name="value">完整范围内的显式输入，包括零和 ulong.MaxValue；调用方负责具体用途的键组合。</param>
    /// <returns>仅由输入决定的混合值；不依赖调用顺序，也不修改任何生成器状态。</returns>
    /// <example>输入 <c>ulong.MaxValue</c> 得到 <c>0xe4d971771b652c20</c>，溢出按模 2^64 处理。</example>
    public static ulong Mix(ulong value)
    {
        unchecked
        {
            value += Increment;
            value = (value ^ (value >> FirstShift)) * FirstMultiplier;
            value = (value ^ (value >> SecondShift)) * SecondMultiplier;
            return value ^ (value >> FinalShift);
        }
    }
}
