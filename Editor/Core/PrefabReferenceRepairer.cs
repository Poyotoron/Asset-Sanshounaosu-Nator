using System;
using System.IO;
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
                if (canUseSerializedProperty && !EditorUtility.DisplayDialog(AsnText.WindowTitle, AsnText.RepairWarning + "\n\n保存先: " + runDirectory + "\n\n候補: " + target.AssetPath + "\n" + AsnText.GuessWarning, "修復する", "キャンセル"))
                {
                    result.Message = "ユーザーが修復をキャンセルしました。バックアップは保存済みです。";
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
            var diff = $"{AsnText.RepairWarning}\n{AsnText.GuessWarning}\n\nYAML 直接書き換えは Unity 非公式の手法です。\n\n@@ {source.SourceAssetPath}:{source.LineNumber} @@\n- {oldLine.Trim()}\n+ {newLine.Trim()}";
            if (!EditorUtility.DisplayDialog("Diff プレビュー", diff, "この Diff を適用", "キャンセル")) { result.Message = "Diff の適用がキャンセルされました。"; return result; }
            ReplaceLinePreservingFormat(absolutePath, source.LineNumber, oldLine, newLine);
            AssetDatabase.ImportAsset(source.SourceAssetPath, ImportAssetOptions.ForceUpdate);
            result.Success = true;
            result.Message = "YAML の対象行のみを書き換えて再インポートしました。";
            return result;
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
