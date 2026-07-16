using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Maaaaa.Asn.Editor.Core
{
    internal static class ExecutionLogger
    {
        public static string CreateRunDirectory()
        {
            var root = Path.Combine(AssetPathUtility.ProjectRoot, "AssetSanshounaosuNator_Backup");
            Directory.CreateDirectory(root);
            var baseName = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var path = Path.Combine(root, baseName);
            var suffix = 1;
            while (Directory.Exists(path)) path = Path.Combine(root, baseName + "_" + suffix++);
            Directory.CreateDirectory(path);
            return path;
        }

        public static void WriteInspection(InspectionResult result)
        {
            var directory = CreateRunDirectory();
            var text = new StringBuilder();
            text.AppendLine("# ASN 検査ログ");
            text.AppendLine("timestamp: " + DateTime.Now.ToString("O"));
            text.AppendLine("target: " + result.RootAssetPath);
            foreach (var item in result.Issues)
                text.AppendLine($"issue: {ReferenceClassifier.Label(item)} | file={item.SourceAssetPath} | line={item.LineNumber} | guid={item.Guid} | fileID={item.FileId} | property={item.PropertyName}");
            foreach (var error in result.Errors) text.AppendLine("error: " + error);
            File.WriteAllText(Path.Combine(directory, "inspection.md"), text.ToString());
        }

        public static void WriteBatchInspection(BatchInspectionResult batch)
        {
            var directory = CreateRunDirectory();
            var text = new StringBuilder();
            text.AppendLine("# ASN 一括検査ログ");
            text.AppendLine("timestamp: " + DateTime.Now.ToString("O"));
            text.AppendLine("cancelled: " + batch.Cancelled.ToString().ToLowerInvariant());
            text.AppendLine("prefabs: " + batch.InspectedCount);
            text.AppendLine("problemPrefabs: " + batch.ProblemPrefabCount);
            text.AppendLine("issues: " + batch.IssueCount);
            foreach (var result in batch.Results)
            {
                text.AppendLine();
                text.AppendLine("## " + result.RootAssetPath);
                foreach (var item in result.Issues)
                    text.AppendLine($"issue: {ReferenceClassifier.Label(item)} | file={item.SourceAssetPath} | line={item.LineNumber} | guid={item.Guid} | fileID={item.FileId} | property={item.PropertyName}");
                foreach (var error in result.Errors) text.AppendLine("error: " + error);
            }
            foreach (var error in batch.Errors) text.AppendLine("error: " + error);
            File.WriteAllText(Path.Combine(directory, "batch-inspection.md"), text.ToString());
        }

        public static void WriteRecovery(RecycleBinEntry entry, string destination, bool success, string message)
        {
            var directory = CreateRunDirectory();
            var text = new StringBuilder();
            text.AppendLine("# ASN ごみ箱回収ログ");
            text.AppendLine("timestamp: " + DateTime.Now.ToString("O"));
            text.AppendLine("success: " + success.ToString().ToLowerInvariant());
            text.AppendLine("sourceAsset: " + (entry != null ? entry.RecycledAssetPath : string.Empty));
            text.AppendLine("sourceMeta: " + (entry != null ? entry.RecycledMetaPath : string.Empty));
            text.AppendLine("destination: " + destination);
            text.AppendLine("message: " + message);
            File.WriteAllText(Path.Combine(directory, "recovery.md"), text.ToString());
        }

        public static void WriteRepair(string directory, ReferenceRecord source, RepairCandidate target, RepairMethod method, bool success, string message)
        {
            var path = Path.Combine(directory, "repair.md");
            var text = new StringBuilder();
            text.AppendLine("## repair");
            text.AppendLine("timestamp: " + DateTime.Now.ToString("O"));
            text.AppendLine("success: " + success.ToString().ToLowerInvariant());
            text.AppendLine("method: " + (method == RepairMethod.SerializedProperty ? "R-1 SerializedProperty" : "R-2 YAML"));
            text.AppendLine("file: " + source.SourceAssetPath);
            text.AppendLine("line: " + source.LineNumber);
            text.AppendLine("oldGuid: " + source.Guid);
            text.AppendLine("oldFileID: " + source.FileId);
            text.AppendLine("newGuid: " + target.Guid);
            text.AppendLine("newFileID: " + target.FileId);
            text.AppendLine("message: " + message);
            text.AppendLine();
            File.AppendAllText(path, text.ToString());
        }

        public static void WritePrefabGroupRepair(string directory, PrefabReferenceGroup group, RepairCandidate target,
            IReadOnlyList<PrefabRepairLineChange> changes, bool success, string message)
        {
            var path = Path.Combine(directory, "repair.md");
            var text = new StringBuilder();
            text.AppendLine("## prefab group repair");
            text.AppendLine("timestamp: " + DateTime.Now.ToString("O"));
            text.AppendLine("success: " + success.ToString().ToLowerInvariant());
            text.AppendLine("method: R-2 YAML group (guid only)");
            text.AppendLine("file: " + group.SourceAssetPath);
            text.AppendLine("oldGuid: " + group.Guid);
            text.AppendLine("newGuid: " + target.Guid);
            text.AppendLine("references: " + group.References.Count);
            text.AppendLine("changedLines: " + changes.Count);
            text.AppendLine("message: " + message);
            if (group.OtherSourceAssetPaths.Count > 0)
            {
                text.AppendLine("notModifiedOtherFiles:");
                foreach (var other in group.OtherSourceAssetPaths) text.AppendLine("- " + other);
            }
            text.AppendLine();
            text.AppendLine("### full diff");
            foreach (var change in changes)
            {
                text.AppendLine("@@ " + group.SourceAssetPath + ":" + change.LineNumber + " @@");
                text.AppendLine("- " + change.OldLine);
                text.AppendLine("+ " + change.NewLine);
            }
            File.AppendAllText(path, text.ToString());
        }
    }
}
