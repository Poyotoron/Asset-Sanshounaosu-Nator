using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Security.Principal;
using UnityEditor;
using UnityEngine;

namespace Maaaaa.Asn.Editor.Core
{
    internal sealed class RecycleBinEntry
    {
        public string RecycledAssetPath;
        public string RecycledMetaPath;
        public string OriginalPath;
        public string OriginalFileName;
        public DateTime? DeletedUtc;
        public string Guid;
        public bool HasAsset => !string.IsNullOrEmpty(RecycledAssetPath) && (File.Exists(RecycledAssetPath) || Directory.Exists(RecycledAssetPath));
        public bool HasMeta => !string.IsNullOrEmpty(RecycledMetaPath) && File.Exists(RecycledMetaPath);
    }

    internal static class RecycleBinScanner
    {
        private static readonly Regex GuidRegex = new Regex(@"^guid:\s*(?<guid>[0-9a-fA-F]{32})\s*$", RegexOptions.Compiled | RegexOptions.Multiline);
        private static readonly HashSet<string> AssetExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".prefab", ".unity", ".mat", ".fbx", ".obj", ".asset", ".controller", ".anim", ".overrideController",
            ".png", ".jpg", ".jpeg", ".tga", ".psd", ".exr", ".wav", ".mp3", ".ogg", ".cs", ".shader",
            ".shadergraph", ".vfx", ".ttf", ".otf", ".bytes", ".txt", ".json", ".asmdef", ".dll"
        };
        private static List<RecycleBinEntry> _cache;
        private static readonly List<string> _warnings = new List<string>();
        private static readonly Dictionary<string, List<string>> _likelyNames = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        public static string SupportDescription
        {
            get
            {
                if (Application.platform == RuntimePlatform.WindowsEditor) return "Windows ごみ箱 ($Recycle.Bin)";
                if (Application.platform == RuntimePlatform.OSXEditor) return "macOS ごみ箱 (~/.Trash / 実装済み・実機未検証)";
                if (Application.platform == RuntimePlatform.LinuxEditor) return "Linux XDG ごみ箱 (実装済み・実機未検証)";
                return "この OS のごみ箱探索には対応していません。";
            }
        }

        public static List<string> Warnings => new List<string>(_warnings);

        public static List<RecycleBinEntry> Scan(bool showProgress)
        {
            var entries = new List<RecycleBinEntry>();
            _warnings.Clear();
            _likelyNames.Clear();
            try
            {
                if (Application.platform == RuntimePlatform.WindowsEditor) entries = ScanWindows(showProgress);
                else if (Application.platform == RuntimePlatform.OSXEditor) entries = ScanMac(showProgress);
                else if (Application.platform == RuntimePlatform.LinuxEditor) entries = ScanLinux(showProgress);
                else _warnings.Add("この OS のごみ箱探索には対応していません。");
            }
            finally { if (showProgress) EditorUtility.ClearProgressBar(); }
            _cache = entries;
            return entries;
        }

        public static List<RepairCandidate> CreateCandidates(ReferenceRecord record, bool forceScan)
        {
            if (forceScan) Scan(true);
            var result = new List<RepairCandidate>();
            if (_cache == null) return result;
            var likelyNames = new List<string>();
            _likelyNames.Remove(record.Guid ?? string.Empty);
            // この走査中は likelyNames をまだ公開しない。同じ record の全候補で同一ヒントを使い、
            // 走査完了後にだけモード 1 へ名前をフィードする。
            var hints = SimilarAssetFinder.BuildHints(record);
            foreach (var entry in _cache ?? new List<RecycleBinEntry>())
            {
                var guidMatch = !string.IsNullOrEmpty(entry.Guid) && string.Equals(entry.Guid, record.Guid, StringComparison.OrdinalIgnoreCase);
                if (!guidMatch && entry.HasMeta) continue;
                var candidate = new RepairCandidate
                {
                    Guid = guidMatch ? entry.Guid : record.Guid,
                    FileId = record.FileId,
                    AssetPath = string.IsNullOrEmpty(entry.OriginalPath) ? entry.OriginalFileName : entry.OriginalPath,
                    ExternalPath = entry.RecycledAssetPath,
                    OriginalAssetPath = entry.OriginalPath,
                    Certainty = guidMatch ? CandidateCertainty.Certain : CandidateCertainty.Guess,
                    Score = guidMatch ? 1000f : 0f,
                    ScoreReason = guidMatch ? "GUID 完全一致" : string.Empty,
                    SourceKind = CandidateSourceKind.RecycleBin,
                    SourceLabel = guidMatch ? "ごみ箱 (確実)" : "ごみ箱 (推測)",
                    OriginDescription = guidMatch ? "ごみ箱の .meta から GUID 一致" : "ごみ箱内のファイル名一致（.meta なし）",
                    CanRepair = false,
                    RecycleEntry = entry
                };
                if (!guidMatch)
                {
                    SimilarAssetFinder.ScoreCandidate(candidate, hints);
                    if (candidate.Score < 600f) continue;
                    if (!likelyNames.Contains(entry.OriginalFileName, StringComparer.OrdinalIgnoreCase)) likelyNames.Add(entry.OriginalFileName);
                }
                candidate.SourceKinds.Add(CandidateSourceKind.RecycleBin);
                result.Add(candidate);
            }
            if (likelyNames.Count > 0) _likelyNames[record.Guid ?? string.Empty] = likelyNames;
            return result;
        }

