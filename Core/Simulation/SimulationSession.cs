// Train.Core.Simulation 提供核心运行边界的会话模板。
// 会话拥有最后完整状态；共享计算尚未实现，也尚未接入游戏宿主。

namespace Train.Core.Simulation;

/// <summary>拥有最后完整边界，在单一宿主线程上接受命令并原子发布完整模拟步。</summary>
/// <remarks>当前 Advance 与 Submit 为待实现模板。调用结束后才向外层返回事件；不支持并发调用或模拟阶段内重入。</remarks>
/// <example>Advance(10) 的第 4 步故障时，前三步保留，返回完成 3 步，并冻结在第 3 步结束后的状态。</example>
public sealed class SimulationSession
{
    private SimulationState _state;
    private readonly SimulationCalculations _calculations;

    /// <summary>保存一个完整起点及其领域计算，准备由调用方驱动会话。</summary>
    /// <param name="initialState">不可变起点；正式运行前应通过对应不变量检查。</param>
    /// <param name="calculations">读取此领域状态、计算候选结果的计算对象，不拥有会话状态。</param>
    /// <remarks>模板构造器只保存输入，不执行模拟、验证、恢复或外部写入。</remarks>
    public SimulationSession(SimulationState initialState, SimulationCalculations calculations)
    {
        _state = initialState;
        _calculations = calculations;
    }

    /// <summary>返回最后一个完整边界的不可变状态，后续发布不会修改此值。</summary>
    /// <returns>会话当前的完整状态；故障时包含冻结原因。</returns>
    public SimulationState GetState() => _state;

    /// <summary>待实现：依次计算和发布指定数量的完整模拟步，首次内部故障后停止。</summary>
    /// <param name="steps">要求完整推进的步数；零不产生事务，冻结时不推进。</param>
    /// <returns>实际完成步数、最终完整状态及成功步的事件。</returns>
    /// <remarks>各步独立发布，单步调用也使用此入口。阶段计算和不变量检查全部成功后才能发布候选状态。</remarks>
    /// <exception cref="NotImplementedException">当前是会话模板。</exception>
    /// <example>从 (60, 2) 推进一步后到达 (61, 0)，下一条命令使用 (61, 1)。</example>
    public AdvanceResult Advance(ulong steps) => throw new NotImplementedException();

    /// <summary>待实现：在当前边界分配命令身份，计算、检查并发布一条命令的结果。</summary>
    /// <param name="command">已解析命令，不带目标步号。</param>
    /// <param name="source">仅用于记录来源，不改变校验和事务规则。</param>
    /// <returns>无身份的入口拒收，或执行、预期拒绝、内部故障的完整结果。</returns>
    /// <remarks>拒绝不重试、不换目标、不撤销此前命令。故障恢复命令前全部状态再附故障；冻结时不接受新命令。</remarks>
    /// <exception cref="NotImplementedException">当前是会话模板。</exception>
    /// <example>暂停时依次成功、拒绝、成功，三条命令占用连续位置，前后成功结果均保留。</example>
    public SubmissionResult Submit(CoreCommand command, CommandSource source) =>
        throw new NotImplementedException();
}
