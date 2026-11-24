#if UNITEXT_VRCSDK3 && UNITEXT_MA && UNITEXT_NDMF
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace UniText.Editor
{
    public static class UniTextAssetBuilder
    {
        public const string PackageRoot = "Packages/dev.dreaminko.unitext";
        internal const int ParamBridgeLayer = 9;
        internal const int ParamCameraCullingMask = (1 << 9) | (1 << 10);
        private const string TexturesDir = PackageRoot + "/Textures";
        private const string MaterialsDir = PackageRoot + "/Materials";
        private const string PrefabsDir = PackageRoot + "/Prefabs";
        private const string ShadersDir = PackageRoot + "/Shaders";

        private static readonly (UniTextTier Tier, string Name, int PayloadBytes, int Bits, string PrefabName)[] Tiers =
        {
            (UniTextTier.Lite,     "Lite",     3,  33,  "Board_Lite_33b.prefab"),
            (UniTextTier.Compact,  "Compact",  4,  45,  "Board_Compact_45b.prefab"),
            (UniTextTier.Balanced, "Balanced", 6,  57,  "Board_Balanced_57b.prefab"),
            (UniTextTier.Standard, "Standard", 8,  73,  "Board_Standard_73b.prefab"),
            (UniTextTier.Turbo,    "Turbo",    16, 137, "Board_Turbo_137b.prefab"),
        };

        public const string FontAtlasPath = TexturesDir + "/FontAtlas_BMP_Packed.png";
        public const string AuxTexturePath = TexturesDir + "/UniTextAux_64x64.png";

        [MenuItem("Tools/UniText/Verify & Refresh Package Assets", priority = 100)]
        public static void BuildAllMenu()
        {
            BuildAll();
            EditorUtility.DisplayDialog(
                "UniText",
                "Verified packed Font Atlas & Aux lookup texture settings, CRT/RenderTextures, Materials, and refreshed the 5 non-destructive Modular Avatar Prefabs.",
                "OK"
            );
        }

        public static void BuildAllBatch()
        {
            BuildAll();
        }

        public static void BuildAll()
        {
            EnsureFolder(TexturesDir);
            EnsureFolder(MaterialsDir);
            EnsureFolder(PrefabsDir);

            ConfigureFontAtlasImporter();
            ConfigureAuxImporter();

            foreach (var tier in Tiers)
            {
                var paramBridge = EnsureParamBridgeRenderTexture(tier.Name);
                var encoderMat = EnsureParamEncoderMaterial(tier.Name);
                var bufferMat = EnsureBufferCrtMaterial(tier.Name, paramBridge);
                var bufferCrt = EnsureBufferCrtAsset(tier.Name, bufferMat);
                var displayMat = EnsureDisplayMaterial(tier.Name, bufferCrt);

                BuildTierPrefab(
                    tier.Tier,
                    tier.PrefabName,
                    paramBridge,
                    encoderMat,
                    displayMat,
                    bufferCrt
                );
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[UniText] Verify & refresh package assets completed cleanly.");
        }

        /// <summary>
        /// Builds a complete Direct BlendTree FX AnimatorController in memory for NDMF non-destructive merging.
        /// Live resize no longer needs Animator curves: the sender broadcasts CMD_SET_GEOMETRY and Display.shader
        /// scales the panel from the CRT, so UniText_Width/Height only drive the local OSC sender.
        /// </summary>
        public static AnimatorController BuildFxControllerInMemory(
            string tierName,
            int payloadBytes,
            Action<UnityEngine.Object> saveAsset,
            bool hasD4Nibble = false)
        {
            var controller = new AnimatorController { name = $"UniText_FX_{tierName}" };
            saveAsset?.Invoke(controller);

            // Ctrl & D0..D{N-1} MUST be Float in AnimatorController; VRChat maps Int 0..255 -> 0.0..255.0
            AddParam(controller, "UniText_One", AnimatorControllerParameterType.Float, defaultFloat: 1f);
            AddParam(controller, "UniText_Ctrl", AnimatorControllerParameterType.Float, defaultFloat: 0f);
            for (int i = 0; i < payloadBytes; i++)
            {
                AddParam(controller, $"UniText_D{i}", AnimatorControllerParameterType.Float, defaultFloat: 0f);
            }
            if (hasD4Nibble)
            {
                // Compact36 nibble bits (synced Bool, Float here so they can weight Direct BlendTree children)
                for (int bit = 4; bit < 8; bit++)
                {
                    AddParam(controller, $"UniText_D4b{bit}", AnimatorControllerParameterType.Float, defaultFloat: 0f);
                }
            }
            AddParam(controller, "UniText_Visible", AnimatorControllerParameterType.Bool, defaultBool: false);
            AddParam(controller, "UniText_WorldDrop", AnimatorControllerParameterType.Bool, defaultBool: false);
            // Column bank & row page (synced Bool in VRCExpressionParameters, Float here so Direct BlendTree drives EncoderQuad)
            AddParam(controller, "UniText_Bank", AnimatorControllerParameterType.Float, defaultFloat: 0f);
            AddParam(controller, "UniText_RowPage", AnimatorControllerParameterType.Float, defaultFloat: 0f);
            AddParam(controller, "UniText_Billboard", AnimatorControllerParameterType.Float, defaultFloat: 0f);
            AddParam(controller, "UniText_Scale", AnimatorControllerParameterType.Float, defaultFloat: 0.5f);
            AddParam(controller, "UniText_Opacity", AnimatorControllerParameterType.Float, defaultFloat: 1f);
            AddParam(controller, "UniText_LocalClear", AnimatorControllerParameterType.Float, defaultFloat: 0f);

            // Layer 0: Single Direct BlendTree driving all numeric uniforms + Scale/Opacity/Billboard/LocalClear
            BuildDirectBlendTreeLayer(controller, payloadBytes, hasD4Nibble, saveAsset);

            // Layer 1: BoardPivot visibility + ParamCamera enabled toggle
            BuildBoolLayer(
                controller,
                "UniText_Visible",
                "UniText_Visible",
                CreateVisibilityClip("Visible_Off", false, saveAsset),
                CreateVisibilityClip("Visible_On", true, saveAsset),
                saveAsset
            );

            // Layer 2: VRCParentConstraint FreezeToWorld toggle (component type via assembly-qualified name fallback)
            Type constraintType = typeof(VRC.SDK3.Dynamics.Constraint.Components.VRCParentConstraint);
            BuildBoolLayer(
                controller,
                "UniText_WorldDrop",
                "UniText_WorldDrop",
                CreateSingleValueClip("WorldDrop_Follow", "BoardPivot", constraintType, "FreezeToWorld", 0f, saveAsset),
                CreateSingleValueClip("WorldDrop_Freeze", "BoardPivot", constraintType, "FreezeToWorld", 1f, saveAsset),
                saveAsset
            );

            return controller;
        }

        /// <summary>
        /// Builds a clean 1-slot wrapper Expressions Menu containing the UniText submenu
        /// and a nested Appearance submenu for Scale, Opacity, and optional Width / Height.
        /// </summary>
        public static VRCExpressionsMenu CreateMenuHierarchyInMemory(
            string menuName,
            Action<UnityEngine.Object> saveAsset,
            bool allowRuntimeResize = true)
        {
            var appearanceMenu = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
            appearanceMenu.name = "UniText_AppearanceMenu";
            appearanceMenu.controls = new List<VRCExpressionsMenu.Control>
            {
                new VRCExpressionsMenu.Control
                {
                    name = "Scale",
                    type = VRCExpressionsMenu.Control.ControlType.RadialPuppet,
                    subParameters = new[]
                    {
                        new VRCExpressionsMenu.Control.Parameter { name = "UniText_Scale" }
                    }
                },
                new VRCExpressionsMenu.Control
                {
                    name = "Opacity",
                    type = VRCExpressionsMenu.Control.ControlType.RadialPuppet,
                    subParameters = new[]
                    {
                        new VRCExpressionsMenu.Control.Parameter { name = "UniText_Opacity" }
                    }
                }
            };
            if (allowRuntimeResize)
            {
                appearanceMenu.controls.Add(new VRCExpressionsMenu.Control
                {
                    name = "Width",
                    type = VRCExpressionsMenu.Control.ControlType.RadialPuppet,
                    subParameters = new[]
                    {
                        new VRCExpressionsMenu.Control.Parameter { name = "UniText_Width" }
                    }
                });
                appearanceMenu.controls.Add(new VRCExpressionsMenu.Control
                {
                    name = "Height",
                    type = VRCExpressionsMenu.Control.ControlType.RadialPuppet,
                    subParameters = new[]
                    {
                        new VRCExpressionsMenu.Control.Parameter { name = "UniText_Height" }
                    }
                });
            }
            saveAsset?.Invoke(appearanceMenu);

            var subMenu = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
            subMenu.name = "UniText_SubMenu";
            subMenu.controls = new List<VRCExpressionsMenu.Control>
            {
                new VRCExpressionsMenu.Control
                {
                    name = "Show Board",
                    type = VRCExpressionsMenu.Control.ControlType.Toggle,
                    parameter = new VRCExpressionsMenu.Control.Parameter { name = "UniText_Visible" },
                    value = 1f
                },
                new VRCExpressionsMenu.Control
                {
                    name = "World Drop",
                    type = VRCExpressionsMenu.Control.ControlType.Toggle,
                    parameter = new VRCExpressionsMenu.Control.Parameter { name = "UniText_WorldDrop" },
                    value = 1f
                },
                new VRCExpressionsMenu.Control
                {
                    name = "Billboard",
                    type = VRCExpressionsMenu.Control.ControlType.Toggle,
                    parameter = new VRCExpressionsMenu.Control.Parameter { name = "UniText_Billboard" },
                    value = 1f
                },
                new VRCExpressionsMenu.Control
                {
                    name = "Clear Screen",
                    type = VRCExpressionsMenu.Control.ControlType.Button,
                    parameter = new VRCExpressionsMenu.Control.Parameter { name = "UniText_LocalClear" },
                    value = 1f
                },
                new VRCExpressionsMenu.Control
                {
                    name = "Appearance",
                    type = VRCExpressionsMenu.Control.ControlType.SubMenu,
                    subMenu = appearanceMenu
                }
            };
            saveAsset?.Invoke(subMenu);

            var rootWrapper = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
            rootWrapper.name = "UniText_RootEntry";
            rootWrapper.controls = new List<VRCExpressionsMenu.Control>
            {
                new VRCExpressionsMenu.Control
                {
                    name = menuName,
                    type = VRCExpressionsMenu.Control.ControlType.SubMenu,
                    subMenu = subMenu
                }
            };
            saveAsset?.Invoke(rootWrapper);

            return rootWrapper;
        }

        private static void ConfigureFontAtlasImporter()
        {
            ConfigureLinearPointTextureImporter(
                FontAtlasPath,
                "Run tools/font-atlas/generate_atlas.py first."
            );
        }

        private static void ConfigureAuxImporter()
        {
            ConfigureLinearPointTextureImporter(
                AuxTexturePath,
                "Run tools/font-atlas/generate_atlas.py first."
            );
        }

        private static void ConfigureLinearPointTextureImporter(string texturePath, string missingHint)
        {
            var importer = AssetImporter.GetAtPath(texturePath) as TextureImporter;
            if (importer == null)
            {
                Debug.LogWarning($"[UniText] Texture not found at {texturePath}. {missingHint}");
                return;
            }

            bool dirty = false;
            if (importer.textureType != TextureImporterType.Default) { importer.textureType = TextureImporterType.Default; dirty = true; }
            if (importer.sRGBTexture) { importer.sRGBTexture = false; dirty = true; }
            if (importer.alphaIsTransparency) { importer.alphaIsTransparency = false; dirty = true; }
            if (importer.mipmapEnabled) { importer.mipmapEnabled = false; dirty = true; }
            if (importer.npotScale != TextureImporterNPOTScale.None) { importer.npotScale = TextureImporterNPOTScale.None; dirty = true; }
            if (importer.filterMode != FilterMode.Point) { importer.filterMode = FilterMode.Point; dirty = true; }
            if (importer.wrapMode != TextureWrapMode.Clamp) { importer.wrapMode = TextureWrapMode.Clamp; dirty = true; }
            if (importer.textureCompression != TextureImporterCompression.Uncompressed) { importer.textureCompression = TextureImporterCompression.Uncompressed; dirty = true; }
            if (importer.maxTextureSize < 2048) { importer.maxTextureSize = 2048; dirty = true; }

            var defaultSettings = importer.GetDefaultPlatformTextureSettings();
            if (!defaultSettings.overridden || defaultSettings.format != TextureImporterFormat.RGBA32 || defaultSettings.textureCompression != TextureImporterCompression.Uncompressed)
            {
                defaultSettings.overridden = true;
                defaultSettings.format = TextureImporterFormat.RGBA32;
                defaultSettings.textureCompression = TextureImporterCompression.Uncompressed;
                defaultSettings.maxTextureSize = 2048;
                importer.SetPlatformTextureSettings(defaultSettings);
                dirty = true;
            }

            if (dirty)
            {
                importer.SaveAndReimport();
            }
        }

        private static RenderTexture EnsureParamBridgeRenderTexture(string tierName)
        {
            string path = $"{TexturesDir}/ParamBridge_8x1_{tierName}.renderTexture";
            var rt = AssetDatabase.LoadAssetAtPath<RenderTexture>(path);
            if (rt == null)
            {
                rt = new RenderTexture(8, 1, 0, GraphicsFormat.R8G8B8A8_UNorm)
                {
                    name = $"ParamBridge_8x1_{tierName}",
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                    useMipMap = false,
                    autoGenerateMips = false,
                    antiAliasing = 1
                };
                AssetDatabase.CreateAsset(rt, path);
            }
            else
            {
                rt.filterMode = FilterMode.Point;
                rt.wrapMode = TextureWrapMode.Clamp;
                EditorUtility.SetDirty(rt);
            }
            return rt;
        }

        private static Material EnsureParamEncoderMaterial(string tierName)
        {
            string path = $"{MaterialsDir}/UniText_ParamEncoder_{tierName}.mat";
            var shader = Shader.Find("UniText/ParamEncoder")
                ?? AssetDatabase.LoadAssetAtPath<Shader>($"{ShadersDir}/ParamEncoder.shader");
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(shader) { name = $"UniText_ParamEncoder_{tierName}" };
                AssetDatabase.CreateAsset(mat, path);
            }
            else if (shader != null && mat.shader != shader)
            {
                mat.shader = shader;
                EditorUtility.SetDirty(mat);
            }
            return mat;
        }

        private static Material EnsureBufferCrtMaterial(string tierName, RenderTexture paramBridge)
        {
            string path = $"{MaterialsDir}/UniText_BufferCRT_{tierName}.mat";
            var shader = Shader.Find("UniText/BufferCRT")
                ?? AssetDatabase.LoadAssetAtPath<Shader>($"{ShadersDir}/BufferCRT.shader");
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(shader) { name = $"UniText_BufferCRT_{tierName}" };
                AssetDatabase.CreateAsset(mat, path);
            }
            else if (shader != null && mat.shader != shader)
            {
                mat.shader = shader;
            }

            mat.DisableKeyword("_TIER_LITE");
            mat.DisableKeyword("_TIER_COMPACT");
            mat.DisableKeyword("_TIER_BALANCED");
            mat.DisableKeyword("_TIER_STANDARD");
            mat.DisableKeyword("_TIER_TURBO");
            if (tierName == "Lite")
            {
                mat.EnableKeyword("_TIER_LITE");
                mat.SetFloat("_Tier", 3f);
            }
            else if (tierName == "Compact")
            {
                mat.EnableKeyword("_TIER_COMPACT");
                mat.SetFloat("_Tier", 1f);
            }
            else if (tierName == "Balanced")
            {
                mat.EnableKeyword("_TIER_BALANCED");
                mat.SetFloat("_Tier", 4f);
            }
            else if (tierName == "Turbo")
            {
                mat.EnableKeyword("_TIER_TURBO");
                mat.SetFloat("_Tier", 2f);
            }
            else
            {
                mat.EnableKeyword("_TIER_STANDARD");
                mat.SetFloat("_Tier", 0f);
            }

            mat.SetFloat("_UseParamTex", 1f);
            mat.SetTexture("_ParamTex", paramBridge);
            var auxTex = AssetDatabase.LoadAssetAtPath<Texture2D>(AuxTexturePath);
            if (auxTex != null)
            {
                mat.SetTexture("_AuxTex", auxTex);
            }
            EditorUtility.SetDirty(mat);
            return mat;
        }

        private static CustomRenderTexture EnsureBufferCrtAsset(string tierName, Material bufferMat)
        {
            string path = $"{TexturesDir}/Buffer_64x32_{tierName}.asset";
            var crt = AssetDatabase.LoadAssetAtPath<CustomRenderTexture>(path);
            bool created = crt == null;
            if (created)
            {
                crt = new CustomRenderTexture(64, 32, GraphicsFormat.R8G8B8A8_UNorm)
                {
                    name = $"Buffer_64x32_{tierName}",
                    useMipMap = false,
                    autoGenerateMips = false
                };
            }
            else if (crt.width != 64 || crt.height != 32)
            {
                crt.Release();
                crt.width = 64;
                crt.height = 32;
            }

            crt.filterMode = FilterMode.Point;
            crt.wrapMode = TextureWrapMode.Clamp;
            crt.doubleBuffered = true;
            crt.updateMode = CustomRenderTextureUpdateMode.Realtime;
            crt.initializationMode = CustomRenderTextureUpdateMode.OnLoad;
            crt.initializationSource = CustomRenderTextureInitializationSource.TextureAndColor;
            crt.initializationTexture = null;
            crt.initializationMaterial = null;
            crt.initializationColor = Color.clear;
            crt.material = bufferMat;
            crt.shaderPass = 0;
            crt.ClearUpdateZones();

            if (created)
            {
                AssetDatabase.CreateAsset(crt, path);
            }

            crt.Initialize();
            EditorUtility.SetDirty(crt);
            return crt;
        }

        private static Material EnsureDisplayMaterial(string tierName, CustomRenderTexture bufferCrt)
        {
            string path = $"{MaterialsDir}/UniText_Display_{tierName}.mat";
            var shader = Shader.Find("UniText/Display")
                ?? AssetDatabase.LoadAssetAtPath<Shader>($"{ShadersDir}/Display.shader");
            var atlas = AssetDatabase.LoadAssetAtPath<Texture2D>(FontAtlasPath);
            var auxTex = AssetDatabase.LoadAssetAtPath<Texture2D>(AuxTexturePath);
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(shader) { name = $"UniText_Display_{tierName}" };
                AssetDatabase.CreateAsset(mat, path);
            }
            else if (shader != null && mat.shader != shader)
            {
                mat.shader = shader;
            }

            mat.SetTexture("_BufferTex", bufferCrt);
            if (atlas != null)
            {
                mat.SetTexture("_FontAtlas", atlas);
            }
            if (auxTex != null)
            {
                mat.SetTexture("_AuxTex", auxTex);
            }
            // Prefabs default to allowRuntimeResize = true and BottomRight inner-bottom anchor; NDMF re-bakes per UniTextProp.
            mat.SetFloat("_LiveGeometry", 1f);
            Vector2 defaultAnchor = UniTextProp.GetAnchorNormalized(UniTextBoardAnchor.BottomRight);
            mat.SetVector("_AnchorOffset", new Vector4(defaultAnchor.x, defaultAnchor.y, 0f, 0f));
            EditorUtility.SetDirty(mat);
            return mat;
        }

        private static void BuildDirectBlendTreeLayer(
            AnimatorController controller,
            int payloadBytes,
            bool hasD4Nibble,
            Action<UnityEngine.Object> saveAsset)
        {
            var sm = new AnimatorStateMachine { name = "UniText_DataBlend" };
            saveAsset?.Invoke(sm);

            var layer = new AnimatorControllerLayer
            {
                name = "UniText_DataBlend",
                defaultWeight = 1f,
                stateMachine = sm
            };
            controller.AddLayer(layer);

            var directTree = new BlendTree
            {
                name = "UniText_DirectRoot",
                blendType = BlendTreeType.Direct
            };
            saveAsset?.Invoke(directTree);

            var directChildren = new List<ChildMotion>
            {
                Build1DRangeChild("Ctrl", "UniText_Ctrl", "ParamBridge/EncoderQuad", "material._Ctrl", 0f, 255f, 0f, 255f, saveAsset)
            };

            for (int i = 0; i < payloadBytes; i++)
            {
                directChildren.Add(Build1DRangeChild(
                    $"D{i}", $"UniText_D{i}",
                    "ParamBridge/EncoderQuad", $"material._D{i}",
                    0f, 255f, 0f, 255f, saveAsset
                ));
            }

            if (hasD4Nibble)
            {
                // Compact36: 4 synced Bools carry bits 4..7 of D4 into ParamEncoder.shader (identical pattern to Bank/RowPage)
                for (int bit = 4; bit < 8; bit++)
                {
                    directChildren.Add(Build1DRangeChild(
                        $"D4b{bit}", $"UniText_D4b{bit}",
                        "ParamBridge/EncoderQuad", $"material._D4b{bit}",
                        0f, 1f, 0f, 1f, saveAsset
                    ));
                }
            }

            directChildren.Add(Build1DRangeChild(
                "LocalClear", "UniText_LocalClear",
                "ParamBridge/EncoderQuad", "material._LocalClear",
                0f, 1f, 0f, 1f, saveAsset
            ));
            directChildren.Add(Build1DRangeChild(
                "Bank", "UniText_Bank",
                "ParamBridge/EncoderQuad", "material._Bank",
                0f, 1f, 0f, 1f, saveAsset
            ));
            directChildren.Add(Build1DRangeChild(
                "RowPage", "UniText_RowPage",
                "ParamBridge/EncoderQuad", "material._RowPage",
                0f, 1f, 0f, 1f, saveAsset
            ));
            directChildren.Add(Build1DRangeChild(
                "Billboard", "UniText_Billboard",
                "BoardPivot/DisplayQuad", "material._Billboard",
                0f, 1f, 0f, 1f, saveAsset
            ));
            directChildren.Add(Build1DRangeChild(
                "Opacity", "UniText_Opacity",
                "BoardPivot/DisplayQuad", "material._Opacity",
                0f, 1f, 0.12f, 1.0f, saveAsset
            ));
            directChildren.Add(Build1DScaleChild(saveAsset));
            // Live resize (rows / row_px + panel size) is driven by CMD_SET_GEOMETRY inside Display.shader, not by Animator curves.

            directTree.children = directChildren.ToArray();

            var treeSo = new SerializedObject(directTree);
            treeSo.Update();
            var normProp = treeSo.FindProperty("m_NormalizedBlendValues");
            if (normProp != null)
            {
                normProp.boolValue = false;
                treeSo.ApplyModifiedPropertiesWithoutUndo();
            }

            var state = sm.AddState("BlendTree");
            state.motion = directTree;
            state.writeDefaultValues = false;
            sm.defaultState = state;
            saveAsset?.Invoke(state);
        }

        private static ChildMotion Build1DRangeChild(
            string id,
            string paramName,
            string targetPath,
            string propertyName,
            float paramMin,
            float paramMax,
            float valMin,
            float valMax,
            Action<UnityEngine.Object> saveAsset)
        {
            var subTree = new BlendTree
            {
                name = $"BT_{id}",
                blendType = BlendTreeType.Simple1D,
                blendParameter = paramName,
                useAutomaticThresholds = false
            };
            saveAsset?.Invoke(subTree);

            var clipMin = CreateSingleValueClip($"{id}_Min", targetPath, typeof(MeshRenderer), propertyName, valMin, saveAsset);
            var clipMax = CreateSingleValueClip($"{id}_Max", targetPath, typeof(MeshRenderer), propertyName, valMax, saveAsset);

            subTree.AddChild(clipMin, paramMin);
            subTree.AddChild(clipMax, paramMax);

            return new ChildMotion
            {
                motion = subTree,
                directBlendParameter = "UniText_One",
                timeScale = 1f
            };
        }

        private static ChildMotion Build1DScaleChild(Action<UnityEngine.Object> saveAsset)
        {
            var subTree = new BlendTree
            {
                name = "BT_Scale",
                blendType = BlendTreeType.Simple1D,
                blendParameter = "UniText_Scale",
                useAutomaticThresholds = false
            };
            saveAsset?.Invoke(subTree);

            var clipMin = CreateScaleClip("Scale_Min", "BoardPivot", new Vector3(0.5f, 0.5f, 0.5f), saveAsset);
            var clipMax = CreateScaleClip("Scale_Max", "BoardPivot", new Vector3(1.5f, 1.5f, 1.5f), saveAsset);

            subTree.AddChild(clipMin, 0f);
            subTree.AddChild(clipMax, 1f);

            return new ChildMotion
            {
                motion = subTree,
                directBlendParameter = "UniText_One",
                timeScale = 1f
            };
        }

        private static void BuildBoolLayer(
            AnimatorController controller,
            string layerName,
            string boolParam,
            AnimationClip offClip,
            AnimationClip onClip,
            Action<UnityEngine.Object> saveAsset)
        {
            var sm = new AnimatorStateMachine { name = layerName };
            saveAsset?.Invoke(sm);

            var layer = new AnimatorControllerLayer
            {
                name = layerName,
                defaultWeight = 1f,
                stateMachine = sm
            };
            controller.AddLayer(layer);

            var offState = sm.AddState("Off");
            offState.motion = offClip;
            offState.writeDefaultValues = false;
            saveAsset?.Invoke(offState);

            var onState = sm.AddState("On");
            onState.motion = onClip;
            onState.writeDefaultValues = false;
            saveAsset?.Invoke(onState);

            sm.defaultState = offState;

            var toOn = offState.AddTransition(onState);
            toOn.hasExitTime = false;
            toOn.duration = 0f;
            toOn.AddCondition(AnimatorConditionMode.If, 0f, boolParam);
            saveAsset?.Invoke(toOn);

            var toOff = onState.AddTransition(offState);
            toOff.hasExitTime = false;
            toOff.duration = 0f;
            toOff.AddCondition(AnimatorConditionMode.IfNot, 0f, boolParam);
            saveAsset?.Invoke(toOff);
        }

        private static AnimationClip CreateVisibilityClip(
            string clipName,
            bool visible,
            Action<UnityEngine.Object> saveAsset)
        {
            float v = visible ? 1f : 0f;
            float dt = 1f / 60f;
            var clip = new AnimationClip { name = clipName };
            clip.SetCurve("BoardPivot", typeof(GameObject), "m_IsActive", AnimationCurve.Linear(0f, v, dt, v));
            clip.SetCurve("ParamBridge/ParamCamera", typeof(Camera), "m_Enabled", AnimationCurve.Linear(0f, v, dt, v));
            saveAsset?.Invoke(clip);
            return clip;
        }

        private static AnimationClip CreateSingleValueClip(
            string clipName,
            string relativePath,
            Type targetType,
            string propertyName,
            float value,
            Action<UnityEngine.Object> saveAsset)
        {
            var clip = new AnimationClip { name = clipName };
            var curve = AnimationCurve.Linear(0f, value, 1f / 60f, value);
            clip.SetCurve(relativePath, targetType, propertyName, curve);
            saveAsset?.Invoke(clip);
            return clip;
        }

        private static AnimationClip CreateScaleClip(
            string clipName,
            string relativePath,
            Vector3 scale,
            Action<UnityEngine.Object> saveAsset)
        {
            var clip = new AnimationClip { name = clipName };
            float dt = 1f / 60f;
            clip.SetCurve(relativePath, typeof(Transform), "m_LocalScale.x", AnimationCurve.Linear(0f, scale.x, dt, scale.x));
            clip.SetCurve(relativePath, typeof(Transform), "m_LocalScale.y", AnimationCurve.Linear(0f, scale.y, dt, scale.y));
            clip.SetCurve(relativePath, typeof(Transform), "m_LocalScale.z", AnimationCurve.Linear(0f, scale.z, dt, scale.z));
            saveAsset?.Invoke(clip);
            return clip;
        }

        private static void BuildTierPrefab(
            UniTextTier tier,
            string prefabFileName,
            RenderTexture paramBridge,
            Material encoderMat,
            Material displayMat,
            CustomRenderTexture bufferCrt)
        {
            string prefabPath = $"{PrefabsDir}/{prefabFileName}";
            string rootName = Path.GetFileNameWithoutExtension(prefabFileName);

            var root = new GameObject(rootName);
            try
            {
                var prop = root.AddComponent<UniTextProp>();
                prop.tier = tier;
                prop.placementPreset = UniTextPlacementPreset.RightSide;
                prop.boardAnchor = UniTextBoardAnchor.BottomRight;
                prop.useBoneProxy = true;
                prop.followBone = HumanBodyBones.Chest;
                prop.autoPositionToBone = true;
                prop.boneOffset = UniTextProp.DefaultBoneOffset;
                prop.syncWorldDrop = true;
                prop.syncWideRows = true;
                prop.installMenu = true;
                prop.menuName = "UniText";
                prop.stripOnAndroid = true;
                prop.viewportWidth = UniTextProp.DefaultViewportWidth;
                prop.viewportHeight = UniTextProp.DefaultViewportHeight;
                prop.displayRows = UniTextProp.DefaultDisplayRows;
                prop.autoFitRowPx = true;
                prop.rowPx = UniTextProp.DefaultRowPx;
                prop.allowRuntimeResize = true;
                prop.paramBridgeTexture = paramBridge;
                prop.encoderMaterial = encoderMat;
                prop.displayMaterial = displayMat;
                prop.bufferCRT = bufferCrt;

                var anchor = new GameObject("SideFollowAnchor");
                anchor.transform.SetParent(root.transform, false);
                anchor.transform.localPosition = new Vector3(0.28f, 1.20f, 0.16f);
                anchor.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);

                // Kept active in Edit Mode for Scene positioning; NDMF disables until UniText_Visible = true
                var boardPivot = new GameObject("BoardPivot");
                boardPivot.transform.SetParent(root.transform, false);
                boardPivot.transform.localPosition = anchor.transform.localPosition;
                boardPivot.transform.localRotation = anchor.transform.localRotation;

                var displayQuad = GameObject.CreatePrimitive(PrimitiveType.Quad);
                displayQuad.name = "DisplayQuad";
                displayQuad.transform.SetParent(boardPivot.transform, false);
                displayQuad.transform.localPosition = prop.GetDisplayQuadLocalPosition();
                displayQuad.transform.localScale = new Vector3(0.84f, 0.2625f, 1f);
                UnityEngine.Object.DestroyImmediate(displayQuad.GetComponent<Collider>());

                var displayRenderer = displayQuad.GetComponent<MeshRenderer>();
                displayRenderer.sharedMaterial = displayMat;
                displayRenderer.shadowCastingMode = ShadowCastingMode.Off;
                displayRenderer.receiveShadows = false;
                displayRenderer.lightProbeUsage = LightProbeUsage.Off;
                displayRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;

                var paramBridgeGo = new GameObject("ParamBridge");
                paramBridgeGo.transform.SetParent(root.transform, false);

                var camGo = new GameObject("ParamCamera");
                camGo.transform.SetParent(paramBridgeGo.transform, false);
                var cam = camGo.AddComponent<Camera>();
                cam.orthographic = true;
                cam.orthographicSize = 0.1f;
                cam.nearClipPlane = 0.001f;
                cam.farClipPlane = 0.5f;
                cam.stereoTargetEye = StereoTargetEyeMask.None;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Color.clear;
                cam.cullingMask = ParamCameraCullingMask;
                cam.renderingPath = RenderingPath.Forward;
                cam.depthTextureMode = DepthTextureMode.None;
                cam.targetTexture = paramBridge;
                cam.depth = -100f;
                cam.allowHDR = false;
                cam.allowMSAA = false;
                cam.allowDynamicResolution = false;
                cam.useOcclusionCulling = false;
                cam.enabled = false;

                var encoderQuad = GameObject.CreatePrimitive(PrimitiveType.Quad);
                encoderQuad.name = "EncoderQuad";
                encoderQuad.layer = ParamBridgeLayer;
                encoderQuad.transform.SetParent(paramBridgeGo.transform, false);
                encoderQuad.transform.localPosition = new Vector3(0f, 0f, 0.05f);
                encoderQuad.transform.localScale = new Vector3(0.01f, 0.01f, 0.01f);
                UnityEngine.Object.DestroyImmediate(encoderQuad.GetComponent<Collider>());

                var encRenderer = encoderQuad.GetComponent<MeshRenderer>();
                encRenderer.sharedMaterial = encoderMat;
                encRenderer.allowOcclusionWhenDynamic = false;
                encRenderer.shadowCastingMode = ShadowCastingMode.Off;
                encRenderer.receiveShadows = false;
                encRenderer.lightProbeUsage = LightProbeUsage.Off;
                encRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;

                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        private static void AddParam(AnimatorController controller, string name, AnimatorControllerParameterType type, float defaultFloat = 0f, bool defaultBool = false)
        {
            controller.AddParameter(new AnimatorControllerParameter
            {
                name = name,
                type = type,
                defaultFloat = defaultFloat,
                defaultBool = defaultBool
            });
        }

        private static void EnsureFolder(string assetPath)
        {
            if (AssetDatabase.IsValidFolder(assetPath))
            {
                return;
            }
            string parent = Path.GetDirectoryName(assetPath)?.Replace("\\", "/");
            string name = Path.GetFileName(assetPath);
            if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent))
            {
                EnsureFolder(parent);
            }
            AssetDatabase.CreateFolder(parent, name);
        }
    }
}
#endif
