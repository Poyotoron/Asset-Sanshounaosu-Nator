using System;
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
    }
}
