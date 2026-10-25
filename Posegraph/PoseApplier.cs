using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;

namespace Posegraph;

/// <summary>插件界面用的姿势读写（HTTP 接口走 BridgeServer，逻辑相同）。</summary>
public sealed class PoseApplier(IFramework framework, IClientState clientState, ITargetManager targetManager, BrioBridge brio)
{
    private const int FreezeSettleTicks = 5;

    /// <summary>从差异很大的姿势切换时，Brio 首次应用会残留最多约 1° 偏差（头颈、手指），隔一会儿再应用一次即收敛。</summary>
    private const int SecondApplyTicks = 30;

    /// <summary>actor 为 ObjectIndex，null 表示 GPose 当前目标。必须在主线程调用。</summary>
    public IGameObject? Resolve(int? actor)
        => actor is null ? targetManager.GPoseTarget : brio.GetActors().FirstOrDefault(a => a.ObjectIndex == actor);

    public Task<string?> GetPoseAsync(int? actor) => framework.RunOnFrameworkThread(() =>
        clientState.IsGPosing && Resolve(actor) is { } a ? brio.GetPose(a) : null);

    /// <summary>角色的种族骨架编号（cXX01），读不到为 null。</summary>
    public Task<string?> GetRaceAsync(int? actor) => framework.RunOnFrameworkThread(() =>
        clientState.IsGPosing ? Races.FromCharacter(Resolve(actor)) : null);

    /// <summary>冻结动画、等几帧再导入（否则待机动画会让姿势漂移），之后再应用一次。返回错误信息，成功为 null。</summary>
    public async Task<string?> ApplyAsync(int? actor, string poseJson)
    {
        var error = await framework.RunOnFrameworkThread(() =>
        {
            if (!clientState.IsGPosing) return Loc.T("不在 GPose 中");
            var a = Resolve(actor);
            if (a is null) return Loc.T("找不到角色（请在 GPose 中选中目标）");
            brio.Freeze(a);
            return null;
        });
        if (error is not null) return error;
        await framework.DelayTicks(FreezeSettleTicks);
        for (var i = 0; i < 2; i++)
        {
            if (i > 0) await framework.DelayTicks(SecondApplyTicks);
            var ok = await framework.RunOnFrameworkThread(() => Resolve(actor) is { } a && brio.ApplyPose(a, poseJson));
            if (!ok) return Loc.T("Brio 应用姿势失败");
        }
        return null;
    }
}
