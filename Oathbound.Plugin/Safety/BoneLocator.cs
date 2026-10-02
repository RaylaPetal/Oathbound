using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.Havok.Animation.Rig;

namespace Oathbound.Plugin.Safety;

/// World-space bone positions for a drawn character (skeleton transform x Havok model-space pose). Only the body
/// skeleton is read. Every pointer walk is null-checked, so a moved layout means no leash, never a guessed point.
public static unsafe class BoneLocator
{
    public const string Neck = "j_kubi";
    public const string RightHand = "j_te_r";

    /// Keyed by pointer; a reused address could map to another skeleton, so every hit is re-checked by name.
    private static readonly Dictionary<(nint Skeleton, string Bone), int> IndexCache = new();

    public static bool TryGetWorld(ICharacter character, string boneName, out Vector3 world)
    {
        world = default;
        var native = (Character*)character.Address;
        if (native == null || native->DrawObject == null) return false;
        if (native->DrawObject->GetObjectType() != ObjectType.CharacterBase) return false;
        var characterBase = (CharacterBase*)native->DrawObject;
        if (characterBase->GetModelType() != CharacterBase.ModelType.Human) return false;

        var skeleton = characterBase->Skeleton;
        if (skeleton == null || skeleton->PartialSkeletonCount < 1) return false;

        var pose = skeleton->PartialSkeletons[0].GetHavokPose(0);
        if (pose == null || pose->Skeleton == null) return false;

        var bones = pose->Skeleton->Bones;
        var key = ((nint)pose->Skeleton, boneName);
        if (!IndexCache.TryGetValue(key, out var index) || index >= bones.Length || bones[index].Name.String != boneName)
        {
            index = -1;
            for (var i = 0; i < bones.Length; i++)
            {
                if (bones[i].Name.String == boneName)
                {
                    index = i;
                    break;
                }
            }
            if (index < 0) return false;
            IndexCache[key] = index;
        }

        var modelSpace = pose->AccessBoneModelSpace(index, hkaPose.PropagateOrNot.DontPropagate);
        if (modelSpace == null) return false;

        // Mixing vector types makes operators ambiguous.
        var transform = skeleton->Transform;
        var position = new Vector3(transform.Position.X, transform.Position.Y, transform.Position.Z);
        var rotation = new Quaternion(transform.Rotation.X, transform.Rotation.Y, transform.Rotation.Z, transform.Rotation.W);
        var scale = new Vector3(transform.Scale.X, transform.Scale.Y, transform.Scale.Z);
        var local = new Vector3(modelSpace->Translation.X, modelSpace->Translation.Y, modelSpace->Translation.Z);
        world = position + Vector3.Transform(local * scale, rotation);
        return true;
    }

    /// Cleared on territory change so it can't grow across a long session.
    public static void ClearCache() => IndexCache.Clear();
}
