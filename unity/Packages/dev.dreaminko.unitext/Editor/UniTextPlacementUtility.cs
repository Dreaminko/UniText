#if UNITEXT_VRCSDK3 && UNITEXT_MA && UNITEXT_NDMF
using System;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace UniText.Editor
{
    public static class UniTextPlacementUtility
    {
        private const float StandardEyeHeight = 1.35f;

        private static readonly HumanBodyBones[] CuratedFollowBones =
        {
            HumanBodyBones.Head,
            HumanBodyBones.Neck,
            HumanBodyBones.UpperChest,
            HumanBodyBones.Chest,
            HumanBodyBones.Spine,
            HumanBodyBones.Hips,
            HumanBodyBones.LeftHand,
            HumanBodyBones.RightHand
        };

        private static readonly GUIContent[] CuratedFollowBoneLabelsEn =
        {
            new GUIContent("Head"),
            new GUIContent("Neck"),
            new GUIContent("Upper Chest"),
            new GUIContent("Chest"),
            new GUIContent("Spine"),
            new GUIContent("Hips"),
            new GUIContent("Left Hand"),
            new GUIContent("Right Hand")
        };

        private static readonly GUIContent[] CuratedFollowBoneLabelsZh =
        {
            new GUIContent("头部 (Head)"),
            new GUIContent("颈部 (Neck)"),
            new GUIContent("上胸 (Upper Chest)"),
            new GUIContent("胸部 (Chest)"),
            new GUIContent("脊柱 (Spine)"),
            new GUIContent("臀部 (Hips)"),
            new GUIContent("左手 (Left Hand)"),
            new GUIContent("右手 (Right Hand)")
        };

        public static string GetFollowBoneDisplayName(HumanBodyBones bone, bool isChinese)
        {
            int index = Array.IndexOf(CuratedFollowBones, bone);
            if (index >= 0)
            {
                GUIContent[] labels = isChinese ? CuratedFollowBoneLabelsZh : CuratedFollowBoneLabelsEn;
                return labels[index].text;
            }

            return ObjectNames.NicifyVariableName(bone.ToString());
        }

        public static HumanBodyBones DrawFollowBonePopup(GUIContent label, HumanBodyBones current, bool isChinese)
        {
            Rect rect = EditorGUILayout.GetControlRect(true);
            return DrawFollowBonePopup(rect, label, current, isChinese);
        }

        public static HumanBodyBones DrawFollowBonePopup(Rect rect, GUIContent label, HumanBodyBones current, bool isChinese)
        {
            GUIContent[] baseOptions = isChinese ? CuratedFollowBoneLabelsZh : CuratedFollowBoneLabelsEn;
            int selectedIndex = Array.IndexOf(CuratedFollowBones, current);
            GUIContent[] options = baseOptions;

            if (selectedIndex < 0)
            {
                string boneName = ObjectNames.NicifyVariableName(current.ToString());
                options = new GUIContent[baseOptions.Length + 1];
                Array.Copy(baseOptions, options, baseOptions.Length);
                options[baseOptions.Length] = new GUIContent(isChinese ? $"其他 ({boneName})" : $"Other ({boneName})");
                selectedIndex = CuratedFollowBones.Length;
            }

            int nextIndex = EditorGUI.Popup(rect, label, selectedIndex, options);
            if (nextIndex >= 0 && nextIndex < CuratedFollowBones.Length)
            {
                return CuratedFollowBones[nextIndex];
            }

            return current;
        }

        public static void DrawFollowBoneProperty(SerializedProperty property, bool isChinese = false)
        {
            if (property == null)
            {
                return;
            }

            GUIContent label = new GUIContent(property.displayName, property.tooltip);
            Rect rect = EditorGUILayout.GetControlRect(true);
            EditorGUI.BeginProperty(rect, label, property);

            bool prevMixed = EditorGUI.showMixedValue;
            EditorGUI.showMixedValue = property.hasMultipleDifferentValues;
            EditorGUI.BeginChangeCheck();
            HumanBodyBones current = (HumanBodyBones)property.intValue;
            HumanBodyBones next = DrawFollowBonePopup(rect, label, current, isChinese);
            if (EditorGUI.EndChangeCheck())
            {
                property.intValue = (int)next;
            }
            EditorGUI.showMixedValue = prevMixed;

            EditorGUI.EndProperty();
        }

        public static Vector3 GetDefaultPresetOffset(UniTextPlacementPreset preset, float heightScale = 1f)
        {
            switch (preset)
            {
                case UniTextPlacementPreset.LeftSide:
                    return new Vector3(-0.28f * heightScale, 0.14f * heightScale, 0.16f * heightScale);
                case UniTextPlacementPreset.AboveHead:
                    return new Vector3(0f, 0.50f * heightScale, 0.06f * heightScale);
                default:
                    return new Vector3(0.28f * heightScale, 0.14f * heightScale, 0.16f * heightScale);
            }
        }

        public static Vector3 ComputeAdaptiveBoneOffset(
            GameObject avatarRoot,
            UniTextPlacementPreset preset,
            HumanBodyBones followBone,
            Vector3 fallbackOffset)
        {
            if (preset == UniTextPlacementPreset.Custom)
            {
                return fallbackOffset;
            }
            if (avatarRoot == null)
            {
                return GetDefaultPresetOffset(preset);
            }

            Quaternion invRot = Quaternion.Inverse(avatarRoot.transform.rotation);
            Vector3 rootPos = avatarRoot.transform.position;
            Vector3 ToAvatarMeters(Vector3 worldPos) => invRot * (worldPos - rootPos);

            var animator = avatarRoot.GetComponent<Animator>();
            Transform head = (animator != null && animator.isHuman)
                ? animator.GetBoneTransform(HumanBodyBones.Head)
                : null;

            var descriptor = avatarRoot.GetComponent<VRCAvatarDescriptor>();
            float rootScaleY = Mathf.Abs(avatarRoot.transform.lossyScale.y);
            float eyeY = (descriptor != null && descriptor.ViewPosition.y > 0.1f)
                ? descriptor.ViewPosition.y * (rootScaleY > 1e-4f ? rootScaleY : 1f)
                : (head != null ? ToAvatarMeters(head.position).y + 0.08f : StandardEyeHeight);

            float heightScale = Mathf.Clamp(eyeY / StandardEyeHeight, 0.45f, 1.65f);
            Transform targetBone = UniTextNdmfPlugin.ResolveHumanoidBone(avatarRoot, followBone);
            if (targetBone == null)
            {
                return GetDefaultPresetOffset(preset, heightScale);
            }

            Vector3 boneM = ToAvatarMeters(targetBone.position);

            if (preset == UniTextPlacementPreset.AboveHead)
            {
                float headTopY = head != null
                    ? Mathf.Max(eyeY + 0.14f * heightScale, ToAvatarMeters(head.position).y + 0.22f * heightScale)
                    : eyeY + 0.16f * heightScale;
                return new Vector3(0f, headTopY - boneM.y, 0.06f * heightScale);
            }

            float shoulderHalfX = EstimateShoulderHalfWidth(animator, ToAvatarMeters, heightScale);
            float sideX = shoulderHalfX + 0.10f * heightScale;
            float targetY = head != null
                ? ToAvatarMeters(head.position).y - 0.06f * heightScale
                : Mathf.Lerp(boneM.y, eyeY, 0.55f);
            float deltaY = targetY - boneM.y;
            if (followBone == HumanBodyBones.Chest ||
                followBone == HumanBodyBones.UpperChest ||
                followBone == HumanBodyBones.Spine)
            {
                deltaY = Mathf.Clamp(deltaY, 0.02f * heightScale, 0.55f * heightScale);
            }

            float signedX = preset == UniTextPlacementPreset.LeftSide ? -sideX : sideX;
            return new Vector3(signedX, deltaY, 0.16f * heightScale);
        }

        internal struct SelfViewMetrics
        {
            public Transform HeadBone;
            public Transform BodyBone;
            public Vector3 AnchorWorldPos;
            public Quaternion AnchorWorldRot;
            public float DistMin;
            public float DistMax;
            public float HeightMin;
            public float HeightMax;
        }

        internal static SelfViewMetrics ComputeSelfViewMetrics(GameObject avatarRoot)
        {
            if (avatarRoot == null)
            {
                return new SelfViewMetrics
                {
                    AnchorWorldPos = new Vector3(0f, StandardEyeHeight - 0.08f, 0f),
                    AnchorWorldRot = Quaternion.identity,
                    DistMin = 0.46f,
                    DistMax = 1.16f,
                    HeightMin = -0.34f,
                    HeightMax = 0.26f
                };
            }

            Quaternion rootRot = avatarRoot.transform.rotation;
            Quaternion invRot = Quaternion.Inverse(rootRot);
            Vector3 rootPos = avatarRoot.transform.position;
            Vector3 ToAvatarMeters(Vector3 worldPos) => invRot * (worldPos - rootPos);

            var animator = avatarRoot.GetComponent<Animator>();
            Transform headBone = (animator != null && animator.isHuman)
                ? animator.GetBoneTransform(HumanBodyBones.Head)
                : null;
            Transform bodyBone = UniTextNdmfPlugin.ResolveHumanoidBone(avatarRoot, HumanBodyBones.Chest);

            var descriptor = avatarRoot.GetComponent<VRCAvatarDescriptor>();
            float rootScaleY = Mathf.Abs(avatarRoot.transform.lossyScale.y);
            float rootScaleZ = Mathf.Abs(avatarRoot.transform.lossyScale.z);
            float eyeY = (descriptor != null && descriptor.ViewPosition.y > 0.1f)
                ? descriptor.ViewPosition.y * (rootScaleY > 1e-4f ? rootScaleY : 1f)
                : (headBone != null ? ToAvatarMeters(headBone.position).y + 0.08f : StandardEyeHeight);

            float heightScale = Mathf.Clamp(eyeY / StandardEyeHeight, 0.45f, 1.65f);
            Vector3 anchorWorldPos = headBone != null
                ? headBone.position
                : rootPos + rootRot * new Vector3(0f, eyeY - 0.08f * heightScale, 0f);
            Vector3 baseLocal = ToAvatarMeters(anchorWorldPos);

            float eyeDeltaY = Mathf.Clamp(eyeY - baseLocal.y, 0f, 0.25f * heightScale);
            float eyeDeltaZ = (descriptor != null && descriptor.ViewPosition.y > 0.1f)
                ? Mathf.Clamp(descriptor.ViewPosition.z * (rootScaleZ > 1e-4f ? rootScaleZ : 1f) - baseLocal.z, -0.15f, 0.25f)
                : 0.06f * heightScale;

            return new SelfViewMetrics
            {
                HeadBone = headBone,
                BodyBone = bodyBone,
                AnchorWorldPos = anchorWorldPos,
                AnchorWorldRot = rootRot,
                DistMin = eyeDeltaZ + 0.40f * heightScale,
                DistMax = eyeDeltaZ + 1.10f * heightScale,
                HeightMin = eyeDeltaY - 0.42f * heightScale,
                HeightMax = eyeDeltaY + 0.18f * heightScale
            };
        }

        public static void ApplyTransformPreview(GameObject avatarRoot, UniTextProp prop, bool recordUndo)
        {
            if (prop == null)
            {
                return;
            }

            Transform rootTf = prop.transform;
            Transform anchor = rootTf.Find("SideFollowAnchor");
            Transform boardPivot = rootTf.Find("BoardPivot");
            Transform displayQuad = boardPivot != null ? boardPivot.Find("DisplayQuad") : null;

            if (displayQuad != null)
            {
                float w = Mathf.Clamp(prop.viewportWidth, UniTextProp.MinViewportWidth, UniTextProp.MaxViewportWidth);
                float h = Mathf.Clamp(prop.viewportHeight, UniTextProp.MinViewportHeight, UniTextProp.MaxViewportHeight);
                Vector3 quadPos = prop.GetDisplayQuadLocalPosition();
                Vector3 quadScale = new Vector3(w, h, 1f);

                if (recordUndo)
                {
                    Undo.RecordObject(displayQuad, "Update UniText DisplayQuad Preview");
                }
                displayQuad.localPosition = quadPos;
                displayQuad.localScale = quadScale;
                if (recordUndo)
                {
                    PrefabUtility.RecordPrefabInstancePropertyModifications(displayQuad);
                }
            }

            if (!prop.useBoneProxy || avatarRoot == null)
            {
                return;
            }

            if (prop.autoPositionToBone)
            {
                Transform targetBone = UniTextNdmfPlugin.ResolveHumanoidBone(avatarRoot, prop.followBone);
                if (targetBone == null)
                {
                    return;
                }

                Vector3 worldPos = targetBone.position + avatarRoot.transform.rotation * prop.boneOffset;
                Quaternion worldRot = avatarRoot.transform.rotation * Quaternion.Euler(0f, 180f, 0f);
                SetWorldPose(anchor, worldPos, worldRot, recordUndo, "Align UniText SideFollowAnchor");
                SetWorldPose(boardPivot, worldPos, worldRot, recordUndo, "Align UniText BoardPivot");
            }
            else if (!recordUndo && anchor != null && boardPivot != null)
            {
                anchor.SetPositionAndRotation(boardPivot.position, boardPivot.rotation);
            }
        }

        private static float EstimateShoulderHalfWidth(
            Animator animator,
            System.Func<Vector3, Vector3> toAvatarMeters,
            float heightScale)
        {
            float halfX = 0f;
            if (animator != null && animator.isHuman)
            {
                halfX = Mathf.Max(
                    GetAbsX(animator.GetBoneTransform(HumanBodyBones.LeftUpperArm), toAvatarMeters),
                    GetAbsX(animator.GetBoneTransform(HumanBodyBones.RightUpperArm), toAvatarMeters)
                );
                if (halfX < 1e-4f)
                {
                    halfX = Mathf.Max(
                        GetAbsX(animator.GetBoneTransform(HumanBodyBones.LeftShoulder), toAvatarMeters),
                        GetAbsX(animator.GetBoneTransform(HumanBodyBones.RightShoulder), toAvatarMeters)
                    );
                }
            }

            if (halfX < 1e-4f)
            {
                return 0.20f * heightScale;
            }

            return Mathf.Clamp(halfX + 0.07f * heightScale, 0.14f * heightScale, 0.36f * heightScale);
        }

        private static float GetAbsX(Transform tf, System.Func<Vector3, Vector3> toAvatarMeters)
        {
            return tf != null ? Mathf.Abs(toAvatarMeters(tf.position).x) : 0f;
        }

        private static void SetWorldPose(
            Transform tf,
            Vector3 worldPos,
            Quaternion worldRot,
            bool recordUndo,
            string undoName)
        {
            if (tf == null)
            {
                return;
            }

            if (recordUndo)
            {
                Undo.RecordObject(tf, undoName);
            }
            tf.SetPositionAndRotation(worldPos, worldRot);
            if (recordUndo)
            {
                PrefabUtility.RecordPrefabInstancePropertyModifications(tf);
            }
        }
    }
}
#endif
