using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Maaaaa.Asn.Editor.Core
{
    internal static class SubAssetCandidateFinder
    {
        public static List<RepairCandidate> Find(ReferenceRecord record)
        {
            var result = new List<RepairCandidate>();
            if (record == null || record.Issue != IssueKind.FileIdMissing || record.BackingFileMissing ||
                string.IsNullOrEmpty(record.ResolvedAssetPath) || PrefabReferenceScanner.IsBackingFileMissing(record.ResolvedAssetPath)) return result;
            var hints = SimilarAssetFinder.BuildHints(record);
            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(record.ResolvedAssetPath))
            {
                if (asset == null || (record.ExpectedType != null && !record.ExpectedType.IsAssignableFrom(asset.GetType()))) continue;
                if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string guid, out long fileId)) continue;
                if (!string.Equals(guid, record.Guid, StringComparison.OrdinalIgnoreCase) || fileId == record.FileId) continue;
                var candidate = new RepairCandidate
                {
                    Asset = asset,
                    AssetPath = record.ResolvedAssetPath,
                    Guid = record.Guid,
                    FileId = fileId,
                    Certainty = CandidateCertainty.Guess,
                    SourceKind = CandidateSourceKind.SubAsset,
                    SourceLabel = "同一アセット内 (推測)",
                    OriginDescription = "同一アセット内のサブアセット"
                };
                candidate.SourceKinds.Add(CandidateSourceKind.SubAsset);
                SimilarAssetFinder.ScoreCandidate(candidate, hints);
                result.Add(candidate);
            }
            result.Sort((left, right) => right.Score.CompareTo(left.Score));
            if (result.Count == 1) result[0].OriginDescription += "（期待型に一致する候補はこれのみ）";
            return result;
        }
    }

    internal static class MonoScriptCandidateFinder
    {
        private static readonly Regex PropertyRegex = new Regex(@"^\s{2}(?<name>[A-Za-z_][A-Za-z0-9_]*):", RegexOptions.Compiled);
        private static readonly HashSet<string> IgnoredFields = new HashSet<string>(StringComparer.Ordinal)
        {
            "m_ObjectHideFlags", "m_CorrespondingSourceObject", "m_PrefabInstance", "m_PrefabAsset", "m_GameObject",
            "m_Enabled", "m_EditorHideFlags", "m_Script", "m_Name", "m_EditorClassIdentifier"
        };

        public static List<RepairCandidate> Find(ReferenceRecord record)
        {
            var candidates = new List<RepairCandidate>();
            if (record == null || !record.IsScript) return candidates;
            var originalNames = UnityPackageIndexStore.GetOriginalPaths(record.Guid)
                .Select(path => Path.GetFileNameWithoutExtension(path)).Where(name => !string.IsNullOrEmpty(name)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            originalNames.AddRange(RecycleBinScanner.GetCachedNames(record.Guid).Select(Path.GetFileNameWithoutExtension)
                .Where(name => !string.IsNullOrEmpty(name) && !originalNames.Contains(name, StringComparer.OrdinalIgnoreCase)));
            var yamlFields = ReadComponentFields(record);
            if (originalNames.Count == 0 && yamlFields.Count == 0) return candidates;

            foreach (var scriptGuid in AssetDatabase.FindAssets("t:MonoScript"))
            {
                var path = AssetDatabase.GUIDToAssetPath(scriptGuid);
                if (PrefabReferenceScanner.IsBackingFileMissing(path)) continue;
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
                if (script == null) continue;
                var type = script.GetClass();
                if (type == null) continue;
                if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(script, out string guid, out long fileId)) continue;
                var score = 0f;
                var reasons = new List<string>();
                foreach (var name in originalNames)
                {
                    if (string.Equals(script.name, name, StringComparison.OrdinalIgnoreCase)) { score = Math.Max(score, 1000f); reasons.Add("索引のスクリプト名と完全一致「" + name + "」"); }
                    else if (script.name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf(script.name, StringComparison.OrdinalIgnoreCase) >= 0) { score = Math.Max(score, 600f); reasons.Add("索引のスクリプト名と部分一致「" + name + "」"); }
                }
                if (yamlFields.Count > 0)
                {
                    var fields = SerializableFieldNames(type);
                    var overlap = yamlFields.Count(name => fields.Contains(name));
                    if (overlap > 0)
                    {
                        var fieldScore = 400f + 400f * overlap / yamlFields.Count;
                        if (fieldScore > score) score = fieldScore;
                        reasons.Add("残存フィールド " + overlap + "/" + yamlFields.Count + " 一致");
                    }
                }
                if (score <= 0f) continue;
                var candidate = new RepairCandidate
                {
                    Asset = script,
                    AssetPath = path,
                    Guid = guid,
                    FileId = fileId,
                    Score = score,
                    ScoreReason = string.Join(" / ", reasons.ToArray()),
                    Certainty = CandidateCertainty.Guess,
                    SourceKind = CandidateSourceKind.MonoScript,
                    SourceLabel = "MonoScript (推測)",
                    OriginDescription = "スクリプト名・残存フィールド構成"
                };
                candidate.SourceKinds.Add(CandidateSourceKind.MonoScript);
                candidates.Add(candidate);
            }
            candidates.Sort((left, right) => right.Score.CompareTo(left.Score));
            if (candidates.Count > 8) candidates.RemoveRange(8, candidates.Count - 8);
            return candidates;
        }

        private static HashSet<string> ReadComponentFields(ReferenceRecord record)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                var lines = File.ReadAllLines(AssetPathUtility.ToAbsolutePath(record.SourceAssetPath));
                var header = "&" + record.SourceObjectFileId;
                var inside = false;
                foreach (var line in lines)
                {
                    if (line.StartsWith("--- !u!", StringComparison.Ordinal))
                    {
                        if (inside) break;
                        inside = line.EndsWith(header, StringComparison.Ordinal);
                        continue;
                    }
                    if (!inside) continue;
                    var match = PropertyRegex.Match(line);
                    if (match.Success && !IgnoredFields.Contains(match.Groups["name"].Value)) result.Add(match.Groups["name"].Value);
                }
            }
            catch { /* 候補を無理に出さないため、手がかり無しとして扱う。 */ }
            return result;
        }

        private static HashSet<string> SerializableFieldNames(Type type)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            for (var current = type; current != null && current != typeof(MonoBehaviour); current = current.BaseType)
            {
                foreach (var field in current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (!field.IsStatic && !field.IsNotSerialized && (field.IsPublic || field.GetCustomAttributes(typeof(SerializeField), true).Length > 0)) result.Add(field.Name);
            }
            return result;
        }
    }
}
