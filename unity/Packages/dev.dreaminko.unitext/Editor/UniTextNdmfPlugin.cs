#if UNITEXT_VRCSDK3 && UNITEXT_MA && UNITEXT_NDMF
using System;
using System.Collections.Generic;
using System.Reflection;
using nadena.dev.modular_avatar.core;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.Dynamics;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.Constraint.Components;

[assembly: ExportsPlugin(typeof(UniText.Editor.UniTextNdmfPlugin))]

namespace UniText.Editor
{
    /// <summary>
    /// Non-destructive NDMF build plugin that expands UniTextProp prefabs into
    /// Modular Avatar components (BoneProxy, MergeAnimator, Parameters, MenuInstaller / MenuItem)
    /// and VRCParentConstraint immediately before Modular Avatar processes the avatar.
    /// </summary>
    public class UniTextNdmfPlugin : Plugin<UniTextNdmfPlugin>
    {
        public override string QualifiedName => "dev.dreaminko.unitext";
        public override string DisplayName => "UniText (Modular Avatar Non-Destructive)";

        protected override void Configure()
        {
            InPhase(BuildPhase.Generating)
                .BeforePlugin("nadena.dev.modular-avatar")
                .Run("Expand UniText Props for Modular Avatar", ExpandAll);
        }

        private static void ExpandAll(BuildContext ctx)
        {
            if (ctx.AvatarRootObject == null)
            {
                return;
            }

            var props = ctx.AvatarRootObject.GetComponentsInChildren<UniTextProp>(true);
            if (props == null || props.Length == 0)
            {
                return;
            }

            bool isAndroidBuild = EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android;
            var activeProps = new List<UniTextProp>(props.Length);

            foreach (var prop in props)
            {
                if (prop == null)
                {
                    continue;
                }

                if (isAndroidBuild && prop.stripOnAndroid)
                {
                    Debug.Log($"[UniText] Stripping '{prop.gameObject.name}' for Quest/Android target (CRT unsupported).");
                    UnityEngine.Object.DestroyImmediate(prop.gameObject);
                    continue;
                }

                activeProps.Add(prop);
            }

            bool isSingleProp = activeProps.Count == 1;
            foreach (var prop in activeProps)
            {
                ExpandSingleProp(ctx, prop, isSingleProp);
            }
        }

        private static readonly string[] KnownTierNames = { "Lite", "Compact", "Balanced", "Standard", "Turbo" };

        private static T ResolveTierAsset<T>(
            T assigned,
            string expectedPath,
            string tierName,
            string assetLabel,
            GameObject root) where T : UnityEngine.Object
        {
            if (assigned == null)
            {
                return AssetDatabase.LoadAssetAtPath<T>(expectedPath);
            }

            foreach (string known in KnownTierNames)
            {
                if (!string.Equals(known, tierName, StringComparison.OrdinalIgnoreCase) &&
                    assigned.name.EndsWith("_" + known, StringComparison.OrdinalIgnoreCase))
                {
                    T resolved = AssetDatabase.LoadAssetAtPath<T>(expectedPath);
                    if (resolved != null)
                    {
                        Debug.LogWarning(
                            $"[UniText] '{root.name}' is set to tier '{tierName}', but {assetLabel} '{assigned.name}' belongs to '{known}'. Rebinding to '{resolved.name}' for this build.",
                            root
                        );
                        return resolved;
                    }
                    break;
                }
            }

            return assigned;
        }

