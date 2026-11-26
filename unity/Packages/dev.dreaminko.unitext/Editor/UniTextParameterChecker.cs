#if UNITEXT_VRCSDK3 && UNITEXT_MA && UNITEXT_NDMF
using System;
using System.Collections.Generic;
using System.Reflection;
using nadena.dev.modular_avatar.core;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace UniText.Editor
{
    public static class UniTextParameterChecker
    {
        internal const int MaxSyncedBits = 256;

        internal struct AvatarBudgetSummary
        {
            public int BaseUsedBits;
            public int CurrentTotalUsedBits;
            public HashSet<string> BaseSyncedParamNames;
            public UniTextProp[] AttachedProps;
            public List<string> AttachedSummaries;
        }

        internal static AvatarBudgetSummary AnalyzeAvatar(VRCAvatarDescriptor descriptor)
        {
            var seenNames = new HashSet<string>();
            if (descriptor == null)
            {
                return new AvatarBudgetSummary
                {
                    BaseUsedBits = 0,
                    CurrentTotalUsedBits = 0,
                    BaseSyncedParamNames = seenNames,
                    AttachedProps = Array.Empty<UniTextProp>(),
                    AttachedSummaries = new List<string>()
                };
            }

            int usedBits = 0;

            if (descriptor.expressionParameters != null && descriptor.expressionParameters.parameters != null)
            {
                foreach (var p in descriptor.expressionParameters.parameters)
                {
                    if (p == null || string.IsNullOrEmpty(p.name) || !p.networkSynced)
                    {
                        continue;
                    }
                    if (seenNames.Add(p.name))
                    {
                        usedBits += GetBitCost(p.valueType);
                    }
                }
            }

            foreach (var maParams in descriptor.GetComponentsInChildren<ModularAvatarParameters>(true))
            {
                if (maParams == null || maParams.parameters == null)
                {
                    continue;
                }
                if (maParams.GetComponentsInParent<UniTextProp>(true).Length > 0)
                {
                    continue;
                }
                foreach (var cfg in maParams.parameters)
                {
                    if (string.IsNullOrEmpty(cfg.nameOrPrefix) || cfg.syncType == ParameterSyncType.NotSynced || IsLocalOnly(cfg))
                    {
                        continue;
                    }
                    string finalName = string.IsNullOrEmpty(cfg.remapTo) ? cfg.nameOrPrefix : cfg.remapTo;
                    if (seenNames.Add(finalName))
                    {
                        usedBits += GetMaBitCost(cfg.syncType);
                    }
                }
            }

            int baseUsedBits = usedBits;
            var baseSyncedParamNames = new HashSet<string>(seenNames);

            var attachedProps = descriptor.GetComponentsInChildren<UniTextProp>(true);
            var attachedSummaries = new List<string>();
            foreach (var prop in attachedProps)
            {
                if (prop == null)
                {
                    continue;
                }
                attachedSummaries.Add($"{prop.gameObject.name} ({prop.TierName}, {prop.SyncedBitCost}b)");

                if (seenNames.Add("UniText_Visible")) usedBits += 1;
                if (prop.syncWorldDrop && seenNames.Add("UniText_WorldDrop")) usedBits += 1;
                if (prop.syncBillboard && seenNames.Add("UniText_Billboard")) usedBits += 1;
                if (prop.syncWideRows && seenNames.Add("UniText_Bank")) usedBits += 1;
                if (prop.NeedsRowPage && seenNames.Add("UniText_RowPage")) usedBits += 1;
                if (seenNames.Add("UniText_Ctrl")) usedBits += 8;
                for (int i = 0; i < prop.PayloadBytes; i++)
                {
                    if (seenNames.Add($"UniText_D{i}")) usedBits += 8;
                }
                if (prop.HasD4Nibble)
                {
                    for (int bit = 4; bit < 8; bit++)
                    {
                        if (seenNames.Add($"UniText_D4b{bit}")) usedBits += 1;
                    }
                }
                // Live resize: UniText_Width / UniText_Height are local-only (geometry travels via CMD_SET_GEOMETRY).
            }

            return new AvatarBudgetSummary
            {
                BaseUsedBits = baseUsedBits,
                CurrentTotalUsedBits = usedBits,
                BaseSyncedParamNames = baseSyncedParamNames,
                AttachedProps = attachedProps,
                AttachedSummaries = attachedSummaries
            };
        }

        /// <summary>Number of synced Int payload parameters (D0..D{N-1}); Compact36 adds 4 Bool nibble bits on top.</summary>
        internal static int GetPayloadBytes(UniTextTier tier) => UniTextProp.GetPayloadIntBytes(tier);

        /// <summary>Total synced payload bits including Compact36's D4 nibble Bools.</summary>
        internal static int GetPayloadBits(UniTextTier tier) => UniTextProp.GetPayloadBits(tier);

        internal static void ProjectWithUniText(
            AvatarBudgetSummary summary,
            UniTextTier tier,
            bool syncWorldDrop,
            bool syncBillboard,
            out int addedBits,
            out int projectedTotalBits,
            bool syncWideRows = true,
            bool syncExtendedRows = false,
            bool allowRuntimeResize = false)
        {
            var seenNames = summary.BaseSyncedParamNames != null
                ? new HashSet<string>(summary.BaseSyncedParamNames)
                : new HashSet<string>();

            int delta = 0;
            int payloadBytes = GetPayloadBytes(tier);

            if (seenNames.Add("UniText_Visible")) delta += 1;
            if (syncWorldDrop && seenNames.Add("UniText_WorldDrop")) delta += 1;
            if (syncBillboard && seenNames.Add("UniText_Billboard")) delta += 1;
            if (syncWideRows && seenNames.Add("UniText_Bank")) delta += 1;
            if ((syncExtendedRows || allowRuntimeResize) && seenNames.Add("UniText_RowPage")) delta += 1;
            if (seenNames.Add("UniText_Ctrl")) delta += 8;
            for (int i = 0; i < payloadBytes; i++)
            {
                if (seenNames.Add($"UniText_D{i}")) delta += 8;
            }
            if (UniTextProp.TierHasD4Nibble(tier))
            {
                for (int bit = 4; bit < 8; bit++)
                {
                    if (seenNames.Add($"UniText_D4b{bit}")) delta += 1;
                }
            }

            addedBits = delta;
            projectedTotalBits = summary.BaseUsedBits + addedBits;
        }

        [MenuItem("Tools/UniText/Check Avatar Parameter Budget", priority = 110)]
        public static void CheckSelectedAvatarBudget()
        {
            var go = Selection.activeGameObject;
            var descriptor = go != null ? go.GetComponentInParent<VRCAvatarDescriptor>() : null;
            if (descriptor == null)
            {
                EditorUtility.DisplayDialog(
                    "UniText Parameter Budget",
                    "Please select an Avatar (GameObject with VRCAvatarDescriptor) in the Hierarchy first.",
                    "OK"
                );
                return;
            }

            var summary = AnalyzeAvatar(descriptor);
            int usedBits = summary.CurrentTotalUsedBits;
            int remaining = Mathf.Max(0, MaxSyncedBits - usedBits);
            string attachedInfo = summary.AttachedSummaries != null && summary.AttachedSummaries.Count > 0
                ? $"Attached UniText Prop(s): {string.Join(", ", summary.AttachedSummaries)}\n"
                : "Attached UniText Prop(s): None\n";

            string report =
                $"Avatar: {descriptor.gameObject.name}\n" +
                attachedInfo +
                $"Currently Used Synced Bits (incl. attached MA/UniText): {usedBits} / {MaxSyncedBits}\n" +
                $"Remaining Synced Bits: {remaining}\n\n" +
                $"UniText Tier Compatibility (+1b WorldDrop / +1b Billboard / +1b Wide Rows / +1b RowPage for >6 rows or Live Resize; Resize W/H itself is local-only):\n" +
                $"  • Lite     (33b / 36b): {(remaining >= 36 ? "FITS (with WorldDrop + Billboard + Wide Rows)" : remaining >= 33 ? "FITS (local options / narrow rows)" : "NOT ENOUGH BITS")}\n" +
                $"  • Compact  (45b / 48b): {(remaining >= 48 ? "FITS (with WorldDrop + Billboard + Wide Rows)" : remaining >= 45 ? "FITS (local options / narrow rows)" : "NOT ENOUGH BITS")}\n" +
                $"  • Balanced (57b / 60b): {(remaining >= 60 ? "FITS (with WorldDrop + Billboard + Wide Rows)" : remaining >= 57 ? "FITS (local options / narrow rows)" : "NOT ENOUGH BITS")}\n" +
                $"  • Standard (73b / 76b): {(remaining >= 76 ? "FITS (with WorldDrop + Billboard + Wide Rows)" : remaining >= 73 ? "FITS (local options / narrow rows)" : "NOT ENOUGH BITS")}\n" +
                $"  • Turbo   (137b / 140b): {(remaining >= 140 ? "FITS (with WorldDrop + Billboard + Wide Rows)" : remaining >= 137 ? "FITS (local options / narrow rows)" : "NOT ENOUGH BITS")}" +
                (remaining < 33 ? "\n\nTip: Use VRCFury Parameter Compressor / Modular Avatar compression on your avatar's static menu toggles/sliders to free bits." : "");

            Debug.Log("[UniText]\n" + report);
            EditorUtility.DisplayDialog("UniText Parameter Budget", report, "OK");
        }

        private static int GetBitCost(VRCExpressionParameters.ValueType valueType)
        {
            switch (valueType)
            {
                case VRCExpressionParameters.ValueType.Bool: return 1;
                case VRCExpressionParameters.ValueType.Int:
                case VRCExpressionParameters.ValueType.Float: return 8;
                default: return 0;
            }
        }

        private static int GetMaBitCost(ParameterSyncType syncType)
        {
            switch (syncType)
            {
                case ParameterSyncType.Bool: return 1;
                case ParameterSyncType.Int:
                case ParameterSyncType.Float: return 8;
                default: return 0;
            }
        }

        private static bool IsLocalOnly(ParameterConfig cfg)
        {
            Type type = typeof(ParameterConfig);
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;

            FieldInfo field = type.GetField("localOnly", flags);
            if (field != null && field.FieldType == typeof(bool))
            {
                return (bool)field.GetValue(cfg);
            }

            PropertyInfo prop = type.GetProperty("localOnly", flags);
            if (prop != null && prop.CanRead && prop.PropertyType == typeof(bool))
            {
                return (bool)prop.GetValue(cfg, null);
            }

            return false;
        }
    }
}
#endif
