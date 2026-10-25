using System.Numerics;
using System.Text.Json;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.Havok.Animation.Rig;
using Posegraph.Backend;

namespace Posegraph;

/// <summary>Read the loaded actor's complete neutral rig, including face/hair partials.</summary>
public static class ActorRigReader
{
    private sealed class Node(string name, Vector3 position, Quaternion rotation, Vector3 scale)
    {
        public string Name = name;
        public Vector3 Position = position;
        public Quaternion Rotation = rotation;
        public Vector3 Scale = scale;
        public Node? Parent;
    }

    /// <summary>Must run on the framework thread. Only character bones in Brio's pose are returned.</summary>
    public static unsafe ActorRig? Read(IGameObject actor, string poseJson)
    {
        if (actor.Address == 0) return null;
        var gameObject = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)actor.Address;
        var draw = (CharacterBase*)gameObject->DrawObject;
        if (draw == null || draw->GetModelType() != CharacterBase.ModelType.Human || draw->Skeleton == null) return null;
        var skeleton = draw->Skeleton;
        if (skeleton->PartialSkeletons == null || skeleton->PartialSkeletonCount is 0 or > 64) return null;

        using var pose = JsonDocument.Parse(poseJson);
        if (!pose.RootElement.TryGetProperty("Bones", out var poseBones) || poseBones.ValueKind != JsonValueKind.Object) return null;
        var exported = poseBones.EnumerateObject().Select(b => b.Name).ToHashSet(StringComparer.Ordinal);
        var partials = new List<Node[]>();
        var all = new List<Node>();
        for (var partialIndex = 0; partialIndex < skeleton->PartialSkeletonCount; partialIndex++)
        {
            var partial = &skeleton->PartialSkeletons[partialIndex];
            hkaPose* havokPose = null;
            for (var poseIndex = 0; poseIndex < 4 && havokPose == null; poseIndex++)
                havokPose = partial->GetHavokPose(poseIndex);
            var raw = havokPose == null ? null : havokPose->Skeleton;
            if (raw == null || raw->Bones.Data == null || raw->ParentIndices.Data == null || raw->ReferencePose.Data == null ||
                raw->Bones.Length is <= 0 or > 2048 || raw->ParentIndices.Length != raw->Bones.Length ||
                raw->ReferencePose.Length != raw->Bones.Length)
            {
                partials.Add([]);
                continue;
            }
            if (all.Count + raw->Bones.Length > 4096) return null;
            var nodes = new Node[raw->Bones.Length];
            for (var index = 0; index < nodes.Length; index++)
            {
                var name = raw->Bones[index].Name.String;
                if (string.IsNullOrWhiteSpace(name) || name.Length > 128) return null;
                var reference = raw->ReferencePose[index];
                var position = new Vector3(reference.Translation.X, reference.Translation.Y, reference.Translation.Z);
                var rotation = new Quaternion(reference.Rotation.X, reference.Rotation.Y, reference.Rotation.Z, reference.Rotation.W);
                var scale = new Vector3(reference.Scale.X, reference.Scale.Y, reference.Scale.Z);
                if (!Finite(position) || !Finite(scale) || !float.IsFinite(rotation.LengthSquared()) || rotation.LengthSquared() < 1e-10f) return null;
                nodes[index] = new Node(name, position, Quaternion.Normalize(rotation), scale);
            }
            for (var index = 0; index < nodes.Length; index++)
            {
                var parent = raw->ParentIndices[index];
                if (parent >= nodes.Length || parent == index) return null;
                if (parent >= 0) nodes[index].Parent = nodes[parent];
            }
            // Match Brio's joining of game partials. Shared roots are aliases of
            // their body attachment, not an extra head transform to apply twice.
            if (partialIndex > 0 && partials.Count > 0 && partials[0].Length > 0)
            {
                var roots = nodes.Where(n => n.Parent is null).ToArray();
                if (roots.Length == 1 && partial->ConnectedParentBoneIndex >= 0 && partial->ConnectedParentBoneIndex < partials[0].Length &&
                    partial->ConnectedBoneIndex >= 0 && partial->ConnectedBoneIndex < nodes.Length)
                {
                    Attach(nodes[partial->ConnectedBoneIndex], partials[0][partial->ConnectedParentBoneIndex]);
                }
                else
                {
                    foreach (var root in roots)
                    {
                        var parent = partials[0].FirstOrDefault(n => n.Name == root.Name);
                        if (parent is not null) Attach(root, parent);
                    }
                }
            }
            partials.Add(nodes);
            all.AddRange(nodes);
        }
        var canonical = new Dictionary<string, Node>(StringComparer.Ordinal);
        foreach (var node in all)
            if (exported.Contains(node.Name)) canonical.TryAdd(node.Name, node);
        var result = new List<ActorRigBone>();
        foreach (var node in canonical.Values)
        {
            var parent = node.Parent;
            var position = node.Position;
            var rotation = node.Rotation;
            var scale = node.Scale;
            var visited = new HashSet<Node> { node };
            while (parent is not null && (!canonical.ContainsKey(parent.Name) || parent.Name == node.Name))
            {
                if (!visited.Add(parent)) return null;
                position = parent.Position + Vector3.Transform(position * parent.Scale, parent.Rotation);
                rotation = Quaternion.Normalize(parent.Rotation * rotation);
                scale *= parent.Scale;
                parent = parent.Parent;
            }
            result.Add(new ActorRigBone(node.Name, parent?.Name,
                [position.X, position.Y, position.Z], [rotation.X, rotation.Y, rotation.Z, rotation.W], [scale.X, scale.Y, scale.Z]));
        }
        return result.Count is > 0 and <= 2048 ? new ActorRig(result.ToArray()) : null;
    }

    private static void Attach(Node node, Node parent)
    {
        node.Parent = parent;
        node.Position = Vector3.Zero;
        node.Rotation = Quaternion.Identity;
        node.Scale = Vector3.One;
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
