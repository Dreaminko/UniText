using UnityEngine;

namespace UniText
{
    public enum UniTextTier
    {
        // Compact36: D0..D3 (Int) + UniText_D4b4..D4b7 (Bool high nibble of D4) = 36 payload bits, 45 base synced bits
        Compact = 0,
        Standard = 1,
        Turbo = 2,
        Lite = 3,
        Balanced = 4
    }

    [DisallowMultipleComponent]
    [AddComponentMenu("UniText/UniText Subtitle Prop")]
#if UNITEXT_VRCSDK3
    public class UniTextProp : MonoBehaviour, VRC.SDKBase.IEditorOnly
#else
    public class UniTextProp : MonoBehaviour
#endif
    {
        public const int MinDisplayRows = 4;
        public const int DefaultDisplayRows = 6;
        public const int MaxDisplayRows = 12;
        public const float MinViewportWidth = 0.35f;
        public const float MaxViewportWidth = 1.80f;
        public const float DefaultViewportWidth = 0.84f;
        public const float MinViewportHeight = 0.16f;
        public const float MaxViewportHeight = 0.90f;
        public const float DefaultViewportHeight = 0.2625f;
        public const int MinRowPx = 192;
        public const int MaxRowPx = 640;
        public const int DefaultRowPx = 384;

        [Header("Bandwidth Tier")]
        public UniTextTier tier = UniTextTier.Standard;

        [Header("Viewport & Layout")]
        [Range(MinViewportWidth, MaxViewportWidth)]
        [Tooltip("Base viewport width in meters.")]
        public float viewportWidth = DefaultViewportWidth;

        [Range(MinViewportHeight, MaxViewportHeight)]
        [Tooltip("Base viewport height in meters.")]
        public float viewportHeight = DefaultViewportHeight;

        [Range(MinDisplayRows, MaxDisplayRows)]
        [Tooltip("Guaranteed number of visible subtitle rows (4..12). Values above 6 sync UniText_RowPage (+1 bit).")]
        public int displayRows = DefaultDisplayRows;

        [Tooltip("Automatically derive logical row pixel width from viewport aspect ratio and display rows.")]
        public bool autoFitRowPx = true;

        [Range(MinRowPx, MaxRowPx)]
        [Tooltip("Manual logical row width in glyph pixels (192..640, stepped by 8) when Auto-Fit is off.")]
        public int rowPx = DefaultRowPx;

        [Tooltip("Add a Resize (W/H) Two-Axis Puppet so in-game resizing reflows row count and line width for local and remote players. UniText_Width/Height stay local-only (0 synced bits): the sender (VRCS) broadcasts the resulting geometry via CMD_SET_GEOMETRY. Costs +1 bit (UniText_RowPage).")]
        public bool allowRuntimeResize = true;

        [Header("Modular Avatar Attachment")]
        [Tooltip("Automatically attach SideFollowAnchor to a humanoid bone via Modular Avatar Bone Proxy at build time.")]
        public bool useBoneProxy = true;

        [Tooltip("Humanoid bone that SideFollowAnchor follows when not in World Drop mode.")]
        public HumanBodyBones followBone = HumanBodyBones.Chest;

        [Tooltip("Automatically align SideFollowAnchor and BoardPivot relative to the target humanoid bone at build time.")]
        public bool autoPositionToBone = true;

        [Tooltip("Offset in avatar root space relative to the follow bone when autoPositionToBone is enabled.")]
        public Vector3 boneOffset = new Vector3(0.48f, 0.18f, 0.16f);

        [Header("Parameters & Expressions Menu")]
        [Tooltip("Sync World Drop toggle across network (+1 synced bit) so remote players also see the board frozen in world space.")]
        public bool syncWorldDrop = true;

        [Tooltip("Wide Rows: sync the UniText_Bank column-bank bit (+1 synced bit) so each row holds up to 64 proportional cells (~2x more English per row). Disable to keep 32 cells per row.")]
        public bool syncWideRows = true;

        [Tooltip("Install a UniText submenu into the avatar's VRChat Expressions Menu via Modular Avatar Menu Installer.")]
        public bool installMenu = true;

        [Tooltip("Submenu title added to the avatar Expressions Menu.")]
        public string menuName = "UniText";

#if UNITEXT_VRCSDK3
        [Tooltip("Optional target submenu to install into. Leave empty to install at the root Expressions Menu.")]
        public VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu installTargetMenu;
#endif

        [Header("Platform Safety")]
        [Tooltip("Automatically strip this prop non-destructively when building for Quest / Android.")]
        public bool stripOnAndroid = true;

        [Header("Package Asset References (Auto-Wired)")]
        public RenderTexture paramBridgeTexture;
        public Material encoderMaterial;
        public Material displayMaterial;
        public CustomRenderTexture bufferCRT;

        public const int ProtocolVersion = 2;

        /// <summary>Number of synced Int payload parameters (UniText_D0..D{N-1}).</summary>
        public int PayloadBytes => GetPayloadIntBytes(tier);

        /// <summary>Compact36 adds 4 synced Bools (UniText_D4b4..D4b7) carrying the high nibble of D4.</summary>
        public bool HasD4Nibble => TierHasD4Nibble(tier);

        /// <summary>Total synced payload bits (D bytes + nibble Bools).</summary>
        public int PayloadBits => GetPayloadBits(tier);

        public static int GetPayloadIntBytes(UniTextTier t)
        {
            switch (t)
            {
                case UniTextTier.Lite: return 3;
                case UniTextTier.Compact: return 4;
                case UniTextTier.Balanced: return 6;
                case UniTextTier.Turbo: return 16;
                default: return 8;
            }
        }

        public static bool TierHasD4Nibble(UniTextTier t) => t == UniTextTier.Compact;

        public static int GetPayloadBits(UniTextTier t) => GetPayloadIntBytes(t) * 8 + (TierHasD4Nibble(t) ? 4 : 0);

        /// <summary>Base synced bits: Visible (1) + Ctrl (8) + payload.</summary>
        public static int GetBaseSyncedBits(UniTextTier t) => 1 + 8 + GetPayloadBits(t);

        /// <summary>LUT12 CJK characters written per packet.</summary>
        public static int GetLut12PerPacket(UniTextTier t) => GetPayloadBits(t) / 12;

        public string TierName
        {
            get
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
        }

        public int EffectiveDisplayRows => Mathf.Clamp(displayRows, MinDisplayRows, MaxDisplayRows);
        public bool NeedsRowPage => EffectiveDisplayRows > 6 || allowRuntimeResize;
        public int EffectiveRowPx => ComputeRowPx(viewportWidth, viewportHeight, displayRows, autoFitRowPx, rowPx, syncWideRows);
        public int EstimatedCjkPerRow => EffectiveRowPx / 12;
        public int EstimatedTotalCjkCapacity => EstimatedCjkPerRow * EffectiveDisplayRows;

        public static int ComputeRowPx(float width, float height, int rows, bool autoFit, int manualRowPx, bool wideRows)
        {
            int maxAllowed = wideRows ? MaxRowPx : DefaultRowPx;
            if (!autoFit)
            {
                return Mathf.Clamp(Mathf.RoundToInt(manualRowPx / 8f) * 8, MinRowPx, maxAllowed);
            }
            float w = Mathf.Clamp(width, MinViewportWidth, MaxViewportWidth);
            float h = Mathf.Clamp(height, MinViewportHeight, MaxViewportHeight);
            int r = Mathf.Clamp(rows, MinDisplayRows, MaxDisplayRows);
            int raw = Mathf.RoundToInt((w / h) * 20f * r / 8f) * 8;
            return Mathf.Clamp(raw, MinRowPx, maxAllowed);
        }

        public static void ComputeRuntimeBounds(
            float baseWidth,
            float baseHeight,
            int baseRows,
            int baseRowPx,
            bool wideRows,
            out float wMin,
            out float wMax,
            out int rowPxMin,
            out int rowPxMax,
            out float hMin,
            out float hMax)
        {
            float w = Mathf.Clamp(baseWidth, MinViewportWidth, MaxViewportWidth);
            float h = Mathf.Clamp(baseHeight, MinViewportHeight, MaxViewportHeight);
            int rows = Mathf.Clamp(baseRows, MinDisplayRows, MaxDisplayRows);
            int maxAllowed = wideRows ? MaxRowPx : DefaultRowPx;
            int rPx = Mathf.Clamp(Mathf.RoundToInt(baseRowPx / 8f) * 8, MinRowPx, maxAllowed);

            rowPxMin = Mathf.Clamp(Mathf.RoundToInt(rPx * 0.5f / 8f) * 8, MinRowPx, rPx);
            rowPxMax = Mathf.Clamp(Mathf.RoundToInt(rPx * 1.5f / 8f) * 8, rPx, maxAllowed);
            wMin = w * ((float)rowPxMin / rPx);
            wMax = w * ((float)rowPxMax / rPx);
            hMin = h * ((float)MinDisplayRows / rows);
            hMax = h * ((float)MaxDisplayRows / rows);
        }

        // Live resize no longer syncs UniText_Width/Height (local-only); remote viewers follow CMD_SET_GEOMETRY.
        public int SyncedBitCost => GetBaseSyncedBits(tier) + (syncWorldDrop ? 1 : 0) + (syncWideRows ? 1 : 0) + (NeedsRowPage ? 1 : 0);

        private void OnValidate()
        {
            viewportWidth = Mathf.Clamp(viewportWidth, MinViewportWidth, MaxViewportWidth);
            viewportHeight = Mathf.Clamp(viewportHeight, MinViewportHeight, MaxViewportHeight);
            displayRows = Mathf.Clamp(displayRows, MinDisplayRows, MaxDisplayRows);
            rowPx = Mathf.Clamp(Mathf.RoundToInt(rowPx / 8f) * 8, MinRowPx, MaxRowPx);
        }
    }
}
