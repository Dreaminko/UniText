#if UNITEXT_VRCSDK3 && UNITEXT_MA && UNITEXT_NDMF
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UniText.Editor
{
    public static class UniTextExporter
    {
        private const string PackageJsonPath = "Packages/dev.dreaminko.unitext/package.json";

        [Serializable]
        private class PackageManifestStub
        {
            public string version = "1.0.0";
        }

        [MenuItem("Tools/UniText/Export .unitypackage", priority = 120)]
        public static void ExportUnityPackageMenu()
        {
            string version = ReadPackageVersion();
            string defaultName = $"UniText-{version}.unitypackage";
            string outputPath = EditorUtility.SaveFilePanel(
                "Export UniText .unitypackage",
                Path.GetFullPath(Path.Combine(Application.dataPath, "..")),
                defaultName,
                "unitypackage"
            );

            if (string.IsNullOrEmpty(outputPath))
            {
                return;
            }

            ExportToPath(outputPath);
            EditorUtility.DisplayDialog("UniText Export", $"Exported:\n{outputPath}", "OK");
        }

        public static void ExportUnityPackageBatch()
        {
            UniTextAssetBuilder.BuildAll();
            string version = ReadPackageVersion();
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string outputPath = Path.Combine(projectRoot, "..", "dist", $"UniText-{version}.unitypackage");
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
            ExportToPath(outputPath);
        }

        public static void ExportToPath(string outputPath)
        {
            string[] roots = { UniTextAssetBuilder.PackageRoot };
            AssetDatabase.ExportPackage(
                roots,
                outputPath,
                ExportPackageOptions.Recurse
            );
            Debug.Log($"[UniText] Exported .unitypackage to {outputPath}");
        }

        private static string ReadPackageVersion()
        {
            if (!File.Exists(PackageJsonPath))
            {
                return "1.0.0";
            }
            try
            {
                var stub = JsonUtility.FromJson<PackageManifestStub>(File.ReadAllText(PackageJsonPath));
                return string.IsNullOrEmpty(stub?.version) ? "1.0.0" : stub.version;
            }
            catch
            {
                return "1.0.0";
            }
        }
    }
}
#endif
