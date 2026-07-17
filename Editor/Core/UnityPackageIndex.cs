using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Maaaaa.Asn.Editor.Core
{
    [Serializable]
    internal sealed class UnityPackageIndexData
    {
        public int schemaVersion = 1;
        public string lastScanTime;
        public bool scanCompleted;
        public List<UnityPackageFileIndex> packages = new List<UnityPackageFileIndex>();
    }

    [Serializable]
    internal sealed class UnityPackageFileIndex
    {
        public string path;
        public long size;
        public long lastWriteUtcTicks;
        public double scanSeconds;
        public List<UnityPackageGuidEntry> entries = new List<UnityPackageGuidEntry>();
    }

    [Serializable]
    internal sealed class UnityPackageGuidEntry
    {
        public string guid;
        public string originalPath;
    }

    internal struct UnityPackageIndexStatus
    {
        public string LastScanTime;
        public bool Completed;
        public int PackageCount;
        public int GuidCount;
    }

    internal sealed class UnityPackageNameSearchDiagnostics
    {
        public string[] Hints = new string[0];
        public int IndexedNames;
        public int ExtensionExcludedEntries;
        public int MatchedNames;
        public int MatchedEntries;
        public int MatchedPackages;
        public int CommonNameLimit;
        public int CommonNamesExcluded;
        public int CandidateLimitExcluded;
        public int Presented;
        public readonly List<string> Notes = new List<string>();
    }

    internal static class UnityPackageIndexStore
    {
        private sealed class LookupEntry
        {
            public string Guid;
            public string OriginalPath;
            public string PackagePath;
        }

        private const int SchemaVersion = 1;
        private const int MinimumNameScore = 600;
        private const int MaximumPresentedNames = 8;
        private const int MaximumPresentedPackages = 8;
        private const int MinimumCommonNameLimit = 2;
        private const double MaximumPackageRatio = 0.05;
        private static UnityPackageIndexData _cached;
        private static Dictionary<string, List<LookupEntry>> _guidLookup;
        private static Dictionary<string, List<LookupEntry>> _nameLookup;
        private static string IndexPath => Path.Combine(AssetPathUtility.ProjectRoot, "Library", "AssetSanshounaosuNator", "unitypackage-index.json");

        public static UnityPackageIndexData Load()
        {
            if (_cached != null) return _cached;
            try
            {
                if (File.Exists(IndexPath))
                {
                    _cached = JsonUtility.FromJson<UnityPackageIndexData>(File.ReadAllText(IndexPath));
                    if (_cached != null && _cached.schemaVersion == SchemaVersion)
                    {
                        _guidLookup = null;
                        _nameLookup = null;
                        return _cached;
                    }
                }
            }
            catch (Exception exception) { Debug.LogWarning("ASN: .unitypackage 索引を読み込めませんでした: " + exception.Message); }
            _cached = new UnityPackageIndexData { schemaVersion = SchemaVersion };
            _guidLookup = null;
            _nameLookup = null;
            return _cached;
        }

        public static UnityPackageIndexStatus GetStatus()
        {
            var data = Load();
            return new UnityPackageIndexStatus
            {
                LastScanTime = data.lastScanTime,
                Completed = data.scanCompleted,
                PackageCount = data.packages.Count,
                GuidCount = data.packages.Sum(item => item.entries.Count)
            };
        }

        public static List<string> GetOriginalPaths(string guid)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(guid)) return result;
            if (!GetLookup().TryGetValue(guid, out var entries)) return result;
            foreach (var entry in entries)
                if (!result.Contains(entry.OriginalPath)) result.Add(entry.OriginalPath);
            return result;
        }

        public static List<RepairCandidate> CreateCandidates(ReferenceRecord record)
        {
            var matches = new Dictionary<string, RepairCandidate>(StringComparer.OrdinalIgnoreCase);
            if (record == null || string.IsNullOrEmpty(record.Guid)) return matches.Values.ToList();
            if (!GetLookup().TryGetValue(record.Guid, out var entries)) return matches.Values.ToList();
            foreach (var entry in entries)
            {
                if (!matches.TryGetValue(entry.OriginalPath, out var candidate))
                {
                    candidate = new RepairCandidate
                    {
                        Guid = record.Guid,
                        FileId = record.FileId,
                        AssetPath = entry.OriginalPath,
                        OriginalAssetPath = entry.OriginalPath,
                        ExternalPath = entry.PackagePath,
                        Certainty = CandidateCertainty.Certain,
                        Score = 1000f,
                        ScoreReason = "GUID 完全一致",
                        SourceKind = CandidateSourceKind.UnityPackage,
                        SourceLabel = ".unitypackage (確実)",
                        OriginDescription = ".unitypackage の GUID 一致",
                        CanRepair = false
                    };
                    candidate.SourceKinds.Add(CandidateSourceKind.UnityPackage);
                    matches.Add(entry.OriginalPath, candidate);
                }
                if (!candidate.PackagePaths.Contains(entry.PackagePath)) candidate.PackagePaths.Add(entry.PackagePath);
            }
            return matches.Values.ToList();
        }

        public static List<RepairCandidate> CreateNameCandidates(ReferenceRecord record,
            out UnityPackageNameSearchDiagnostics diagnostics)
        {
            diagnostics = new UnityPackageNameSearchDiagnostics();
            var result = new List<RepairCandidate>();
            if (record == null) return result;
            var hints = SimilarAssetFinder.BuildAssetNameHints(record);
            diagnostics.Hints = hints;
            if (hints.Length == 0) return result;

            var lookup = GetNameLookup();
            diagnostics.IndexedNames = lookup.Count;
            var prefabOnly = SimilarAssetFinder.IsPrefabReference(record);
            var indexedPackageCount = Math.Max(1, Load().packages.Count);
            // 実測では 93.1% の名前が 1～2 package に収まる。小規模索引では 5% を上限にし、
            // 受理した GUID/package をすべて提示できるよう表示上限 8 件を絶対上限にする。
            var commonNameLimit = Math.Min(MaximumPresentedPackages,
                Math.Max(MinimumCommonNameLimit, (int)Math.Ceiling(indexedPackageCount * MaximumPackageRatio)));
            diagnostics.CommonNameLimit = commonNameLimit;
            foreach (var pair in lookup)
            {
                var bestScore = 0f;
                var bestReason = string.Empty;
                foreach (var hint in hints)
                {
                    var score = SimilarAssetFinder.ScoreName(pair.Key, hint, out var reason);
                    if (score <= bestScore) continue;
                    bestScore = score;
                    bestReason = reason;
                }
                // 型・文脈で絞れないため、部分一致以上だけを名前候補として採用する。
                if (bestScore < MinimumNameScore) continue;

                var entries = pair.Value
                    .Where(item => !prefabOnly || string.Equals(Path.GetExtension(item.OriginalPath), ".prefab", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                diagnostics.ExtensionExcludedEntries += pair.Value.Count - entries.Count;
                if (entries.Count == 0) continue;
                var packagePaths = entries.Select(item => item.PackagePath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var assetGuids = entries.Select(item => item.Guid).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                diagnostics.MatchedNames++;
                diagnostics.MatchedEntries += entries.Count;
                diagnostics.MatchedPackages += packagePaths.Count;
                if (assetGuids.Count > commonNameLimit || packagePaths.Count > commonNameLimit)
                {
                    diagnostics.CommonNamesExcluded++;
                    diagnostics.Notes.Add("名前『" + pair.Key + "』は " + assetGuids.Count + " GUID / " + entries.Count + " エントリ / " + packagePaths.Count +
                        " package に存在し、現在の上限 " + commonNameLimit + " 件を超えるため除外しました。");
                    continue;
                }

                foreach (var guidGroup in entries.GroupBy(item => item.Guid, StringComparer.OrdinalIgnoreCase))
                {
                    var groupedEntries = guidGroup.ToList();
                    var groupedPackages = groupedEntries.Select(item => item.PackagePath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    var representative = groupedEntries[0];
                    var candidate = new RepairCandidate
                    {
                        Guid = representative.Guid,
                        FileId = record.FileId,
                        AssetPath = representative.OriginalPath,
                        OriginalAssetPath = representative.OriginalPath,
                        ExternalPath = representative.PackagePath,
                        Certainty = CandidateCertainty.Guess,
                        Score = bestScore,
                        ScoreReason = bestReason,
                        SourceKind = CandidateSourceKind.UnityPackage,
                        SourceLabel = ".unitypackage (名前一致・推測)",
                        OriginDescription = ".unitypackage 内の名前一致『" + pair.Key + "』: package 内 GUID " +
                            representative.Guid + " / " + groupedEntries.Count + " エントリ / " + groupedPackages.Count + " package",
                        CanRepair = false
                    };
                    candidate.SourceKinds.Add(CandidateSourceKind.UnityPackage);
                    candidate.PackagePaths.AddRange(groupedPackages);
                    result.Add(candidate);
                }
            }
            result = result.OrderByDescending(item => item.Score).ThenBy(item => item.AssetPath, StringComparer.OrdinalIgnoreCase).ToList();
            if (result.Count > MaximumPresentedNames)
            {
                diagnostics.CandidateLimitExcluded = result.Count - MaximumPresentedNames;
                result.RemoveRange(MaximumPresentedNames, result.Count - MaximumPresentedNames);
            }
            diagnostics.Presented = result.Count;
            return result;
        }

        private static Dictionary<string, List<LookupEntry>> GetLookup()
        {
            var data = Load();
            if (_guidLookup != null) return _guidLookup;
            var lookup = new Dictionary<string, List<LookupEntry>>(StringComparer.OrdinalIgnoreCase);
            foreach (var package in data.packages)
            {
                foreach (var entry in package.entries)
                {
                    if (string.IsNullOrEmpty(entry.guid)) continue;
                    if (!lookup.TryGetValue(entry.guid, out var entries))
                        lookup.Add(entry.guid, entries = new List<LookupEntry>());
                    entries.Add(new LookupEntry { Guid = entry.guid, OriginalPath = entry.originalPath, PackagePath = package.path });
                }
            }
            _guidLookup = lookup;
            return _guidLookup;
        }

        private static Dictionary<string, List<LookupEntry>> GetNameLookup()
        {
            var data = Load();
            if (_nameLookup != null) return _nameLookup;
            var lookup = new Dictionary<string, List<LookupEntry>>(StringComparer.OrdinalIgnoreCase);
            foreach (var package in data.packages)
            {
                foreach (var entry in package.entries)
                {
                    if (string.IsNullOrEmpty(entry.guid)) continue;
                    var name = Path.GetFileNameWithoutExtension(entry.originalPath ?? string.Empty);
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (!lookup.TryGetValue(name, out var entries))
                        lookup.Add(name, entries = new List<LookupEntry>());
                    entries.Add(new LookupEntry { Guid = entry.guid, OriginalPath = entry.originalPath, PackagePath = package.path });
                }
            }
            _nameLookup = lookup;
            return _nameLookup;
        }

        public static bool BuildOrUpdate(IReadOnlyList<string> folders, out string message)
        {
            message = string.Empty;
            var files = new List<string>();
            foreach (var folder in folders ?? new string[0])
            {
                if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) continue;
                try { files.AddRange(Directory.GetFiles(folder, "*.unitypackage", SearchOption.AllDirectories)); }
                catch (Exception exception) { Debug.LogWarning("ASN: フォルダを列挙できません: " + folder + "\n" + exception.Message); }
            }
            files = files.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToList();
            if (!EditorUtility.DisplayDialog(AsnText.WindowTitle,
                    files.Count + " 件の .unitypackage を索引化します。大きなフォルダでは数分〜数十分かかる場合があります。\n\n変更のないファイルはスキップします。",
                    "構築 / 更新", "キャンセル"))
            {
                message = "索引構築を開始しませんでした。";
                return false;
            }

            var old = Load().packages.ToDictionary(item => item.path, StringComparer.OrdinalIgnoreCase);
            var next = new UnityPackageIndexData { schemaVersion = SchemaVersion, scanCompleted = true };
            var cancelled = false;
            try
            {
                for (var index = 0; index < files.Count; index++)
                {
                    var path = files[index];
                    if (EditorUtility.DisplayCancelableProgressBar(AsnText.WindowTitle,
                            (index + 1) + " / " + files.Count + ": " + Path.GetFileName(path),
                            files.Count == 0 ? 1f : (float)index / files.Count))
                    {
                        cancelled = true;
                        next.scanCompleted = false;
                        for (var remaining = index; remaining < files.Count; remaining++)
                            if (old.TryGetValue(files[remaining], out var retained) && !next.packages.Any(item => string.Equals(item.path, retained.path, StringComparison.OrdinalIgnoreCase)))
                                next.packages.Add(retained);
                        break;
                    }
                    try
                    {
                        var info = new FileInfo(path);
                        if (old.TryGetValue(path, out var existing) && existing.size == info.Length && existing.lastWriteUtcTicks == info.LastWriteTimeUtc.Ticks)
                        {
                            next.packages.Add(existing);
                            continue;
                        }
                        var stopwatch = Stopwatch.StartNew();
                        var parsed = UnityPackageTarReader.Read(path);
                        stopwatch.Stop();
                        parsed.path = path;
                        parsed.size = info.Length;
                        parsed.lastWriteUtcTicks = info.LastWriteTimeUtc.Ticks;
                        parsed.scanSeconds = stopwatch.Elapsed.TotalSeconds;
                        next.packages.Add(parsed);
                        Debug.Log("ASN: .unitypackage を走査しました: " + Path.GetFileName(path) + " / " + parsed.entries.Count + " GUID / " + stopwatch.Elapsed.TotalSeconds.ToString("0.00") + " 秒");
                    }
                    catch (Exception exception)
                    {
                        Debug.LogError("ASN: .unitypackage の走査に失敗しました: " + path + "\n" + exception);
                    }
                }
            }
            finally { EditorUtility.ClearProgressBar(); }

            next.lastScanTime = DateTime.Now.ToString("O");
            _cached = next;
            _guidLookup = null;
            _nameLookup = null;
            Save(next);
            message = (cancelled ? "索引構築をキャンセルしました。走査済み分を保存しました。" : "索引を更新しました。") +
                "\n対象: " + next.packages.Count + " 件 / GUID: " + next.packages.Sum(item => item.entries.Count) + " 件";
            return !cancelled;
        }

        private static void Save(UnityPackageIndexData data)
        {
            var directory = Path.GetDirectoryName(IndexPath);
            Directory.CreateDirectory(directory);
            var temporary = IndexPath + ".tmp";
            File.WriteAllText(temporary, JsonUtility.ToJson(data, true));
            File.Copy(temporary, IndexPath, true);
            File.Delete(temporary);
        }
    }

    internal static class UnityPackageTarReader
    {
        private const int BlockSize = 512;

        public static UnityPackageFileIndex Read(string path)
        {
            var result = new UnityPackageFileIndex();
            using (var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var gzip = new GZipStream(file, CompressionMode.Decompress, false))
            {
                var header = new byte[BlockSize];
                var skipBuffer = new byte[81920];
                var reportedTypes = new HashSet<char>();
                while (ReadExactly(gzip, header, 0, BlockSize))
                {
                    if (IsZeroBlock(header)) break;
                    var name = ReadString(header, 0, 100);
                    var prefix = ReadString(header, 345, 155);
                    if (!string.IsNullOrEmpty(prefix)) name = prefix + "/" + name;
                    var size = ReadOctal(header, 124, 12);
                    var type = (char)header[156];
                    if (type == 'L' || type == 'K')
                        Debug.LogWarning("ASN: tar の GNU LongLink エントリをスキップします: " + path);
                    else if (type != '\0' && type != '0' && type != '5' && reportedTypes.Add(type))
                        Debug.LogWarning("ASN: 未対応の tar エントリ形式 '" + type + "' をスキップします: " + path);
                    if (name.EndsWith("/pathname", StringComparison.Ordinal) && size >= 0 && size <= 1024 * 1024)
                    {
                        var bytes = new byte[(int)size];
                        if (!ReadExactly(gzip, bytes, 0, bytes.Length)) throw new EndOfStreamException("pathname エントリが途中で終了しました。");
                        var text = Encoding.UTF8.GetString(bytes).TrimStart('\ufeff');
                        var lineEnd = text.IndexOfAny(new[] { '\r', '\n' });
                        var firstLine = (lineEnd >= 0 ? text.Substring(0, lineEnd) : text).Trim();
                        var slash = name.IndexOf('/');
                        var guid = slash > 0 ? name.Substring(0, slash) : string.Empty;
                        if (IsGuid(guid) && !string.IsNullOrWhiteSpace(firstLine))
                            result.entries.Add(new UnityPackageGuidEntry { guid = guid.ToLowerInvariant(), originalPath = firstLine.Trim() });
                    }
                    else
                    {
                        Skip(gzip, size, skipBuffer);
                    }
                    var padding = (BlockSize - size % BlockSize) % BlockSize;
                    Skip(gzip, padding, skipBuffer);
                }
            }
            return result;
        }

        private static bool IsGuid(string value)
        {
            if (value == null || value.Length != 32) return false;
            foreach (var character in value)
                if (!Uri.IsHexDigit(character)) return false;
            return true;
        }

        private static long ReadOctal(byte[] buffer, int offset, int count)
        {
            var text = ReadString(buffer, offset, count).Trim();
            if (text.Length == 0) return 0;
            try { return Convert.ToInt64(text, 8); }
            catch (Exception exception) { throw new InvalidDataException("tar サイズが不正です: " + text, exception); }
        }

        private static string ReadString(byte[] buffer, int offset, int count)
        {
            var end = offset;
            while (end < offset + count && buffer[end] != 0) end++;
            return Encoding.UTF8.GetString(buffer, offset, end - offset).Trim();
        }

        private static bool IsZeroBlock(byte[] buffer)
        {
            for (var index = 0; index < buffer.Length; index++) if (buffer[index] != 0) return false;
            return true;
        }

        private static bool ReadExactly(Stream stream, byte[] buffer, int offset, int count)
        {
            var read = 0;
            while (read < count)
            {
                var current = stream.Read(buffer, offset + read, count - read);
                if (current == 0) return read == 0 ? false : throw new EndOfStreamException("tar ヘッダが途中で終了しました。");
                read += current;
            }
            return true;
        }

        private static void Skip(Stream stream, long count, byte[] buffer)
        {
            while (count > 0)
            {
                var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
                if (read <= 0) throw new EndOfStreamException("tar エントリが途中で終了しました。");
                count -= read;
            }
        }
    }
}
