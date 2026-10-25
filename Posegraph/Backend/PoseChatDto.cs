using System.Text.Json.Serialization;

namespace Posegraph.Backend;

public sealed record PoseChatMessage(string Role, string Content);

public sealed record PoseChatProvider(
    string Endpoint, [property: JsonPropertyName("api_key")] string ApiKey, string Model);

public sealed record ActorRigBone(
    string Name, string? Parent,
    [property: JsonPropertyName("reference_position")] float[] ReferencePosition,
    [property: JsonPropertyName("reference_rotation")] float[] ReferenceRotation,
    [property: JsonPropertyName("reference_scale")] float[] ReferenceScale);

public sealed record ActorRig(ActorRigBone[] Bones);

public sealed record PoseChatRequest(
    string Prompt, PoseChatMessage[] History, string? Pose, string? Race,
    [property: JsonPropertyName("session_id")] string? SessionId,
    [property: JsonPropertyName("include_image")] bool IncludeImage,
    bool Ground, PoseChatProvider Provider,
    ActorRig? Rig = null,
    [property: JsonPropertyName("actor_pose")] string? ActorPose = null);

public sealed record PoseChatResponse(string Reply, RetargetResult Result, PoseChatMessage[] History);
