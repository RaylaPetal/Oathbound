using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.Havok.Animation.Rig;

namespace Oathbound.Plugin.Safety;

/// World-space bone positions for a drawn character (skeleton transform x Havok model-space pose). Only the body
/// skeleton is read. Every pointer walk is null-checked, so a moved layout means no leash, never a guessed point.
public static unsafe class BoneLocator
{
    public const string Neck = "j_kubi";
    public const string RightHand = "j_te_r";
    public const string Head = "j_kao";
    public const string UpperChest = "j_sebo_c";
    public const string Waist = "j_kosi";

    public readonly record struct LegBones(string Hip, string Knee, string Ankle);
    public static readonly LegBones LeftLeg = new("j_asi_a_l", "j_asi_c_l", "j_asi_d_l");
    public static readonly LegBones RightLeg = new("j_asi_a_r", "j_asi_c_r", "j_asi_d_r");

    /// Keyed by pointer; a reused address could map to another skeleton, so every hit is re-checked by name.
    private static readonly Dictionary<(nint Skeleton, string Bone), int> IndexCache = new();

    public static bool TryGetWorld(ICharacter character, string boneName, out Vector3 world)
    {
        world = default;
        return TryGetPose(character, out var pose) && pose.TryGetWorld(boneName, out world);
    }

    /// One walk to the character's pose, so several bones can be read from it. Only valid for the current frame.
    public static bool TryGetPose(ICharacter character, out Pose pose)
    {
        pose = default;
        var native = (Character*)character.Address;
        if (native == null || native->DrawObject == null) return false;
        if (native->DrawObject->GetObjectType() != ObjectType.CharacterBase) return false;
        var characterBase = (CharacterBase*)native->DrawObject;
        if (characterBase->GetModelType() != CharacterBase.ModelType.Human) return false;

        var skeleton = characterBase->Skeleton;
        if (skeleton == null || skeleton->PartialSkeletonCount < 1) return false;

        var havokPose = skeleton->PartialSkeletons[0].GetHavokPose(0);
        if (havokPose == null || havokPose->Skeleton == null) return false;

        pose = new Pose(skeleton, havokPose);
        return true;
    }

    public readonly struct Pose
    {
        private readonly Skeleton* skeleton;
        private readonly hkaPose* pose;

        internal Pose(Skeleton* skeleton, hkaPose* pose)
        {
            this.skeleton = skeleton;
            this.pose = pose;
        }

        public bool TryGetWorld(string boneName, out Vector3 world)
        {
            world = default;
            if (pose == null) return false;
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
    }

    /// Cleared on territory change so it can't grow across a long session.
    public static void ClearCache() => IndexCache.Clear();
}
