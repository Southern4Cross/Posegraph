using System.Numerics;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using GameCamera = FFXIVClientStructs.FFXIV.Client.Game.Camera;

namespace Posegraph;

/// <summary>
/// GPose 活动相机的字段布局。偏移与 Brio 0.8（Brio/Game/Camera/BrioCamera.cs，
/// 部分来自 Lights, Camera, Action by NeNeppie）一致。
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 0x2B0)]
public struct GposeCameraStruct
{
    [FieldOffset(0x000)] public GameCamera Camera;
    [FieldOffset(0x124)] public float Distance;
    [FieldOffset(0x128)] public float MinDistance;
    [FieldOffset(0x12C)] public float MaxDistance;
    [FieldOffset(0x130)] public float FoV;
    [FieldOffset(0x13C)] public float Zoom;      // 叠加在 FoV 上的视角增量（弧度），GPose 默认 0
    [FieldOffset(0x140)] public Vector2 Angle;   // 绕目标的水平 / 俯仰角
    [FieldOffset(0x160)] public Vector2 Pan;     // 相机不动、视线的水平 / 垂直偏转
    [FieldOffset(0x170)] public float Roll;
}

/// <summary>读写 GPose 相机。所有方法必须在游戏主线程调用。</summary>
public static unsafe class GposeCamera
{
    private static GposeCameraStruct* Active => (GposeCameraStruct*)CameraManager.Instance()->GetActiveCamera();

    public static object? Read()
    {
        var cam = Active;
        if (cam == null) return null;
        var scene = cam->Camera.CameraBase.SceneCamera;
        var view = scene.ViewMatrix;
        view.M44 = 1;
        Matrix4x4.Invert(view, out var world);
        var render = scene.RenderCamera;
        return new
        {
            angle = new[] { cam->Angle.X, cam->Angle.Y },
            pan = new[] { cam->Pan.X, cam->Pan.Y },
            roll = cam->Roll,
            distance = cam->Distance,
            minDistance = cam->MinDistance,
            maxDistance = cam->MaxDistance,
            fov = cam->FoV,
            zoom = cam->Zoom,
            position = new[] { world.M41, world.M42, world.M43 },
            lookAt = new[] { scene.LookAtVector.X, scene.LookAtVector.Y, scene.LookAtVector.Z },
            view = Rows(view),
            projection = render != null ? Rows(render->ProjectionMatrix) : null,
        };
    }

    public static bool Write(CameraRequest req)
    {
        var cam = Active;
        if (cam == null) return false;
        if (req.Delimit == true)
        {
            cam->MinDistance = 0f;
            cam->MaxDistance = 500f;
        }
        if (req.Distance is { } d) cam->Distance = d;
        if (req.Angle is { Length: 2 } a) cam->Angle = new Vector2(a[0], a[1]);
        if (req.Pan is { Length: 2 } p) cam->Pan = new Vector2(p[0], p[1]);
        if (req.Roll is { } r) cam->Roll = r;
        if (req.Zoom is { } z) cam->Zoom = z;
        return true;
    }

    private static float[][] Rows(Matrix4x4 m) =>
    [
        [m.M11, m.M12, m.M13, m.M14],
        [m.M21, m.M22, m.M23, m.M24],
        [m.M31, m.M32, m.M33, m.M34],
        [m.M41, m.M42, m.M43, m.M44],
    ];
}

public sealed class CameraRequest
{
    public float? Distance { get; set; }
    public float[]? Angle { get; set; }
    public float[]? Pan { get; set; }
    public float? Roll { get; set; }
    public float? Zoom { get; set; }
    /// <summary>解除距离上下限（GPose 默认 1.5~20）。</summary>
    public bool? Delimit { get; set; }
}
