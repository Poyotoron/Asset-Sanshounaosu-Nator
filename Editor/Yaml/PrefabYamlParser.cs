using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Maaaaa.Asn.Editor.Core;

namespace Maaaaa.Asn.Editor.Yaml
{
    internal static class PrefabYamlParser
    {
        private static readonly Regex DocumentRegex = new Regex(@"^--- !u!(?<class>-?\d+) &(?<id>-?\d+)", RegexOptions.Compiled);
        private static readonly Regex ReferenceRegex = new Regex(@"\{\s*fileID:\s*(?<file>-?\d+)(?:\s*,\s*guid:\s*(?<guid>[0-9a-fA-F]{32}))?(?:\s*,\s*type:\s*(?<type>\d+))?\s*\}", RegexOptions.Compiled);
        private static readonly Regex PropertyRegex = new Regex(@"^\s*(?:-\s*)?(?<name>[A-Za-z_][A-Za-z0-9_\.]*):", RegexOptions.Compiled);
        private static readonly Regex NameRegex = new Regex(@"^\s*m_Name:\s*(?<name>.*)$", RegexOptions.Compiled);
        private static readonly Regex ComponentRegex = new Regex(@"^\s*-\s*component:\s*\{fileID:\s*(?<id>-?\d+)\}", RegexOptions.Compiled);
        private static readonly Regex GameObjectRegex = new Regex(@"^\s*m_GameObject:\s*\{fileID:\s*(?<id>-?\d+)\}", RegexOptions.Compiled);
        private static readonly Regex FatherRegex = new Regex(@"^\s*m_Father:\s*\{fileID:\s*(?<id>-?\d+)\}", RegexOptions.Compiled);
        private static readonly Regex PropertyPathRegex = new Regex(@"^\s*propertyPath:\s*(?<path>.*)$", RegexOptions.Compiled);
        private static readonly Regex ValueRegex = new Regex(@"^\s*value:\s*(?<value>.*)$", RegexOptions.Compiled);

        private sealed class Document
        {
            public long Id;
            public int ClassId;
            public string Name;
            public long GameObjectId;
            public long FatherTransformId;
            public string ReferencedName;
            public readonly List<long> Components = new List<long>();
        }

        public static List<ReferenceRecord> Parse(string assetPath, string absolutePath)
        {
            var lines = File.ReadAllLines(absolutePath);
            var documents = ReadDocuments(lines);
            var componentToGameObject = new Dictionary<long, long>();
            var transformToGameObject = new Dictionary<long, long>();
            foreach (var pair in documents)
            {
                foreach (var component in pair.Value.Components)
                    componentToGameObject[component] = pair.Key;
                if (pair.Value.ClassId == 4 || pair.Value.ClassId == 224)
                    transformToGameObject[pair.Key] = pair.Value.GameObjectId;
            }

            var records = new List<ReferenceRecord>();
            Document current = null;
            var inModifications = false;
            var modificationsIndent = -1;
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index];
                var docMatch = DocumentRegex.Match(line);
                if (docMatch.Success)
                {
                    documents.TryGetValue(ParseLong(docMatch.Groups["id"].Value), out current);
                    inModifications = false;
                }

                var trimmed = line.TrimStart();
                var indent = line.Length - trimmed.Length;
                if (trimmed.StartsWith("m_Modifications:", StringComparison.Ordinal))
                {
                    inModifications = true;
                    modificationsIndent = indent;
                }
                else if (inModifications && indent <= modificationsIndent && trimmed.Length > 0 && !trimmed.StartsWith("-", StringComparison.Ordinal))
                    inModifications = false;

