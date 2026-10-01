using System;
using System.Runtime.InteropServices;

namespace Oathbound.Plugin.Commands;

[StructLayout(LayoutKind.Explicit)]
public unsafe struct OathboundMoveController
{
    [FieldOffset(0x3F)] public byte MouseRunning;
    [FieldOffset(0x110)] public int WishdirChanged;
}

/// collar/leash steering: the active camera's horizontal direction, same layout vnavmesh reads (CameraEx).
[StructLayout(LayoutKind.Explicit)]
public unsafe struct OathboundCameraEx
{
    [FieldOffset(0x140)] public float DirH; // 0 is north, increases clockwise
}

[StructLayout(LayoutKind.Explicit)]
public unsafe struct OathboundFollowState
{
    [FieldOffset(0x4C4)] public short FollowingTarget;
}