        private static void ExpandSingleProp(BuildContext ctx, UniTextProp prop, bool isSingleProp)
        {
            GameObject root = prop.gameObject;
            string tierName = prop.TierName;
            int payloadBytes = prop.PayloadBytes;

            // Ensure child hierarchy & asset bindings
            Transform anchor = root.transform.Find("SideFollowAnchor");
            Transform boardPivot = root.transform.Find("BoardPivot");
            Transform displayQuad = boardPivot != null ? boardPivot.Find("DisplayQuad") : null;
            Transform paramBridge = root.transform.Find("ParamBridge");
            Transform paramCamTf = paramBridge != null ? paramBridge.Find("ParamCamera") : null;
            Transform encoderQuadTf = paramBridge != null ? paramBridge.Find("EncoderQuad") : null;

            if (anchor == null || boardPivot == null || displayQuad == null || paramCamTf == null || encoderQuadTf == null)
            {
                Debug.LogError($"[UniText] '{root.name}' is missing required child hierarchy (SideFollowAnchor / BoardPivot / DisplayQuad / ParamBridge). Skipping expansion.", root);
                return;
            }

            // Verify/wire RenderTexture & Materials (protect against changing UniTextProp.tier in Inspector without updating asset refs)
            RenderTexture paramRt = ResolveTierAsset(
                prop.paramBridgeTexture,
                $"{UniTextAssetBuilder.PackageRoot}/Textures/ParamBridge_8x1_{tierName}.renderTexture",
                tierName,
                "paramBridgeTexture",
                root
            );
            Material encMat = ResolveTierAsset(
                prop.encoderMaterial,
                $"{UniTextAssetBuilder.PackageRoot}/Materials/UniText_ParamEncoder_{tierName}.mat",
                tierName,
                "encoderMaterial",
                root
            );
            Material dispMat = ResolveTierAsset(
                prop.displayMaterial,
                $"{UniTextAssetBuilder.PackageRoot}/Materials/UniText_Display_{tierName}.mat",
                tierName,
                "displayMaterial",
                root
            );

            Camera paramCam = paramCamTf.GetComponent<Camera>();
            if (paramCam != null)
            {
                if (paramRt != null)
                {
                    paramCam.targetTexture = paramRt;
                }
                paramCam.orthographic = true;
                paramCam.orthographicSize = 0.1f;
                paramCam.nearClipPlane = 0.001f;
                paramCam.farClipPlane = 0.5f;
                paramCam.stereoTargetEye = StereoTargetEyeMask.None;
                paramCam.clearFlags = CameraClearFlags.SolidColor;
                paramCam.backgroundColor = Color.clear;
                paramCam.cullingMask = UniTextAssetBuilder.ParamCameraCullingMask;
                paramCam.renderingPath = RenderingPath.Forward;
                paramCam.depthTextureMode = DepthTextureMode.None;
                paramCam.allowHDR = false;
                paramCam.allowMSAA = false;
                paramCam.allowDynamicResolution = false;
                paramCam.useOcclusionCulling = false;
                paramCam.enabled = false;
            }

            encoderQuadTf.gameObject.layer = UniTextAssetBuilder.ParamBridgeLayer;

            if (Mathf.Abs(encoderQuadTf.localPosition.z - 0.02f) < 0.001f)
            {
                encoderQuadTf.localPosition = new Vector3(0f, 0f, 0.05f);
            }

            MeshRenderer encRenderer = encoderQuadTf.GetComponent<MeshRenderer>();
            if (encRenderer != null)
            {
                encRenderer.allowOcclusionWhenDynamic = false;
                if (encMat != null)
                {
                    encRenderer.sharedMaterial = encMat;
                }
            }

            CustomRenderTexture bufferCrt = ResolveTierAsset(
                prop.bufferCRT,
                $"{UniTextAssetBuilder.PackageRoot}/Textures/Buffer_64x32_{tierName}.asset",
                tierName,
                "bufferCRT",
                root
            );
            if (bufferCrt != null && (
                bufferCrt.initializationMode != CustomRenderTextureUpdateMode.OnLoad ||
                bufferCrt.initializationSource != CustomRenderTextureInitializationSource.TextureAndColor ||
                bufferCrt.initializationColor.a > 0.01f ||
                bufferCrt.updateMode != CustomRenderTextureUpdateMode.Realtime ||
                !bufferCrt.doubleBuffered))
            {
                Debug.LogWarning(
                    $"[UniText] '{bufferCrt.name}' has invalid CRT initialization/update settings (may render yellow tofu blocks or freeze). Run 'Tools/UniText/Verify & Refresh Package Assets' and reimport.",
                    bufferCrt
                );
            }

            int displayRows = prop.EffectiveDisplayRows;
            int rowPx = prop.EffectiveRowPx;
            UniTextPlacementUtility.ApplyTransformPreview(ctx.AvatarRootObject, prop, recordUndo: false);
            MeshFilter baseDispMeshFilter = displayQuad.GetComponent<MeshFilter>();
            Mesh baseQuadMesh = baseDispMeshFilter != null ? baseDispMeshFilter.sharedMesh : null;

            MeshRenderer dispRenderer = displayQuad.GetComponent<MeshRenderer>();
            if (dispRenderer != null && dispMat != null)
            {
                float liveGeometry = prop.allowRuntimeResize ? 1f : 0f;
                float currentAutoFlip = dispMat.HasProperty("_AutoFlip") ? dispMat.GetFloat("_AutoFlip") : 2f;
                float desiredAutoFlip = prop.autoFlipText ? (currentAutoFlip > 0.5f ? currentAutoFlip : 2f) : 0f;
                bool autoFlipMismatch = !dispMat.HasProperty("_AutoFlip") || !Mathf.Approximately(currentAutoFlip, desiredAutoFlip);
                Vector2 anchorNorm = UniTextProp.GetAnchorNormalized(prop.boardAnchor);
                bool anchorMismatch = !dispMat.HasProperty("_AnchorOffset") ||
                    ((Vector2)dispMat.GetVector("_AnchorOffset") - anchorNorm).sqrMagnitude > 1e-6f;
                bool needsClone =
                    displayRows != UniTextProp.DefaultDisplayRows ||
                    rowPx != UniTextProp.DefaultRowPx ||
                    !dispMat.HasProperty("_LiveGeometry") ||
                    !Mathf.Approximately(dispMat.GetFloat("_LiveGeometry"), liveGeometry) ||
                    autoFlipMismatch ||
                    anchorMismatch;
                if (needsClone)
                {
                    // Base geometry is baked here; live resize (CMD_SET_GEOMETRY from the CRT) scales the quad in the vertex shader.
                    dispMat = new Material(dispMat) { name = $"{dispMat.name}_{root.name}" };
                    dispMat.SetFloat("_DisplayRows", displayRows);
                    dispMat.SetFloat("_RowPx", rowPx);
                    dispMat.SetFloat("_LiveGeometry", liveGeometry);
                    dispMat.SetFloat("_AutoFlip", desiredAutoFlip);
                    dispMat.SetVector("_AnchorOffset", new Vector4(anchorNorm.x, anchorNorm.y, 0f, 0f));
                    ctx.AssetSaver.SaveAsset(dispMat);
                }
                dispRenderer.sharedMaterial = dispMat;
            }

            ApplyExpandedBillboardBounds(ctx, prop, displayQuad, displayRows, rowPx);

            // 1. Optional Modular Avatar Bone Proxy for SideFollowAnchor
            if (prop.useBoneProxy)
            {
                Transform targetBone = ResolveHumanoidBone(ctx.AvatarRootObject, prop.followBone);
                if (targetBone != null)
                {
                    var boneProxy = anchor.GetComponent<ModularAvatarBoneProxy>() ?? anchor.gameObject.AddComponent<ModularAvatarBoneProxy>();
                    boneProxy.target = targetBone;
                    boneProxy.attachmentMode = BoneProxyAttachmentMode.AsChildKeepWorldPose;
                }
                else
                {
                    Debug.LogWarning($"[UniText] Humanoid bone not found on '{ctx.AvatarRootObject.name}'; SideFollowAnchor will stay attached to '{root.name}'.", root);
                }
            }

            // Ensure MA Menu hierarchy exists when installMenu is enabled without pre-generated MA menu items
            GameObject menuSearchScope = isSingleProp ? ctx.AvatarRootObject : root;
            bool hasMaMenu = UniTextMenuUtility.HasUniTextMenuInHierarchy(ctx.AvatarRootObject, includeInactive: true);
            bool hasRootInstaller = root.GetComponent<ModularAvatarMenuInstaller>() != null;
            if (!hasMaMenu && !hasRootInstaller && prop.installMenu)
            {
                string title = UniTextMenuUtility.SanitizeMenuName(prop.menuName);
                UniTextMenuUtility.CreateMenuHierarchy(
                    root,
                    title,
                    prop.installTargetMenu,
                    prop.syncWorldDrop,
                    prop.syncBillboard,
                    prop.allowRuntimeResize,
                    recordUndo: false
                );
            }
            else
            {
                if (!prop.allowRuntimeResize)
                {
                    UniTextMenuUtility.DeactivateResizeMenuItems(menuSearchScope);
                }
                if (prop.installMenu)
                {
                    GameObject existingMenuRoot = UniTextMenuUtility.FindMenuRoot(root, isSingleProp ? ctx.AvatarRootObject : null);
                    if (existingMenuRoot != null)
                    {
                        UniTextMenuUtility.EnsureSelfViewSubMenu(existingMenuRoot, menuSearchScope, recordUndo: false);
                    }
                }
            }

            UniTextMenuUtility.AlignBuildMenuItemSyncFlags(menuSearchScope, prop.syncWorldDrop, prop.syncBillboard);

            Dictionary<string, UniTextMenuUtility.MenuParameterOverride> menuOverrides =
                UniTextMenuUtility.CollectActiveMenuParameterOverrides(ctx.AvatarRootObject);
            bool visibleDefault = ResolveDefault(menuOverrides, UniTextMenuUtility.ParamVisible, false);
            bool worldDropDefault = ResolveDefault(menuOverrides, UniTextMenuUtility.ParamWorldDrop, false);
            bool billboardDefault = ResolveDefault(menuOverrides, UniTextMenuUtility.ParamBillboard, false);
            bool selfViewHeadDefault = ResolveDefault(menuOverrides, UniTextMenuUtility.ParamSelfViewHead, false);

            // 2. VRCParentConstraint on BoardPivot (Side Follow / World Drop)
            var constraint = boardPivot.GetComponent<VRCParentConstraint>() ?? boardPivot.gameObject.AddComponent<VRCParentConstraint>();
            constraint.SetSources(new[] { new VRCConstraintSource(anchor, 1f) });
            constraint.GlobalWeight = 1f;
            constraint.IsActive = true;
            constraint.Locked = true;
            constraint.FreezeToWorld = worldDropDefault;
            constraint.AffectsPositionX = true;
            constraint.AffectsPositionY = true;
            constraint.AffectsPositionZ = true;
            constraint.AffectsRotationX = false;
            constraint.AffectsRotationY = true;
            constraint.AffectsRotationZ = false;
            constraint.ApplyConfigurationChanges();

            // 2b. Non-destructive local-only Self View rig (Approach A: Dual Quad + Mirror/Camera view filter)
            BuildSelfViewRig(
                ctx,
                prop,
                root,
                displayQuad,
                baseQuadMesh,
                dispMat,
                displayRows,
                rowPx,
                selfViewHeadDefault,
                out UniTextPlacementUtility.SelfViewMetrics selfViewMetrics
            );

            // Match UniText_Visible default in built avatar while leaving Edit Mode preview visible
            boardPivot.gameObject.SetActive(visibleDefault);
            if (paramCam != null)
            {
                paramCam.enabled = visibleDefault;
            }

            // 3. Build Direct BlendTree FX AnimatorController in memory & attach ModularAvatarMergeAnimator
            AnimatorController fxController = UniTextAssetBuilder.BuildFxControllerInMemory(
                tierName,
                payloadBytes,
                asset => ctx.AssetSaver.SaveAsset(asset),
                prop.HasD4Nibble,
                visibleDefault,
                worldDropDefault,
                billboardDefault,
                selfViewHeadDefault,
                selfViewMetrics.DistMin,
                selfViewMetrics.DistMax,
                selfViewMetrics.HeightMin,
                selfViewMetrics.HeightMax
            );

            var mergeAnimator = root.GetComponent<ModularAvatarMergeAnimator>() ?? root.AddComponent<ModularAvatarMergeAnimator>();
            mergeAnimator.animator = fxController;
            mergeAnimator.layerType = VRCAvatarDescriptor.AnimLayerType.FX;
            mergeAnimator.pathMode = MergeAnimatorPathMode.Relative;
            mergeAnimator.matchAvatarWriteDefaults = true;
            mergeAnimator.deleteAttachedAnimator = true;

            // 4. Attach ModularAvatarParameters
            var maParams = root.GetComponent<ModularAvatarParameters>() ?? root.AddComponent<ModularAvatarParameters>();
            maParams.parameters = BuildParameterConfigs(
                payloadBytes,
                prop.HasD4Nibble,
                prop.syncWorldDrop,
                prop.syncBillboard,
                prop.syncWideRows,
                displayRows,
                rowPx,
                prop.allowRuntimeResize,
                menuOverrides
            );

            // 5. Remove UniTextProp marker so no custom script remains on final avatar
            UnityEngine.Object.DestroyImmediate(prop);
        }

