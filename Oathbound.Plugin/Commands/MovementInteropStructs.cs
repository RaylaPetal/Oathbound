using System;
using System.Runtime.InteropServices;

namespace Oathbound.Plugin.Commands;

[StructLayout(LayoutKind.Explicit)]
public unsafe struct OathboundMoveController
{
    [FieldOffset(0x3F)] public byte MouseRunning;
    [FieldOffset(0x110)] public int WishdirChanged;
}

/// The fly-input result vnavmesh also rewrites.
[StructLayout(LayoutKind.Explicit, Size = 0x18)]
public unsafe struct OathboundFlyInput
{
    [FieldOffset(0x0)] public float Forward;
    [FieldOffset(0x4)] public float Left;
    [FieldOffset(0x8)] public float Up;
    [FieldOffset(0xC)] public float Turn;
}

/// The active camera's horizontal direction.
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