                var matches = ReferenceRegex.Matches(line);
                foreach (Match match in matches)
                {
                    var propertyMatch = PropertyRegex.Match(line);
                    var propertyName = propertyMatch.Success ? propertyMatch.Groups["name"].Value : "(不明)";
                    var guid = match.Groups["guid"].Success ? match.Groups["guid"].Value.ToLowerInvariant() : string.Empty;
                    var fileId = ParseLong(match.Groups["file"].Value);
                    if (string.IsNullOrEmpty(guid) && fileId != 0)
                        continue; // ローカルオブジェクト参照は AssetDatabase 照合の対象外。

                    var gameObjectId = ResolveGameObjectId(current, componentToGameObject, transformToGameObject);
                    var gameObjectName = documents.TryGetValue(gameObjectId, out var gameObject) ? gameObject.Name : string.Empty;
                    records.Add(new ReferenceRecord
                    {
                        SourceAssetPath = assetPath,
                        LineNumber = index + 1,
                        RawLine = line,
                        RawReference = match.Value,
                        ReferenceColumn = match.Index,
                        SourceObjectFileId = current != null ? current.Id : 0,
                        ReferencedName = propertyName == "m_SourcePrefab" && current != null ? current.ReferencedName : string.Empty,
                        PropertyName = propertyName,
                        GameObjectName = gameObjectName,
                        GameObjectPath = BuildPath(gameObjectId, documents, transformToGameObject),
                        ComponentType = ClassName(current != null ? current.ClassId : 0),
                        Guid = guid,
                        FileId = fileId,
                        Type = match.Groups["type"].Success ? (int)ParseLong(match.Groups["type"].Value) : 0,
                        IsScript = propertyName == "m_Script",
                        IsModificationTarget = inModifications && propertyName == "target"
                    });
                }
            }
            return records;
        }

        private static Dictionary<long, Document> ReadDocuments(string[] lines)
        {
            var result = new Dictionary<long, Document>();
            Document current = null;
            var waitingForNameValue = false;
            foreach (var line in lines)
            {
                var header = DocumentRegex.Match(line);
                if (header.Success)
                {
                    current = new Document { Id = ParseLong(header.Groups["id"].Value), ClassId = (int)ParseLong(header.Groups["class"].Value) };
                    result[current.Id] = current;
                    waitingForNameValue = false;
                    continue;
                }
                if (current == null) continue;
                var name = NameRegex.Match(line);
                if (name.Success) current.Name = name.Groups["name"].Value.Trim();
                var component = ComponentRegex.Match(line);
                if (component.Success) current.Components.Add(ParseLong(component.Groups["id"].Value));
                var gameObject = GameObjectRegex.Match(line);
                if (gameObject.Success) current.GameObjectId = ParseLong(gameObject.Groups["id"].Value);
                var father = FatherRegex.Match(line);
                if (father.Success) current.FatherTransformId = ParseLong(father.Groups["id"].Value);
                if (current.ClassId == 1001)
                {
                    var propertyPath = PropertyPathRegex.Match(line);
                    if (propertyPath.Success)
                        waitingForNameValue = propertyPath.Groups["path"].Value.Trim() == "m_Name";
                    else if (waitingForNameValue)
                    {
                        var value = ValueRegex.Match(line);
                        if (value.Success)
                        {
                            if (string.IsNullOrEmpty(current.ReferencedName))
                                current.ReferencedName = NormalizeYamlScalar(value.Groups["value"].Value);
                            waitingForNameValue = false;
                        }
                    }
                }
            }
            return result;
        }

        private static string NormalizeYamlScalar(string value)
        {
            value = (value ?? string.Empty).Trim();
            if (value.Length < 2) return value;
            if (value[0] == '\'' && value[value.Length - 1] == '\'')
                return value.Substring(1, value.Length - 2).Replace("''", "'");
            if (value[0] != '"' || value[value.Length - 1] != '"') return value;

            // YAML の Unicode / backslash escape は二重引用符スカラー内だけで解釈する。
            // 逐次処理により "\\\\u0041" はリテラル "\\u0041" のまま保つ。
            var content = value.Substring(1, value.Length - 2);
            var decoded = new StringBuilder(content.Length);
            for (var index = 0; index < content.Length; index++)
            {
                var current = content[index];
                if (current != '\\' || index + 1 >= content.Length)
                {
                    decoded.Append(current);
                    continue;
                }
                var escape = content[++index];
                if (escape == 'u' && index + 4 < content.Length && IsHex(content, index + 1, 4))
                {
                    decoded.Append((char)int.Parse(content.Substring(index + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    index += 4;
                }
                else if (escape == '\\') decoded.Append('\\');
                else if (escape == '"') decoded.Append('"');
                else
                {
                    // 未対応 escape は情報を失わないよう元の 2 文字を維持する。
                    decoded.Append('\\').Append(escape);
                }
            }
            return decoded.ToString();
        }

        private static bool IsHex(string value, int start, int count)
        {
            if (start < 0 || start + count > value.Length) return false;
            for (var index = start; index < start + count; index++)
                if (!Uri.IsHexDigit(value[index])) return false;
            return true;
        }

        private static long ResolveGameObjectId(Document document, Dictionary<long, long> componentMap, Dictionary<long, long> transformMap)
        {
            if (document == null) return 0;
            if (document.ClassId == 1) return document.Id;
            if (document.GameObjectId != 0) return document.GameObjectId;
            if (componentMap.TryGetValue(document.Id, out var gameObjectId)) return gameObjectId;
            return transformMap.TryGetValue(document.Id, out gameObjectId) ? gameObjectId : 0;
        }

        private static string BuildPath(long gameObjectId, Dictionary<long, Document> documents, Dictionary<long, long> transformMap)
        {
            if (gameObjectId == 0 || !documents.TryGetValue(gameObjectId, out var gameObject)) return string.Empty;
            var names = new List<string>();
            var visited = new HashSet<long>();
            while (gameObject != null && visited.Add(gameObject.Id))
            {
                names.Add(string.IsNullOrEmpty(gameObject.Name) ? "(名称なし)" : gameObject.Name);
                Document transform = null;
                foreach (var componentId in gameObject.Components)
                    if (documents.TryGetValue(componentId, out var candidate) && (candidate.ClassId == 4 || candidate.ClassId == 224)) { transform = candidate; break; }
                if (transform == null || transform.FatherTransformId == 0 || !transformMap.TryGetValue(transform.FatherTransformId, out var parentId) || !documents.TryGetValue(parentId, out gameObject))
                    break;
            }
            names.Reverse();
            return string.Join("/", names.ToArray());
        }

        private static string ClassName(int classId)
        {
            switch (classId)
            {
                case 1: return "GameObject";
                case 4: return "Transform";
                case 114: return "MonoBehaviour";
                case 224: return "RectTransform";
                case 1001: return "PrefabInstance";
                default: return "ClassID " + classId.ToString(CultureInfo.InvariantCulture);
            }
        }

        private static long ParseLong(string value)
        {
            return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0L;
        }
    }
}