        private static void BuildSelfViewRig(
            BuildContext ctx,
            UniTextProp prop,
            GameObject root,
            Transform displayQuad,
            Mesh baseQuadMesh,
            Material dispMat,
            int displayRows,
            int rowPx,
            bool defaultFollowHead,
            out UniTextPlacementUtility.SelfViewMetrics metrics)
        {
            metrics = UniTextPlacementUtility.ComputeSelfViewMetrics(ctx.AvatarRootObject);

            var bodyAnchor = new GameObject("SelfViewBodyAnchor");
            bodyAnchor.transform.SetParent(root.transform, false);
            bodyAnchor.transform.SetPositionAndRotation(metrics.AnchorWorldPos, metrics.AnchorWorldRot);
            if (metrics.BodyBone != null)
            {
                var bodyProxy = bodyAnchor.AddComponent<ModularAvatarBoneProxy>();
                bodyProxy.target = metrics.BodyBone;
                bodyProxy.attachmentMode = BoneProxyAttachmentMode.AsChildKeepWorldPose;
            }

            var headAnchor = new GameObject("SelfViewHeadAnchor");
            headAnchor.transform.SetParent(root.transform, false);
            headAnchor.transform.SetPositionAndRotation(metrics.AnchorWorldPos, metrics.AnchorWorldRot);
            Transform headTarget = metrics.HeadBone != null ? metrics.HeadBone : metrics.BodyBone;
            if (headTarget != null)
            {
                var headProxy = headAnchor.AddComponent<ModularAvatarBoneProxy>();
                headProxy.target = headTarget;
                headProxy.attachmentMode = BoneProxyAttachmentMode.AsChildKeepWorldPose;
            }

            var rig = new GameObject("SelfViewRig");
            rig.transform.SetParent(root.transform, false);
            rig.SetActive(false);

            var bodyPivot = new GameObject("BodyPivot");
            bodyPivot.transform.SetParent(rig.transform, false);
            bodyPivot.transform.SetPositionAndRotation(metrics.AnchorWorldPos, metrics.AnchorWorldRot);
            var bodyConstraint = bodyPivot.AddComponent<VRCParentConstraint>();
            bodyConstraint.SetSources(new[] { new VRCConstraintSource(bodyAnchor.transform, 1f) });
            bodyConstraint.GlobalWeight = 1f;
            bodyConstraint.IsActive = true;
            bodyConstraint.Locked = true;
            bodyConstraint.FreezeToWorld = false;
            bodyConstraint.AffectsPositionX = true;
            bodyConstraint.AffectsPositionY = true;
            bodyConstraint.AffectsPositionZ = true;
            bodyConstraint.AffectsRotationX = false;
            bodyConstraint.AffectsRotationY = true;
            bodyConstraint.AffectsRotationZ = false;
            bodyConstraint.ApplyConfigurationChanges();

            var headPivot = new GameObject("HeadPivot");
            headPivot.transform.SetParent(rig.transform, false);
            headPivot.transform.SetPositionAndRotation(metrics.AnchorWorldPos, metrics.AnchorWorldRot);
            var headConstraint = headPivot.AddComponent<VRCParentConstraint>();
            headConstraint.SetSources(new[] { new VRCConstraintSource(headAnchor.transform, 1f) });
            headConstraint.GlobalWeight = 1f;
            headConstraint.IsActive = true;
            headConstraint.Locked = true;
            headConstraint.FreezeToWorld = false;
            headConstraint.AffectsPositionX = true;
            headConstraint.AffectsPositionY = true;
            headConstraint.AffectsPositionZ = true;
            headConstraint.AffectsRotationX = true;
            headConstraint.AffectsRotationY = true;
            headConstraint.AffectsRotationZ = false;
            headConstraint.ApplyConfigurationChanges();

            var boardHolder = new GameObject("BoardHolder");
            boardHolder.transform.SetParent(rig.transform, false);
            boardHolder.transform.SetPositionAndRotation(metrics.AnchorWorldPos, metrics.AnchorWorldRot);
            var holderConstraint = boardHolder.AddComponent<VRCParentConstraint>();
            holderConstraint.SetSources(new[]
            {
                new VRCConstraintSource(bodyPivot.transform, defaultFollowHead ? 0f : 1f),
                new VRCConstraintSource(headPivot.transform, defaultFollowHead ? 1f : 0f)
            });
            holderConstraint.GlobalWeight = 1f;
            holderConstraint.IsActive = true;
            holderConstraint.Locked = true;
            holderConstraint.FreezeToWorld = false;
            holderConstraint.AffectsPositionX = true;
            holderConstraint.AffectsPositionY = true;
            holderConstraint.AffectsPositionZ = true;
            holderConstraint.AffectsRotationX = true;
            holderConstraint.AffectsRotationY = true;
            holderConstraint.AffectsRotationZ = true;
            holderConstraint.ApplyConfigurationChanges();

            var offsetHeight = new GameObject("OffsetHeight");
            offsetHeight.transform.SetParent(boardHolder.transform, false);
            offsetHeight.transform.localPosition = new Vector3(0f, 0.5f * (metrics.HeightMin + metrics.HeightMax), 0f);

            var offsetDist = new GameObject("OffsetDist");
            offsetDist.transform.SetParent(offsetHeight.transform, false);
            offsetDist.transform.localPosition = new Vector3(0f, 0f, 0.5f * (metrics.DistMin + metrics.DistMax));
            offsetDist.transform.localScale = new Vector3(0.65f, 0.65f, 0.65f);

            var selfViewQuadGo = new GameObject("DisplayQuad");
            selfViewQuadGo.transform.SetParent(offsetDist.transform, false);
            selfViewQuadGo.transform.localPosition = Vector3.zero;
            selfViewQuadGo.transform.localRotation = Quaternion.identity;
            selfViewQuadGo.transform.localScale = displayQuad.localScale;

            var meshFilter = selfViewQuadGo.AddComponent<MeshFilter>();
            meshFilter.sharedMesh = baseQuadMesh;

            var meshRenderer = selfViewQuadGo.AddComponent<MeshRenderer>();
            meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;
            meshRenderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            meshRenderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;

            if (dispMat != null)
            {
                var selfViewMat = new Material(dispMat) { name = $"{dispMat.name}_{root.name}_SelfView" };
                selfViewMat.SetFloat("_DisplayRows", displayRows);
                selfViewMat.SetFloat("_RowPx", rowPx);
                selfViewMat.SetFloat("_LiveGeometry", prop.allowRuntimeResize ? 1f : 0f);
                selfViewMat.SetVector("_AnchorOffset", Vector4.zero);
                selfViewMat.SetFloat("_ViewFilter", 2f);
                ctx.AssetSaver.SaveAsset(selfViewMat);
                meshRenderer.sharedMaterial = selfViewMat;
            }

            ApplyExpandedBillboardBounds(ctx, prop, selfViewQuadGo.transform, displayRows, rowPx, UniTextBoardAnchor.Center);
        }

