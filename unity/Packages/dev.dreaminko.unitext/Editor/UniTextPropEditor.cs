#if UNITEXT_VRCSDK3 && UNITEXT_MA && UNITEXT_NDMF
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace UniText.Editor
{
    [CustomEditor(typeof(UniTextProp))]
    public class UniTextPropEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var prop = (UniTextProp)target;
            VRCAvatarDescriptor avatar = prop.GetComponentInParent<VRCAvatarDescriptor>();
            GameObject avatarRoot = avatar != null ? avatar.gameObject : null;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Quick Placement", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            DrawPresetButton(prop, avatarRoot, UniTextPlacementPreset.RightSide, "Right");
            DrawPresetButton(prop, avatarRoot, UniTextPlacementPreset.LeftSide, "Left");
            DrawPresetButton(prop, avatarRoot, UniTextPlacementPreset.AboveHead, "Above Head");
            DrawPresetButton(prop, avatarRoot, UniTextPlacementPreset.Custom, "Manual");
            EditorGUILayout.EndHorizontal();

            EditorGUI.BeginDisabledGroup(avatarRoot == null);
            if (GUILayout.Button("Auto-Fit to Avatar"))
            {
                UniTextPlacementPreset fitPreset = prop.placementPreset == UniTextPlacementPreset.Custom
                    ? UniTextPlacementPreset.RightSide
                    : prop.placementPreset;
                ApplyPreset(prop, avatarRoot, fitPreset);
            }
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(4f);
            serializedObject.Update();
            EditorGUI.BeginChangeCheck();
            SerializedProperty iterator = serializedObject.GetIterator();
            bool enterChildren = true;
            while (iterator.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (iterator.propertyPath == "m_Script")
                {
                    using (new EditorGUI.DisabledScope(true))
                    {
                        EditorGUILayout.PropertyField(iterator, true);
                    }
                }
                else if (iterator.propertyPath == nameof(UniTextProp.followBone))
                {
                    UniTextPlacementUtility.DrawFollowBoneProperty(iterator);
                }
                else
                {
                    EditorGUILayout.PropertyField(iterator, true);
                }
            }
            bool guiChanged = EditorGUI.EndChangeCheck();
            bool modified = serializedObject.ApplyModifiedProperties();
            if (guiChanged || modified)
            {
                UniTextPlacementUtility.ApplyTransformPreview(avatarRoot, prop, recordUndo: true);
            }
        }

        private void OnSceneGUI()
        {
            var prop = (UniTextProp)target;
            Transform boardPivot = prop.transform.Find("BoardPivot");
            if (boardPivot == null)
            {
                return;
            }

            Color prevColor = Handles.color;
            Handles.color = new Color(0.32f, 0.78f, 1.0f, 0.9f);
            float size = HandleUtility.GetHandleSize(boardPivot.position) * 0.045f;
            Handles.SphereHandleCap(0, boardPivot.position, Quaternion.identity, size, EventType.Repaint);

            if (prop.useBoneProxy)
            {
                VRCAvatarDescriptor avatar = prop.GetComponentInParent<VRCAvatarDescriptor>();
                Transform bone = avatar != null
                    ? UniTextNdmfPlugin.ResolveHumanoidBone(avatar.gameObject, prop.followBone)
                    : null;
                if (bone != null)
                {
                    Handles.DrawDottedLine(bone.position, boardPivot.position, 4f);
                }
            }

            Handles.color = prevColor;
        }

        private static void DrawPresetButton(
            UniTextProp prop,
            GameObject avatarRoot,
            UniTextPlacementPreset preset,
            string label)
        {
            bool active = prop.placementPreset == preset;
            Color prevBg = GUI.backgroundColor;
            if (active)
            {
                GUI.backgroundColor = new Color(0.45f, 0.78f, 1.0f, 1.0f);
            }

            if (GUILayout.Button(label, EditorStyles.miniButton))
            {
                ApplyPreset(prop, avatarRoot, preset);
            }

            GUI.backgroundColor = prevBg;
        }

        private static void ApplyPreset(UniTextProp prop, GameObject avatarRoot, UniTextPlacementPreset preset)
        {
            Undo.RecordObject(prop, "Change UniText Placement Preset");
            prop.placementPreset = preset;
            if (preset == UniTextPlacementPreset.Custom)
            {
                prop.autoPositionToBone = false;
            }
            else
            {
                prop.boardAnchor = UniTextProp.GetPresetAnchor(preset);
                prop.autoPositionToBone = true;
                prop.boneOffset = UniTextPlacementUtility.ComputeAdaptiveBoneOffset(
                    avatarRoot,
                    preset,
                    prop.followBone,
                    UniTextPlacementUtility.GetDefaultPresetOffset(preset)
                );
            }

            EditorUtility.SetDirty(prop);
            PrefabUtility.RecordPrefabInstancePropertyModifications(prop);
            UniTextPlacementUtility.ApplyTransformPreview(avatarRoot, prop, recordUndo: true);
        }
    }
}
#endif
