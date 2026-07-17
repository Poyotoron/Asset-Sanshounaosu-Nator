using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Maaaaa.Asn.Editor.Core
{
    internal sealed class CandidateSearchDiagnostics
    {
        public string[] NameHints = new string[0];
        public bool ProjectEnabled;
        public bool UnityPackageEnabled;
        public bool RecycleBinEnabled;
        public int ProjectReturned;
        public int UnityPackageReturned;
        public int UnityPackageGuidReturned;
        public int UnityPackageNameReturned;
        public int RecycleBinReturned;
        public ProjectCandidateSearchDiagnostics Project = new ProjectCandidateSearchDiagnostics();
        public UnityPackageIndexStatus UnityPackageStatus;
        public UnityPackageNameSearchDiagnostics UnityPackageNameSearch = new UnityPackageNameSearchDiagnostics();

        public string ToDisplayText()
        {
            var text = new StringBuilder();
            text.AppendLine(NameHints.Length == 0
                ? "プロジェクト内探索の手がかり: なし"
                : "プロジェクト内探索の手がかり: " + string.Join(" / ", NameHints.Select(item => "『" + item + "』").ToArray()));
            text.AppendLine(ProjectEnabled
                ? "プロジェクト内: " + ProjectReturned + " 件"
                : "プロジェクト内: 無効");
            if (ProjectEnabled)
            {
                text.AppendLine("  t:Prefab等 " + Project.Enumerated + " 件 → 実体なし " + Project.MissingFileExcluded +
                    " / 自分自身 " + Project.SourceAssetExcluded + " / 非Prefab " + Project.NonPrefabExcluded +
                    " / 読込不可 " + Project.LoadFailed + "（案内候補 " + Project.BrokenPrefabPresented + "）" +
                    " / 型不一致 " + Project.TypeMismatchExcluded + " / ID取得不可 " + Project.IdentifierExcluded +
                    " / スコア不足 " + Project.ScoreExcluded + " → 条件一致 " + Project.Matched + " 件中 " + Project.Presented + " 件を提示");
            }
            if (UnityPackageEnabled)
            {
                text.AppendLine(".unitypackage: GUID 一致 " + UnityPackageGuidReturned + " 件 / 名前一致 " +
                    UnityPackageNameReturned + " 件（索引 " + UnityPackageStatus.PackageCount + " package / " +
                    UnityPackageStatus.GuidCount + " GUID" + (UnityPackageStatus.Completed ? "" : " / 未完了") + "）");
                if (UnityPackageGuidReturned == 0)
                {
                    text.AppendLine(UnityPackageNameSearch.Hints.Length == 0
                        ? "  名前探索の手がかり: なし"
                        : "  名前探索の手がかり: " + string.Join(" / ", UnityPackageNameSearch.Hints.Select(item => "『" + item + "』").ToArray()));
                    text.AppendLine("  当該 GUID は索引にありません。名前 " + UnityPackageNameSearch.IndexedNames +
                        " 種を照合 → 部分一致以上 " + UnityPackageNameSearch.MatchedNames + " 種 / " +
                        UnityPackageNameSearch.MatchedEntries + " エントリ / " + UnityPackageNameSearch.MatchedPackages +
                        " package → 重複上限 " + UnityPackageNameSearch.CommonNameLimit + " 件 / ありふれた名前を " + UnityPackageNameSearch.CommonNamesExcluded +
                        " 種除外 / 上限超過 " + UnityPackageNameSearch.CandidateLimitExcluded + " 件");
                    foreach (var note in UnityPackageNameSearch.Notes) text.AppendLine("  " + note);
                }
            }
            else text.AppendLine(".unitypackage: 無効");
            text.Append(RecycleBinEnabled
                ? "ごみ箱: " + RecycleBinReturned + " 件（現在の走査キャッシュ）"
                : "ごみ箱: 無効");
            return text.ToString();
        }
    }

    internal interface ICandidateSource
    {
        CandidateSourceKind Kind { get; }
        string DisplayName { get; }
        List<RepairCandidate> Find(ReferenceRecord record);
    }

    internal sealed class ProjectCandidateSource : ICandidateSource
    {
        public CandidateSourceKind Kind => CandidateSourceKind.Project;
        public string DisplayName => "プロジェクト内";
        public List<RepairCandidate> Find(ReferenceRecord record)
        {
            return Find(record, null);
        }

        internal List<RepairCandidate> Find(ReferenceRecord record, ProjectCandidateSearchDiagnostics diagnostics)
        {
            var result = SimilarAssetFinder.Find(record, 8, diagnostics);
            foreach (var item in result)
            {
                item.SourceKind = Kind;
                if (string.IsNullOrEmpty(item.SourceLabel)) item.SourceLabel = DisplayName;
                if (string.IsNullOrEmpty(item.OriginDescription)) item.OriginDescription = "プロジェクト内の名前類似度";
                if (!item.SourceKinds.Contains(Kind)) item.SourceKinds.Add(Kind);
            }
            return result;
        }
    }

    internal sealed class UnityPackageCandidateSource : ICandidateSource
    {
        public CandidateSourceKind Kind => CandidateSourceKind.UnityPackage;
        public string DisplayName => ".unitypackage";
        public List<RepairCandidate> Find(ReferenceRecord record)
        {
            return Find(record, out _, out _);
        }

        internal List<RepairCandidate> Find(ReferenceRecord record,
            out int guidMatchCount, out UnityPackageNameSearchDiagnostics nameDiagnostics)
        {
            var guidMatches = UnityPackageIndexStore.CreateCandidates(record);
            guidMatchCount = guidMatches.Count;
            if (guidMatches.Count > 0)
            {
                nameDiagnostics = new UnityPackageNameSearchDiagnostics();
                return guidMatches;
            }
            return UnityPackageIndexStore.CreateNameCandidates(record, out nameDiagnostics);
        }
    }

    internal sealed class RecycleBinCandidateSource : ICandidateSource
    {
        public CandidateSourceKind Kind => CandidateSourceKind.RecycleBin;
        public string DisplayName => "ごみ箱";
        public List<RepairCandidate> Find(ReferenceRecord record)
        {
            return RecycleBinScanner.CreateCandidates(record, false);
        }
    }

    internal static class CandidateAggregator
    {
        public static List<RepairCandidate> Find(ReferenceRecord record)
        {
            return Find(record, out _);
        }

        public static List<RepairCandidate> Find(ReferenceRecord record, out CandidateSearchDiagnostics diagnostics)
        {
            var settings = AsnSettings.GetOrCreate();
            diagnostics = new CandidateSearchDiagnostics
            {
                ProjectEnabled = settings.projectSearchEnabled,
                UnityPackageEnabled = settings.unityPackageSearchEnabled,
                RecycleBinEnabled = settings.recycleBinSearchEnabled
            };
            var sourceResults = new List<KeyValuePair<ICandidateSource, List<RepairCandidate>>>();
            if (settings.unityPackageSearchEnabled)
            {
                var source = new UnityPackageCandidateSource();
                var found = source.Find(record, out var guidMatches, out var nameDiagnostics);
                diagnostics.UnityPackageReturned = found.Count;
                diagnostics.UnityPackageGuidReturned = guidMatches;
                diagnostics.UnityPackageNameReturned = guidMatches > 0 ? 0 : found.Count;
                diagnostics.UnityPackageNameSearch = nameDiagnostics;
                diagnostics.UnityPackageStatus = UnityPackageIndexStore.GetStatus();
                sourceResults.Add(new KeyValuePair<ICandidateSource, List<RepairCandidate>>(source, found));
            }
            if (settings.recycleBinSearchEnabled)
            {
                var source = new RecycleBinCandidateSource();
                var found = source.Find(record);
                diagnostics.RecycleBinReturned = found.Count;
                sourceResults.Add(new KeyValuePair<ICandidateSource, List<RepairCandidate>>(source, found));
            }
            if (settings.projectSearchEnabled)
            {
                var source = new ProjectCandidateSource();
                var found = source.Find(record, diagnostics.Project);
                diagnostics.ProjectReturned = found.Count;
                sourceResults.Add(new KeyValuePair<ICandidateSource, List<RepairCandidate>>(source, found));
            }
            diagnostics.NameHints = SimilarAssetFinder.BuildHints(record);

            var mergedIndexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var result = new List<RepairCandidate>();
            foreach (var sourceResult in sourceResults)
            {
                var source = sourceResult.Key;
                foreach (var candidate in sourceResult.Value)
                {
                    if (!candidate.SourceKinds.Contains(source.Kind)) candidate.SourceKinds.Add(source.Kind);
                    // 外部候補は「インポート／回収」という別アクションなので、プロジェクト内候補とは統合しない。
                    if (!candidate.CanRepair)
                    {
                        result.Add(candidate);
                        continue;
                    }
                    var key = candidate.Guid + ":" + candidate.FileId;
                    if (!mergedIndexes.TryGetValue(key, out var currentIndex))
                    {
                        mergedIndexes[key] = result.Count;
                        result.Add(candidate);
                        continue;
                    }
                    var current = result[currentIndex];
                    foreach (var kind in candidate.SourceKinds)
                        if (!current.SourceKinds.Contains(kind)) current.SourceKinds.Add(kind);
                    if (candidate.Certainty < current.Certainty ||
                        (candidate.Certainty == current.Certainty && candidate.Score > current.Score))
                    {
                        candidate.SourceKinds.Clear();
                        candidate.SourceKinds.AddRange(current.SourceKinds);
                        result[currentIndex] = candidate;
                    }
                }
            }
            // LINQ の OrderBy は安定ソートなので、同点時は各探索モードが返した従来順を維持する。
            return result.OrderBy(item => item.Certainty).ThenByDescending(item => item.Score).ToList();
        }
    }
}