        private static void ApplyExpandedBillboardBounds(
            BuildContext ctx,
            UniTextProp prop,
            Transform displayQuad,
            int displayRows,
            int rowPx,
            UniTextBoardAnchor? anchorOverride = null)
        {
            MeshFilter meshFilter = displayQuad.GetComponent<MeshFilter>();
            Mesh srcMesh = meshFilter != null ? meshFilter.sharedMesh : null;
            if (srcMesh == null || srcMesh.vertexCount != 4 || srcMesh.bounds.size.z >= 1e-3f)
            {
                return;
            }

            float sx = Mathf.Max(Mathf.Abs(displayQuad.localScale.x), 1e-4f);
            float sy = Mathf.Max(Mathf.Abs(displayQuad.localScale.y), 1e-4f);
            float sz = Mathf.Max(Mathf.Abs(displayQuad.localScale.z), 1e-4f);

            float minScaleX = 1f;
            float maxScaleX = 1f;
            float minScaleY = 1f;
            float maxScaleY = 1f;
            if (prop.allowRuntimeResize)
            {
                UniTextProp.ComputeRuntimeBounds(
                    sx, sy, displayRows, rowPx, prop.syncWideRows,
                    out float wMin, out float wMax, out _, out _, out float hMin, out float hMax
                );
                minScaleX = wMin / sx;
                maxScaleX = wMax / sx;
                minScaleY = hMin / sy;
                maxScaleY = hMax / sy;
            }

            float maxW = sx * maxScaleX;
            float maxH = sy * maxScaleY;
            float rWorld = 0.5f * Mathf.Sqrt(maxW * maxW + maxH * maxH);
            float rx = rWorld / sx;
            float ry = rWorld / sy;
            float rz = rWorld / sz;

            Vector2 anchor = UniTextProp.GetAnchorNormalized(anchorOverride ?? prop.boardAnchor);
            float pivotMinX = Mathf.Min(0f, anchor.x * (1f - minScaleX), anchor.x * (1f - maxScaleX));
            float pivotMaxX = Mathf.Max(0f, anchor.x * (1f - minScaleX), anchor.x * (1f - maxScaleX));
            float pivotMinY = Mathf.Min(0f, anchor.y * (1f - minScaleY), anchor.y * (1f - maxScaleY));
            float pivotMaxY = Mathf.Max(0f, anchor.y * (1f - minScaleY), anchor.y * (1f - maxScaleY));

            var expandedBounds = new Bounds();
            expandedBounds.SetMinMax(
                new Vector3(pivotMinX - rx, pivotMinY - ry, -rz),
                new Vector3(pivotMaxX + rx, pivotMaxY + ry, rz)
            );

            Mesh expandedMesh = UnityEngine.Object.Instantiate(srcMesh);
            expandedMesh.name = $"{srcMesh.name}_UniTextBounds";
            expandedMesh.bounds = expandedBounds;
            ctx.AssetSaver.SaveAsset(expandedMesh);
            meshFilter.sharedMesh = expandedMesh;
        }

