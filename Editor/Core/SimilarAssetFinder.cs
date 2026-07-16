using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Maaaaa.Asn.Editor.Core
{
    internal sealed class ProjectCandidateSearchDiagnostics
    {
        public int Enumerated;
        public int MissingFileExcluded;
        public int SourceAssetExcluded;
        public int NonPrefabExcluded;
        public int LoadFailed;
        public int BrokenPrefabPresented;
        public int TypeMismatchExcluded;
        public int IdentifierExcluded;
        public int ScoreExcluded;
        public int Matched;
        public int Presented;
    }

    internal static class SimilarAssetFinder
    {
        private const float MinimumDisplayScore = 250f;
        public static List<RepairCandidate> Find(ReferenceRecord record, int limit = 8)
        {
            return Find(record, limit, null);
        }

        internal static List<RepairCandidate> Find(ReferenceRecord record, int limit, ProjectCandidateSearchDiagnostics diagnostics)
        {
            var hints = BuildHints(record);
            var candidates = new List<RepairCandidate>();
            if (hints.Length == 0) return candidates;
            var prefabOnly = IsPrefabReference(record);
            var filter = prefabOnly ? "t:Prefab" : record.ExpectedType != null ? "t:" + record.ExpectedType.Name : hints[0];
            foreach (var guid in AssetDatabase.FindAssets(filter))
            {
                if (diagnostics != null) diagnostics.Enumerated++;
                var path = AssetDatabase.GUIDToAssetPath(guid);
                // AssetDatabase の import キャッシュだけに残った候補は提示しない。
                if (PrefabReferenceScanner.IsBackingFileMissing(path)) { if (diagnostics != null) diagnostics.MissingFileExcluded++; continue; }
                if (path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) && path == record.SourceAssetPath) { if (diagnostics != null) diagnostics.SourceAssetExcluded++; continue; }
                if (prefabOnly && !path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) { if (diagnostics != null) diagnostics.NonPrefabExcluded++; continue; }
                var assets = prefabOnly ? new[] { AssetDatabase.LoadMainAssetAtPath(path) } : AssetDatabase.LoadAllAssetsAtPath(path);
                if (prefabOnly && (assets.Length == 0 || assets[0] == null))
                {
                    if (diagnostics != null) diagnostics.LoadFailed++;
                    var match = Score(string.Empty, Path.GetFileNameWithoutExtension(path), hints);
                    if (match.Score < MinimumDisplayScore) { if (diagnostics != null) diagnostics.ScoreExcluded++; continue; }
                    candidates.Add(new RepairCandidate
                    {
                        AssetPath = path,
                        Guid = guid,
                        FileId = record.FileId,
                        Score = match.Score,
                        ScoreReason = match.Reason,
                        SourceKind = CandidateSourceKind.Project,
                        SourceLabel = "プロジェクト内（読み込み不可）",
                        OriginDescription = "名前は一致しますが、この Prefab 自身も読み込めません。先にこの Prefab の壊れた参照を修復してください。",
                        CanRepair = false
                    });
                    if (diagnostics != null) { diagnostics.BrokenPrefabPresented++; diagnostics.Matched++; }
                    continue;
                }
                foreach (var asset in assets)
                {
                    if (asset == null) { if (diagnostics != null) diagnostics.LoadFailed++; continue; }
                    if (record.ExpectedType != null && !record.ExpectedType.IsAssignableFrom(asset.GetType())) { if (diagnostics != null) diagnostics.TypeMismatchExcluded++; continue; }
                    if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string assetGuid, out long fileId)) { if (diagnostics != null) diagnostics.IdentifierExcluded++; continue; }
                    var match = Score(asset.name, Path.GetFileNameWithoutExtension(path), hints);
                    if (match.Score < MinimumDisplayScore) { if (diagnostics != null) diagnostics.ScoreExcluded++; continue; }
                    candidates.Add(new RepairCandidate { Asset = asset, AssetPath = path, Guid = assetGuid, FileId = fileId, Score = match.Score, ScoreReason = match.Reason, SourceKind = CandidateSourceKind.Project, SourceLabel = "プロジェクト内", OriginDescription = "プロジェクト内の名前類似度" });
                    if (diagnostics != null) diagnostics.Matched++;
                }
            }
            candidates.Sort((left, right) => right.Score.CompareTo(left.Score));
            if (candidates.Count > limit) candidates.RemoveRange(limit, candidates.Count - limit);
            if (diagnostics != null) diagnostics.Presented = candidates.Count;
            return candidates;
        }

        public static string[] BuildHints(ReferenceRecord record)
        {
            var hints = new List<string>();
            foreach (var path in UnityPackageIndexStore.GetOriginalPaths(record.Guid))
                AddHint(hints, Path.GetFileNameWithoutExtension(path));
            foreach (var name in RecycleBinScanner.GetCachedNames(record.Guid))
                AddHint(hints, Path.GetFileNameWithoutExtension(name));
            if (!string.IsNullOrEmpty(record.ResolvedAssetPath))
                AddHint(hints, Path.GetFileNameWithoutExtension(record.ResolvedAssetPath));
            AddHint(hints, record.ReferencedName);
            AddHint(hints, record.GameObjectName);
            return hints.ToArray();
        }

        internal static void ScoreCandidate(RepairCandidate candidate, string[] hints)
        {
            var match = Score(candidate.Asset != null ? candidate.Asset.name : string.Empty,
                Path.GetFileNameWithoutExtension(candidate.AssetPath ?? string.Empty), hints);
            candidate.Score = match.Score;
            candidate.ScoreReason = match.Reason;
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

        internal static bool IsPrefabReference(ReferenceRecord record)
        {
            return record.PropertyName == "m_SourcePrefab" ||
                (!string.IsNullOrEmpty(record.ResolvedAssetPath) && record.ResolvedAssetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase));
        }

        private static void AddHint(List<string> hints, string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value == "(不明)") return;
            if (value.Length > 1 && !hints.Exists(item => string.Equals(item, value, StringComparison.OrdinalIgnoreCase))) hints.Add(value);
        }

        internal static float ScoreName(string candidateName, string hint, out string reason)
        {
            var match = ScoreOne(candidateName, hint, "ファイル名");
            reason = match.Reason;
            return match.Score;
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
