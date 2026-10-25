using System.Text.Json.Serialization;

namespace Posegraph.Backend;

public sealed record PoseAdviceRequest(
    string Pose, string? Race, PoseChatProvider Provider, string Focus = "",
    RetargetParams? Params = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PoseWarning[]? Warnings = null,
    [property: JsonPropertyName("session_id")] string? SessionId = null,
    [property: JsonPropertyName("include_image")] bool IncludeImage = false,
    ActorRig? Rig = null,
    [property: JsonPropertyName("actor_pose")] string? ActorPose = null);

public sealed record PoseAdviceSuggestion(string? Bone, string Issue, string Evidence, string Recommendation);

public sealed record PoseAdviceResponse(string Summary, PoseAdviceSuggestion[] Suggestions, string[] Limitations);
