using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Maaaaa.Asn.Editor.Core
{
    internal static class SimilarAssetFinder
    {
        public static List<RepairCandidate> Find(ReferenceRecord record, int limit = 8)
        {
            var hints = BuildHints(record);
            var candidates = new List<RepairCandidate>();
            if (hints.Length == 0) return candidates;
            var prefabOnly = IsPrefabReference(record);
            var filter = prefabOnly ? "t:Prefab" : record.ExpectedType != null ? "t:" + record.ExpectedType.Name : hints[0];
            foreach (var guid in AssetDatabase.FindAssets(filter))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) && path == record.SourceAssetPath) continue;
                if (prefabOnly && !path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) continue;
                var assets = prefabOnly ? new[] { AssetDatabase.LoadMainAssetAtPath(path) } : AssetDatabase.LoadAllAssetsAtPath(path);
                foreach (var asset in assets)
                {
                    if (asset == null || (record.ExpectedType != null && !record.ExpectedType.IsAssignableFrom(asset.GetType()))) continue;
                    if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string assetGuid, out long fileId)) continue;
                    var match = Score(asset.name, Path.GetFileNameWithoutExtension(path), hints);
                    if (match.Score <= 0f) continue;
                    candidates.Add(new RepairCandidate { Asset = asset, AssetPath = path, Guid = assetGuid, FileId = fileId, Score = match.Score, ScoreReason = match.Reason });
                }
            }
            candidates.Sort((left, right) => right.Score.CompareTo(left.Score));
            if (candidates.Count > limit) candidates.RemoveRange(limit, candidates.Count - limit);
            return candidates;
        }

        public static string[] BuildHints(ReferenceRecord record)
        {
            var hints = new List<string>();
            if (!string.IsNullOrEmpty(record.ResolvedAssetPath))
                AddHint(hints, Path.GetFileNameWithoutExtension(record.ResolvedAssetPath), false);
            AddHint(hints, record.ReferencedName, false);
            AddHint(hints, record.PropertyName);
            AddHint(hints, record.GameObjectName);
            if (record.ExpectedType != null) AddHint(hints, record.ExpectedType.Name);
            return hints.ToArray();
        }

        public static List<RepairCandidate> FindRootReplacement(string missingAssetPath, int limit = 8)
        {
            return Find(new ReferenceRecord
            {
                SourceAssetPath = missingAssetPath,
                ResolvedAssetPath = missingAssetPath,
                PropertyName = "m_SourcePrefab",
                ExpectedType = typeof(GameObject)
            }, limit);
        }

        private static bool IsPrefabReference(ReferenceRecord record)
        {
            return record.PropertyName == "m_SourcePrefab" ||
                (!string.IsNullOrEmpty(record.ResolvedAssetPath) && record.ResolvedAssetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase));
        }

        private static void AddHint(List<string> hints, string value, bool normalize = true)
        {
            if (string.IsNullOrWhiteSpace(value) || value == "(不明)") return;
            if (normalize) value = value.TrimStart('m', '_').Replace("Reference", string.Empty).Replace("Asset", string.Empty);
            if (value.Length > 1 && !hints.Exists(item => string.Equals(item, value, StringComparison.OrdinalIgnoreCase))) hints.Add(value);
        }

        private struct ScoreMatch
        {
            public float Score;
            public string Reason;
        }

        private static ScoreMatch Score(string objectName, string fileName, string[] hints)
        {
            var best = new ScoreMatch();
            foreach (var hint in hints)
            {
                KeepBetter(ref best, ScoreOne(objectName, hint, "アセット名"));
                KeepBetter(ref best, ScoreOne(fileName, hint, "ファイル名"));
            }
            return best;
        }

        private static void KeepBetter(ref ScoreMatch best, ScoreMatch candidate)
        {
            if (candidate.Score > best.Score) best = candidate;
        }

        private static ScoreMatch ScoreOne(string candidate, string hint, string targetLabel)
        {
            if (string.IsNullOrEmpty(candidate) || string.IsNullOrEmpty(hint)) return new ScoreMatch();
            if (string.Equals(candidate, hint, StringComparison.OrdinalIgnoreCase))
                return Match(1000f, "完全一致", hint, targetLabel);
            if (IsExtensionDifferenceMatch(candidate, hint))
                return Match(800f, "拡張子違いの一致", hint, targetLabel);
            if (candidate.StartsWith(hint, StringComparison.OrdinalIgnoreCase) || hint.StartsWith(candidate, StringComparison.OrdinalIgnoreCase))
                return Match(800f, "前方一致", hint, targetLabel);
            if (candidate.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0 || hint.IndexOf(candidate, StringComparison.OrdinalIgnoreCase) >= 0)
                return Match(600f, "部分一致", hint, targetLabel);
            var distance = Levenshtein(candidate.ToLowerInvariant(), hint.ToLowerInvariant());
            return Match(Mathf.Max(1f, 400f - distance * 30f), "編集距離 " + distance, hint, targetLabel);
        }

        private static bool IsExtensionDifferenceMatch(string candidate, string hint)
        {
            var candidateStem = Path.GetFileNameWithoutExtension(candidate);
            var hintStem = Path.GetFileNameWithoutExtension(hint);
            return (!string.Equals(candidateStem, candidate, StringComparison.Ordinal) || !string.Equals(hintStem, hint, StringComparison.Ordinal)) &&
                string.Equals(candidateStem, hintStem, StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(candidateStem, hint, StringComparison.OrdinalIgnoreCase) || string.Equals(hintStem, candidate, StringComparison.OrdinalIgnoreCase));
        }

        private static ScoreMatch Match(float score, string kind, string hint, string targetLabel)
        {
            return new ScoreMatch { Score = score, Reason = kind + "「" + hint + "」(" + targetLabel + ")" };
        }

        private static int Levenshtein(string left, string right)
        {
            var costs = new int[right.Length + 1];
            for (var j = 0; j <= right.Length; j++) costs[j] = j;
            for (var i = 1; i <= left.Length; i++)
            {
                var previous = costs[0]; costs[0] = i;
                for (var j = 1; j <= right.Length; j++)
                {
                    var old = costs[j];
                    costs[j] = Math.Min(Math.Min(costs[j] + 1, costs[j - 1] + 1), previous + (left[i - 1] == right[j - 1] ? 0 : 1));
                    previous = old;
                }
            }
            return costs[right.Length];
        }
    }
}
