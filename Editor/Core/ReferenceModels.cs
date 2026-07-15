using System;
using System.Collections.Generic;
using UnityEngine;

namespace Maaaaa.Asn.Editor.Core
{
    internal enum IssueKind { None, GuidMissing, FileIdMissing, MissingScript, EmptyReference, TypeMismatch }
    internal enum IssueSeverity { None, Warning, Error }
    internal enum CandidateCertainty { Certain, Guess }
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
    }

    internal sealed class RepairResult
    {
        public bool Success;
        public string Message;
        public string BackupDirectory;
        public RepairMethod Method;
    }
}
