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
        private static readonly Vector3 DefaultBoneOffset = new Vector3(0.48f, 0.18f, 0.16f);

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

        private static readonly UniTextTier[] TierDisplayOrder =
        {
            UniTextTier.Lite,
            UniTextTier.Compact,
            UniTextTier.Balanced,
            UniTextTier.Standard,
            UniTextTier.Turbo
        };

        [SerializeField] private GameObject _rawTargetObject;
        [SerializeField] private UniTextTier _tier = UniTextTier.Standard;
        [SerializeField] private bool _useBoneProxy = true;
        [SerializeField] private HumanBodyBones _followBone = HumanBodyBones.Chest;
        [SerializeField] private bool _autoPositionToBone = true;
        [SerializeField] private Vector3 _boneOffset = DefaultBoneOffset;
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
                    fontSize = 15
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
            _statusMessage = null;
            _statusMessageType = MessageType.None;

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
        }

        private void SyncSettingsFromProp(UniTextProp prop)
        {
            _tier = prop.tier;
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

            EditorGUILayout.Space(8f);
            DrawTierSection();

            UniTextParameterChecker.AvatarBudgetSummary budgetSummary = default(UniTextParameterChecker.AvatarBudgetSummary);
            int projectedTotal = 0;
            if (descriptor != null)
            {
                EditorGUILayout.Space(8f);
                budgetSummary = UniTextParameterChecker.AnalyzeAvatar(descriptor);
                DrawBudgetSection(budgetSummary, out projectedTotal);
            }

            EditorGUILayout.Space(8f);
            DrawConfigurationSection();

            GameObject tierPrefab = LoadTierPrefab(_tier);
            DrawDiagnosticsSection(descriptor, tierPrefab);

            EditorGUILayout.Space(6f);
            EditorGUILayout.EndScrollView();

            DrawFooterActions(descriptor, budgetSummary, tierPrefab, projectedTotal);
        }

        private void DrawHeader()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.BeginVertical();
            GUILayout.Label(L("UniText Avatar 安装向导", "UniText Avatar Installer"), _titleStyle);
            GUILayout.Label(
                L(
                    "无损安装（NDMF + Modular Avatar）：拖入场景中的 Avatar 后点击安装即可自动应用。",
                    "Non-destructive setup (NDMF + Modular Avatar): drop a scene Avatar and click Install."
                ),
                _subtitleStyle
            );
            EditorGUILayout.EndVertical();

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
            Rect dropRect = GUILayoutUtility.GetRect(0f, 68f, GUILayout.ExpandWidth(true));
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
            else if (resolvedDescriptor != null)
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

            Rect mainLabelRect = new Rect(dropRect.x + 10f, dropRect.y + 10f, dropRect.width - 20f, 26f);
            Rect subLabelRect = new Rect(dropRect.x + 10f, dropRect.y + 36f, dropRect.width - 20f, 24f);

            if (isHovered && isDragging && draggedGo != null)
            {
                GUI.Label(mainLabelRect, L($"松开鼠标以选择: {draggedGo.name}", $"Drop to select: {draggedGo.name}"), _dropMainLabelStyle);
                GUI.Label(subLabelRect, L("将自动识别所属的 VRCAvatarDescriptor 根节点", "Will automatically resolve parent VRCAvatarDescriptor"), _dropSubLabelStyle);
            }
            else if (resolvedDescriptor != null)
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
            GUILayout.Label(L("2. 带宽档位 (Parameter Tier)", "2. Bandwidth Tier"), _sectionHeaderStyle);
            EditorGUILayout.Space(4f);

            string[] labels =
            {
                "Lite (33b)",
                "Compact (45b)",
                "Balanced (57b)",
                "Standard (73b)",
                "Turbo (137b)"
            };
            int selectedIndex = Array.IndexOf(TierDisplayOrder, _tier);
            if (selectedIndex < 0)
            {
                selectedIndex = 3; // Standard
            }
            int nextIndex = GUILayout.SelectionGrid(selectedIndex, labels, 3, GUILayout.Height(48f));
            if (nextIndex >= 0 && nextIndex < TierDisplayOrder.Length && nextIndex != selectedIndex)
            {
                _tier = TierDisplayOrder[nextIndex];
            }

            string tierSummary;
            switch (_tier)
            {
                case UniTextTier.Lite:
                    tierSummary = L(
                        "3 字节负载 (D0..D2) · LUT12 每包 2 汉字 / Ring-Buffer 滚屏 · 极低参数占用 (33b)",
                        "3 payload bytes (D0..D2) · 2 LUT12 CJK / 3 ASCII per packet + Ring-Buffer scroll · Ultra-low 33b cost"
                    );
                    break;
                case UniTextTier.Compact:
                    tierSummary = L(
                        "36 bit 负载 (D0..D3 + D4 高 4 位 Bool) · LUT12 每包 3 汉字 / 4 ASCII · 适合参数预算紧张的模型",
                        "36 payload bits (D0..D3 + 4 Bool nibble bits) · 3 LUT12 CJK / 4 ASCII per packet · Tight budgets"
                    );
                    break;
                case UniTextTier.Balanced:
                    tierSummary = L(
                        "6 字节负载 (D0..D5) · LUT12 每包 4 汉字（等效原 73b）/ 6 ASCII · 省 16b 推荐均衡档",
                        "6 payload bytes (D0..D5) · 4 LUT12 CJK (matches 73b Standard) / 6 ASCII · Saves 16 synced bits"
                    );
                    break;
                case UniTextTier.Turbo:
                    tierSummary = L(
                        "16 字节负载 (D0..D15) · LUT12 每包 10 汉字 / 16 ASCII · 最低字幕延迟",
                        "16 payload bytes (D0..D15) · 10 LUT12 CJK / 16 ASCII chars per packet · Lowest latency"
                    );
                    break;
                default:
                    tierSummary = L(
                        "8 字节负载 (D0..D7) · LUT12 每包 5 汉字 / 8 ASCII · 推荐默认档位",
                        "8 payload bytes (D0..D7) · 5 LUT12 CJK / 8 ASCII chars per packet · Recommended default"
                    );
                    break;
            }

            EditorGUILayout.Space(2f);
            EditorGUILayout.LabelField(tierSummary, _subtitleStyle);
            EditorGUILayout.EndVertical();
        }

        private void DrawBudgetSection(UniTextParameterChecker.AvatarBudgetSummary budgetSummary, out int projectedTotal)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Label(L("3. 同步参数预算预览", "3. Synced Parameter Budget"), _sectionHeaderStyle);
            EditorGUILayout.Space(4f);

            UniTextParameterChecker.ProjectWithUniText(
                budgetSummary,
                _tier,
                _syncWorldDrop,
                out int addedBits,
                out projectedTotal,
                _syncWideRows,
                _displayRows > 6,
                _allowRuntimeResize
            );

            int maxBits = UniTextParameterChecker.MaxSyncedBits;
            int remaining = Mathf.Max(0, maxBits - projectedTotal);

            Rect barRect = EditorGUILayout.GetControlRect(false, 20f);
            Color prevColor = GUI.color;
            if (projectedTotal > maxBits)
            {
                GUI.color = new Color(1.0f, 0.45f, 0.45f, 1.0f);
            }
            else if (projectedTotal > 200)
            {
                GUI.color = new Color(1.0f, 0.85f, 0.40f, 1.0f);
            }
            else
            {
                GUI.color = new Color(0.55f, 0.95f, 0.65f, 1.0f);
            }

            EditorGUI.ProgressBar(
                barRect,
                Mathf.Clamp01((float)projectedTotal / maxBits),
                $"{projectedTotal} / {maxBits} bits"
            );
            GUI.color = prevColor;

            EditorGUILayout.Space(2f);
            EditorGUILayout.LabelField(
                L(
                    $"原模型已用: {budgetSummary.BaseUsedBits}b  +  UniText: {addedBits}b  =  预计总计: {projectedTotal}b（剩余: {remaining}b）",
                    $"Base Avatar: {budgetSummary.BaseUsedBits}b  +  UniText: {addedBits}b  =  Projected: {projectedTotal}b (Remaining: {remaining}b)"
                ),
                EditorStyles.miniLabel
            );

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
                        "Suggestion: switch to Lite (33b) tier and disable 'Allow In-Game Resize (+1b)', 'Sync World Drop (+1b)', or 'Wide Rows (+1b)'."
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
                        $"预计超出 VRChat 256 bit 上限 {overBits} bits。\n{suggestion}",
                        $"Projected usage exceeds VRChat's 256-bit limit by {overBits} bits.\n{suggestion}"
                    ),
                    MessageType.Warning
                );
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawConfigurationSection()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Label(L("4. 安装选项", "4. Options"), _sectionHeaderStyle);
            EditorGUILayout.Space(4f);

            _syncWorldDrop = EditorGUILayout.ToggleLeft(
                new GUIContent(
                    L("同步世界固定 (World Drop, +1 bit)", "Sync World Drop (+1 synced bit)"),
                    L("开启后，其他玩家也能看到字幕板停留在世界空间中。", "Remote players also see the subtitle board stay frozen in world space.")
                ),
                _syncWorldDrop
            );

            _syncWideRows = EditorGUILayout.ToggleLeft(
                new GUIContent(
                    L("宽行模式 (比例字体 64 格/行, +1 bit)", "Wide Rows (proportional, 64 cells/row, +1 synced bit)"),
                    L("同步 UniText_Bank 列分区位，使每行最多容纳 64 个比例宽度字符（英文每行约多一倍）。关闭后每行仍为 32 格，但依旧按比例宽度排版。",
                      "Syncs the UniText_Bank column-bank bit so each row holds up to 64 proportional cells (about 2x more English per row). When off, rows keep 32 cells but still render proportionally.")
                ),
                _syncWideRows
            );

            _installMenu = EditorGUILayout.ToggleLeft(
                new GUIContent(
                    L("安装表情菜单子菜单 (UniText)", "Install Expressions Submenu (UniText)"),
                    L("通过 Modular Avatar Menu Installer 自动添加显示/固定/缩放/透明度/清屏控制菜单。", "Adds Show Board, World Drop, Billboard, Scale, Opacity, and Clear Screen controls.")
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

            EditorGUILayout.Space(6f);
            DrawViewportSection();

            EditorGUILayout.Space(4f);
            _showAdvanced = EditorGUILayout.Foldout(
                _showAdvanced,
                L("高级选项（骨骼绑定 / 偏移 / 菜单位置）", "Advanced Options (Bone Proxy / Offset / Menu)"),
                true
            );

            if (_showAdvanced)
            {
                EditorGUI.indentLevel++;
                _useBoneProxy = EditorGUILayout.Toggle(
                    L("启用骨骼绑定 (Bone Proxy)", "Use MA Bone Proxy"),
                    _useBoneProxy
                );

                EditorGUI.BeginDisabledGroup(!_useBoneProxy);
                _followBone = (HumanBodyBones)EditorGUILayout.EnumPopup(
                    L("跟随骨骼", "Follow Bone"),
                    _followBone
                );
                _autoPositionToBone = EditorGUILayout.Toggle(
                    L("自动对齐骨骼偏移", "Auto-Align to Bone"),
                    _autoPositionToBone
                );

                EditorGUI.BeginDisabledGroup(!_autoPositionToBone);
                EditorGUILayout.BeginHorizontal();
                _boneOffset = EditorGUILayout.Vector3Field(L("相对偏移", "Bone Offset"), _boneOffset);
                if (GUILayout.Button(L("重置", "Reset"), GUILayout.Width(54f), GUILayout.Height(18f)))
                {
                    _boneOffset = DefaultBoneOffset;
                    GUIUtility.keyboardControl = 0;
                }
                EditorGUILayout.EndHorizontal();
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
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawViewportSection()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Label(L("视窗尺寸与显示行数", "Viewport & Display Rows"), EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(L("标准 0.84×0.26 · 6行", "Standard 0.84×0.26 · 6r"), EditorStyles.miniButton))
            {
                _viewportWidth = 0.84f; _viewportHeight = 0.2625f; _displayRows = 6;
            }
            if (GUILayout.Button(L("长句 0.84×0.35 · 8行", "Long 0.84×0.35 · 8r"), EditorStyles.miniButton))
            {
                _viewportWidth = 0.84f; _viewportHeight = 0.35f; _displayRows = 8;
            }
            if (GUILayout.Button(L("紧凑 0.56×0.35 · 8行", "Compact 0.56×0.35 · 8r"), EditorStyles.miniButton))
            {
                _viewportWidth = 0.56f; _viewportHeight = 0.35f; _displayRows = 8;
            }
            EditorGUILayout.EndHorizontal();

            _viewportWidth = EditorGUILayout.Slider(L("视窗宽度 (m)", "Width (m)"), _viewportWidth, UniTextProp.MinViewportWidth, UniTextProp.MaxViewportWidth);
            _viewportHeight = EditorGUILayout.Slider(L("视窗高度 (m)", "Height (m)"), _viewportHeight, UniTextProp.MinViewportHeight, UniTextProp.MaxViewportHeight);
            _displayRows = EditorGUILayout.IntSlider(
                new GUIContent(L("显示行数 (保底≥4行)", "Display Rows (min 4)"), L("大于 6 行时自动启用 UniText_RowPage (+1 bit)。", "Rows > 6 automatically sync UniText_RowPage (+1 bit).")),
                _displayRows,
                UniTextProp.MinDisplayRows,
                UniTextProp.MaxDisplayRows
            );

            _allowRuntimeResize = EditorGUILayout.ToggleLeft(
                new GUIContent(
                    L("允许游戏内实时调宽高与重排 (Resize W/H, +1b)", "Allow In-Game Live Resize & Reflow (Resize W/H, +1b)"),
                    L("UniText_Width / UniText_Height 仅本地（0 bit），由 VRCS 通过 CMD_SET_GEOMETRY 把重排后的行数与行宽广播给所有玩家；仅需启用 UniText_RowPage（+1 bit）。",
                      "UniText_Width / UniText_Height stay local-only (0 bits); VRCS broadcasts the reflowed rows / row width to everyone via CMD_SET_GEOMETRY. Only UniText_RowPage (+1 bit) is synced.")
                ),
                _allowRuntimeResize
            );
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

            int effRowPx = UniTextProp.ComputeRowPx(_viewportWidth, _viewportHeight, _displayRows, _autoFitRowPx, _rowPx, _syncWideRows);
            int cjkPerRow = effRowPx / 12;
            int totalCjk = cjkPerRow * _displayRows;
            EditorGUILayout.LabelField(
                L(
                    $"预计容量: 每行约 {cjkPerRow} 汉字 (~{effRowPx / 6} 英文) · 整屏约 {totalCjk} 汉字 ({effRowPx}px × {_displayRows}行)",
                    $"Capacity: ~{cjkPerRow} CJK / row (~{effRowPx / 6} EN) · ~{totalCjk} CJK total ({effRowPx}px × {_displayRows}r)"
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
                    EditorGUILayout.HelpBox(
                        L(
                            $"Avatar 未映射骨骼 \"{_followBone}\"，构建时将自动回退绑定到 \"{fallbackName}\"。",
                            $"Bone '{_followBone}' is not mapped on this Avatar; build will fall back to '{fallbackName}'."
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

            if (!string.IsNullOrEmpty(_statusMessage) && _statusMessageType != MessageType.None)
            {
                EditorGUILayout.HelpBox(_statusMessage, _statusMessageType);
            }
        }

        private void DrawFooterActions(
            VRCAvatarDescriptor descriptor,
            UniTextParameterChecker.AvatarBudgetSummary budgetSummary,
            GameObject tierPrefab,
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

            GetTierMetadata(_tier, out _, out _, out string tierLabel);

            string installButtonText;
            if (!hasExisting)
            {
                installButtonText = L($"安装 UniText 到 Avatar ({tierLabel})", $"Install UniText to Avatar ({tierLabel})");
            }
            else if (canUpdateInPlace)
            {
                installButtonText = L($"更新已安装的 UniText ({tierLabel})", $"Update Installed UniText ({tierLabel})");
            }
            else
            {
                installButtonText = L($"替换为 {tierLabel}", $"Replace with {tierLabel}");
            }

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();

            GUI.enabled = descriptor != null && tierPrefab != null;
            if (GUILayout.Button(installButtonText, _primaryButtonStyle, GUILayout.Height(36f)))
            {
                InstallOrUpdate(descriptor, budgetSummary, tierPrefab, projectedTotal);
            }
            GUI.enabled = true;

            if (hasExisting)
            {
                if (GUILayout.Button(L("卸载", "Uninstall"), GUILayout.Width(104f), GUILayout.Height(36f)))
                {
                    UninstallAll(descriptor, existingProps);
                }
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
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
                        $"安装后预计同步参数为 {projectedTotal}/{UniTextParameterChecker.MaxSyncedBits} bits（超出 {over} bits），VRChat 可能会拒绝上传。\n\n是否仍要继续安装？",
                        $"Projected synced parameter usage is {projectedTotal}/{UniTextParameterChecker.MaxSyncedBits} bits ({over} bits over limit), which may block VRChat avatar uploads.\n\nInstall anyway?"
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

            Transform boardPivot = propRoot.transform.Find("BoardPivot");
            Transform displayQuad = boardPivot != null ? boardPivot.Find("DisplayQuad") : null;
            if (displayQuad != null)
            {
                Undo.RecordObject(displayQuad, "Resize UniText DisplayQuad Preview");
                displayQuad.localScale = new Vector3(
                    Mathf.Clamp(_viewportWidth, UniTextProp.MinViewportWidth, UniTextProp.MaxViewportWidth),
                    Mathf.Clamp(_viewportHeight, UniTextProp.MinViewportHeight, UniTextProp.MaxViewportHeight),
                    1f
                );
                PrefabUtility.RecordPrefabInstancePropertyModifications(displayQuad);
            }

            if (!_useBoneProxy || !_autoPositionToBone || descriptor == null)
            {
                return;
            }

            Transform targetBone = UniTextNdmfPlugin.ResolveHumanoidBone(descriptor.gameObject, _followBone);
            if (targetBone == null)
            {
                return;
            }

            Transform anchor = propRoot.transform.Find("SideFollowAnchor");
            if (anchor == null || boardPivot == null)
            {
                return;
            }

            Vector3 worldPos = targetBone.position + descriptor.transform.TransformDirection(_boneOffset);
            Quaternion worldRot = descriptor.transform.rotation;

            Undo.RecordObject(anchor, "Align UniText SideFollowAnchor");
            anchor.SetPositionAndRotation(worldPos, worldRot);
            PrefabUtility.RecordPrefabInstancePropertyModifications(anchor);

            Undo.RecordObject(boardPivot, "Align UniText BoardPivot");
            boardPivot.SetPositionAndRotation(worldPos, worldRot);
            PrefabUtility.RecordPrefabInstancePropertyModifications(boardPivot);
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
