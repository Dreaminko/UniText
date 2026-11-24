#if UNITEXT_VRCSDK3 && UNITEXT_MA && UNITEXT_NDMF
using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace UniText.Editor
{
    /// <summary>
    /// Interactive Unity Editor window accessible from the top menu bar (UniText -> Install to Avatar)
    /// that lets creators drag &amp; drop a VRChat Avatar and apply UniText non-destructively in one click.
    /// </summary>
    public class UniTextInstallerWindow : EditorWindow
    {
        private const string LanguagePrefKey = "UniText.Installer.Language";

        private const string LitePrefabName = "Board_Lite_33b.prefab";
        private const string CompactPrefabName = "Board_Compact_45b.prefab";
        private const string BalancedPrefabName = "Board_Balanced_57b.prefab";
        private const string StandardPrefabName = "Board_Standard_73b.prefab";
        private const string TurboPrefabName = "Board_Turbo_137b.prefab";

        private const string LitePrefabGuid = "e7a100054d9c40008000000000000005";
        private const string CompactPrefabGuid = "e7a100054d9c40008000000000000002";
        private const string BalancedPrefabGuid = "e7a100054d9c40008000000000000006";
        private const string StandardPrefabGuid = "e7a100054d9c40008000000000000003";
        private const string TurboPrefabGuid = "e7a100054d9c40008000000000000004";

        [SerializeField] private GameObject _rawTargetObject;
        [SerializeField] private UniTextTier _tier = UniTextTier.Standard;
        [SerializeField] private UniTextPlacementPreset _placementPreset = UniTextPlacementPreset.RightSide;
        [SerializeField] private UniTextBoardAnchor _boardAnchor = UniTextBoardAnchor.BottomRight;
        [SerializeField] private bool _useBoneProxy = true;
        [SerializeField] private HumanBodyBones _followBone = HumanBodyBones.Chest;
        [SerializeField] private bool _autoPositionToBone = true;
        [SerializeField] private Vector3 _boneOffset = new Vector3(0.28f, 0.14f, 0.16f);
        [SerializeField] private bool _syncWorldDrop = true;
        [SerializeField] private bool _syncWideRows = true;
        [SerializeField] private bool _installMenu = true;
        [SerializeField] private string _menuName = "UniText";
        [SerializeField] private VRCExpressionsMenu _installTargetMenu;
        [SerializeField] private bool _stripOnAndroid = true;
        [SerializeField] private float _viewportWidth = UniTextProp.DefaultViewportWidth;
        [SerializeField] private float _viewportHeight = UniTextProp.DefaultViewportHeight;
        [SerializeField] private int _displayRows = UniTextProp.DefaultDisplayRows;
        [SerializeField] private bool _autoFitRowPx = true;
        [SerializeField] private int _rowPx = UniTextProp.DefaultRowPx;
        [SerializeField] private bool _allowRuntimeResize = true;
        [SerializeField] private bool _showAdvanced;

        private bool _isChinese;
        [SerializeField] private int _lastSyncedAvatarId;
        private Vector2 _scrollPos;
        private string _statusMessage;
        private MessageType _statusMessageType = MessageType.None;

        private GUIStyle _titleStyle;
        private GUIStyle _subtitleStyle;
        private GUIStyle _sectionHeaderStyle;
        private GUIStyle _dropMainLabelStyle;
        private GUIStyle _dropSubLabelStyle;
        private GUIStyle _primaryButtonStyle;
        private GUIStyle _selectedButtonStyle;
        private GUIStyle _selectedMiniButtonStyle;
        private GUIStyle _barLabelStyle;
        private GUIStyle _dirtyLabelStyle;
        private GUIStyle _previewCaptionStyle;

        [MenuItem("UniText/Install to Avatar", priority = 0)]
        private static void OpenFromTopMenu()
        {
            OpenWindow();
        }

        [MenuItem("Tools/UniText/Install to Avatar", priority = 0)]
        private static void OpenFromToolsMenu()
        {
            OpenWindow();
        }

        public static void OpenWindow()
        {
            var window = GetWindow<UniTextInstallerWindow>(false, "UniText Installer", true);
            window.minSize = new Vector2(400f, 540f);
            window.TryAutoSelectFromHierarchy();
            window.Show();
        }

        private void OnEnable()
        {
            if (EditorPrefs.HasKey(LanguagePrefKey))
            {
                _isChinese = EditorPrefs.GetInt(LanguagePrefKey, 0) == 1;
            }
            else
            {
                _isChinese = Application.systemLanguage == SystemLanguage.Chinese
                    || Application.systemLanguage == SystemLanguage.ChineseSimplified
                    || Application.systemLanguage == SystemLanguage.ChineseTraditional;
                EditorPrefs.SetInt(LanguagePrefKey, _isChinese ? 1 : 0);
            }
        }

        private void OnHierarchyChange()
        {
            Repaint();
        }

        private void OnSelectionChange()
        {
            Repaint();
        }

        private string L(string zh, string en)
        {
            return _isChinese ? zh : en;
        }

        private void EnsureStyles()
        {
            if (_titleStyle == null)
            {
                _titleStyle = new GUIStyle(EditorStyles.boldLabel)
                {
                    fontSize = 15,
                    alignment = TextAnchor.MiddleLeft
                };
            }

            if (_subtitleStyle == null)
            {
                _subtitleStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    wordWrap = true
                };
            }

            if (_sectionHeaderStyle == null)
            {
                _sectionHeaderStyle = new GUIStyle(EditorStyles.boldLabel)
                {
                    fontSize = 12
                };
            }

            if (_dropMainLabelStyle == null)
            {
                _dropMainLabelStyle = new GUIStyle(EditorStyles.boldLabel)
                {
                    alignment = TextAnchor.MiddleCenter,
                    wordWrap = true,
                    fontSize = 12
                };
            }

            if (_dropSubLabelStyle == null)
            {
                _dropSubLabelStyle = new GUIStyle(EditorStyles.centeredGreyMiniLabel)
                {
                    alignment = TextAnchor.MiddleCenter,
                    wordWrap = true
                };
            }

            if (_primaryButtonStyle == null)
            {
                _primaryButtonStyle = new GUIStyle(GUI.skin.button)
                {
                    fontStyle = FontStyle.Bold,
                    fontSize = 12
                };
            }

            if (_selectedButtonStyle == null)
            {
                _selectedButtonStyle = new GUIStyle(GUI.skin.button)
                {
                    fontStyle = FontStyle.Bold
                };
            }

            if (_selectedMiniButtonStyle == null)
            {
                _selectedMiniButtonStyle = new GUIStyle(EditorStyles.miniButton)
                {
                    fontStyle = FontStyle.Bold
                };
            }

            if (_barLabelStyle == null)
            {
                _barLabelStyle = new GUIStyle(EditorStyles.boldLabel)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = 11
                };
                _barLabelStyle.normal.textColor = Color.white;
            }

            if (_dirtyLabelStyle == null)
            {
                _dirtyLabelStyle = new GUIStyle(EditorStyles.miniBoldLabel);
                _dirtyLabelStyle.normal.textColor = EditorGUIUtility.isProSkin
                    ? new Color(1.0f, 0.78f, 0.32f, 1f)
                    : new Color(0.72f, 0.42f, 0.0f, 1f);
            }

            if (_previewCaptionStyle == null)
            {
                _previewCaptionStyle = new GUIStyle(EditorStyles.centeredGreyMiniLabel)
                {
                    fontSize = 10
                };
            }
        }

        private void ClearStatusMessage()
        {
            _statusMessage = null;
            _statusMessageType = MessageType.None;
        }

        private void TryAutoSelectFromHierarchy()
        {
            if (_rawTargetObject != null || Selection.activeGameObject == null)
            {
                return;
            }

            GameObject selected = Selection.activeGameObject;
            if (EditorUtility.IsPersistent(selected) || !selected.scene.IsValid())
            {
                return;
            }

            VRCAvatarDescriptor descriptor = selected.GetComponentInParent<VRCAvatarDescriptor>();
            if (descriptor != null)
            {
                SetTargetObject(descriptor.gameObject);
            }
        }

        private void SetTargetObject(GameObject target)
        {
            _rawTargetObject = target;
            ClearStatusMessage();

            if (target == null)
            {
                _lastSyncedAvatarId = 0;
                return;
            }

            if (!EditorUtility.IsPersistent(target) && target.scene.IsValid())
            {
                VRCAvatarDescriptor descriptor = target.GetComponentInParent<VRCAvatarDescriptor>();
                if (descriptor != null)
                {
                    _rawTargetObject = descriptor.gameObject;
                    SyncFromAvatarIfChanged(descriptor);
                }
                else
                {
                    _lastSyncedAvatarId = 0;
                }
            }
            else
            {
                _lastSyncedAvatarId = 0;
            }
        }

        private void SyncFromAvatarIfChanged(VRCAvatarDescriptor descriptor)
        {
            if (descriptor == null)
            {
                _lastSyncedAvatarId = 0;
                return;
            }

            int instanceId = descriptor.GetInstanceID();
            if (instanceId == _lastSyncedAvatarId)
            {
                return;
            }

            _lastSyncedAvatarId = instanceId;
            UniTextProp[] existingProps = descriptor.GetComponentsInChildren<UniTextProp>(true);
            if (existingProps != null && existingProps.Length > 0 && existingProps[0] != null)
            {
                SyncSettingsFromProp(existingProps[0]);
            }
            else if (_autoPositionToBone && _placementPreset != UniTextPlacementPreset.Custom)
            {
                _boneOffset = UniTextPlacementUtility.ComputeAdaptiveBoneOffset(
                    descriptor.gameObject,
                    _placementPreset,
                    _followBone,
                    UniTextProp.DefaultBoneOffset
                );
            }
        }

        private void SyncSettingsFromProp(UniTextProp prop)
        {
            _tier = prop.tier;
            _placementPreset = prop.placementPreset;
            _boardAnchor = prop.boardAnchor;
            _useBoneProxy = prop.useBoneProxy;
            _followBone = prop.followBone;
            _autoPositionToBone = prop.autoPositionToBone;
            _boneOffset = prop.boneOffset;
            _syncWorldDrop = prop.syncWorldDrop;
            _syncWideRows = prop.syncWideRows;
            _installMenu = prop.installMenu;
            _menuName = string.IsNullOrWhiteSpace(prop.menuName) ? "UniText" : prop.menuName;
            _installTargetMenu = prop.installTargetMenu;
            _stripOnAndroid = prop.stripOnAndroid;
            _viewportWidth = Mathf.Clamp(prop.viewportWidth, UniTextProp.MinViewportWidth, UniTextProp.MaxViewportWidth);
            _viewportHeight = Mathf.Clamp(prop.viewportHeight, UniTextProp.MinViewportHeight, UniTextProp.MaxViewportHeight);
            _displayRows = prop.EffectiveDisplayRows;
            _autoFitRowPx = prop.autoFitRowPx;
            _rowPx = prop.rowPx;
            _allowRuntimeResize = prop.allowRuntimeResize;
        }

        private void OnGUI()
        {
            EnsureStyles();

            DrawHeader();

            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);
            EditorGUILayout.Space(4f);

            VRCAvatarDescriptor descriptor = DrawTargetAvatarSection();

            EditorGUI.BeginChangeCheck();

            EditorGUILayout.Space(8f);
            DrawTierSection();

            UniTextParameterChecker.AvatarBudgetSummary budgetSummary = descriptor != null
                ? UniTextParameterChecker.AnalyzeAvatar(descriptor)
                : default(UniTextParameterChecker.AvatarBudgetSummary);

            UniTextParameterChecker.ProjectWithUniText(
                budgetSummary,
                _tier,
                _syncWorldDrop,
                out int addedBits,
                out int projectedTotal,
                _syncWideRows,
                _displayRows > 6,
                _allowRuntimeResize
            );

            EditorGUILayout.Space(8f);
            DrawBudgetSection(descriptor, budgetSummary, addedBits, projectedTotal);

            EditorGUILayout.Space(8f);
            DrawConfigurationSection();

            EditorGUILayout.Space(8f);
            DrawViewportSection(descriptor);

            EditorGUILayout.Space(8f);
            DrawAdvancedSection(descriptor);

            if (EditorGUI.EndChangeCheck())
            {
                ClearStatusMessage();
            }

            GameObject tierPrefab = LoadTierPrefab(_tier);
            DrawDiagnosticsSection(descriptor, tierPrefab);

            EditorGUILayout.Space(6f);
            EditorGUILayout.EndScrollView();

            DrawStatusBanner();
            DrawFooterActions(descriptor, budgetSummary, tierPrefab, addedBits, projectedTotal);
        }

        private void DrawHeader()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(
                L("UniText Avatar 安装向导", "UniText Avatar Installer"),
                _titleStyle,
                GUILayout.ExpandWidth(true),
                GUILayout.Height(24f)
            );

            if (GUILayout.Button(_isChinese ? "EN" : "中文", GUILayout.Width(44f), GUILayout.Height(24f)))
            {
                _isChinese = !_isChinese;
                EditorPrefs.SetInt(LanguagePrefKey, _isChinese ? 1 : 0);
                GUIUtility.keyboardControl = 0;
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

        private VRCAvatarDescriptor DrawTargetAvatarSection()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Label(L("1. 目标 Avatar", "1. Target Avatar"), _sectionHeaderStyle);
            EditorGUILayout.Space(4f);

            VRCAvatarDescriptor resolvedDescriptor = ResolveValidDescriptor();
            if (resolvedDescriptor != null)
            {
                _rawTargetObject = resolvedDescriptor.gameObject;
                SyncFromAvatarIfChanged(resolvedDescriptor);
            }

            DrawAvatarDropZone(resolvedDescriptor);

            EditorGUILayout.Space(4f);
            EditorGUILayout.BeginHorizontal();
            float prevLabelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = _isChinese ? 60f : 50f;
            EditorGUI.BeginChangeCheck();
            GameObject picked = (GameObject)EditorGUILayout.ObjectField(
                L("目标对象", "Target"),
                _rawTargetObject,
                typeof(GameObject),
                true
            );
            if (EditorGUI.EndChangeCheck())
            {
                SetTargetObject(picked);
                resolvedDescriptor = ResolveValidDescriptor();
            }
            EditorGUIUtility.labelWidth = prevLabelWidth;

            GUI.enabled = Selection.activeGameObject != null;
            if (GUILayout.Button(L("使用选中", "Use Selected"), GUILayout.Width(82f)))
            {
                SetTargetObject(Selection.activeGameObject);
                resolvedDescriptor = ResolveValidDescriptor();
                GUIUtility.keyboardControl = 0;
            }

            GUI.enabled = _rawTargetObject != null;
            if (GUILayout.Button(L("清空", "Clear"), GUILayout.Width(54f)))
            {
                SetTargetObject(null);
                resolvedDescriptor = null;
                GUIUtility.keyboardControl = 0;
            }
            GUI.enabled = true;
            EditorGUILayout.EndHorizontal();

            // Validation messages
            if (_rawTargetObject == null)
            {
                EditorGUILayout.HelpBox(
                    L(
                        "请将场景 Hierarchy 中带有 VRCAvatarDescriptor 的 Avatar 拖入上方区域。",
                        "Drag an Avatar GameObject (with VRCAvatarDescriptor) from the Scene Hierarchy into the box above."
                    ),
                    MessageType.Info
                );
            }
            else if (EditorUtility.IsPersistent(_rawTargetObject) || !_rawTargetObject.scene.IsValid())
            {
                EditorGUILayout.HelpBox(
                    L(
                        "不支持直接拖入 Project 面板中的 Prefab 资产。请先将 Avatar 拖入场景，再从 Hierarchy 拖到此处。",
                        "Project prefab assets cannot be modified directly. Place the Avatar in your Scene and drag it from the Hierarchy."
                    ),
                    MessageType.Error
                );
            }
            else if (resolvedDescriptor == null)
            {
                EditorGUILayout.HelpBox(
                    L(
                        $"在 \"{_rawTargetObject.name}\" 及其父级中未找到 VRCAvatarDescriptor 组件。",
                        $"No VRCAvatarDescriptor found on '{_rawTargetObject.name}' or its parents."
                    ),
                    MessageType.Error
                );
            }
            else
            {
                UniTextProp[] existingProps = resolvedDescriptor.GetComponentsInChildren<UniTextProp>(true);
                if (existingProps != null && existingProps.Length > 0)
                {
                    EditorGUILayout.Space(2f);
                    EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
                    string propNames = FormatPropSummaryList(existingProps);
                    string bannerText = existingProps.Length == 1
                        ? L($"已安装 UniText: {propNames}", $"Installed UniText: {propNames}")
                        : L($"检测到多个 UniText 组件: {propNames}（点击安装将整理为单个实例）", $"Multiple UniText props detected: {propNames} (Install will consolidate to one)");
                    EditorGUILayout.LabelField(bannerText, EditorStyles.wordWrappedMiniLabel);
                    if (GUILayout.Button(L("选中", "Select"), GUILayout.Width(58f), GUILayout.Height(20f)))
                    {
                        Selection.activeGameObject = existingProps[0].gameObject;
                        EditorGUIUtility.PingObject(existingProps[0].gameObject);
                    }
                    EditorGUILayout.EndHorizontal();
                }
            }

            EditorGUILayout.EndVertical();
            return resolvedDescriptor;
        }

        private VRCAvatarDescriptor ResolveValidDescriptor()
        {
            if (_rawTargetObject == null)
            {
                return null;
            }
            if (EditorUtility.IsPersistent(_rawTargetObject) || !_rawTargetObject.scene.IsValid())
            {
                return null;
            }
            return _rawTargetObject.GetComponentInParent<VRCAvatarDescriptor>();
        }

        private void DrawAvatarDropZone(VRCAvatarDescriptor resolvedDescriptor)
        {
            bool isReady = resolvedDescriptor != null;
            float zoneHeight = isReady ? 44f : 62f;
            Rect dropRect = GUILayoutUtility.GetRect(0f, zoneHeight, GUILayout.ExpandWidth(true));
            Event evt = Event.current;
            bool isHovered = dropRect.Contains(evt.mousePosition);
            bool isDragging = evt.type == EventType.DragUpdated || evt.type == EventType.DragPerform;

            GameObject draggedGo = null;
            if (isHovered && isDragging)
            {
                draggedGo = TryGetDraggedGameObject(DragAndDrop.objectReferences);
                DragAndDrop.visualMode = draggedGo != null ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;

                if (evt.type == EventType.DragPerform && draggedGo != null)
                {
                    DragAndDrop.AcceptDrag();
                    SetTargetObject(draggedGo);
                    GUIUtility.keyboardControl = 0;
                    evt.Use();
                    Repaint();
                }
            }

            Color bgColor;
            Color borderColor;
            if (isHovered && isDragging && draggedGo != null)
            {
                bgColor = new Color(0.18f, 0.44f, 0.72f, 0.28f);
                borderColor = new Color(0.32f, 0.72f, 1.0f, 0.95f);
            }
            else if (isReady)
            {
                bgColor = new Color(0.16f, 0.56f, 0.30f, 0.18f);
                borderColor = new Color(0.28f, 0.78f, 0.44f, 0.85f);
            }
            else if (_rawTargetObject != null)
            {
                bgColor = new Color(0.72f, 0.22f, 0.22f, 0.18f);
                borderColor = new Color(0.88f, 0.34f, 0.34f, 0.85f);
            }
            else
            {
                bgColor = EditorGUIUtility.isProSkin
                    ? new Color(0.15f, 0.15f, 0.15f, 0.45f)
                    : new Color(0.75f, 0.75f, 0.75f, 0.35f);
                borderColor = EditorGUIUtility.isProSkin
                    ? new Color(0.45f, 0.45f, 0.45f, 0.65f)
                    : new Color(0.35f, 0.35f, 0.35f, 0.65f);
            }

            EditorGUI.DrawRect(dropRect, bgColor);
            DrawRectOutline(dropRect, borderColor, 1.5f);

            Rect mainLabelRect = isReady
                ? new Rect(dropRect.x + 10f, dropRect.y + 4f, dropRect.width - 20f, 20f)
                : new Rect(dropRect.x + 10f, dropRect.y + 9f, dropRect.width - 20f, 24f);
            Rect subLabelRect = isReady
                ? new Rect(dropRect.x + 10f, dropRect.y + 23f, dropRect.width - 20f, 16f)
                : new Rect(dropRect.x + 10f, dropRect.y + 33f, dropRect.width - 20f, 22f);

            if (isHovered && isDragging && draggedGo != null)
            {
                GUI.Label(mainLabelRect, L($"松开鼠标以选择: {draggedGo.name}", $"Drop to select: {draggedGo.name}"), _dropMainLabelStyle);
                GUI.Label(subLabelRect, L("将自动识别所属的 VRCAvatarDescriptor 根节点", "Will automatically resolve parent VRCAvatarDescriptor"), _dropSubLabelStyle);
            }
            else if (isReady)
            {
                GUI.Label(mainLabelRect, L($"✓ 已就绪: {resolvedDescriptor.gameObject.name}", $"✓ Ready: {resolvedDescriptor.gameObject.name}"), _dropMainLabelStyle);
                GUI.Label(subLabelRect, L("可拖入其他 Avatar 替换目标", "Drag another Hierarchy Avatar here to switch target"), _dropSubLabelStyle);
            }
            else
            {
                GUI.Label(mainLabelRect, L("将 Avatar 从 Hierarchy 拖到此处", "Drag & Drop Avatar from Hierarchy Here"), _dropMainLabelStyle);
                GUI.Label(subLabelRect, L("支持拖入 Avatar 根节点或任意子骨骼/网格物体", "Supports Avatar root or any child bone/mesh inside it"), _dropSubLabelStyle);
            }
        }

        private void DrawTierSection()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Label(L("2. 带宽档位", "2. Bandwidth Tier"), _sectionHeaderStyle);
            EditorGUILayout.Space(4f);

            EditorGUILayout.BeginHorizontal();
            DrawTierButton(UniTextTier.Lite, "Lite (33b)");
            DrawTierButton(UniTextTier.Compact, "Compact (45b)");
            DrawTierButton(UniTextTier.Balanced, "Balanced (57b)");
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(2f);
            EditorGUILayout.BeginHorizontal();
            DrawTierButton(UniTextTier.Standard, "Standard (73b) ★");
            DrawTierButton(UniTextTier.Turbo, "Turbo (137b)");
            EditorGUILayout.EndHorizontal();

            string tierSummary;
            switch (_tier)
            {
                case UniTextTier.Lite:
                    tierSummary = L(
                        "基础 33b · 每包 2 汉字 / 3 英文 · 极低参数占用，适合参数预算非常紧张的模型",
                        "Base 33b · 2 CJK / 3 Latin per packet · Ultra-low cost for very tight budgets"
                    );
                    break;
                case UniTextTier.Compact:
                    tierSummary = L(
                        "基础 45b · 每包 3 汉字 / 4 英文 · 低参数占用，兼顾预算紧张与刷新速度",
                        "Base 45b · 3 CJK / 4 Latin per packet · Low cost with faster refresh than Lite"
                    );
                    break;
                case UniTextTier.Balanced:
                    tierSummary = L(
                        "基础 57b · 每包 4 汉字 / 6 英文 · 均衡省参，比 Standard 节省 16b 且流畅度接近",
                        "Base 57b · 4 CJK / 6 Latin per packet · Balanced cost, saves 16b vs Standard"
                    );
                    break;
                case UniTextTier.Turbo:
                    tierSummary = L(
                        "基础 137b · 每包 10 汉字 / 16 英文 · 极速刷新最低延迟，适合参数余量充足的模型",
                        "Base 137b · 10 CJK / 16 Latin per packet · Fastest refresh for avatars with ample budget"
                    );
                    break;
                default:
                    tierSummary = L(
                        "基础 73b · 每包 5 汉字 / 8 英文 · 推荐默认档位，刷新流畅且参数占用适中",
                        "Base 73b · 5 CJK / 8 Latin per packet · Recommended default with smooth refresh"
                    );
                    break;
            }

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField(tierSummary, _subtitleStyle);
            EditorGUILayout.EndVertical();
        }

        private void DrawTierButton(UniTextTier tier, string label)
        {
            bool isSelected = _tier == tier;
            Color prevBg = GUI.backgroundColor;
            if (isSelected)
            {
                GUI.backgroundColor = new Color(0.45f, 0.78f, 1.0f, 1.0f);
            }

            GUIStyle style = isSelected ? _selectedButtonStyle : GUI.skin.button;
            if (GUILayout.Button(label, style, GUILayout.Height(24f), GUILayout.ExpandWidth(true)))
            {
                if (_tier != tier)
                {
                    _tier = tier;
                    ClearStatusMessage();
                }
            }

            GUI.backgroundColor = prevBg;
        }

        private void DrawBudgetSection(
            VRCAvatarDescriptor descriptor,
            UniTextParameterChecker.AvatarBudgetSummary budgetSummary,
            int addedBits,
            int projectedTotal)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Label(L("3. 同步参数预算", "3. Synced Parameter Budget"), _sectionHeaderStyle);
            EditorGUILayout.Space(4f);

            int maxBits = UniTextParameterChecker.MaxSyncedBits;
            int baseBits = descriptor != null ? budgetSummary.BaseUsedBits : 0;
            int remaining = Mathf.Max(0, maxBits - projectedTotal);

            Color baseColor = new Color(0.28f, 0.46f, 0.70f, 1f);
            Color uniTextColor;
            if (projectedTotal > maxBits)
            {
                uniTextColor = new Color(0.82f, 0.28f, 0.28f, 1f);
            }
            else if (projectedTotal > 200)
            {
                uniTextColor = new Color(0.82f, 0.60f, 0.16f, 1f);
            }
            else
            {
                uniTextColor = new Color(0.22f, 0.66f, 0.38f, 1f);
            }

            Rect barRect = EditorGUILayout.GetControlRect(false, 20f);
            DrawSegmentedBudgetBar(barRect, baseBits, projectedTotal, addedBits, maxBits, descriptor != null, baseColor, uniTextColor);

            EditorGUILayout.Space(3f);
            if (descriptor != null)
            {
                EditorGUILayout.BeginHorizontal();
                DrawColorSwatch(baseColor);
                GUILayout.Label(L($"原模型 {baseBits}b", $"Base {baseBits}b"), EditorStyles.miniLabel, GUILayout.ExpandWidth(false));
                GUILayout.Label("+", EditorStyles.miniLabel, GUILayout.ExpandWidth(false));
                DrawColorSwatch(uniTextColor);
                GUILayout.Label($"UniText {addedBits}b", EditorStyles.miniLabel, GUILayout.ExpandWidth(false));
                GUILayout.Label(
                    L($"= 预计总计 {projectedTotal}b（剩余 {remaining}b）", $"= Projected {projectedTotal}b (Left {remaining}b)"),
                    EditorStyles.miniLabel,
                    GUILayout.ExpandWidth(true)
                );
                EditorGUILayout.EndHorizontal();

                if (projectedTotal > maxBits)
                {
                    int overBits = projectedTotal - maxBits;
                    int currentBits = UniTextParameterChecker.GetPayloadBits(_tier);

                    UniTextTier? suggestedTier = null;
                    UniTextTier[] fallbackOrder =
                    {
                        UniTextTier.Standard,
                        UniTextTier.Balanced,
                        UniTextTier.Compact,
                        UniTextTier.Lite
                    };
                    foreach (UniTextTier candidate in fallbackOrder)
                    {
                        if (UniTextParameterChecker.GetPayloadBits(candidate) >= currentBits)
                        {
                            continue;
                        }
                        UniTextParameterChecker.ProjectWithUniText(budgetSummary, candidate, _syncWorldDrop, out _, out int candidateTotal, _syncWideRows, _displayRows > 6, _allowRuntimeResize);
                        if (candidateTotal <= maxBits)
                        {
                            suggestedTier = candidate;
                            break;
                        }
                    }

                    UniTextParameterChecker.ProjectWithUniText(budgetSummary, UniTextTier.Lite, false, out _, out int liteLocalWd, false, false, false);

                    string suggestion;
                    if (suggestedTier.HasValue)
                    {
                        GetTierMetadata(suggestedTier.Value, out _, out _, out string suggestedLabel);
                        suggestion = L(
                            $"建议切换到 {suggestedLabel} 档位即可适配当前 Avatar。",
                            $"Suggestion: switch to {suggestedLabel} tier to fit within budget."
                        );
                    }
                    else if (liteLocalWd <= maxBits)
                    {
                        suggestion = L(
                            "建议切换到 Lite (33b) 档位，并关闭“游戏内实时调宽高 (+1b)”、“同步世界固定 (+1b)”或“宽行模式 (+1b)”。",
                            "Suggestion: switch to Lite (33b) tier and disable 'Allow In-Game Live Resize (+1b)', 'Sync World Drop (+1b)', or 'Wide Rows (+1b)'."
                        );
                    }
                    else
                    {
                        suggestion = L(
                            "请清理未使用的 Expression 参数，或用 VRCFury Parameter Compressor / Modular Avatar 压缩 Avatar 自身的静态菜单开关与滑块。",
                            "Consider freeing unused synced parameters, or compressing the Avatar's static menu toggles/sliders with VRCFury Parameter Compressor."
                        );
                    }

                    EditorGUILayout.HelpBox(
                        L(
                            $"预计超出 VRChat {maxBits}b 上限 {overBits}b。\n{suggestion}",
                            $"Projected usage exceeds VRChat's {maxBits}b limit by {overBits}b.\n{suggestion}"
                        ),
                        MessageType.Warning
                    );
                }
            }
            else
            {
                int baseTierBits = UniTextProp.GetBaseSyncedBits(_tier);
                int optBits = Mathf.Max(0, addedBits - baseTierBits);
                EditorGUILayout.LabelField(
                    L(
                        $"UniText 预计占用: {addedBits}b（基础 {baseTierBits}b + 选项 {optBits}b）· 拖入 Avatar 后显示完整预算",
                        $"UniText Est.: {addedBits}b (Base {baseTierBits}b + Options {optBits}b) · Drop an Avatar for full budget"
                    ),
                    _subtitleStyle
                );
            }

            EditorGUILayout.EndVertical();
        }

        private static void DrawColorSwatch(Color color)
        {
            Rect rect = GUILayoutUtility.GetRect(9f, 14f, GUILayout.Width(9f), GUILayout.Height(14f));
            EditorGUI.DrawRect(new Rect(rect.x, rect.y + 3f, 8f, 8f), color);
        }

        private void DrawSegmentedBudgetBar(
            Rect barRect,
            int baseBits,
            int projectedTotal,
            int addedBits,
            int maxBits,
            bool hasAvatar,
            Color baseColor,
            Color uniTextColor)
        {
            Color trackBg = EditorGUIUtility.isProSkin
                ? new Color(0.14f, 0.14f, 0.14f, 1f)
                : new Color(0.42f, 0.42f, 0.42f, 1f);
            Color trackBorder = EditorGUIUtility.isProSkin
                ? new Color(0.32f, 0.32f, 0.32f, 1f)
                : new Color(0.28f, 0.28f, 0.28f, 1f);

            EditorGUI.DrawRect(barRect, trackBg);

            Rect inner = new Rect(
                barRect.x + 1f,
                barRect.y + 1f,
                Mathf.Max(0f, barRect.width - 2f),
                Mathf.Max(0f, barRect.height - 2f)
            );

            float baseWidth = 0f;
            if (hasAvatar && baseBits > 0)
            {
                baseWidth = inner.width * Mathf.Clamp01((float)baseBits / maxBits);
                EditorGUI.DrawRect(new Rect(inner.x, inner.y, baseWidth, inner.height), baseColor);
            }

            float totalWidth = inner.width * Mathf.Clamp01((float)projectedTotal / maxBits);
            float addedWidth = Mathf.Max(0f, totalWidth - baseWidth);
            if (addedWidth > 0f)
            {
                EditorGUI.DrawRect(new Rect(inner.x + baseWidth, inner.y, addedWidth, inner.height), uniTextColor);
            }

            DrawRectOutline(barRect, trackBorder, 1f);

            string barText = hasAvatar ? $"{projectedTotal} / {maxBits} b" : $"UniText: {addedBits} / {maxBits} b";
            GUI.Label(barRect, barText, _barLabelStyle);
        }

        private void DrawConfigurationSection()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Label(L("4. 安装选项", "4. Options"), _sectionHeaderStyle);
            EditorGUILayout.Space(4f);

            _syncWorldDrop = EditorGUILayout.ToggleLeft(
                new GUIContent(
                    L("同步世界固定 (World Drop, +1b)", "Sync World Drop (+1b)"),
                    L("开启后，其他玩家也能看到字幕板停留在世界空间中。", "Remote players also see the subtitle board stay frozen in world space.")
                ),
                _syncWorldDrop
            );

            _syncWideRows = EditorGUILayout.ToggleLeft(
                new GUIContent(
                    L("宽行模式（比例字体 64 格/行，+1b）", "Wide Rows (64 proportional cells/row, +1b)"),
                    L("同步 UniText_Bank 列分区位，使每行最多容纳 64 个比例宽度字符（英文每行约多一倍）。关闭后每行仍为 32 格，但依旧按比例宽度排版。",
                      "Syncs the UniText_Bank column-bank bit so each row holds up to 64 proportional cells (about 2x more English per row). When off, rows keep 32 cells but still render proportionally.")
                ),
                _syncWideRows
            );

            _installMenu = EditorGUILayout.ToggleLeft(
                new GUIContent(
                    L("安装表情菜单子菜单 (UniText)", "Install Expressions Submenu (UniText)"),
                    L("通过 Modular Avatar Menu Installer 自动添加显示/固定/朝向/清屏控制，并将缩放/透明度/宽度/高度归类到 Appearance 子菜单。", "Adds Show Board, World Drop, Billboard, Clear Screen, and an Appearance submenu (Scale, Opacity, Width, Height).")
                ),
                _installMenu
            );

            _stripOnAndroid = EditorGUILayout.ToggleLeft(
                new GUIContent(
                    L("Quest / Android 构建时自动无损剥离", "Auto-Strip on Quest / Android Builds"),
                    L("Android 平台不支持 CustomRenderTexture，开启后可在构建 Quest 版时自动剥离而保留 PC 设置。", "Quest/Android does not support CustomRenderTexture; safely strips UniText only during Android builds.")
                ),
                _stripOnAndroid
            );

            EditorGUILayout.EndVertical();
        }

        private void DrawViewportSection(VRCAvatarDescriptor descriptor)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Label(L("5. 视窗与摆放", "5. Viewport & Placement"), _sectionHeaderStyle);
            EditorGUILayout.Space(4f);

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(L("摆放位置", "Placement"), EditorStyles.miniLabel, GUILayout.Width(_isChinese ? 54f : 62f));
            DrawPlacementPresetButton(descriptor, UniTextPlacementPreset.RightSide, L("右肩侧", "Right"));
            DrawPlacementPresetButton(descriptor, UniTextPlacementPreset.LeftSide, L("左肩侧", "Left"));
            DrawPlacementPresetButton(descriptor, UniTextPlacementPreset.AboveHead, L("头顶", "Head"));
            DrawPlacementPresetButton(descriptor, UniTextPlacementPreset.Custom, L("手动", "Manual"));
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(3f);
            EditorGUILayout.BeginHorizontal();
            DrawViewportPresetButton(L("标准 0.84×0.26 · 6行", "Standard 0.84×0.26 · 6r"), 0.84f, 0.2625f, 6);
            DrawViewportPresetButton(L("长句 0.84×0.35 · 8行", "Long 0.84×0.35 · 8r"), 0.84f, 0.35f, 8);
            DrawViewportPresetButton(L("紧凑 0.56×0.35 · 8行", "Compact 0.56×0.35 · 8r"), 0.56f, 0.35f, 8);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4f);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.BeginVertical(GUILayout.ExpandWidth(true));
            float prevLabelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = _isChinese ? 84f : 86f;
            _viewportWidth = EditorGUILayout.Slider(L("视窗宽度 (m)", "Width (m)"), _viewportWidth, UniTextProp.MinViewportWidth, UniTextProp.MaxViewportWidth);
            _viewportHeight = EditorGUILayout.Slider(L("视窗高度 (m)", "Height (m)"), _viewportHeight, UniTextProp.MinViewportHeight, UniTextProp.MaxViewportHeight);
            _displayRows = EditorGUILayout.IntSlider(
                new GUIContent(
                    L("显示行数", "Display Rows"),
                    L("范围 4–12 行；大于 6 行时自动同步 UniText_RowPage (+1b)。", "Range 4–12 rows; > 6 rows automatically syncs UniText_RowPage (+1b).")
                ),
                _displayRows,
                UniTextProp.MinDisplayRows,
                UniTextProp.MaxDisplayRows
            );
            EditorGUIUtility.labelWidth = prevLabelWidth;
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(4f);
            DrawViewportPreview();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(2f);
            string resizeLabel = _displayRows > 6
                ? L("允许游戏内实时调宽高与重排（已与 >6 行共享，+0b）", "Allow In-Game Live Resize & Reflow (shared with >6 rows, +0b)")
                : L("允许游戏内实时调宽高与重排（+1b）", "Allow In-Game Live Resize & Reflow (+1b)");
            string resizeTooltip = L(
                "UniText_Width / UniText_Height 仅本地占用 (0b)，由发送端通过 CMD_SET_GEOMETRY 广播重排后的行数与行宽。仅在显示行数 ≤ 6 时需额外启用 UniText_RowPage (+1b)。",
                "UniText_Width / UniText_Height stay local-only (0b); the sender broadcasts reflowed rows and width via CMD_SET_GEOMETRY. Only adds UniText_RowPage (+1b) when Display Rows ≤ 6."
            );
            _allowRuntimeResize = EditorGUILayout.ToggleLeft(new GUIContent(resizeLabel, resizeTooltip), _allowRuntimeResize);

            int effRowPx = UniTextProp.ComputeRowPx(_viewportWidth, _viewportHeight, _displayRows, _autoFitRowPx, _rowPx, _syncWideRows);
            int cjkPerRow = effRowPx / 12;
            int totalCjk = cjkPerRow * _displayRows;
            string manualSuffix = _autoFitRowPx ? string.Empty : L(" · 手动行宽", " · manual width");
            EditorGUILayout.LabelField(
                L(
                    $"预计容量: 每行约 {cjkPerRow} 汉字 (~{effRowPx / 6} 英文) · 整屏约 {totalCjk} 汉字 ({effRowPx}px × {_displayRows}行){manualSuffix}",
                    $"Capacity: ~{cjkPerRow} CJK / row (~{effRowPx / 6} EN) · ~{totalCjk} CJK total ({effRowPx}px × {_displayRows}r){manualSuffix}"
                ),
                _subtitleStyle
            );

            if (totalCjk < 140)
            {
                EditorGUILayout.HelpBox(
                    L("当前整屏总字数偏少，超长句或双语对照可能显示不完，建议增加显示行数或放宽视窗宽度。",
                      "Total viewport capacity is low; long or bilingual subtitles may overflow. Increase Display Rows or Width."),
                    MessageType.Warning
                );
            }
            EditorGUILayout.EndVertical();
        }

        private void DrawPlacementPresetButton(VRCAvatarDescriptor descriptor, UniTextPlacementPreset preset, string label)
        {
            bool isMatch = _placementPreset == preset;
            Color prevBg = GUI.backgroundColor;
            if (isMatch)
            {
                GUI.backgroundColor = new Color(0.45f, 0.78f, 1.0f, 1.0f);
            }

            GUIStyle style = isMatch ? _selectedMiniButtonStyle : EditorStyles.miniButton;
            if (GUILayout.Button(label, style))
            {
                _placementPreset = preset;
                if (preset == UniTextPlacementPreset.Custom)
                {
                    _autoPositionToBone = false;
                }
                else
                {
                    _boardAnchor = UniTextProp.GetPresetAnchor(preset);
                    _autoPositionToBone = true;
                    _boneOffset = UniTextPlacementUtility.ComputeAdaptiveBoneOffset(
                        descriptor != null ? descriptor.gameObject : null,
                        preset,
                        _followBone,
                        UniTextPlacementUtility.GetDefaultPresetOffset(preset)
                    );
                }
                ClearStatusMessage();
                GUIUtility.keyboardControl = 0;
            }

            GUI.backgroundColor = prevBg;
        }

        private void DrawViewportPresetButton(string label, float width, float height, int rows)
        {
            bool isMatch = Mathf.Abs(_viewportWidth - width) < 0.005f
                && Mathf.Abs(_viewportHeight - height) < 0.005f
                && _displayRows == rows;

            Color prevBg = GUI.backgroundColor;
            if (isMatch)
            {
                GUI.backgroundColor = new Color(0.45f, 0.78f, 1.0f, 1.0f);
            }

            GUIStyle style = isMatch ? _selectedMiniButtonStyle : EditorStyles.miniButton;
            if (GUILayout.Button(label, style))
            {
                if (!isMatch)
                {
                    _viewportWidth = width;
                    _viewportHeight = height;
                    _displayRows = rows;
                    ClearStatusMessage();
                }
                GUIUtility.keyboardControl = 0;
            }

            GUI.backgroundColor = prevBg;
        }

        private void DrawViewportPreview()
        {
            EditorGUILayout.BeginVertical(GUILayout.Width(86f));
            Rect canvas = GUILayoutUtility.GetRect(84f, 46f, GUILayout.Width(84f), GUILayout.Height(46f));
            EditorGUI.DrawRect(
                canvas,
                EditorGUIUtility.isProSkin ? new Color(0.11f, 0.12f, 0.14f, 1f) : new Color(0.22f, 0.24f, 0.27f, 1f)
            );
            DrawRectOutline(canvas, new Color(0.38f, 0.38f, 0.40f, 0.8f), 1f);

            const float pad = 5f;
            float availW = canvas.width - pad * 2f;
            float availH = canvas.height - pad * 2f;
            float aspect = Mathf.Max(0.1f, _viewportWidth / Mathf.Max(0.01f, _viewportHeight));

            float boardW;
            float boardH;
            if (aspect >= availW / availH)
            {
                boardW = availW;
                boardH = availW / aspect;
            }
            else
            {
                boardH = availH;
                boardW = availH * aspect;
            }

            boardW = Mathf.Max(12f, boardW);
            boardH = Mathf.Max(8f, boardH);

            Rect boardRect = new Rect(
                canvas.x + (canvas.width - boardW) * 0.5f,
                canvas.y + (canvas.height - boardH) * 0.5f,
                boardW,
                boardH
            );

            EditorGUI.DrawRect(boardRect, new Color(0.18f, 0.36f, 0.52f, 0.45f));
            DrawRectOutline(boardRect, new Color(0.42f, 0.76f, 1.0f, 0.90f), 1f);

            Color rowLineColor = new Color(0.75f, 0.90f, 1.0f, 0.28f);
            for (int r = 1; r < _displayRows; r++)
            {
                float y = boardRect.y + boardRect.height * ((float)r / _displayRows);
                EditorGUI.DrawRect(new Rect(boardRect.x + 2f, y, Mathf.Max(0f, boardRect.width - 4f), 1f), rowLineColor);
            }

            GUILayout.Label($"{aspect:F2}:1", _previewCaptionStyle, GUILayout.Width(84f), GUILayout.Height(12f));
            EditorGUILayout.EndVertical();
        }

        private void DrawAdvancedSection(VRCAvatarDescriptor descriptor)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            _showAdvanced = EditorGUILayout.Foldout(
                _showAdvanced,
                L("高级选项（骨骼绑定 / 偏移 / 菜单 / 行宽）", "Advanced Options (Bone Proxy / Offset / Menu / Row Width)"),
                true
            );

            if (_showAdvanced)
            {
                EditorGUILayout.Space(4f);
                float prevLabelWidth = EditorGUIUtility.labelWidth;
                EditorGUIUtility.labelWidth = _isChinese ? 96f : 118f;

                _boardAnchor = (UniTextBoardAnchor)EditorGUILayout.EnumPopup(
                    L("面板锚点", "Board Anchor"),
                    _boardAnchor
                );

                _useBoneProxy = EditorGUILayout.ToggleLeft(
                    L("启用骨骼绑定 (MA Bone Proxy)", "Use MA Bone Proxy"),
                    _useBoneProxy
                );

                EditorGUI.BeginDisabledGroup(!_useBoneProxy);
                EditorGUI.BeginChangeCheck();
                _followBone = UniTextPlacementUtility.DrawFollowBonePopup(
                    new GUIContent(
                        L("跟随骨骼", "Follow Bone"),
                        L("未开启世界固定时，字幕板跟随的 Humanoid 主骨骼。", "Humanoid bone that SideFollowAnchor follows when not in World Drop mode.")
                    ),
                    _followBone,
                    _isChinese
                );
                if (EditorGUI.EndChangeCheck() && _autoPositionToBone && _placementPreset != UniTextPlacementPreset.Custom && descriptor != null)
                {
                    _boneOffset = UniTextPlacementUtility.ComputeAdaptiveBoneOffset(
                        descriptor.gameObject,
                        _placementPreset,
                        _followBone,
                        UniTextProp.DefaultBoneOffset
                    );
                }
                _autoPositionToBone = EditorGUILayout.ToggleLeft(
                    L("自动对齐骨骼偏移", "Auto-Align to Bone"),
                    _autoPositionToBone
                );

                EditorGUI.BeginDisabledGroup(!_autoPositionToBone);
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label(L("相对偏移 (m)", "Bone Offset (m)"));
                GUILayout.FlexibleSpace();
                EditorGUI.BeginDisabledGroup(descriptor == null);
                if (GUILayout.Button(L("自动适配", "Auto-Fit"), EditorStyles.miniButton, GUILayout.Width(64f)))
                {
                    UniTextPlacementPreset fitPreset = _placementPreset == UniTextPlacementPreset.Custom
                        ? UniTextPlacementPreset.RightSide
                        : _placementPreset;
                    _boneOffset = UniTextPlacementUtility.ComputeAdaptiveBoneOffset(
                        descriptor.gameObject,
                        fitPreset,
                        _followBone,
                        UniTextProp.DefaultBoneOffset
                    );
                    ClearStatusMessage();
                    GUIUtility.keyboardControl = 0;
                }
                EditorGUI.EndDisabledGroup();
                if (GUILayout.Button(L("重置", "Reset"), EditorStyles.miniButton, GUILayout.Width(52f)))
                {
                    _boneOffset = UniTextProp.DefaultBoneOffset;
                    ClearStatusMessage();
                    GUIUtility.keyboardControl = 0;
                }
                EditorGUILayout.EndHorizontal();
                _boneOffset = EditorGUILayout.Vector3Field(GUIContent.none, _boneOffset);
                EditorGUI.EndDisabledGroup();
                EditorGUI.EndDisabledGroup();

                EditorGUILayout.Space(4f);
                EditorGUI.BeginDisabledGroup(!_installMenu);
                _menuName = EditorGUILayout.TextField(L("菜单名称", "Menu Name"), _menuName);
                _installTargetMenu = (VRCExpressionsMenu)EditorGUILayout.ObjectField(
                    L("父级菜单 (可选)", "Target Parent Menu"),
                    _installTargetMenu,
                    typeof(VRCExpressionsMenu),
                    false
                );
                EditorGUI.EndDisabledGroup();

                EditorGUILayout.Space(4f);
                _autoFitRowPx = EditorGUILayout.ToggleLeft(
                    L("自动按视窗比例匹配行像素宽", "Auto-Fit Row Pixel Width to Aspect"),
                    _autoFitRowPx
                );
                if (!_autoFitRowPx)
                {
                    int maxRowPx = _syncWideRows ? UniTextProp.MaxRowPx : UniTextProp.DefaultRowPx;
                    _rowPx = EditorGUILayout.IntSlider(L("行像素宽 (px)", "Row Width (px)"), _rowPx, UniTextProp.MinRowPx, maxRowPx);
                    _rowPx = Mathf.Clamp(Mathf.RoundToInt(_rowPx / 8f) * 8, UniTextProp.MinRowPx, maxRowPx);
                }

                EditorGUIUtility.labelWidth = prevLabelWidth;
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawDiagnosticsSection(VRCAvatarDescriptor descriptor, GameObject tierPrefab)
        {
            if (tierPrefab == null)
            {
                EditorGUILayout.Space(4f);
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.HelpBox(
                    L(
                        $"未找到所选档位 ({_tier}) 的 Prefab 资源，请点击下方按钮验证并重建包资源。",
                        $"Could not locate the Prefab for tier '{_tier}'. Click below to verify and rebuild package assets."
                    ),
                    MessageType.Error
                );
                if (GUILayout.Button(L("验证并刷新 UniText 包资源", "Verify & Refresh Package Assets"), GUILayout.Height(24f)))
                {
                    UniTextAssetBuilder.BuildAll();
                    Repaint();
                }
                EditorGUILayout.EndVertical();
            }

            if (descriptor != null && _useBoneProxy)
            {
                Animator animator = descriptor.GetComponent<Animator>();
                if (animator == null || !animator.isHuman)
                {
                    EditorGUILayout.HelpBox(
                        L(
                            "该 Avatar 未检测到 Humanoid Animator，构建时 SideFollowAnchor 将停留在 UniText 根节点上。",
                            "Avatar does not have a Humanoid Animator; SideFollowAnchor will remain parented to the UniText root."
                        ),
                        MessageType.Warning
                    );
                }
                else if (_followBone == HumanBodyBones.LastBone || animator.GetBoneTransform(_followBone) == null)
                {
                    Transform fallback = UniTextNdmfPlugin.ResolveHumanoidBone(descriptor.gameObject, _followBone);
                    string fallbackName = fallback != null ? fallback.name : L("根节点", "prop root");
                    string boneLabel = UniTextPlacementUtility.GetFollowBoneDisplayName(_followBone, _isChinese);
                    EditorGUILayout.HelpBox(
                        L(
                            $"Avatar 未映射骨骼 \"{boneLabel}\"，构建时将自动回退绑定到 \"{fallbackName}\"。",
                            $"Bone '{boneLabel}' is not mapped on this Avatar; build will fall back to '{fallbackName}'."
                        ),
                        MessageType.Info
                    );
                }
            }

            if (EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android && _stripOnAndroid)
            {
                EditorGUILayout.HelpBox(
                    L(
                        "当前 Unity 构建目标为 Android（Quest）。已启用自动剥离：安装到场景可保留 PC 配置，但构建 Android 版时会自动忽略。",
                        "Active build target is Android (Quest). Auto-strip is enabled so Android builds remain clean while keeping your PC setup."
                    ),
                    MessageType.Info
                );
            }
        }

        private void DrawStatusBanner()
        {
            if (string.IsNullOrEmpty(_statusMessage) || _statusMessageType == MessageType.None)
            {
                return;
            }

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.HelpBox(_statusMessage, _statusMessageType);
            if (GUILayout.Button("×", EditorStyles.miniButton, GUILayout.Width(20f), GUILayout.Height(20f)))
            {
                ClearStatusMessage();
                GUIUtility.keyboardControl = 0;
            }
            EditorGUILayout.EndHorizontal();
        }

        private void DrawFooterActions(
            VRCAvatarDescriptor descriptor,
            UniTextParameterChecker.AvatarBudgetSummary budgetSummary,
            GameObject tierPrefab,
            int addedBits,
            int projectedTotal)
        {
            UniTextProp[] existingProps = descriptor != null ? budgetSummary.AttachedProps : null;
            bool hasExisting = existingProps != null && existingProps.Length > 0;
            bool canUpdateInPlace = hasExisting
                && existingProps.Length == 1
                && existingProps[0] != null
                && existingProps[0].tier == _tier
                && HasValidPropHierarchy(existingProps[0].gameObject)
                && existingProps[0].gameObject != descriptor.gameObject;
            bool hasPendingChanges = canUpdateInPlace && HasPendingChanges(existingProps[0]);

            string tierName = GetTierShortName(_tier);
            string installButtonText;
            if (!hasExisting)
            {
                installButtonText = L($"安装 UniText · {tierName} ({addedBits}b)", $"Install UniText · {tierName} ({addedBits}b)");
            }
            else if (canUpdateInPlace)
            {
                installButtonText = hasPendingChanges
                    ? L($"更新 UniText 配置 · {tierName} ({addedBits}b)", $"Update UniText · {tierName} ({addedBits}b)")
                    : L($"重新应用配置 · {tierName} ({addedBits}b)", $"Re-Apply Settings · {tierName} ({addedBits}b)");
            }
            else
            {
                installButtonText = L($"替换为 {tierName} ({addedBits}b)", $"Replace with {tierName} ({addedBits}b)");
            }

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            if (hasPendingChanges)
            {
                int installedBits = existingProps[0].SyncedBitCost;
                string dirtyText = installedBits != addedBits
                    ? L($"● 有未应用的更改 ({installedBits}b → {addedBits}b)", $"● Unapplied changes ({installedBits}b → {addedBits}b)")
                    : L("● 有未应用的更改", "● Unapplied changes");

                EditorGUILayout.BeginHorizontal();
                GUILayout.Label(dirtyText, _dirtyLabelStyle);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(L("还原", "Revert"), EditorStyles.miniButton, GUILayout.Width(52f)))
                {
                    SyncSettingsFromProp(existingProps[0]);
                    ClearStatusMessage();
                    GUIUtility.keyboardControl = 0;
                }
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.Space(2f);
            }

            EditorGUILayout.BeginHorizontal();

            bool canRunPrimary = descriptor != null && tierPrefab != null;
            bool highlightPrimary = canRunPrimary && (!canUpdateInPlace || hasPendingChanges);
            Color prevBg = GUI.backgroundColor;
            if (highlightPrimary)
            {
                GUI.backgroundColor = new Color(0.40f, 0.76f, 1.0f, 1.0f);
            }

            GUI.enabled = canRunPrimary;
            if (GUILayout.Button(installButtonText, _primaryButtonStyle, GUILayout.Height(34f)))
            {
                InstallOrUpdate(descriptor, budgetSummary, tierPrefab, projectedTotal);
            }
            GUI.enabled = true;
            GUI.backgroundColor = prevBg;

            if (hasExisting)
            {
                GUILayout.Space(6f);
                GUI.backgroundColor = new Color(0.95f, 0.48f, 0.48f, 1.0f);
                if (GUILayout.Button(L("卸载", "Uninstall"), GUILayout.Width(84f), GUILayout.Height(34f)))
                {
                    UninstallAll(descriptor, existingProps);
                }
                GUI.backgroundColor = prevBg;
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

        private bool HasPendingChanges(UniTextProp prop)
        {
            if (prop == null)
            {
                return false;
            }

            string currentMenuName = string.IsNullOrWhiteSpace(_menuName) ? "UniText" : _menuName.Trim();
            string propMenuName = string.IsNullOrWhiteSpace(prop.menuName) ? "UniText" : prop.menuName.Trim();

            int clampedRowPx = Mathf.Clamp(Mathf.RoundToInt(_rowPx / 8f) * 8, UniTextProp.MinRowPx, UniTextProp.MaxRowPx);
            int propRowPx = Mathf.Clamp(Mathf.RoundToInt(prop.rowPx / 8f) * 8, UniTextProp.MinRowPx, UniTextProp.MaxRowPx);

            return prop.tier != _tier
                || prop.placementPreset != _placementPreset
                || prop.boardAnchor != _boardAnchor
                || prop.useBoneProxy != _useBoneProxy
                || prop.followBone != _followBone
                || prop.autoPositionToBone != _autoPositionToBone
                || Vector3.SqrMagnitude(prop.boneOffset - _boneOffset) > 1e-6f
                || prop.syncWorldDrop != _syncWorldDrop
                || prop.syncWideRows != _syncWideRows
                || prop.installMenu != _installMenu
                || !string.Equals(propMenuName, currentMenuName, StringComparison.Ordinal)
                || prop.installTargetMenu != _installTargetMenu
                || prop.stripOnAndroid != _stripOnAndroid
                || Mathf.Abs(Mathf.Clamp(prop.viewportWidth, UniTextProp.MinViewportWidth, UniTextProp.MaxViewportWidth) - _viewportWidth) > 0.0005f
                || Mathf.Abs(Mathf.Clamp(prop.viewportHeight, UniTextProp.MinViewportHeight, UniTextProp.MaxViewportHeight) - _viewportHeight) > 0.0005f
                || prop.EffectiveDisplayRows != _displayRows
                || prop.autoFitRowPx != _autoFitRowPx
                || (!_autoFitRowPx && propRowPx != clampedRowPx)
                || prop.allowRuntimeResize != _allowRuntimeResize;
        }

        private static string GetTierShortName(UniTextTier tier)
        {
            switch (tier)
            {
                case UniTextTier.Lite: return "Lite";
                case UniTextTier.Compact: return "Compact";
                case UniTextTier.Balanced: return "Balanced";
                case UniTextTier.Turbo: return "Turbo";
                default: return "Standard";
            }
        }

        private void InstallOrUpdate(
            VRCAvatarDescriptor descriptor,
            UniTextParameterChecker.AvatarBudgetSummary budgetSummary,
            GameObject tierPrefab,
            int projectedTotal)
        {
            if (descriptor == null || tierPrefab == null)
            {
                return;
            }

            if (projectedTotal > UniTextParameterChecker.MaxSyncedBits)
            {
                int over = projectedTotal - UniTextParameterChecker.MaxSyncedBits;
                bool proceed = EditorUtility.DisplayDialog(
                    L("参数预算超出上限", "Parameter Budget Exceeded"),
                    L(
                        $"安装后预计同步参数为 {projectedTotal}/{UniTextParameterChecker.MaxSyncedBits}b（超出 {over}b），VRChat 可能会拒绝上传。\n\n是否仍要继续安装？",
                        $"Projected synced parameter usage is {projectedTotal}/{UniTextParameterChecker.MaxSyncedBits}b ({over}b over limit), which may block VRChat avatar uploads.\n\nInstall anyway?"
                    ),
                    L("仍然安装", "Install Anyway"),
                    L("取消", "Cancel")
                );
                if (!proceed)
                {
                    return;
                }
            }

            UniTextProp[] existingProps = budgetSummary.AttachedProps;
            bool canUpdateInPlace = existingProps != null
                && existingProps.Length == 1
                && existingProps[0] != null
                && existingProps[0].tier == _tier
                && HasValidPropHierarchy(existingProps[0].gameObject)
                && existingProps[0].gameObject != descriptor.gameObject;

            if (!canUpdateInPlace && !TryValidateRemovableProps(descriptor, existingProps, out string removeError))
            {
                EditorUtility.DisplayDialog(L("无法替换现有 UniText", "Cannot Replace Existing UniText"), removeError, "OK");
                _statusMessage = removeError;
                _statusMessageType = MessageType.Error;
                return;
            }

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName(canUpdateInPlace ? "Update UniText on Avatar" : "Install UniText to Avatar");
            int undoGroup = Undo.GetCurrentGroup();

            try
            {
                GameObject resultGo;
                if (canUpdateInPlace)
                {
                    UniTextProp existingProp = existingProps[0];
                    ApplyPropSettings(existingProp, _tier);
                    AlignEditModePreview(descriptor, existingProp.gameObject);
                    resultGo = existingProp.gameObject;

                    _statusMessage = L(
                        $"已更新 \"{descriptor.gameObject.name}\" 上的 {existingProp.TierName} 配置。",
                        $"Updated {existingProp.TierName} settings on '{descriptor.gameObject.name}'."
                    );
                    _statusMessageType = MessageType.Info;
                    Debug.Log($"[UniText] Updated '{resultGo.name}' ({existingProp.TierName}) on avatar '{descriptor.gameObject.name}'.", resultGo);
                }
                else
                {
                    RemoveExistingProps(descriptor, existingProps);

                    GameObject instance = PrefabUtility.InstantiatePrefab(tierPrefab, descriptor.transform) as GameObject;
                    if (instance == null)
                    {
                        _statusMessage = L("实例化 UniText Prefab 失败，请检查控制台日志。", "Failed to instantiate UniText Prefab. Check Console for details.");
                        _statusMessageType = MessageType.Error;
                        return;
                    }

                    Undo.RegisterCreatedObjectUndo(instance, "Install UniText Prop");
                    instance.transform.localPosition = Vector3.zero;
                    instance.transform.localRotation = Quaternion.identity;
                    instance.transform.localScale = Vector3.one;
                    PrefabUtility.RecordPrefabInstancePropertyModifications(instance.transform);

                    UniTextProp prop = instance.GetComponent<UniTextProp>();
                    if (prop != null)
                    {
                        ApplyPropSettings(prop, _tier);
                    }

                    AlignEditModePreview(descriptor, instance);
                    resultGo = instance;

                    string tierLabel = prop != null ? prop.TierName : _tier.ToString();
                    _statusMessage = L(
                        $"已成功将 UniText ({tierLabel}) 安装到 \"{descriptor.gameObject.name}\"！可直接进入 Play Mode 预览或通过 VRChat SDK 上传。",
                        $"Installed UniText ({tierLabel}) onto '{descriptor.gameObject.name}'! Enter Play Mode to preview or upload via VRChat SDK."
                    );
                    _statusMessageType = MessageType.Info;
                    Debug.Log($"[UniText] Installed '{resultGo.name}' ({tierLabel}) onto avatar '{descriptor.gameObject.name}'.", resultGo);
                }

                if (descriptor.gameObject.scene.IsValid())
                {
                    EditorSceneManager.MarkSceneDirty(descriptor.gameObject.scene);
                }

                Selection.activeGameObject = resultGo;
                EditorGUIUtility.PingObject(resultGo);
            }
            finally
            {
                Undo.CollapseUndoOperations(undoGroup);
            }
        }

        private void UninstallAll(VRCAvatarDescriptor descriptor, UniTextProp[] existingProps)
        {
            if (descriptor == null || existingProps == null || existingProps.Length == 0)
            {
                return;
            }

            if (!TryValidateRemovableProps(descriptor, existingProps, out string removeError))
            {
                EditorUtility.DisplayDialog(L("无法卸载 UniText", "Cannot Uninstall UniText"), removeError, "OK");
                _statusMessage = removeError;
                _statusMessageType = MessageType.Error;
                return;
            }

            bool confirmUninstall = EditorUtility.DisplayDialog(
                L("确认卸载 UniText", "Confirm Uninstall UniText"),
                L(
                    $"确定要从 \"{descriptor.gameObject.name}\" 移除已安装的 UniText 吗？\n\n移除后仍可通过 Ctrl+Z 撤销。",
                    $"Remove installed UniText from '{descriptor.gameObject.name}'?\n\nYou can still undo this action with Ctrl+Z."
                ),
                L("确认卸载", "Uninstall"),
                L("取消", "Cancel")
            );
            if (!confirmUninstall)
            {
                return;
            }

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Uninstall UniText from Avatar");
            int undoGroup = Undo.GetCurrentGroup();

            try
            {
                RemoveExistingProps(descriptor, existingProps);

                if (descriptor.gameObject.scene.IsValid())
                {
                    EditorSceneManager.MarkSceneDirty(descriptor.gameObject.scene);
                }

                _statusMessage = L(
                    $"已从 \"{descriptor.gameObject.name}\" 移除 UniText。",
                    $"Removed UniText from '{descriptor.gameObject.name}'."
                );
                _statusMessageType = MessageType.Info;
                Debug.Log($"[UniText] Uninstalled UniText from avatar '{descriptor.gameObject.name}'.", descriptor.gameObject);
            }
            finally
            {
                Undo.CollapseUndoOperations(undoGroup);
            }
        }

        private bool TryValidateRemovableProps(VRCAvatarDescriptor descriptor, UniTextProp[] props, out string errorReason)
        {
            errorReason = null;
            if (props == null)
            {
                return true;
            }

            foreach (UniTextProp prop in props)
            {
                if (prop == null)
                {
                    continue;
                }

                GameObject go = prop.gameObject;
                if (go == descriptor.gameObject)
                {
                    continue;
                }

                if (PrefabUtility.IsPartOfPrefabInstance(go)
                    && !PrefabUtility.IsAddedGameObjectOverride(go)
                    && !PrefabUtility.IsOutermostPrefabInstanceRoot(go))
                {
                    errorReason = L(
                        $"无法自动移除 \"{go.name}\"，因为它属于外部 Avatar Prefab 资产内部结构（非新增子物体）。请先对 Avatar 执行 Unpack Prefab 或在 Prefab 编辑模式下手动移除。",
                        $"Cannot automatically remove '{go.name}' because it is part of an outer Avatar Prefab instance (not an added override). Unpack the Avatar Prefab or remove it in Prefab Mode first."
                    );
                    return false;
                }
            }

            return true;
        }

        private static void RemoveExistingProps(VRCAvatarDescriptor descriptor, UniTextProp[] props)
        {
            if (props == null)
            {
                return;
            }

            foreach (UniTextProp prop in props)
            {
                if (prop == null)
                {
                    continue;
                }

                if (prop.gameObject == descriptor.gameObject)
                {
                    // Never destroy the Avatar root GameObject; remove only the component.
                    Undo.DestroyObjectImmediate(prop);
                }
                else
                {
                    Undo.DestroyObjectImmediate(prop.gameObject);
                }
            }
        }

        private void ApplyPropSettings(UniTextProp prop, UniTextTier tier)
        {
            var so = new SerializedObject(prop);
            so.Update();

            SetIntProp(so, "tier", (int)tier);
            SetIntProp(so, "placementPreset", (int)_placementPreset);
            SetIntProp(so, "boardAnchor", (int)_boardAnchor);
            SetBoolProp(so, "useBoneProxy", _useBoneProxy);
            SetIntProp(so, "followBone", (int)_followBone);
            SetBoolProp(so, "autoPositionToBone", _autoPositionToBone);
            SetVector3Prop(so, "boneOffset", _boneOffset);
            SetBoolProp(so, "syncWorldDrop", _syncWorldDrop);
            SetBoolProp(so, "syncWideRows", _syncWideRows);
            SetBoolProp(so, "installMenu", _installMenu);
            string sanitizedMenuName = string.IsNullOrWhiteSpace(_menuName) ? "UniText" : _menuName.Trim();
            SetStringProp(so, "menuName", sanitizedMenuName);
            SetObjectProp(so, "installTargetMenu", _installTargetMenu);
            SetBoolProp(so, "stripOnAndroid", _stripOnAndroid);
            SetFloatProp(so, "viewportWidth", Mathf.Clamp(_viewportWidth, UniTextProp.MinViewportWidth, UniTextProp.MaxViewportWidth));
            SetFloatProp(so, "viewportHeight", Mathf.Clamp(_viewportHeight, UniTextProp.MinViewportHeight, UniTextProp.MaxViewportHeight));
            SetIntProp(so, "displayRows", Mathf.Clamp(_displayRows, UniTextProp.MinDisplayRows, UniTextProp.MaxDisplayRows));
            SetBoolProp(so, "autoFitRowPx", _autoFitRowPx);
            SetIntProp(so, "rowPx", Mathf.Clamp(Mathf.RoundToInt(_rowPx / 8f) * 8, UniTextProp.MinRowPx, UniTextProp.MaxRowPx));
            SetBoolProp(so, "allowRuntimeResize", _allowRuntimeResize);

            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(prop);
        }

        private void AlignEditModePreview(VRCAvatarDescriptor descriptor, GameObject propRoot)
        {
            if (propRoot == null)
            {
                return;
            }

            UniTextProp prop = propRoot.GetComponent<UniTextProp>();
            UniTextPlacementUtility.ApplyTransformPreview(
                descriptor != null ? descriptor.gameObject : null,
                prop,
                recordUndo: true
            );
        }

        private static bool HasValidPropHierarchy(GameObject root)
        {
            if (root == null)
            {
                return false;
            }

            Transform anchor = root.transform.Find("SideFollowAnchor");
            Transform boardPivot = root.transform.Find("BoardPivot");
            Transform displayQuad = boardPivot != null ? boardPivot.Find("DisplayQuad") : null;
            Transform paramBridge = root.transform.Find("ParamBridge");
            Transform paramCamTf = paramBridge != null ? paramBridge.Find("ParamCamera") : null;
            Transform encoderQuadTf = paramBridge != null ? paramBridge.Find("EncoderQuad") : null;

            return anchor != null
                && boardPivot != null
                && displayQuad != null
                && paramCamTf != null
                && encoderQuadTf != null;
        }

        private static GameObject LoadTierPrefab(UniTextTier tier)
        {
            GetTierMetadata(tier, out string fileName, out string guid, out _);

            string packagePath = $"{UniTextAssetBuilder.PackageRoot}/Prefabs/{fileName}";
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(packagePath);
            if (prefab != null)
            {
                return prefab;
            }

            string guidPath = AssetDatabase.GUIDToAssetPath(guid);
            if (!string.IsNullOrEmpty(guidPath))
            {
                prefab = AssetDatabase.LoadAssetAtPath<GameObject>(guidPath);
                if (prefab != null)
                {
                    return prefab;
                }
            }

            string baseName = Path.GetFileNameWithoutExtension(fileName);
            string[] assetGuids = AssetDatabase.FindAssets($"{baseName} t:Prefab");
            foreach (string candidateGuid in assetGuids)
            {
                string path = AssetDatabase.GUIDToAssetPath(candidateGuid);
                GameObject candidate = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (candidate == null)
                {
                    continue;
                }

                UniTextProp prop = candidate.GetComponent<UniTextProp>();
                if (prop != null && prop.tier == tier)
                {
                    return candidate;
                }
            }

            return null;
        }

        private static void GetTierMetadata(UniTextTier tier, out string prefabFileName, out string prefabGuid, out string tierLabel)
        {
            switch (tier)
            {
                case UniTextTier.Lite:
                    prefabFileName = LitePrefabName;
                    prefabGuid = LitePrefabGuid;
                    tierLabel = "Lite (33b)";
                    break;
                case UniTextTier.Compact:
                    prefabFileName = CompactPrefabName;
                    prefabGuid = CompactPrefabGuid;
                    tierLabel = "Compact (45b)";
                    break;
                case UniTextTier.Balanced:
                    prefabFileName = BalancedPrefabName;
                    prefabGuid = BalancedPrefabGuid;
                    tierLabel = "Balanced (57b)";
                    break;
                case UniTextTier.Turbo:
                    prefabFileName = TurboPrefabName;
                    prefabGuid = TurboPrefabGuid;
                    tierLabel = "Turbo (137b)";
                    break;
                default:
                    prefabFileName = StandardPrefabName;
                    prefabGuid = StandardPrefabGuid;
                    tierLabel = "Standard (73b)";
                    break;
            }
        }

        private static GameObject TryGetDraggedGameObject(UnityEngine.Object[] objects)
        {
            if (objects == null)
            {
                return null;
            }

            foreach (UnityEngine.Object obj in objects)
            {
                if (obj is GameObject go)
                {
                    return go;
                }

                if (obj is Component comp && comp.gameObject != null)
                {
                    return comp.gameObject;
                }
            }

            return null;
        }

        private static string FormatPropSummaryList(UniTextProp[] props)
        {
            if (props == null || props.Length == 0)
            {
                return string.Empty;
            }

            string[] names = new string[props.Length];
            for (int i = 0; i < props.Length; i++)
            {
                UniTextProp p = props[i];
                names[i] = p != null ? $"{p.gameObject.name} ({p.TierName}, {p.SyncedBitCost}b)" : "null";
            }

            return string.Join(", ", names);
        }

        private static void DrawRectOutline(Rect rect, Color color, float thickness)
        {
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, thickness), color);
            EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - thickness, rect.width, thickness), color);
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, thickness, rect.height), color);
            EditorGUI.DrawRect(new Rect(rect.xMax - thickness, rect.y, thickness, rect.height), color);
        }

        private static void SetFloatProp(SerializedObject so, string name, float value)
        {
            SerializedProperty p = so.FindProperty(name);
            if (p != null)
            {
                p.floatValue = value;
            }
        }

        private static void SetIntProp(SerializedObject so, string name, int value)
        {
            SerializedProperty p = so.FindProperty(name);
            if (p != null)
            {
                p.intValue = value;
            }
        }

        private static void SetBoolProp(SerializedObject so, string name, bool value)
        {
            SerializedProperty p = so.FindProperty(name);
            if (p != null)
            {
                p.boolValue = value;
            }
        }

        private static void SetVector3Prop(SerializedObject so, string name, Vector3 value)
        {
            SerializedProperty p = so.FindProperty(name);
            if (p != null)
            {
                p.vector3Value = value;
            }
        }

        private static void SetStringProp(SerializedObject so, string name, string value)
        {
            SerializedProperty p = so.FindProperty(name);
            if (p != null)
            {
                p.stringValue = value;
            }
        }

        private static void SetObjectProp(SerializedObject so, string name, UnityEngine.Object value)
        {
            SerializedProperty p = so.FindProperty(name);
            if (p != null)
            {
                p.objectReferenceValue = value;
            }
        }
    }
}
#endif
