using Brio.API;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin;

namespace Posegraph;

/// <summary>
/// Brio IPC 的薄封装。所有方法必须在游戏主线程调用（由 BridgeServer 负责切换）。
/// </summary>
public sealed class BrioBridge(IDalamudPluginInterface pi)
{
    private readonly ApiVersion _apiVersion = new(pi);
    private readonly IsAvailable _isAvailable = new(pi);
    private readonly GetAllActors _getAllActors = new(pi);
    private readonly GetPoseAsJson _getPoseAsJson = new(pi);
    private readonly LoadPoseFromJson _loadPoseFromJson = new(pi);
    private readonly FreezeActor _freezeActor = new(pi);

    public (int Breaking, int Feature)? TryGetApiVersion()
    {
        try { return _apiVersion.Invoke(); }
        catch { return null; }
    }

    public bool IsAvailable()
    {
        try { return _isAvailable.Invoke(); }
        catch { return false; }
    }

    public IGameObject[] GetActors() => _getAllActors.Invoke() ?? [];

    public string? GetPose(IGameObject actor) => _getPoseAsJson.Invoke(actor);

    public bool ApplyPose(IGameObject actor, string poseJson) => _loadPoseFromJson.Invoke(actor, poseJson);

    /// <summary>
    /// 冻结动画。Brio 导入姿势时会暂停并重置时间轴、导入后恢复原速度，
    /// 姿势以增量叠加在动画帧上，不冻结的话待机动画会继续播放导致姿势漂移。
    /// </summary>
    public bool Freeze(IGameObject actor) => _freezeActor.Invoke(actor);
}