        internal static Transform ResolveHumanoidBone(GameObject avatarRoot, HumanBodyBones preferred)
        {
            var animator = avatarRoot.GetComponent<Animator>();
            if (animator == null || !animator.isHuman)
            {
                return null;
            }

            HumanBodyBones[] fallbacks =
            {
                preferred,
                HumanBodyBones.Chest,
                HumanBodyBones.Spine,
                HumanBodyBones.Hips
            };

            foreach (HumanBodyBones candidate in fallbacks)
            {
                if (candidate == HumanBodyBones.LastBone)
                {
                    continue;
                }
                Transform tf = animator.GetBoneTransform(candidate);
                if (tf != null)
                {
                    return tf;
                }
            }

            return null;
        }

        private static List<ParameterConfig> BuildParameterConfigs(
            int payloadBytes,
            bool hasD4Nibble,
            bool syncWorldDrop,
            bool syncBillboard,
            bool syncWideRows,
            int displayRows,
            int rowPx,
            bool allowRuntimeResize,
            IReadOnlyDictionary<string, UniTextMenuUtility.MenuParameterOverride> menuOverrides)
        {
            bool supportsLocalOnly = HasBoolMember(typeof(ParameterConfig), "localOnly");
            ParameterSyncType localBoolType = supportsLocalOnly ? ParameterSyncType.Bool : ParameterSyncType.NotSynced;
            ParameterSyncType localIntType = supportsLocalOnly ? ParameterSyncType.Int : ParameterSyncType.NotSynced;
            ParameterSyncType localFloatType = supportsLocalOnly ? ParameterSyncType.Float : ParameterSyncType.NotSynced;

            bool visibleDefault = ResolveDefault(menuOverrides, UniTextMenuUtility.ParamVisible, false);
            bool visibleSaved = ResolveSaved(menuOverrides, UniTextMenuUtility.ParamVisible, true);
            bool worldDropDefault = ResolveDefault(menuOverrides, UniTextMenuUtility.ParamWorldDrop, false);
            bool worldDropSaved = ResolveSaved(menuOverrides, UniTextMenuUtility.ParamWorldDrop, false);
            bool billboardDefault = ResolveDefault(menuOverrides, UniTextMenuUtility.ParamBillboard, false);
            bool billboardSaved = ResolveSaved(menuOverrides, UniTextMenuUtility.ParamBillboard, true);
            bool scaleSaved = ResolveSaved(menuOverrides, UniTextMenuUtility.ParamScale, true);
            bool opacitySaved = ResolveSaved(menuOverrides, UniTextMenuUtility.ParamOpacity, true);
            bool widthSaved = ResolveSaved(menuOverrides, UniTextMenuUtility.ParamWidth, true);
            bool heightSaved = ResolveSaved(menuOverrides, UniTextMenuUtility.ParamHeight, true);
            bool selfViewDefault = ResolveDefault(menuOverrides, UniTextMenuUtility.ParamSelfView, false);
            bool selfViewSaved = ResolveSaved(menuOverrides, UniTextMenuUtility.ParamSelfView, true);
            bool selfViewHeadDefault = ResolveDefault(menuOverrides, UniTextMenuUtility.ParamSelfViewHead, false);
            bool selfViewHeadSaved = ResolveSaved(menuOverrides, UniTextMenuUtility.ParamSelfViewHead, true);
            bool selfViewDistSaved = ResolveSaved(menuOverrides, UniTextMenuUtility.ParamSelfViewDist, true);
            bool selfViewHeightSaved = ResolveSaved(menuOverrides, UniTextMenuUtility.ParamSelfViewHeight, true);
            bool selfViewScaleSaved = ResolveSaved(menuOverrides, UniTextMenuUtility.ParamSelfViewScale, true);

            var list = new List<ParameterConfig>
            {
                MakeParam("UniText_Visible", ParameterSyncType.Bool, visibleDefault ? 1f : 0f, saved: visibleSaved, internalParam: false, localOnly: false),
                MakeParam("UniText_WorldDrop", syncWorldDrop ? ParameterSyncType.Bool : localBoolType, worldDropDefault ? 1f : 0f, saved: worldDropSaved, internalParam: false, localOnly: !syncWorldDrop),
                MakeParam("UniText_Billboard", syncBillboard ? ParameterSyncType.Bool : localBoolType, billboardDefault ? 1f : 0f, saved: billboardSaved, internalParam: false, localOnly: !syncBillboard),
                MakeParam("UniText_Ctrl", ParameterSyncType.Int, 0f, saved: false, internalParam: false, localOnly: false),
            };

            for (int i = 0; i < payloadBytes; i++)
            {
                list.Add(MakeParam($"UniText_D{i}", ParameterSyncType.Int, 0f, saved: false, internalParam: false, localOnly: false));
            }

            // Compact36: 4 synced Bools carrying bits 4..7 of D4 (+4 bits) so each packet holds 3 LUT12 indices (36 bits).
            if (hasD4Nibble)
            {
                for (int bit = 4; bit < 8; bit++)
                {
                    list.Add(MakeParam($"UniText_D4b{bit}", ParameterSyncType.Bool, 0f, saved: false, internalParam: false, localOnly: false));
                }
            }

            // Wide Rows: synced column-bank bit (+1 bit). Its presence in OSCQuery tells senders each row holds 64 cells.
            // When disabled it is intentionally NOT registered, so senders fall back to 32 cells per row (animator default 0 = bank 0).
            if (syncWideRows)
            {
                list.Add(MakeParam("UniText_Bank", ParameterSyncType.Bool, 0f, saved: false, internalParam: false, localOnly: false));
            }

            // Extended Rows (> 6 or live resize enabled): synced row-page bit (+1 bit) unlocking rows 6..11 on the 64x32 CRT.
            if (displayRows > 6 || allowRuntimeResize)
            {
                list.Add(MakeParam("UniText_RowPage", ParameterSyncType.Bool, 0f, saved: false, internalParam: false, localOnly: false));
            }

            // Local-only UI controls & capability/geometry markers (0 synced bits)
            list.Add(MakeParam("UniText_ProtoVer", localIntType, UniTextProp.ProtocolVersion, saved: false, internalParam: false, localOnly: true));
            list.Add(MakeParam("UniText_Rows", localIntType, displayRows, saved: false, internalParam: false, localOnly: true));
            list.Add(MakeParam("UniText_RowPx8", localIntType, rowPx / 8f, saved: false, internalParam: false, localOnly: true));
            list.Add(MakeParam("UniText_Scale", localFloatType, 0.5f, saved: scaleSaved, internalParam: false, localOnly: true));
            list.Add(MakeParam("UniText_Opacity", localFloatType, 1f, saved: opacitySaved, internalParam: false, localOnly: true));
            list.Add(MakeParam("UniText_LocalClear", localBoolType, 0f, saved: false, internalParam: false, localOnly: true));
            if (allowRuntimeResize)
            {
                // Local-only Float (0..1, neutral 0.5 for RadialPuppet): VRCS reads it over OSC, reflows, and broadcasts
                // the result to every viewer via CMD_SET_GEOMETRY (Ctrl 251), so both puppets cost 0 synced bits.
                list.Add(MakeParam("UniText_Width", localFloatType, 0.5f, saved: widthSaved, internalParam: false, localOnly: true));
                list.Add(MakeParam("UniText_Height", localFloatType, 0.5f, saved: heightSaved, internalParam: false, localOnly: true));
            }
            list.Add(MakeParam(UniTextMenuUtility.ParamSelfView, localBoolType, selfViewDefault ? 1f : 0f, saved: selfViewSaved, internalParam: false, localOnly: true));
            list.Add(MakeParam(UniTextMenuUtility.ParamSelfViewHead, localBoolType, selfViewHeadDefault ? 1f : 0f, saved: selfViewHeadSaved, internalParam: false, localOnly: true));
            list.Add(MakeParam(UniTextMenuUtility.ParamSelfViewDist, localFloatType, 0.5f, saved: selfViewDistSaved, internalParam: false, localOnly: true));
            list.Add(MakeParam(UniTextMenuUtility.ParamSelfViewHeight, localFloatType, 0.5f, saved: selfViewHeightSaved, internalParam: false, localOnly: true));
            list.Add(MakeParam(UniTextMenuUtility.ParamSelfViewScale, localFloatType, 0.5f, saved: selfViewScaleSaved, internalParam: false, localOnly: true));
            list.Add(MakeParam("UniText_One", ParameterSyncType.NotSynced, 1f, saved: false, internalParam: true, localOnly: false));

            return list;
        }

