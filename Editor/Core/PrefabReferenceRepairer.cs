using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Maaaaa.Asn.Editor.Core
{
    internal static class PrefabReferenceRepairer
    {
        private static readonly Regex FileIdTokenRegex = new Regex(@"(\bfileID:\s*)-?\d+", RegexOptions.Compiled);
        private static readonly Regex GuidTokenRegex = new Regex(@"(\bguid:\s*)[0-9a-fA-F]{32}", RegexOptions.Compiled);

        public static RepairResult Repair(ReferenceRecord source, RepairCandidate target)
        {
            var result = new RepairResult();
            if (source == null || target == null || target.Asset == null) { result.Message = "修復対象または候補が無効です。"; return result; }
            if (string.IsNullOrEmpty(target.AssetPath) || PrefabReferenceScanner.IsBackingFileMissing(target.AssetPath))
            {
                result.Message = "差し替え先のファイル本体が存在しません。AssetDatabase のキャッシュ上にだけ残った候補の可能性があります。再検査してください。";
                return result;
            }
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && string.Equals(stage.assetPath, source.SourceAssetPath, StringComparison.OrdinalIgnoreCase))
            {
                result.Message = "対象 Prefab が Prefab Mode で開かれています。閉じてから修復してください。";
                return result;
            }

            var sourceAbsolute = AssetPathUtility.ToAbsolutePath(source.SourceAssetPath);
            var runDirectory = ExecutionLogger.CreateRunDirectory();
            result.BackupDirectory = runDirectory;
            try
            {
                // P-2: 承認の前であっても、修復処理に入った時点で最初に退避する。
                File.Copy(sourceAbsolute, Path.Combine(runDirectory, Path.GetFileName(sourceAbsolute)), true);
                var canUseSerializedProperty = !source.IsScript && !source.IsModificationTarget && HasSerializedProperty(source);
                var candidateWarning = target.Certainty == CandidateCertainty.Guess ? "\n" + AsnText.GuessWarning : string.Empty;
                if (canUseSerializedProperty && !EditorUtility.DisplayDialog(AsnText.WindowTitle, AsnText.RepairWarning + "\n\n保存先: " + runDirectory + "\n\n候補: " + target.AssetPath + candidateWarning, "修復する", "キャンセル"))
                {
                    result.Message = "ユーザーが修復をキャンセルしました。バックアップは保存済みです。";
                    result.Method = RepairMethod.SerializedProperty;
                    ExecutionLogger.WriteRepair(runDirectory, source, target, result.Method, false, result.Message);
                    return result;
                }

                if (canUseSerializedProperty && PrefabReferenceScanner.IsBackingFileMissing(target.AssetPath))
                {
                    result.Message = "承認待ちの間に差し替え先のファイル本体が無くなりました。何も書き換えていません。再検査してください。";
                    result.Method = RepairMethod.SerializedProperty;
                    ExecutionLogger.WriteRepair(runDirectory, source, target, result.Method, false, result.Message);
                    return result;
                }

                if (canUseSerializedProperty && TrySerializedProperty(source, target.Asset, out var serializedMessage))
                {
                    result.Success = true;
                    result.Method = RepairMethod.SerializedProperty;
                    result.Message = serializedMessage;
                }
                else
                {
                    result.Method = RepairMethod.Yaml;
                    result = RepairYaml(source, target, runDirectory);
                }
            }
            catch (Exception exception)
            {
                result.Message = exception.Message;
            }
            ExecutionLogger.WriteRepair(runDirectory, source, target, result.Method, result.Success, result.Message);
            return result;
        }

        public static RepairResult RepairPrefabGroup(PrefabReferenceGroup group, RepairCandidate target)
        {
            var result = new RepairResult { Method = RepairMethod.Yaml };
            var changes = new List<PrefabRepairLineChange>();
            if (group == null || target == null || target.Asset == null || group.References.Count == 0 ||
                string.IsNullOrEmpty(group.SourceAssetPath) || string.IsNullOrEmpty(group.Guid))
            {
                result.Message = "Prefab グループまたは候補が無効です。";
                return result;
            }
            if (string.IsNullOrEmpty(target.AssetPath) || string.IsNullOrEmpty(target.Guid) ||
                !target.AssetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                result.Message = "Prefab グループの差し替え先には Prefab を指定してください。";
                return result;
            }
            if (PrefabReferenceScanner.IsBackingFileMissing(target.AssetPath))
            {
                result.Message = "差し替え先の Prefab ファイル本体が存在しません。AssetDatabase のキャッシュ上にだけ残った候補の可能性があります。再検査してください。";
                return result;
            }
            if (string.Equals(group.Guid, target.Guid, StringComparison.OrdinalIgnoreCase))
            {
                result.Message = "差し替え前後の GUID が同一です。";
                return result;
            }
            if (group.References.Any(item => !string.Equals(item.SourceAssetPath, group.SourceAssetPath, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(item.Guid, group.Guid, StringComparison.OrdinalIgnoreCase)))
            {
                result.Message = "異なるファイルまたは GUID の参照がグループに混在しています。再検査してください。";
                return result;
            }

            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && string.Equals(stage.assetPath, group.SourceAssetPath, StringComparison.OrdinalIgnoreCase))
            {
                result.Message = "対象 Prefab が Prefab Mode で開かれています。閉じてから修復してください。";
                return result;
            }

            var sourceAbsolute = AssetPathUtility.ToAbsolutePath(group.SourceAssetPath);
            var runDirectory = ExecutionLogger.CreateRunDirectory();
            result.BackupDirectory = runDirectory;
            try
            {
                // P-2: 書き換えへ進む前に、グループ全体の対象ファイルを 1 回だけ退避する。
                File.Copy(sourceAbsolute, Path.Combine(runDirectory, Path.GetFileName(sourceAbsolute)), true);
                result = RepairPrefabGroupYaml(group, target, runDirectory, changes);
            }
            catch (Exception exception)
            {
                result = new RepairResult
                {
                    BackupDirectory = runDirectory,
                    Method = RepairMethod.Yaml,
                    Message = exception.Message
                };
            }
            ExecutionLogger.WritePrefabGroupRepair(runDirectory, group, target, changes, result.Success, result.Message);
            return result;
        }

        private static RepairResult RepairPrefabGroupYaml(PrefabReferenceGroup group, RepairCandidate target,
            string runDirectory, List<PrefabRepairLineChange> changes)
        {
            var result = new RepairResult { BackupDirectory = runDirectory, Method = RepairMethod.Yaml };
            var absolutePath = AssetPathUtility.ToAbsolutePath(group.SourceAssetPath);
            var lines = File.ReadAllLines(absolutePath);
            var records = group.References
                .GroupBy(item => item.LineNumber + ":" + item.ReferenceColumn)
                .Select(item => item.First())
                .OrderBy(item => item.LineNumber)
                .ThenByDescending(item => item.ReferenceColumn)
                .ToList();

            // all-or-nothing: 置換文字列を作る前に、全参照を元ファイルに対して検証する。
            foreach (var record in records)
            {
                if (record.LineNumber < 1 || record.LineNumber > lines.Length ||
                    !string.Equals(lines[record.LineNumber - 1], record.RawLine, StringComparison.Ordinal) ||
                    record.ReferenceColumn < 0 || record.ReferenceColumn + record.RawReference.Length > record.RawLine.Length ||
                    !string.Equals(record.RawLine.Substring(record.ReferenceColumn, record.RawReference.Length), record.RawReference, StringComparison.Ordinal) ||
                    !GuidTokenRegex.IsMatch(record.RawReference))
                {
                    result.Message = "検査時の参照位置が現在のファイルと一致しません。何も書き換えていません。再検査してください。";
                    return result;
                }
            }

            foreach (var lineGroup in records.GroupBy(item => item.LineNumber).OrderBy(item => item.Key))
            {
                var oldLine = lines[lineGroup.Key - 1];
                var newLine = oldLine;
                foreach (var record in lineGroup.OrderByDescending(item => item.ReferenceColumn))
                {
                    // Prefab グループでは GUID のみを変更する。各参照固有の fileID / type は保持する。
                    var replacement = GuidTokenRegex.Replace(record.RawReference,
                        match => match.Groups[1].Value + target.Guid, 1);
                    newLine = newLine.Substring(0, record.ReferenceColumn) + replacement +
                        newLine.Substring(record.ReferenceColumn + record.RawReference.Length);
                }
                if (!string.Equals(oldLine, newLine, StringComparison.Ordinal))
                    changes.Add(new PrefabRepairLineChange { LineNumber = lineGroup.Key, OldLine = oldLine, NewLine = newLine });
            }
            if (changes.Count == 0)
            {
                result.Message = "GUID の置換対象がありません。何も書き換えていません。";
                return result;
            }

            var overrideRecords = group.OverrideReferences.ToList();
            var candidateFileIds = CollectCandidateFileIds(target.AssetPath);
            var resolvedOverrideCount = overrideRecords.Count(item => candidateFileIds.Contains(item.FileId));
            var resolution = "override " + overrideRecords.Count + " 件中 " + resolvedOverrideCount + " 件が差し替え先で解決します。";
            var assessment = resolvedOverrideCount == overrideRecords.Count
                ? "全 override の fileID が存在します。同じ実体の Prefab である可能性が高い候補です。"
                : resolvedOverrideCount == 0 && overrideRecords.Count > 0
                    ? "警告: 解決する override が 1 件もありません。別物の Prefab である可能性が高く、調整値が失われ得ます。"
                    : "警告: 解決しない override があります。その調整値は Unity に破棄される可能性があります。";
            var representativeDiff = BuildRepresentativeDiff(group.SourceAssetPath, changes, 5);
            var guess = target.Certainty == CandidateCertainty.Guess ? "\n" + AsnText.GuessWarning : string.Empty;
            var otherFiles = group.OtherSourceAssetPaths.Count == 0 ? string.Empty :
                "\n\n同じ GUID は他の Prefab からも参照されていますが、この操作では書き換えません:\n" +
                string.Join("\n", group.OtherSourceAssetPaths.ToArray());
            var preview = AsnText.RepairWarning + guess +
                "\n\nYAML 直接書き換えは Unity 非公式の手法であり、将来の Unity では動作しない可能性があります。" +
                "\n\nGUID をまとめて変更します（fileID / type は保持）" +
                "\nm_SourcePrefab: " + group.SourcePrefabCount + " 件" +
                "\noverride target: " + group.ModificationTargetCount + " 件" +
                "\nobjectReference: " + group.ObjectReferenceCount + " 件" +
                "\nその他の同一 GUID 参照: " + Math.Max(0, records.Count - group.SourcePrefabCount - group.ModificationTargetCount - group.ObjectReferenceCount) + " 件" +
                "\n合計参照: " + records.Count + " 件 / 変更行: " + changes.Count + " 行" +
                "\n\n" + resolution + "\n" + assessment + otherFiles +
                "\n\n代表 Diff（全文は修復ログへ記録します）:\n" + representativeDiff;
            if (!EditorUtility.DisplayDialog("Prefab グループ Diff プレビュー", preview, "この Diff をまとめて適用", "キャンセル"))
            {
                result.Message = "Prefab グループ Diff の適用がキャンセルされました。";
                return result;
            }
            if (PrefabReferenceScanner.IsBackingFileMissing(target.AssetPath))
            {
                result.Message = "承認待ちの間に差し替え先の Prefab ファイル本体が無くなりました。何も書き換えていません。再検査してください。";
                return result;
            }

            ReplaceLinesPreservingFormat(absolutePath, changes);
            AssetDatabase.ImportAsset(group.SourceAssetPath, ImportAssetOptions.ForceUpdate);
            result.Success = true;
            result.Message = "同一 GUID の参照 " + records.Count + " 件をまとめて書き換え、再インポートしました。\n" + resolution;
            return result;
        }

        private static HashSet<long> CollectCandidateFileIds(string assetPath)
        {
            var result = new HashSet<long>();
            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(assetPath))
            {
                if (asset != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string _, out long fileId))
                    result.Add(fileId);
            }
            return result;
        }

        private static string BuildRepresentativeDiff(string assetPath, List<PrefabRepairLineChange> changes, int limit)
        {
            var text = new StringBuilder();
            foreach (var change in changes.Take(limit))
            {
                text.AppendLine("@@ " + assetPath + ":" + change.LineNumber + " @@");
                text.AppendLine("- " + change.OldLine.Trim());
                text.AppendLine("+ " + change.NewLine.Trim());
            }
            if (changes.Count > limit) text.AppendLine("... 他 " + (changes.Count - limit) + " 行");
            return text.ToString();
        }

        private static bool HasSerializedProperty(ReferenceRecord source)
        {
            var root = PrefabUtility.LoadPrefabContents(source.SourceAssetPath);
            try
            {
                var transform = FindTransform(root.transform, source.GameObjectPath);
                if (transform == null) return false;
                foreach (var component in transform.GetComponents<Component>())
                {
                    if (component == null) continue;
                    if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(component, out string _, out long componentFileId) || componentFileId != source.SourceObjectFileId) continue;
                    var property = new SerializedObject(component).GetIterator();
                    while (property.NextVisible(true))
                        if (property.propertyType == SerializedPropertyType.ObjectReference && property.name == source.PropertyName)
                            return true;
                }
                return false;
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        private static bool TrySerializedProperty(ReferenceRecord source, UnityEngine.Object target, out string message)
        {
            message = string.Empty;
            var root = PrefabUtility.LoadPrefabContents(source.SourceAssetPath);
            try
            {
                var transform = FindTransform(root.transform, source.GameObjectPath);
                if (transform == null) return false;
                foreach (var component in transform.GetComponents<Component>())
                {
                    if (component == null) continue;
                    if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(component, out string _, out long componentFileId) || componentFileId != source.SourceObjectFileId) continue;
                    var serialized = new SerializedObject(component);
                    var property = serialized.GetIterator();
                    while (property.NextVisible(true))
                    {
                        if (property.propertyType != SerializedPropertyType.ObjectReference || property.name != source.PropertyName) continue;
                        property.objectReferenceValue = target;
                        serialized.ApplyModifiedPropertiesWithoutUndo();
                        PrefabUtility.SaveAsPrefabAsset(root, source.SourceAssetPath);
                        message = "SerializedProperty で参照を修復しました。";
                        return true;
                    }
                }
                return false;
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        private static Transform FindTransform(Transform root, string path)
        {
            if (root == null) return null;
            if (string.IsNullOrEmpty(path) || path == root.name) return root;
            var relative = path.StartsWith(root.name + "/", StringComparison.Ordinal) ? path.Substring(root.name.Length + 1) : path;
            return root.Find(relative);
        }

        private static RepairResult RepairYaml(ReferenceRecord source, RepairCandidate target, string runDirectory)
        {
            var result = new RepairResult { BackupDirectory = runDirectory, Method = RepairMethod.Yaml };
            var absolutePath = AssetPathUtility.ToAbsolutePath(source.SourceAssetPath);
            var lines = File.ReadAllLines(absolutePath);
            if (source.LineNumber < 1 || source.LineNumber > lines.Length) { result.Message = "記録された行番号が現在のファイルと一致しません。再検査してください。"; return result; }
            var oldLine = lines[source.LineNumber - 1];
            if (!string.Equals(oldLine, source.RawLine, StringComparison.Ordinal)) { result.Message = "検査後にファイルが変更されています。再検査してください。"; return result; }
            var replacement = FileIdTokenRegex.Replace(source.RawReference, match => match.Groups[1].Value + target.FileId, 1);
            replacement = GuidTokenRegex.Replace(replacement, match => match.Groups[1].Value + target.Guid, 1);
            if (source.ReferenceColumn < 0 || source.ReferenceColumn + source.RawReference.Length > oldLine.Length ||
                !string.Equals(oldLine.Substring(source.ReferenceColumn, source.RawReference.Length), source.RawReference, StringComparison.Ordinal))
            {
                result.Message = "検査時の参照位置が現在の行と一致しません。再検査してください。";
                return result;
            }
            var newLine = oldLine.Substring(0, source.ReferenceColumn) + replacement + oldLine.Substring(source.ReferenceColumn + source.RawReference.Length);
            if (newLine == oldLine) { result.Message = "置換対象を行内に見つけられません。"; return result; }
            var risk = source.IsScript ? AsnText.MissingScriptRepairWarning : AsnText.RepairWarning;
            var guess = target.Certainty == CandidateCertainty.Guess ? "\n" + AsnText.GuessWarning : string.Empty;
            var diff = $"{risk}{guess}\n\nYAML 直接書き換えは Unity 非公式の手法であり、将来の Unity では動作しない可能性があります。\n\n@@ {source.SourceAssetPath}:{source.LineNumber} @@\n- {oldLine.Trim()}\n+ {newLine.Trim()}";
            if (!EditorUtility.DisplayDialog("Diff プレビュー", diff, "この Diff を適用", "キャンセル")) { result.Message = "Diff の適用がキャンセルされました。"; return result; }
            if (PrefabReferenceScanner.IsBackingFileMissing(target.AssetPath))
            {
                result.Message = "承認待ちの間に差し替え先のファイル本体が無くなりました。何も書き換えていません。再検査してください。";
                return result;
            }
            ReplaceLinePreservingFormat(absolutePath, source.LineNumber, oldLine, newLine);
            AssetDatabase.ImportAsset(source.SourceAssetPath, ImportAssetOptions.ForceUpdate);
            result.Success = true;
            result.Message = "YAML の対象行のみを書き換えて再インポートしました。";
            return result;
        }

        private static void ReplaceLinesPreservingFormat(string path, IReadOnlyList<PrefabRepairLineChange> changes)
        {
            var bytes = File.ReadAllBytes(path);
            Encoding encoding;
            var hasBom = false;
            if (bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf) { encoding = new UTF8Encoding(true); hasBom = true; }
            else if (bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe) { encoding = Encoding.Unicode; hasBom = true; }
            else { encoding = new UTF8Encoding(false); }
            var text = encoding.GetString(bytes);
            if (hasBom && text.Length > 0 && text[0] == '\ufeff') text = text.Substring(1);

            var spans = new Dictionary<int, Tuple<int, int>>();
            var requestedLines = new HashSet<int>(changes.Select(item => item.LineNumber));
            var lineNumber = 1;
            var start = 0;
            while (start <= text.Length)
            {
                var end = start;
                while (end < text.Length && text[end] != '\r' && text[end] != '\n') end++;
                if (requestedLines.Contains(lineNumber)) spans[lineNumber] = Tuple.Create(start, end);
                if (end >= text.Length) break;
                if (text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n') end++;
                start = end + 1;
                lineNumber++;
            }

            foreach (var change in changes.OrderByDescending(item => item.LineNumber))
            {
                if (!spans.TryGetValue(change.LineNumber, out var span) ||
                    !string.Equals(text.Substring(span.Item1, span.Item2 - span.Item1), change.OldLine, StringComparison.Ordinal))
                    throw new IOException("書き込み直前に対象行が変更されました。何も書き換えていません。");
            }
            foreach (var change in changes.OrderByDescending(item => item.LineNumber))
            {
                var span = spans[change.LineNumber];
                text = text.Substring(0, span.Item1) + change.NewLine + text.Substring(span.Item2);
            }

            var output = encoding.GetBytes(text);
            if (hasBom)
            {
                var preamble = encoding.GetPreamble();
                var combined = new byte[preamble.Length + output.Length];
                Buffer.BlockCopy(preamble, 0, combined, 0, preamble.Length);
                Buffer.BlockCopy(output, 0, combined, preamble.Length, output.Length);
                output = combined;
            }
            File.WriteAllBytes(path, output);
        }

        private static void ReplaceLinePreservingFormat(string path, int lineNumber, string oldLine, string newLine)
        {
            var bytes = File.ReadAllBytes(path);
            Encoding encoding;
            var hasBom = false;
            if (bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf) { encoding = new UTF8Encoding(true); hasBom = true; }
            else if (bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe) { encoding = Encoding.Unicode; hasBom = true; }
            else { encoding = new UTF8Encoding(false); }
            var text = encoding.GetString(bytes);
            if (hasBom && text.Length > 0 && text[0] == '\ufeff') text = text.Substring(1);
            var currentLine = 1;
            var start = 0;
            for (var index = 0; index < text.Length && currentLine < lineNumber; index++)
            {
                if (text[index] != '\n' && text[index] != '\r') continue;
                if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
                currentLine++;
                start = index + 1;
            }
            var end = start;
            while (end < text.Length && text[end] != '\r' && text[end] != '\n') end++;
            if (!string.Equals(text.Substring(start, end - start), oldLine, StringComparison.Ordinal)) throw new IOException("書き込み直前に対象行が変更されました。");
            text = text.Substring(0, start) + newLine + text.Substring(end);
            var output = encoding.GetBytes(text);
            if (hasBom)
            {
                var preamble = encoding.GetPreamble();
                var combined = new byte[preamble.Length + output.Length];
                Buffer.BlockCopy(preamble, 0, combined, 0, preamble.Length);
                Buffer.BlockCopy(output, 0, combined, preamble.Length, output.Length);
                output = combined;
            }
            File.WriteAllBytes(path, output);
        }
    }
}
