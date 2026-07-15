using System;
using System.Collections.Generic;
using System.IO;
using Maaaaa.Asn.Editor.Yaml;
using UnityEditor;
using UnityEngine;

namespace Maaaaa.Asn.Editor.Core
{
    internal static class PrefabReferenceScanner
    {
        public static InspectionResult Inspect(string assetPath, bool showProgress)
        {
            var result = new InspectionResult { RootAssetPath = assetPath };
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var backingFileExistence = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            try { InspectRecursive(assetPath, result, visited, backingFileExistence, showProgress, true); }
            catch (Exception exception) { result.Errors.Add(exception.Message); }
            finally { if (showProgress) EditorUtility.ClearProgressBar(); }
            ReferenceClassifier.ClassifyAll(result.References);
            return result;
        }

        private static void InspectRecursive(string assetPath, InspectionResult result, HashSet<string> visited,
            Dictionary<string, bool> backingFileExistence, bool showProgress, bool isRoot)
        {
            if (string.IsNullOrEmpty(assetPath) || !visited.Add(assetPath)) return;
            if (showProgress) EditorUtility.DisplayProgressBar(AsnText.WindowTitle, "参照を走査中: " + assetPath, Mathf.Clamp01(visited.Count / 20f));
            var absolutePath = AssetPathUtility.ToAbsolutePath(assetPath);
            if (!File.Exists(absolutePath))
            {
                if (isRoot)
                {
                    result.RootFileMissing = true;
                    result.Errors.Add("検査対象のファイル本体が見つかりません: " + assetPath);
                }
                return;
            }

            var records = PrefabYamlParser.Parse(assetPath, absolutePath);
            foreach (var record in records)
            {
                Resolve(record, backingFileExistence);
                result.References.Add(record);
            }

            // Prefab Variant の親および Nested Prefab は m_SourcePrefab として表れる。
            foreach (var record in records)
                if (record.PropertyName == "m_SourcePrefab" && record.GuidResolved && record.ResolvedAssetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                    InspectRecursive(record.ResolvedAssetPath, result, visited, backingFileExistence, showProgress, false);
        }

        private static void Resolve(ReferenceRecord record, Dictionary<string, bool> backingFileExistence)
        {
            record.ExpectedType = InferExpectedType(record);
            if (record.FileId == 0) return;
            if (string.IsNullOrEmpty(record.Guid)) return;
            record.ResolvedAssetPath = AssetDatabase.GUIDToAssetPath(record.Guid);
            record.GuidResolved = !string.IsNullOrEmpty(record.ResolvedAssetPath);
            if (!record.GuidResolved) return;
            if (record.ResolvedAssetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                record.ReferencedName = Path.GetFileNameWithoutExtension(record.ResolvedAssetPath);
            record.BackingFileMissing = IsBackingFileMissing(record.ResolvedAssetPath, backingFileExistence);

            var resolvedAssets = AssetDatabase.LoadAllAssetsAtPath(record.ResolvedAssetPath);
            var hasResolvedAsset = false;
            foreach (var asset in resolvedAssets)
            {
                if (asset == null) continue;
                hasResolvedAsset = true;
                if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string guid, out long localId) &&
                    string.Equals(guid, record.Guid, StringComparison.OrdinalIgnoreCase) && localId == record.FileId)
                {
                    record.FileIdResolved = true;
                    record.ResolvedType = asset.GetType();
                    break;
                }
            }

            if (!record.FileIdResolved && record.FileId == 100100000)
            {
                var main = AssetDatabase.LoadMainAssetAtPath(record.ResolvedAssetPath);
                hasResolvedAsset |= main != null;
                record.FileIdResolved = main != null;
                record.ResolvedType = main != null ? main.GetType() : null;
            }
            if (record.BackingFileMissing)
            {
                // Library の import キャッシュが解決できても、物理ファイル欠落を優先する。
                record.FileIdResolved = false;
                record.TypeAssessment = "参照先ファイル本体が物理的に存在しません。";
            }
            else if (!hasResolvedAsset)
                record.TypeAssessment = "GUID のパスは解決しましたが、参照先ファイル本体が見つかりません。";
            else
                record.TypeAssessment = record.ExpectedType == null ? "期待型を YAML から確定できないため判定不可" : null;
        }

        private static bool IsBackingFileMissing(string assetPath, Dictionary<string, bool> existenceCache)
        {
            if (!assetPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) &&
                !assetPath.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
                return false;
            if (!existenceCache.TryGetValue(assetPath, out var exists))
            {
                try
                {
                    var absolutePath = AssetPathUtility.ToAbsolutePath(assetPath);
                    exists = File.Exists(absolutePath) || Directory.Exists(absolutePath);
                }
                catch
                {
                    // パスを安全に確認できない場合は、欠落と断定して誤検知させない。
                    exists = true;
                }
                existenceCache[assetPath] = exists;
            }
            return !exists;
        }

        private static Type InferExpectedType(ReferenceRecord record)
        {
            var name = record.PropertyName ?? string.Empty;
            if (name == "m_SourcePrefab") return typeof(GameObject);
            if (record.IsScript) return typeof(MonoScript);
            if (name.IndexOf("Material", StringComparison.OrdinalIgnoreCase) >= 0) return typeof(Material);
            if (name.IndexOf("Sprite", StringComparison.OrdinalIgnoreCase) >= 0) return typeof(Sprite);
            if (name.IndexOf("Texture", StringComparison.OrdinalIgnoreCase) >= 0) return typeof(Texture);
            if (name.IndexOf("Mesh", StringComparison.OrdinalIgnoreCase) >= 0) return typeof(Mesh);
            if (name.IndexOf("Clip", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Animation", StringComparison.OrdinalIgnoreCase) >= 0) return typeof(AnimationClip);
            if (name.IndexOf("Controller", StringComparison.OrdinalIgnoreCase) >= 0) return typeof(RuntimeAnimatorController);
            if (name.IndexOf("Avatar", StringComparison.OrdinalIgnoreCase) >= 0) return typeof(Avatar);
            return null;
        }
    }

    internal static class AssetPathUtility
    {
        public static string ProjectRoot => Directory.GetParent(Application.dataPath).FullName;
        public static string ToAbsolutePath(string assetPath)
        {
            if (assetPath.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
            {
                var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(assetPath);
                if (package != null && !string.IsNullOrEmpty(package.resolvedPath))
                {
                    var relativePath = assetPath.Length > package.assetPath.Length
                        ? assetPath.Substring(package.assetPath.Length).TrimStart('/', '\\')
                        : string.Empty;
                    return Path.GetFullPath(Path.Combine(package.resolvedPath, relativePath));
                }
            }
            return Path.GetFullPath(Path.Combine(ProjectRoot, assetPath));
        }
    }
}