        private static bool ResolveDefault(
            IReadOnlyDictionary<string, UniTextMenuUtility.MenuParameterOverride> overrides,
            string paramName,
            bool fallback)
        {
            if (overrides != null && overrides.TryGetValue(paramName, out var ov) && ov.HasMenuItem)
            {
                return ov.IsDefault;
            }
            return fallback;
        }

        private static bool ResolveSaved(
            IReadOnlyDictionary<string, UniTextMenuUtility.MenuParameterOverride> overrides,
            string paramName,
            bool fallback)
        {
            if (overrides != null && overrides.TryGetValue(paramName, out var ov) && ov.HasMenuItem)
            {
                return ov.IsSaved;
            }
            return fallback;
        }

        private static ParameterConfig MakeParam(
            string name,
            ParameterSyncType syncType,
            float defaultValue,
            bool saved,
            bool internalParam,
            bool localOnly)
        {
            var cfg = new ParameterConfig
            {
                nameOrPrefix = name,
                syncType = syncType,
                defaultValue = defaultValue,
                saved = saved,
                internalParameter = internalParam,
            };

            object boxed = cfg;
            SetMemberIfExists(boxed, "hasExplicitDefaultValue", true);
            SetMemberIfExists(boxed, "localOnly", localOnly);
            return (ParameterConfig)boxed;
        }

        private static bool HasBoolMember(Type type, string memberName)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;

            FieldInfo field = type.GetField(memberName, flags);
            if (field != null && field.FieldType == typeof(bool))
            {
                return true;
            }

            PropertyInfo prop = type.GetProperty(memberName, flags);
            return prop != null && prop.CanWrite && prop.PropertyType == typeof(bool);
        }

        private static void SetMemberIfExists(object target, string memberName, bool value)
        {
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
    }
}
#endif
