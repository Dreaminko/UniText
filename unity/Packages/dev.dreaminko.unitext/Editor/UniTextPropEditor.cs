#if UNITEXT_VRCSDK3 && UNITEXT_MA && UNITEXT_NDMF
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

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

            bool isSceneProp = !EditorUtility.IsPersistent(prop.gameObject) && prop.gameObject.scene.IsValid();
            GameObject propMenuRoot = isSceneProp ? UniTextMenuUtility.FindMenuRootUnderProp(prop.gameObject) : null;
            GameObject externalMenuRoot = (isSceneProp && propMenuRoot == null && avatarRoot != null)
                ? UniTextMenuUtility.FindMenuRoot(prop.gameObject, avatarRoot)
                : null;
            bool hasLooseAvatarMenu = isSceneProp
                && propMenuRoot == null
                && externalMenuRoot == null
                && avatarRoot != null
                && UniTextMenuUtility.HasUniTextMenuInHierarchy(avatarRoot, includeInactive: true);

            EditorGUILayout.Space(4f);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Expressions Menu (MA Menu Item)", EditorStyles.boldLabel);

            if (!isSceneProp)
            {
                EditorGUILayout.HelpBox(
                    "Place this prefab on your Avatar in the Scene to generate or edit the Modular Avatar menu hierarchy (MA Menu Installer + MA Menu Item). If left ungenerated, NDMF creates it at build time when Install Menu is enabled.",
                    MessageType.Info
                );
            }
            else if (propMenuRoot != null)
            {
                int itemCount = UniTextMenuUtility.CountActiveMenuItems(propMenuRoot);
                string stateText = propMenuRoot.activeInHierarchy
                    ? $"{itemCount} active controls"
                    : "Inactive (disabled in Hierarchy)";
                EditorGUILayout.LabelField($"Menu Root: {propMenuRoot.name} ({stateText})", EditorStyles.miniLabel);
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Select Menu"))
                {
                    Selection.activeGameObject = propMenuRoot;
                    EditorGUIUtility.PingObject(propMenuRoot);
                }
                if (GUILayout.Button("Reset Default Menu"))
                {
                    if (EditorUtility.DisplayDialog(
                        "Reset UniText Menu",
                        $"Reset '{propMenuRoot.name}' under '{prop.gameObject.name}' back to the default Modular Avatar menu hierarchy?\n\nCustom icons, renames, or reordered items inside it will be replaced (Ctrl+Z can undo).",
                        "Reset",
                        "Cancel"))
                    {
                        Undo.IncrementCurrentGroup();
                        Undo.SetCurrentGroupName("Reset UniText MA Menu");
                        int undoGroup = Undo.GetCurrentGroup();
                        try
                        {
                            Undo.RecordObject(prop, "Enable Install Menu");
                            prop.installMenu = true;
                            EditorUtility.SetDirty(prop);
                            PrefabUtility.RecordPrefabInstancePropertyModifications(prop);

                            GameObject resetRoot = UniTextMenuUtility.ResetMenuHierarchy(prop, recordUndo: true);
                            EditorSceneManager.MarkSceneDirty(prop.gameObject.scene);
                            if (resetRoot != null)
                            {
                                EditorGUIUtility.PingObject(resetRoot);
                            }
                        }
                        finally
                        {
                            Undo.CollapseUndoOperations(undoGroup);
                        }
                    }
                }
                EditorGUILayout.EndHorizontal();
            }
            else if (externalMenuRoot != null)
            {
                int itemCount = UniTextMenuUtility.CountActiveMenuItems(externalMenuRoot);
                string stateText = externalMenuRoot.activeInHierarchy
                    ? $"{itemCount} active controls"
                    : "Inactive";
                EditorGUILayout.LabelField($"Moved Menu: {externalMenuRoot.name} ({stateText})", EditorStyles.miniLabel);
                if (GUILayout.Button("Select Menu"))
                {
                    Selection.activeGameObject = externalMenuRoot;
                    EditorGUIUtility.PingObject(externalMenuRoot);
                }
            }
            else if (hasLooseAvatarMenu)
            {
                EditorGUILayout.HelpBox(
                    "UniText MA Menu Item controls were detected elsewhere on this Avatar.",
                    MessageType.Info
                );
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "No MA Menu Item hierarchy found yet. Generate it now to customize names, icons, order, or defaults directly in Hierarchy (otherwise NDMF generates it automatically at build time when Install Menu is enabled).",
                    MessageType.Info
                );
                if (GUILayout.Button("Generate MA Menu Hierarchy"))
                {
                    Undo.IncrementCurrentGroup();
                    Undo.SetCurrentGroupName("Generate UniText MA Menu");
                    int undoGroup = Undo.GetCurrentGroup();
                    try
                    {
                        Undo.RecordObject(prop, "Enable Install Menu");
                        prop.installMenu = true;
                        EditorUtility.SetDirty(prop);
                        PrefabUtility.RecordPrefabInstancePropertyModifications(prop);

                        GameObject createdRoot = UniTextMenuUtility.CreateMenuHierarchy(
                            prop.gameObject,
                            prop.menuName,
                            prop.installTargetMenu,
                            prop.syncWorldDrop,
                            prop.syncBillboard,
                            prop.allowRuntimeResize,
                            recordUndo: true
                        );
                        EditorSceneManager.MarkSceneDirty(prop.gameObject.scene);
                        if (createdRoot != null)
                        {
                            EditorGUIUtility.PingObject(createdRoot);
                        }
                    }
                    finally
                    {
                        Undo.CollapseUndoOperations(undoGroup);
                    }
                }
            }
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(4f);
            bool prevInstallMenu = prop.installMenu;
            string prevMenuName = prop.menuName;
            VRCExpressionsMenu prevTargetMenu = prop.installTargetMenu;
            bool prevSyncWorldDrop = prop.syncWorldDrop;
            bool prevSyncBillboard = prop.syncBillboard;
            bool prevAllowRuntimeResize = prop.allowRuntimeResize;

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

                if (isSceneProp)
                {
                    bool installMenuChanged = prop.installMenu != prevInstallMenu;
                    bool menuNameChanged = !string.Equals(prevMenuName ?? string.Empty, prop.menuName ?? string.Empty, System.StringComparison.Ordinal);
                    bool targetMenuChanged = prop.installTargetMenu != prevTargetMenu;
                    bool syncWorldDropChanged = prop.syncWorldDrop != prevSyncWorldDrop;
                    bool syncBillboardChanged = prop.syncBillboard != prevSyncBillboard;
                    bool allowResizeChanged = prop.allowRuntimeResize != prevAllowRuntimeResize;

                    if (installMenuChanged)
                    {
                        UniTextMenuUtility.ApplyMenuSettings(
                            prop,
                            avatarRoot,
                            prop.installMenu,
                            prop.menuName,
                            prop.installTargetMenu,
                            prop.syncWorldDrop,
                            prop.syncBillboard,
                            prop.allowRuntimeResize,
                            recordUndo: true
                        );
                        EditorSceneManager.MarkSceneDirty(prop.gameObject.scene);
                    }
                    else if (menuNameChanged || targetMenuChanged || syncWorldDropChanged || syncBillboardChanged || allowResizeChanged)
                    {
                        GameObject activeMenuRoot = propMenuRoot != null ? propMenuRoot : externalMenuRoot;
                        if (activeMenuRoot != null)
                        {
                            UniTextMenuUtility.SyncMenuHierarchy(
                                activeMenuRoot,
                                prop.menuName,
                                prop.installTargetMenu,
                                updateMenuName: menuNameChanged,
                                updateTargetMenu: targetMenuChanged,
                                syncWorldDrop: prop.syncWorldDrop,
                                syncBillboard: prop.syncBillboard,
                                allowRuntimeResize: prop.allowRuntimeResize,
                                recordUndo: true
                            );
                            EditorSceneManager.MarkSceneDirty(prop.gameObject.scene);
                        }
                        else if (hasLooseAvatarMenu && (syncWorldDropChanged || syncBillboardChanged || allowResizeChanged) && avatarRoot != null)
                        {
                            UniTextMenuUtility.SyncLooseMenuItems(
                                avatarRoot,
                                prop.syncWorldDrop,
                                prop.syncBillboard,
                                prop.allowRuntimeResize,
                                recordUndo: true
                            );
                            EditorSceneManager.MarkSceneDirty(prop.gameObject.scene);
                        }
                    }
                }
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
