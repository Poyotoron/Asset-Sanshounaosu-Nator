using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Maaaaa.Asn.Editor.Core
{
    internal enum IssueKind { None, GuidMissing, FileIdMissing, MissingScript, EmptyReference, TypeMismatch }
    internal enum IssueSeverity { None, Warning, Error }
    internal enum CandidateCertainty { Certain, Guess }
    internal enum CandidateSourceKind { Project = 1, UnityPackage = 2, RecycleBin = 3, SubAsset = 10, MonoScript = 11 }
    internal enum RepairMethod { SerializedProperty, Yaml }

    [Serializable]
    internal sealed class ReferenceRecord
    {
        public string SourceAssetPath;
        public int LineNumber;
        public string RawLine;
        public string RawReference;
        public int ReferenceColumn;
        public long SourceObjectFileId;
        public string PropertyName;
        public string GameObjectPath;
        public string GameObjectName;
        public string ComponentType;
        public string Guid;
        public long FileId;
        public int Type;
        public bool IsScript;
        public bool IsModificationTarget;
        public string ResolvedAssetPath;
        public string ReferencedName;
        public bool GuidResolved;
        public bool FileIdResolved;
        public bool BackingFileMissing;
        public Type ExpectedType;
        public Type ResolvedType;
        public string TypeAssessment;
        public IssueKind Issue;
        public IssueSeverity Severity;
        public bool CollapsedByDefault;

        public string DisplayPath => string.IsNullOrEmpty(GameObjectPath) ? "(Prefab)" : GameObjectPath;
    }

    internal sealed class InspectionResult
    {
        public string RootAssetPath;
        public bool RootFileMissing;
        public readonly List<ReferenceRecord> References = new List<ReferenceRecord>();
        public readonly List<string> Errors = new List<string>();
        public IEnumerable<ReferenceRecord> Issues
        {
            get
            {
                foreach (var item in References)
                    if (item.Issue != IssueKind.None)
                        yield return item;
            }
        }
    }

    internal sealed class RepairCandidate
    {
        public UnityEngine.Object Asset;
        public string AssetPath;
        public string Guid;
        public long FileId;
        public float Score;
        public string ScoreReason;
        public CandidateCertainty Certainty = CandidateCertainty.Guess;
        public CandidateSourceKind SourceKind = CandidateSourceKind.Project;
        public readonly List<CandidateSourceKind> SourceKinds = new List<CandidateSourceKind>();
        public string SourceLabel;
        public string OriginDescription;
        public bool CanRepair = true;
        public string ExternalPath;
        public string OriginalAssetPath;
        public readonly List<string> PackagePaths = new List<string>();
        public RecycleBinEntry RecycleEntry;
    }

    internal sealed class PrefabReferenceGroup
    {
        public string SourceAssetPath;
        public string Guid;
        public readonly List<ReferenceRecord> References = new List<ReferenceRecord>();
        public readonly List<string> OtherSourceAssetPaths = new List<string>();
        public int SourcePrefabCount => References.Count(item => item.PropertyName == "m_SourcePrefab");
        public int ModificationTargetCount => References.Count(item => item.IsModificationTarget);
        public int ObjectReferenceCount => References.Count(item => item.PropertyName == "objectReference");
        public IEnumerable<ReferenceRecord> OverrideReferences => References.Where(item =>
            item.IsModificationTarget || item.PropertyName == "objectReference");
    }

    internal sealed class PrefabRepairLineChange
    {
        public int LineNumber;
        public string OldLine;
        public string NewLine;
    }

    internal sealed class BatchInspectionResult
    {
        public readonly List<InspectionResult> Results = new List<InspectionResult>();
        public readonly List<string> Errors = new List<string>();
        public bool Cancelled;
        public int InspectedCount => Results.Count;
        public int ProblemPrefabCount => Results.Count(item => item.Errors.Count > 0 || item.Issues.Any());
        public int IssueCount
        {
            get
            {
                var count = 0;
                foreach (var result in Results)
                    foreach (var unused in result.Issues) count++;
                return count;
            }
        }
    }

    internal sealed class RepairResult
    {
        public bool Success;
        public string Message;
        public string BackupDirectory;
        public RepairMethod Method;
    }
}