        public static List<string> GetCachedNames(string guid)
        {
            return _likelyNames.TryGetValue(guid ?? string.Empty, out var names) ? new List<string>(names) : new List<string>();
        }

        private static List<RecycleBinEntry> ScanWindows(bool showProgress)
        {
            var raw = new List<RecycleBinEntry>();
            var cancelled = false;
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (cancelled) break;
                if (!drive.IsReady) continue;
                var root = Path.Combine(drive.RootDirectory.FullName, "$Recycle.Bin");
                if (!Directory.Exists(root)) continue;
                string[] infoFiles;
                try
                {
                    var sid = WindowsIdentity.GetCurrent().User != null ? WindowsIdentity.GetCurrent().User.Value : string.Empty;
                    var userRoot = string.IsNullOrEmpty(sid) ? root : Path.Combine(root, sid);
                    infoFiles = Directory.Exists(userRoot)
                        ? Directory.GetFiles(userRoot, "$I*", SearchOption.TopDirectoryOnly)
                        : Directory.GetDirectories(root).SelectMany(directory =>
                        {
                            try { return Directory.GetFiles(directory, "$I*", SearchOption.TopDirectoryOnly); }
                            catch (Exception exception) { _warnings.Add("列挙できないごみ箱があります: " + directory + " (" + exception.Message + ")"); return new string[0]; }
                        }).ToArray();
                }
                catch (Exception exception) { _warnings.Add("列挙できないごみ箱があります: " + root + " (" + exception.Message + ")"); continue; }
                for (var index = 0; index < infoFiles.Length; index++)
                {
                    if (showProgress && EditorUtility.DisplayCancelableProgressBar(AsnText.WindowTitle, "ごみ箱を走査中: " + drive.Name,
                            infoFiles.Length == 0 ? 1f : (float)index / infoFiles.Length)) { cancelled = true; break; }
                    try
                    {
                        var infoPath = infoFiles[index];
                        if (!TryParseWindowsInfo(infoPath, out var originalPath, out var deletedUtc)) continue;
                        var fileName = Path.GetFileName(infoPath);
                        var recycledName = "$R" + fileName.Substring(2);
                        var recycledPath = Path.Combine(Path.GetDirectoryName(infoPath), recycledName);
                        raw.Add(new RecycleBinEntry
                        {
                            RecycledAssetPath = File.Exists(recycledPath) || Directory.Exists(recycledPath) ? recycledPath : null,
                            OriginalPath = originalPath,
                            OriginalFileName = Path.GetFileName(originalPath),
                            DeletedUtc = deletedUtc
                        });
                    }
                    catch (Exception exception) { _warnings.Add("ごみ箱エントリを読めません: " + infoFiles[index] + " (" + exception.Message + ")"); }
                }
            }
            return PairMetaEntries(raw);
        }

        private static bool TryParseWindowsInfo(string path, out string originalPath, out DateTime? deletedUtc)
        {
            originalPath = null;
            deletedUtc = null;
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < 24) return false;
            var version = BitConverter.ToInt64(bytes, 0);
            var fileTime = BitConverter.ToInt64(bytes, 16);
            if (fileTime > 0) deletedUtc = DateTime.FromFileTimeUtc(fileTime);
            var offset = version == 2 && bytes.Length >= 28 ? 28 : 24;
            var byteCount = bytes.Length - offset;
            if (version == 2)
            {
                var characterCount = BitConverter.ToInt32(bytes, 24);
                if (characterCount > 0) byteCount = Math.Min(byteCount, characterCount * 2);
            }
            originalPath = Encoding.Unicode.GetString(bytes, offset, byteCount).TrimEnd('\0');
            return !string.IsNullOrEmpty(originalPath);
        }

        private static List<RecycleBinEntry> ScanMac(bool showProgress)
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".Trash");
            return ScanSimpleFiles(path, showProgress);
        }

        private static List<RecycleBinEntry> ScanLinux(bool showProgress)
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "Trash");
            var filesRoot = Path.Combine(root, "files");
            var result = ScanSimpleFiles(filesRoot, showProgress);
            foreach (var item in result)
            {
                var info = Path.Combine(root, "info", item.OriginalFileName + ".trashinfo");
                if (!File.Exists(info)) continue;
                foreach (var line in File.ReadAllLines(info))
                {
                    if (line.StartsWith("Path=", StringComparison.Ordinal)) item.OriginalPath = Uri.UnescapeDataString(line.Substring(5));
                    else if (line.StartsWith("DeletionDate=", StringComparison.Ordinal) && DateTime.TryParse(line.Substring(13), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var value)) item.DeletedUtc = value.ToUniversalTime();
                }
            }
            return PairMetaEntries(result);
        }

        private static List<RecycleBinEntry> ScanSimpleFiles(string root, bool showProgress)
        {
            var result = new List<RecycleBinEntry>();
            if (!Directory.Exists(root)) return result;
            string[] files;
            try { files = Directory.GetFiles(root, "*", SearchOption.TopDirectoryOnly); }
            catch (Exception exception) { _warnings.Add("ごみ箱を列挙できません: " + exception.Message); return result; }
            for (var index = 0; index < files.Length; index++)
            {
                if (showProgress && EditorUtility.DisplayCancelableProgressBar(AsnText.WindowTitle, "ごみ箱を走査中", (float)index / files.Length)) break;
                var name = Path.GetFileName(files[index]);
                if (!IsRelevant(name)) continue;
                result.Add(new RecycleBinEntry { RecycledAssetPath = files[index], OriginalPath = null, OriginalFileName = name, DeletedUtc = null });
            }
            return PairMetaEntries(result);
        }

        private static bool IsRelevant(string name)
        {
            if (name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) return true;
            return AssetExtensions.Contains(Path.GetExtension(name));
        }

        private static List<RecycleBinEntry> PairMetaEntries(List<RecycleBinEntry> raw)
        {
            var assets = raw.Where(item => !item.OriginalFileName.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) && IsRelevant(item.OriginalFileName)).ToList();
            var metaEntries = raw.Where(item => item.OriginalFileName.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var meta in metaEntries)
            {
                try
                {
                    if (string.IsNullOrEmpty(meta.RecycledAssetPath) || !File.Exists(meta.RecycledAssetPath)) continue;
                    var match = GuidRegex.Match(File.ReadAllText(meta.RecycledAssetPath));
                    if (!match.Success) continue;
                    var expectedOriginal = string.IsNullOrEmpty(meta.OriginalPath) ? null : meta.OriginalPath.Substring(0, meta.OriginalPath.Length - 5);
                    var asset = assets.FirstOrDefault(item => !string.IsNullOrEmpty(expectedOriginal) && string.Equals(item.OriginalPath, expectedOriginal, StringComparison.OrdinalIgnoreCase));
                    if (asset == null)
                    {
                        var expectedName = Path.GetFileNameWithoutExtension(meta.OriginalFileName);
                        asset = assets.FirstOrDefault(item => string.Equals(item.OriginalFileName, expectedName, StringComparison.OrdinalIgnoreCase));
                    }
                    if (asset == null)
                    {
                        asset = new RecycleBinEntry { OriginalPath = expectedOriginal, OriginalFileName = Path.GetFileNameWithoutExtension(meta.OriginalFileName), DeletedUtc = meta.DeletedUtc };
                        assets.Add(asset);
                    }
                    asset.Guid = match.Groups["guid"].Value.ToLowerInvariant();
                    asset.RecycledMetaPath = meta.RecycledAssetPath;
                }
                catch (Exception exception) { _warnings.Add(".meta を読めません: " + meta.RecycledAssetPath + " (" + exception.Message + ")"); }
            }
            return assets;
        }
    }

    internal static class RecycleBinRecovery
    {
        public static bool Recover(RecycleBinEntry entry, out string message)
        {
            message = string.Empty;
            if (entry == null || !entry.HasAsset) { message = "ごみ箱内の本体は既に失われています。"; return false; }
            if (entry.HasMeta == false && !string.IsNullOrEmpty(entry.Guid)) { message = "GUID を保持する .meta が失われています。"; return false; }
            var assetsRoot = Path.GetFullPath(Application.dataPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string destination = null;
            if (!string.IsNullOrEmpty(entry.OriginalPath))
            {
                var original = Path.GetFullPath(entry.OriginalPath);
                if (original.StartsWith(assetsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) destination = original;
            }
            if (string.IsNullOrEmpty(destination))
            {
                var folder = EditorUtility.OpenFolderPanel("復元先を選択", Application.dataPath, string.Empty);
                if (string.IsNullOrEmpty(folder)) { message = "復元先の選択をキャンセルしました。"; return false; }
                folder = Path.GetFullPath(folder);
                if (!(folder == assetsRoot || folder.StartsWith(assetsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                {
                    message = "復元先はこのプロジェクトの Assets 配下を選んでください。";
                    return false;
                }
                destination = Path.Combine(folder, entry.OriginalFileName);
            }
            var metaDestination = destination + ".meta";
            if (File.Exists(destination) || Directory.Exists(destination) || (entry.HasMeta && File.Exists(metaDestination)))
            {
                message = "復元先に同名ファイルがあるため、上書きせず中止しました: " + destination;
                return false;
            }
            var detail = "本体: " + entry.RecycledAssetPath + "\n復元先: " + destination + (entry.HasMeta ? "\n.meta も同時にコピーして GUID を保持します。" : "\n.meta が無いため GUID は保持できません（推測による回収）。");
            if (!EditorUtility.DisplayDialog(AsnText.WindowTitle, detail, "コピーして復元", "キャンセル")) { message = "回収をキャンセルしました。"; return false; }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                if (Directory.Exists(entry.RecycledAssetPath)) CopyDirectory(entry.RecycledAssetPath, destination);
                else File.Copy(entry.RecycledAssetPath, destination, false);
                if (entry.HasMeta) File.Copy(entry.RecycledMetaPath, metaDestination, false);
                AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
                var assetPath = "Assets" + destination.Substring(assetsRoot.Length).Replace('\\', '/');
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
                var restoredGuid = AssetDatabase.AssetPathToGUID(assetPath);
                var guidKept = !entry.HasMeta || string.IsNullOrEmpty(entry.Guid) || string.Equals(restoredGuid, entry.Guid, StringComparison.OrdinalIgnoreCase);
                message = "ごみ箱の原本を残したままプロジェクトへコピーしました: " + assetPath +
                    (guidKept ? string.Empty : "\n警告: .meta をコピーしましたが GUID が一致しません。再検査結果を確認してください。");
                ExecutionLogger.WriteRecovery(entry, destination, guidKept, message);
                return true;
            }
            catch (Exception exception)
            {
                message = "回収に失敗しました: " + exception.Message;
                try
                {
                    if (File.Exists(metaDestination)) File.Delete(metaDestination);
                    if (File.Exists(destination)) File.Delete(destination);
                    else if (Directory.Exists(destination)) Directory.Delete(destination, true);
                }
                catch (Exception cleanupException) { message += "\n作成途中のファイルを除去できませんでした: " + cleanupException.Message; }
                ExecutionLogger.WriteRecovery(entry, destination, false, message);
                return false;
            }
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), false);
            foreach (var directory in Directory.GetDirectories(source)) CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }
}
