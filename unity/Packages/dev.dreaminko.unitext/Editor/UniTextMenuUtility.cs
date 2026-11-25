#if UNITEXT_VRCSDK3 && UNITEXT_MA && UNITEXT_NDMF
using System;
using System.Collections.Generic;
using System.Reflection;
using nadena.dev.modular_avatar.core;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace UniText.Editor
{
    internal static class UniTextMenuUtility
    {
        public const string DefaultMenuName = "UniText";
        public const string AppearanceMenuName = "Appearance";
        public const string IconsDir = UniTextAssetBuilder.PackageRoot + "/Icons";

        private const string IconGuidShowBoard = "e7a100084d9c40008000000000000001";
        private const string IconGuidWorldDrop = "e7a100084d9c40008000000000000002";
        private const string IconGuidBillboard = "e7a100084d9c40008000000000000003";
        private const string IconGuidClearScreen = "e7a100084d9c40008000000000000004";
        private const string IconGuidScale = "e7a100084d9c40008000000000000005";
        private const string IconGuidOpacity = "e7a100084d9c40008000000000000006";
        private const string IconGuidWidth = "e7a100084d9c40008000000000000007";
        private const string IconGuidHeight = "e7a100084d9c40008000000000000008";
        private const string IconGuidUniText = "e7a100084d9c40008000000000000009";
        private const string IconGuidAppearance = "e7a100084d9c4000800000000000000a";

        public const string ParamVisible = "UniText_Visible";
        public const string ParamWorldDrop = "UniText_WorldDrop";
        public const string ParamBillboard = "UniText_Billboard";
        public const string ParamLocalClear = "UniText_LocalClear";
        public const string ParamScale = "UniText_Scale";
        public const string ParamOpacity = "UniText_Opacity";
        public const string ParamWidth = "UniText_Width";
        public const string ParamHeight = "UniText_Height";

        internal struct MenuParameterOverride
        {
            public bool HasMenuItem;
            public bool IsSaved;
            public bool IsDefault;
            public bool IsSynced;
        }

        public static string SanitizeMenuName(string menuName)
        {
            return string.IsNullOrWhiteSpace(menuName) ? DefaultMenuName : menuName.Trim();
        }

        public static Texture2D LoadMenuIcon(string iconBaseName, string fallbackGuid = null)
        {
            if (!string.IsNullOrEmpty(iconBaseName))
            {
                Texture2D icon = AssetDatabase.LoadAssetAtPath<Texture2D>($"{IconsDir}/{iconBaseName}.png");
                if (icon != null)
                {
                    return icon;
                }
            }

            if (!string.IsNullOrEmpty(fallbackGuid))
            {
                string guidPath = AssetDatabase.GUIDToAssetPath(fallbackGuid);
                if (!string.IsNullOrEmpty(guidPath))
                {
                    return AssetDatabase.LoadAssetAtPath<Texture2D>(guidPath);
                }
            }

            return null;
        }

        public static Texture2D GetDefaultIconForParameter(string paramName)
        {
            if (string.IsNullOrEmpty(paramName))
            {
                return null;
            }

            switch (paramName)
            {
                case ParamVisible: return LoadMenuIcon("ShowBoard", IconGuidShowBoard);
                case ParamWorldDrop: return LoadMenuIcon("WorldDrop", IconGuidWorldDrop);
                case ParamBillboard: return LoadMenuIcon("Billboard", IconGuidBillboard);
                case ParamLocalClear: return LoadMenuIcon("ClearScreen", IconGuidClearScreen);
                case ParamScale: return LoadMenuIcon("Scale", IconGuidScale);
                case ParamOpacity: return LoadMenuIcon("Opacity", IconGuidOpacity);
                case ParamWidth: return LoadMenuIcon("Width", IconGuidWidth);
                case ParamHeight: return LoadMenuIcon("Height", IconGuidHeight);
                default: return null;
            }
        }

        public static Texture2D GetDefaultRootMenuIcon()
        {
            return LoadMenuIcon("UniText", IconGuidUniText) ?? LoadMenuIcon("ShowBoard", IconGuidShowBoard);
        }

        public static Texture2D GetDefaultAppearanceMenuIcon()
        {
            return LoadMenuIcon("Appearance", IconGuidAppearance);
        }

        public static bool IsUniTextMenuParameter(string paramName)
        {
            if (string.IsNullOrEmpty(paramName))
            {
                return false;
            }

            return string.Equals(paramName, ParamVisible, StringComparison.Ordinal)
                || string.Equals(paramName, ParamWorldDrop, StringComparison.Ordinal)
                || string.Equals(paramName, ParamBillboard, StringComparison.Ordinal)
                || string.Equals(paramName, ParamLocalClear, StringComparison.Ordinal)
                || string.Equals(paramName, ParamScale, StringComparison.Ordinal)
                || string.Equals(paramName, ParamOpacity, StringComparison.Ordinal)
                || string.Equals(paramName, ParamWidth, StringComparison.Ordinal)
                || string.Equals(paramName, ParamHeight, StringComparison.Ordinal);
        }

        public static GameObject CreateMenuHierarchy(
            GameObject propRoot,
            string menuName,
            VRCExpressionsMenu installTargetMenu,
            bool syncWorldDrop,
            bool allowRuntimeResize,
            bool recordUndo)
        {
            if (propRoot == null)
            {
                return null;
            }

            string sanitizedName = SanitizeMenuName(menuName);

            var menuRoot = new GameObject(sanitizedName);
            menuRoot.transform.SetParent(propRoot.transform, false);

            var installer = menuRoot.AddComponent<ModularAvatarMenuInstaller>();
            installer.menuToAppend = null;
            installer.installTargetMenu = installTargetMenu;
            EditorUtility.SetDirty(installer);

            var rootItem = CreateSubMenuItem(menuRoot, sanitizedName, GetDefaultRootMenuIcon());
            EditorUtility.SetDirty(rootItem);

            CreateSingleParamItem(
                menuRoot.transform,
                "Show Board",
                VRCExpressionsMenu.Control.ControlType.Toggle,
                ParamVisible,
                1f,
                isSynced: true,
                isSaved: true,
                isDefault: false,
                icon: GetDefaultIconForParameter(ParamVisible)
            );
            CreateSingleParamItem(
                menuRoot.transform,
                "World Drop",
                VRCExpressionsMenu.Control.ControlType.Toggle,
                ParamWorldDrop,
                1f,
                isSynced: syncWorldDrop,
                isSaved: false,
                isDefault: false,
                icon: GetDefaultIconForParameter(ParamWorldDrop)
            );
            CreateSingleParamItem(
                menuRoot.transform,
                "Billboard",
                VRCExpressionsMenu.Control.ControlType.Toggle,
                ParamBillboard,
                1f,
                isSynced: false,
                isSaved: true,
                isDefault: false,
                icon: GetDefaultIconForParameter(ParamBillboard)
            );
            CreateSingleParamItem(
                menuRoot.transform,
                "Clear Screen",
                VRCExpressionsMenu.Control.ControlType.Button,
                ParamLocalClear,
                1f,
                isSynced: false,
                isSaved: false,
                isDefault: false,
                icon: GetDefaultIconForParameter(ParamLocalClear)
            );

            var appearanceGo = new GameObject(AppearanceMenuName);
            appearanceGo.transform.SetParent(menuRoot.transform, false);
            var appearanceItem = CreateSubMenuItem(appearanceGo, AppearanceMenuName, GetDefaultAppearanceMenuIcon());
            EditorUtility.SetDirty(appearanceItem);

            CreateSingleParamItem(
                appearanceGo.transform,
                "Scale",
                VRCExpressionsMenu.Control.ControlType.RadialPuppet,
                ParamScale,
                1f,
                isSynced: false,
                isSaved: true,
                isDefault: false,
                icon: GetDefaultIconForParameter(ParamScale)
            );
            CreateSingleParamItem(
                appearanceGo.transform,
                "Opacity",
                VRCExpressionsMenu.Control.ControlType.RadialPuppet,
                ParamOpacity,
                1f,
                isSynced: false,
                isSaved: true,
                isDefault: false,
                icon: GetDefaultIconForParameter(ParamOpacity)
            );

            if (allowRuntimeResize)
            {
                CreateSingleParamItem(
                    appearanceGo.transform,
                    "Width",
                    VRCExpressionsMenu.Control.ControlType.RadialPuppet,
                    ParamWidth,
                    1f,
                    isSynced: false,
                    isSaved: true,
                    isDefault: false,
                    icon: GetDefaultIconForParameter(ParamWidth)
                );
                CreateSingleParamItem(
                    appearanceGo.transform,
                    "Height",
                    VRCExpressionsMenu.Control.ControlType.RadialPuppet,
                    ParamHeight,
                    1f,
                    isSynced: false,
                    isSaved: true,
                    isDefault: false,
                    icon: GetDefaultIconForParameter(ParamHeight)
                );
            }

            if (recordUndo)
            {
                Undo.RegisterCreatedObjectUndo(menuRoot, "Create UniText MA Menu");
            }

            EditorUtility.SetDirty(menuRoot);
            return menuRoot;
        }

        public static bool HasUniTextMenuInHierarchy(GameObject searchRoot, bool includeInactive = true)
        {
            if (searchRoot == null)
            {
                return false;
            }

            foreach (var item in searchRoot.GetComponentsInChildren<ModularAvatarMenuItem>(includeInactive))
            {
                if (item != null && IsUniTextMenuParameter(GetControlledParameterName(item)))
                {
                    return true;
                }
            }

            return false;
        }

        public static GameObject FindMenuRootUnderProp(GameObject propRoot)
        {
            if (propRoot == null)
            {
                return null;
            }

            foreach (var installer in propRoot.GetComponentsInChildren<ModularAvatarMenuInstaller>(true))
            {
                if (installer == null || installer.gameObject == propRoot || installer.menuToAppend != null)
                {
                    continue;
                }

                var item = installer.GetComponent<ModularAvatarMenuItem>();
                if (item == null)
                {
                    continue;
                }

                bool isDirectSubMenu = installer.transform.parent == propRoot.transform
                    && GetControlType(item) == VRCExpressionsMenu.Control.ControlType.SubMenu;
                if (HasUniTextMenuInHierarchy(installer.gameObject, true) || isDirectSubMenu)
                {
                    return installer.gameObject;
                }
            }

            for (int i = 0; i < propRoot.transform.childCount; i++)
            {
                GameObject child = propRoot.transform.GetChild(i).gameObject;
                if (child.name == "SideFollowAnchor" || child.name == "BoardPivot" || child.name == "ParamBridge")
                {
                    continue;
                }

                var item = child.GetComponent<ModularAvatarMenuItem>();
                if (item != null && (HasUniTextMenuInHierarchy(child, true)
                    || GetControlType(item) == VRCExpressionsMenu.Control.ControlType.SubMenu))
                {
                    return child;
                }
            }

            return null;
        }

        public static GameObject FindMenuRoot(GameObject propRoot, GameObject avatarRoot = null)
        {
            GameObject underProp = FindMenuRootUnderProp(propRoot);
            if (underProp != null)
            {
                return underProp;
            }

            if (avatarRoot == null)
            {
                return null;
            }

            ModularAvatarMenuItem seed = null;
            foreach (var item in avatarRoot.GetComponentsInChildren<ModularAvatarMenuItem>(true))
            {
                if (item == null)
                {
                    continue;
                }

                string paramName = GetControlledParameterName(item);
                if (!IsUniTextMenuParameter(paramName))
                {
                    continue;
                }

                if (paramName == ParamVisible)
                {
                    seed = item;
                    break;
                }

                if (seed == null)
                {
                    seed = item;
                }
            }

            if (seed == null)
            {
                return null;
            }

            Transform current = seed.transform;
            while (current.parent != null && current.parent != avatarRoot.transform)
            {
                Transform parent = current.parent;
                var parentItem = parent.GetComponent<ModularAvatarMenuItem>();
                var parentInstaller = parent.GetComponent<ModularAvatarMenuInstaller>();
                if (parentItem == null && parentInstaller == null)
                {
                    break;
                }

                if (!ContainsOnlyUniTextItems(parent.gameObject))
                {
                    break;
                }

                var currItem = current.GetComponent<ModularAvatarMenuItem>();
                if (currItem != null && GetControlType(currItem) == VRCExpressionsMenu.Control.ControlType.SubMenu
                    && CountUniTextLeaves(parent.gameObject) <= CountUniTextLeaves(current.gameObject))
                {
                    break;
                }

                current = parent;
                if (parentInstaller != null && parentInstaller.menuToAppend == null)
                {
                    break;
                }
            }

            var currentItem = current.GetComponent<ModularAvatarMenuItem>();
            var currentInstaller = current.GetComponent<ModularAvatarMenuInstaller>();
            if ((currentItem != null && GetControlType(currentItem) == VRCExpressionsMenu.Control.ControlType.SubMenu)
                || (currentInstaller != null && currentInstaller.menuToAppend == null))
            {
                return current.gameObject;
            }

            return null;
        }

        public static VRCExpressionsMenu GetInstallTargetMenu(GameObject menuRoot, VRCExpressionsMenu fallback)
        {
            if (menuRoot == null)
            {
                return fallback;
            }

            var installer = menuRoot.GetComponent<ModularAvatarMenuInstaller>();
            if (installer != null && installer.menuToAppend == null)
            {
                return installer.installTargetMenu;
            }

            return fallback;
        }

        public static bool HasMenuInstaller(GameObject menuRoot)
        {
            if (menuRoot == null)
            {
                return false;
            }

            var installer = menuRoot.GetComponent<ModularAvatarMenuInstaller>();
            return installer != null && installer.menuToAppend == null;
        }

        public static int CountActiveMenuItems(GameObject menuRoot)
        {
            if (menuRoot == null)
            {
                return 0;
            }

            int count = 0;
            foreach (var item in menuRoot.GetComponentsInChildren<ModularAvatarMenuItem>(false))
            {
                if (item != null && item.enabled && GetControlType(item) != VRCExpressionsMenu.Control.ControlType.SubMenu)
                {
                    count++;
                }
            }

            return count;
        }

        public static bool CanModifyOrDeleteGameObject(GameObject go)
        {
            if (go == null)
            {
                return false;
            }

            if (!PrefabUtility.IsPartOfPrefabInstance(go))
            {
                return true;
            }

            return PrefabUtility.IsAddedGameObjectOverride(go)
                || PrefabUtility.IsOutermostPrefabInstanceRoot(go);
        }

        public static void SyncMenuHierarchy(
            GameObject menuRoot,
            string desiredMenuName,
            VRCExpressionsMenu desiredTargetMenu,
            bool updateMenuName,
            bool updateTargetMenu,
            bool syncWorldDrop,
            bool allowRuntimeResize,
            bool recordUndo)
        {
            if (menuRoot == null)
            {
                return;
            }

            string sanitizedName = SanitizeMenuName(desiredMenuName);

            if (updateMenuName)
            {
                if (!string.Equals(menuRoot.name, sanitizedName, StringComparison.Ordinal))
                {
                    if (recordUndo)
                    {
                        Undo.RecordObject(menuRoot, "Rename UniText Menu");
                    }
                    menuRoot.name = sanitizedName;
                    EditorUtility.SetDirty(menuRoot);
                }

                var rootItem = menuRoot.GetComponent<ModularAvatarMenuItem>();
                if (rootItem != null)
                {
                    VRCExpressionsMenu.Control existingControl = GetControl(rootItem);
                    if (existingControl == null || !string.Equals(existingControl.name, sanitizedName, StringComparison.Ordinal))
                    {
                        if (recordUndo)
                        {
                            Undo.RecordObject(rootItem, "Rename UniText Menu Control");
                        }

                        VRCExpressionsMenu.Control updated = CloneControl(existingControl);
                        if (existingControl == null)
                        {
                            updated.type = VRCExpressionsMenu.Control.ControlType.SubMenu;
                        }
                        updated.name = sanitizedName;
                        SetControl(rootItem, updated);
                        EditorUtility.SetDirty(rootItem);
                    }
                }
            }

            if (updateTargetMenu)
            {
                var installer = menuRoot.GetComponent<ModularAvatarMenuInstaller>();
                if (installer != null && installer.menuToAppend == null && installer.installTargetMenu != desiredTargetMenu)
                {
                    if (recordUndo)
                    {
                        Undo.RecordObject(installer, "Update UniText Menu Target");
                    }
                    installer.installTargetMenu = desiredTargetMenu;
                    EditorUtility.SetDirty(installer);
                }
            }

            SyncWorldDropAndResizeItems(menuRoot, syncWorldDrop, allowRuntimeResize, recordUndo, allowAddResizeItems: true);
            ApplyDefaultIconsIfMissing(menuRoot, recordUndo);
        }

        public static void ApplyDefaultIconsIfMissing(GameObject menuRoot, bool recordUndo)
        {
            if (menuRoot == null)
            {
                return;
            }

            var rootItem = menuRoot.GetComponent<ModularAvatarMenuItem>();
            if (rootItem != null)
            {
                VRCExpressionsMenu.Control rootCtrl = GetControl(rootItem);
                if (rootCtrl != null && rootCtrl.type == VRCExpressionsMenu.Control.ControlType.SubMenu)
                {
                    Texture2D rootIcon = GetDefaultRootMenuIcon();
                    bool shouldAssignRootIcon = rootIcon != null
                        && (rootCtrl.icon == null
                            || (rootCtrl.icon != rootIcon
                                && rootCtrl.icon == LoadMenuIcon("ShowBoard", IconGuidShowBoard)));
                    if (shouldAssignRootIcon)
                    {
                        if (recordUndo)
                        {
                            Undo.RecordObject(rootItem, "Assign UniText Menu Icon");
                        }
                        VRCExpressionsMenu.Control updated = CloneControl(rootCtrl);
                        updated.icon = rootIcon;
                        SetControl(rootItem, updated);
                        EditorUtility.SetDirty(rootItem);
                    }
                }
            }

            foreach (var item in menuRoot.GetComponentsInChildren<ModularAvatarMenuItem>(true))
            {
                if (item == null || item.gameObject == menuRoot)
                {
                    continue;
                }

                VRCExpressionsMenu.Control ctrl = GetControl(item);
                if (ctrl == null || ctrl.icon != null)
                {
                    continue;
                }

                Texture2D defaultIcon = null;
                if (ctrl.type == VRCExpressionsMenu.Control.ControlType.SubMenu)
                {
                    if (IsAppearanceSubMenu(item, ctrl))
                    {
                        defaultIcon = GetDefaultAppearanceMenuIcon();
                    }
                }
                else
                {
                    defaultIcon = GetDefaultIconForParameter(GetControlledParameterName(item));
                }

                if (defaultIcon == null)
                {
                    continue;
                }

                if (recordUndo)
                {
                    Undo.RecordObject(item, "Assign UniText Menu Icon");
                }
                VRCExpressionsMenu.Control updated = CloneControl(ctrl);
                updated.icon = defaultIcon;
                SetControl(item, updated);
                EditorUtility.SetDirty(item);
            }
        }

        private static bool IsAppearanceSubMenu(ModularAvatarMenuItem subMenuItem, VRCExpressionsMenu.Control ctrl)
        {
            if (string.Equals(subMenuItem.gameObject.name, AppearanceMenuName, StringComparison.Ordinal)
                || string.Equals(ctrl.name, AppearanceMenuName, StringComparison.Ordinal))
            {
                return true;
            }

            foreach (var child in subMenuItem.GetComponentsInChildren<ModularAvatarMenuItem>(true))
            {
                string childParam = GetControlledParameterName(child);
                if (string.Equals(childParam, ParamScale, StringComparison.Ordinal)
                    || string.Equals(childParam, ParamOpacity, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        public static GameObject ApplyMenuSettings(
            UniTextProp prop,
            GameObject avatarRoot,
            bool installMenu,
            string menuName,
            VRCExpressionsMenu installTargetMenu,
            bool syncWorldDrop,
            bool allowRuntimeResize,
            bool recordUndo)
        {
            if (prop == null)
            {
                return null;
            }

            if (!installMenu)
            {
                RemoveMenuRootUnderProp(prop.gameObject, recordUndo);
                return null;
            }

            GameObject underProp = FindMenuRootUnderProp(prop.gameObject);
            GameObject menuRoot = underProp != null ? underProp : FindMenuRoot(prop.gameObject, avatarRoot);
            if (menuRoot != null)
            {
                bool isUnderProp = underProp != null;
                if (isUnderProp && !menuRoot.activeSelf)
                {
                    if (recordUndo)
                    {
                        Undo.RecordObject(menuRoot, "Enable UniText Menu");
                    }
                    menuRoot.SetActive(true);
                    EditorUtility.SetDirty(menuRoot);
                }

                SyncMenuHierarchy(
                    menuRoot,
                    menuName,
                    installTargetMenu,
                    updateMenuName: true,
                    updateTargetMenu: isUnderProp || HasMenuInstaller(menuRoot),
                    syncWorldDrop: syncWorldDrop,
                    allowRuntimeResize: allowRuntimeResize,
                    recordUndo: recordUndo
                );
                return menuRoot;
            }

            if (avatarRoot != null && HasUniTextMenuInHierarchy(avatarRoot, true))
            {
                SyncLooseMenuItems(avatarRoot, syncWorldDrop, allowRuntimeResize, recordUndo);
                return null;
            }

            return CreateMenuHierarchy(
                prop.gameObject,
                menuName,
                installTargetMenu,
                syncWorldDrop,
                allowRuntimeResize,
                recordUndo
            );
        }

        public static void SyncLooseMenuItems(
            GameObject avatarRoot,
            bool syncWorldDrop,
            bool allowRuntimeResize,
            bool recordUndo)
        {
            if (avatarRoot == null)
            {
                return;
            }

            SyncWorldDropAndResizeItems(avatarRoot, syncWorldDrop, allowRuntimeResize, recordUndo, allowAddResizeItems: false);
        }

        public static void RemoveMenuRootUnderProp(GameObject propRoot, bool recordUndo)
        {
            GameObject menuRoot = FindMenuRootUnderProp(propRoot);
            if (menuRoot == null)
            {
                return;
            }

            if (CanModifyOrDeleteGameObject(menuRoot))
            {
                if (recordUndo)
                {
                    Undo.DestroyObjectImmediate(menuRoot);
                }
                else
                {
                    UnityEngine.Object.DestroyImmediate(menuRoot);
                }
            }
            else if (menuRoot.activeSelf)
            {
                if (recordUndo)
                {
                    Undo.RecordObject(menuRoot, "Disable UniText Menu");
                }
                menuRoot.SetActive(false);
                EditorUtility.SetDirty(menuRoot);
            }
        }

        public static GameObject ResetMenuHierarchy(UniTextProp prop, bool recordUndo)
        {
            if (prop == null)
            {
                return null;
            }

            RemoveMenuRootUnderProp(prop.gameObject, recordUndo);

            VRCExpressionsMenu targetMenu = null;
#if UNITEXT_VRCSDK3
            targetMenu = prop.installTargetMenu;
#endif
            return CreateMenuHierarchy(
                prop.gameObject,
                prop.menuName,
                targetMenu,
                prop.syncWorldDrop,
                prop.allowRuntimeResize,
                recordUndo
            );
        }

        internal static Dictionary<string, MenuParameterOverride> CollectActiveMenuParameterOverrides(GameObject avatarRoot)
        {
            var result = new Dictionary<string, MenuParameterOverride>(StringComparer.Ordinal);
            if (avatarRoot == null)
            {
                return result;
            }

            foreach (var item in avatarRoot.GetComponentsInChildren<ModularAvatarMenuItem>(false))
            {
                if (item == null || !item.enabled)
                {
                    continue;
                }

                string paramName = GetControlledParameterName(item);
                if (!IsUniTextMenuParameter(paramName))
                {
                    continue;
                }

                bool isSaved = GetBoolMemberOrDefault(item, "isSaved", false);
                bool isDefault = GetBoolMemberOrDefault(item, "isDefault", false);
                bool isSynced = GetBoolMemberOrDefault(item, "isSynced", false);

                if (result.TryGetValue(paramName, out MenuParameterOverride existing))
                {
                    existing.HasMenuItem = true;
                    existing.IsSaved |= isSaved;
                    existing.IsDefault |= isDefault;
                    existing.IsSynced |= isSynced;
                    result[paramName] = existing;
                }
                else
                {
                    result[paramName] = new MenuParameterOverride
                    {
                        HasMenuItem = true,
                        IsSaved = isSaved,
                        IsDefault = isDefault,
                        IsSynced = isSynced
                    };
                }
            }

            return result;
        }

        public static void DeactivateResizeMenuItems(GameObject searchRoot)
        {
            if (searchRoot == null)
            {
                return;
            }

            foreach (var item in searchRoot.GetComponentsInChildren<ModularAvatarMenuItem>(true))
            {
                if (item == null)
                {
                    continue;
                }

                string paramName = GetControlledParameterName(item);
                if ((string.Equals(paramName, ParamWidth, StringComparison.Ordinal)
                    || string.Equals(paramName, ParamHeight, StringComparison.Ordinal))
                    && item.gameObject.activeSelf)
                {
                    item.gameObject.SetActive(false);
                }
            }
        }

        public static void AlignBuildMenuItemSyncFlags(GameObject searchRoot, bool syncWorldDrop)
        {
            if (searchRoot == null)
            {
                return;
            }

            foreach (var item in searchRoot.GetComponentsInChildren<ModularAvatarMenuItem>(false))
            {
                if (item == null || !item.enabled)
                {
                    continue;
                }

                string paramName = GetControlledParameterName(item);
                if (!IsUniTextMenuParameter(paramName))
                {
                    continue;
                }

                bool expectedSynced = string.Equals(paramName, ParamVisible, StringComparison.Ordinal)
                    || (string.Equals(paramName, ParamWorldDrop, StringComparison.Ordinal) && syncWorldDrop);

                if (GetBoolMemberOrDefault(item, "isSynced", expectedSynced) != expectedSynced)
                {
                    SetBoolMemberIfExists(item, "isSynced", expectedSynced);
                }
            }
        }

        private static void SyncWorldDropAndResizeItems(
            GameObject searchRoot,
            bool syncWorldDrop,
            bool allowRuntimeResize,
            bool recordUndo,
            bool allowAddResizeItems)
        {
            bool hasWidth = false;
            bool hasHeight = false;

            foreach (var item in searchRoot.GetComponentsInChildren<ModularAvatarMenuItem>(true))
            {
                if (item == null)
                {
                    continue;
                }

                string paramName = GetControlledParameterName(item);
                if (string.Equals(paramName, ParamWorldDrop, StringComparison.Ordinal))
                {
                    if (GetBoolMemberOrDefault(item, "isSynced", true) != syncWorldDrop)
                    {
                        if (recordUndo)
                        {
                            Undo.RecordObject(item, "Sync UniText WorldDrop Menu");
                        }
                        SetBoolMemberIfExists(item, "isSynced", syncWorldDrop);
                        EditorUtility.SetDirty(item);
                    }
                    continue;
                }

                bool isWidth = string.Equals(paramName, ParamWidth, StringComparison.Ordinal);
                bool isHeight = string.Equals(paramName, ParamHeight, StringComparison.Ordinal);
                if (!isWidth && !isHeight)
                {
                    continue;
                }

                if (isWidth)
                {
                    hasWidth = true;
                }
                else
                {
                    hasHeight = true;
                }

                if (allowRuntimeResize)
                {
                    if (!item.gameObject.activeSelf)
                    {
                        if (recordUndo)
                        {
                            Undo.RecordObject(item.gameObject, "Enable UniText Resize Menu Item");
                        }
                        item.gameObject.SetActive(true);
                        EditorUtility.SetDirty(item.gameObject);
                    }

                    if (!item.enabled)
                    {
                        if (recordUndo)
                        {
                            Undo.RecordObject(item, "Enable UniText Resize Menu Item");
                        }
                        item.enabled = true;
                        EditorUtility.SetDirty(item);
                    }
                }
                else if (item.gameObject.activeSelf)
                {
                    if (recordUndo)
                    {
                        Undo.RecordObject(item.gameObject, "Disable UniText Resize Menu Item");
                    }
                    item.gameObject.SetActive(false);
                    EditorUtility.SetDirty(item.gameObject);
                }
            }

            if (allowRuntimeResize && allowAddResizeItems && (!hasWidth || !hasHeight))
            {
                Transform container = FindAppearanceContainer(searchRoot);
                if (!hasWidth)
                {
                    GameObject widthGo = CreateSingleParamItem(
                        container,
                        "Width",
                        VRCExpressionsMenu.Control.ControlType.RadialPuppet,
                        ParamWidth,
                        1f,
                        isSynced: false,
                        isSaved: true,
                        isDefault: false,
                        icon: GetDefaultIconForParameter(ParamWidth)
                    );
                    if (recordUndo)
                    {
                        Undo.RegisterCreatedObjectUndo(widthGo, "Add UniText Width Menu Item");
                    }
                }

                if (!hasHeight)
                {
                    GameObject heightGo = CreateSingleParamItem(
                        container,
                        "Height",
                        VRCExpressionsMenu.Control.ControlType.RadialPuppet,
                        ParamHeight,
                        1f,
                        isSynced: false,
                        isSaved: true,
                        isDefault: false,
                        icon: GetDefaultIconForParameter(ParamHeight)
                    );
                    if (recordUndo)
                    {
                        Undo.RegisterCreatedObjectUndo(heightGo, "Add UniText Height Menu Item");
                    }
                }
            }
        }

        private static Transform FindAppearanceContainer(GameObject menuRoot)
        {
            foreach (var item in menuRoot.GetComponentsInChildren<ModularAvatarMenuItem>(true))
            {
                if (item == null || item.gameObject == menuRoot)
                {
                    continue;
                }

                if (GetControlType(item) != VRCExpressionsMenu.Control.ControlType.SubMenu)
                {
                    continue;
                }

                foreach (var child in item.GetComponentsInChildren<ModularAvatarMenuItem>(true))
                {
                    string childParam = GetControlledParameterName(child);
                    if (string.Equals(childParam, ParamScale, StringComparison.Ordinal)
                        || string.Equals(childParam, ParamOpacity, StringComparison.Ordinal))
                    {
                        return item.transform;
                    }
                }
            }

            foreach (var item in menuRoot.GetComponentsInChildren<ModularAvatarMenuItem>(true))
            {
                if (item == null || item.gameObject == menuRoot)
                {
                    continue;
                }

                if (GetControlType(item) != VRCExpressionsMenu.Control.ControlType.SubMenu)
                {
                    continue;
                }

                string ctrlName = GetControl(item)?.name;
                if (string.Equals(item.gameObject.name, AppearanceMenuName, StringComparison.Ordinal)
                    || string.Equals(ctrlName, AppearanceMenuName, StringComparison.Ordinal))
                {
                    return item.transform;
                }
            }

            return menuRoot.transform;
        }

        private static bool ContainsOnlyUniTextItems(GameObject root)
        {
            if (root == null)
            {
                return false;
            }

            bool foundAnyUniText = false;
            foreach (var item in root.GetComponentsInChildren<ModularAvatarMenuItem>(true))
            {
                if (item == null)
                {
                    continue;
                }

                if (GetControlType(item) == VRCExpressionsMenu.Control.ControlType.SubMenu)
                {
                    continue;
                }

                string paramName = GetControlledParameterName(item);
                if (string.IsNullOrEmpty(paramName))
                {
                    continue;
                }

                if (IsUniTextMenuParameter(paramName))
                {
                    foundAnyUniText = true;
                }
                else
                {
                    return false;
                }
            }

            return foundAnyUniText;
        }

        private static int CountUniTextLeaves(GameObject root)
        {
            if (root == null)
            {
                return 0;
            }

            int count = 0;
            foreach (var item in root.GetComponentsInChildren<ModularAvatarMenuItem>(true))
            {
                if (item != null
                    && GetControlType(item) != VRCExpressionsMenu.Control.ControlType.SubMenu
                    && IsUniTextMenuParameter(GetControlledParameterName(item)))
                {
                    count++;
                }
            }

            return count;
        }

        private static ModularAvatarMenuItem CreateSubMenuItem(GameObject go, string displayName, Texture2D icon = null)
        {
            var item = go.AddComponent<ModularAvatarMenuItem>();
            item.MenuSource = SubmenuSource.Children;
            SetControl(item, new VRCExpressionsMenu.Control
            {
                name = displayName,
                icon = icon,
                type = VRCExpressionsMenu.Control.ControlType.SubMenu,
                parameter = new VRCExpressionsMenu.Control.Parameter { name = string.Empty },
                subParameters = Array.Empty<VRCExpressionsMenu.Control.Parameter>()
            });
            SetBoolMemberIfExists(item, "automaticValue", false);
            SetBoolMemberIfExists(item, "isSynced", false);
            SetBoolMemberIfExists(item, "isSaved", false);
            SetBoolMemberIfExists(item, "isDefault", false);
            return item;
        }

        private static GameObject CreateSingleParamItem(
            Transform parent,
            string itemName,
            VRCExpressionsMenu.Control.ControlType controlType,
            string paramName,
            float value,
            bool isSynced,
            bool isSaved,
            bool isDefault,
            Texture2D icon = null)
        {
            var go = new GameObject(itemName);
            go.transform.SetParent(parent, false);

            var item = go.AddComponent<ModularAvatarMenuItem>();
            VRCExpressionsMenu.Control control;
            if (controlType == VRCExpressionsMenu.Control.ControlType.RadialPuppet)
            {
                control = new VRCExpressionsMenu.Control
                {
                    name = itemName,
                    icon = icon,
                    type = VRCExpressionsMenu.Control.ControlType.RadialPuppet,
                    parameter = new VRCExpressionsMenu.Control.Parameter { name = string.Empty },
                    subParameters = new[]
                    {
                        new VRCExpressionsMenu.Control.Parameter { name = paramName }
                    }
                };
            }
            else
            {
                control = new VRCExpressionsMenu.Control
                {
                    name = itemName,
                    icon = icon,
                    type = controlType,
                    parameter = new VRCExpressionsMenu.Control.Parameter { name = paramName },
                    value = value
                };
            }

            SetControl(item, control);
            SetBoolMemberIfExists(item, "automaticValue", false);
            SetBoolMemberIfExists(item, "isSynced", isSynced);
            SetBoolMemberIfExists(item, "isSaved", isSaved);
            SetBoolMemberIfExists(item, "isDefault", isDefault);
            EditorUtility.SetDirty(item);
            return go;
        }

        private static VRCExpressionsMenu.Control GetControl(ModularAvatarMenuItem item)
        {
            if (item == null)
            {
                return null;
            }
#pragma warning disable CS0618
            return item.Control;
#pragma warning restore CS0618
        }

        private static void SetControl(ModularAvatarMenuItem item, VRCExpressionsMenu.Control control)
        {
            if (item == null)
            {
                return;
            }
#pragma warning disable CS0618
            item.Control = control;
#pragma warning restore CS0618
        }

        private static VRCExpressionsMenu.Control.ControlType GetControlType(ModularAvatarMenuItem item)
        {
            VRCExpressionsMenu.Control control = GetControl(item);
            return control != null ? control.type : VRCExpressionsMenu.Control.ControlType.Button;
        }

        private static string GetControlledParameterName(ModularAvatarMenuItem item)
        {
            VRCExpressionsMenu.Control control = GetControl(item);
            if (control == null)
            {
                return null;
            }

            if (control.type == VRCExpressionsMenu.Control.ControlType.RadialPuppet
                || control.type == VRCExpressionsMenu.Control.ControlType.TwoAxisPuppet
                || control.type == VRCExpressionsMenu.Control.ControlType.FourAxisPuppet)
            {
                if (control.subParameters != null)
                {
                    for (int i = 0; i < control.subParameters.Length; i++)
                    {
                        string subName = control.subParameters[i]?.name;
                        if (!string.IsNullOrEmpty(subName))
                        {
                            return subName;
                        }
                    }
                }
                return null;
            }

            string paramName = control.parameter?.name;
            return string.IsNullOrEmpty(paramName) ? null : paramName;
        }

        private static VRCExpressionsMenu.Control CloneControl(VRCExpressionsMenu.Control src)
        {
            if (src == null)
            {
                return new VRCExpressionsMenu.Control
                {
                    parameter = new VRCExpressionsMenu.Control.Parameter { name = string.Empty },
                    subParameters = Array.Empty<VRCExpressionsMenu.Control.Parameter>()
                };
            }

            var clone = new VRCExpressionsMenu.Control
            {
                name = src.name,
                icon = src.icon,
                type = src.type,
                value = src.value,
                subMenu = src.subMenu,
                parameter = src.parameter != null
                    ? new VRCExpressionsMenu.Control.Parameter { name = src.parameter.name }
                    : new VRCExpressionsMenu.Control.Parameter { name = string.Empty }
            };

            if (src.subParameters != null)
            {
                clone.subParameters = new VRCExpressionsMenu.Control.Parameter[src.subParameters.Length];
                for (int i = 0; i < src.subParameters.Length; i++)
                {
                    clone.subParameters[i] = src.subParameters[i] != null
                        ? new VRCExpressionsMenu.Control.Parameter { name = src.subParameters[i].name }
                        : new VRCExpressionsMenu.Control.Parameter { name = string.Empty };
                }
            }

            if (src.labels != null)
            {
                clone.labels = new VRCExpressionsMenu.Control.Label[src.labels.Length];
                Array.Copy(src.labels, clone.labels, src.labels.Length);
            }

            return clone;
        }

        private static void SetBoolMemberIfExists(object target, string memberName, bool value)
        {
            if (target == null)
            {
                return;
            }

            Type type = target.GetType();
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;

            FieldInfo field = type.GetField(memberName, flags);
            if (field != null && field.FieldType == typeof(bool))
            {
                field.SetValue(target, value);
                return;
            }

            PropertyInfo prop = type.GetProperty(memberName, flags);
            if (prop != null && prop.CanWrite && prop.PropertyType == typeof(bool))
            {
                prop.SetValue(target, value, null);
            }
        }

        private static bool GetBoolMemberOrDefault(object target, string memberName, bool defaultValue)
        {
            if (target == null)
            {
                return defaultValue;
            }

            Type type = target.GetType();
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;

            FieldInfo field = type.GetField(memberName, flags);
            if (field != null && field.FieldType == typeof(bool))
            {
                return (bool)field.GetValue(target);
            }

            PropertyInfo prop = type.GetProperty(memberName, flags);
            if (prop != null && prop.CanRead && prop.PropertyType == typeof(bool))
            {
                return (bool)prop.GetValue(target, null);
            }

            return defaultValue;
        }
    }
}
#endif
