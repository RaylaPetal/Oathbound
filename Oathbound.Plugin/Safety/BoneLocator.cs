using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.Havok.Animation.Rig;

namespace Oathbound.Plugin.Safety;

/// collar/leash-visual: read-only world-space bone positions for a drawn Human character, ported from
/// PoseKit's BoneReader (the same skeleton-transform x Havok model-space-pose math, verified in game there
/// via `/posekit bones`). Only partial skeleton 0 (the body) is read - `j_kubi` and `j_te_l`/`j_te_r` all
/// live there. Every pointer walk is null-checked and returns false instead, so a patch-moved layout or an
/// unusual model means "draw no leash", never a guessed end point (spec: "Fails closed on game changes").
public static unsafe class BoneLocator
{
    public const string Neck = "j_kubi";
    public const string RightHand = "j_te_r";

    /// Bone index by name, per Havok skeleton resource - the name scan is the only expensive part, and a
    /// resource is shared by every character using it, so this stays small. Keyed by pointer; a freed and
    /// reused resource address could in theory map to a different skeleton, which is why every hit is
    /// re-checked against the bone's actual name before use.
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

        // Copy to System.Numerics types; mixing vector types makes operators ambiguous.
        var transform = skeleton->Transform;
        var position = new Vector3(transform.Position.X, transform.Position.Y, transform.Position.Z);
        var rotation = new Quaternion(transform.Rotation.X, transform.Rotation.Y, transform.Rotation.Z, transform.Rotation.W);
        var scale = new Vector3(transform.Scale.X, transform.Scale.Y, transform.Scale.Z);
        var local = new Vector3(modelSpace->Translation.X, modelSpace->Translation.Y, modelSpace->Translation.Z);
        world = position + Vector3.Transform(local * scale, rotation);
        return true;
    }

    /// Dropped on territory change so the cache can't grow across a long session of zoning.
    public static void ClearCache() => IndexCache.Clear();
}
