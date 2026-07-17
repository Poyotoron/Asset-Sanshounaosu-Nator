using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;
using Maaaaa.Asn.Editor.Core;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Maaaaa.Asn.Editor
{
    internal sealed class AsnWindow : EditorWindow
    {
        private const int CollapseGroupThreshold = 8;
        [SerializeField] private UnityEngine.Object _targetAsset;
        [SerializeField] private string _targetAssetPath;
        [SerializeField] private List<string> _targetAssetPaths = new List<string>();
        private InspectionResult _result;
        private BatchInspectionResult _batchResult;
        private Vector2 _scroll;
        private bool _showEmptyReferences;
        private readonly Dictionary<IssueKind, bool> _filters = new Dictionary<IssueKind, bool>();
        private readonly Dictionary<string, bool> _foldouts = new Dictionary<string, bool>();
        private ReferenceRecord _selectedIssue;
        private PrefabReferenceGroup _selectedPrefabGroup;
        private string _selectedInspectionRootPath;
        private string _drawingInspectionRootPath;
        private List<RepairCandidate> _candidates;
        private CandidateSearchDiagnostics _candidateDiagnostics;
        private List<RepairCandidate> _rootMissingCandidates;
        private UnityEngine.Object _manualCandidate;
        private string _status;
        private bool _showSearchSettings;

        [MenuItem(AsnInfo.MenuRoot)]
        private static void Open()
        {
            var window = GetWindow<AsnWindow>();
            window.titleContent = new GUIContent(AsnText.WindowTitle);
            window.minSize = new Vector2(620f, 420f);
            window.Show();
        }

        private void OnEnable()
        {
            foreach (IssueKind kind in Enum.GetValues(typeof(IssueKind))) if (kind != IssueKind.None) _filters[kind] = true;
            TryUseSelection();
            EditorApplication.delayCall += ShowFirstRunWarning;
        }

        private void ShowFirstRunWarning()
        {
            if (EditorPrefs.GetBool(AsnInfo.AcknowledgedKey, false)) return;
            if (EditorUtility.DisplayDialog(AsnText.WindowTitle, AsnText.FirstRunWarning, "了承して開く", "閉じる"))
                EditorPrefs.SetBool(AsnInfo.AcknowledgedKey, true);
            else Close();
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Prefab の参照を検査・診断・修復します", EditorStyles.boldLabel);
            DrawVersionWarning();
            var forceText = EditorSettings.serializationMode == SerializationMode.ForceText;
            if (!forceText) DrawForceTextGuide();
            EditorGUILayout.Space();
            DrawSearchSettings();
            EditorGUILayout.Space();
            DrawTargetPicker();
            using (new EditorGUI.DisabledScope(!forceText || _targetAssetPaths.Count == 0))
                if (GUILayout.Button("参照を検査", GUILayout.Height(30f))) Inspect();
            if (!string.IsNullOrEmpty(_status)) EditorGUILayout.HelpBox(_status, MessageType.Info);
            if (_result != null) DrawResults();
            else if (_batchResult != null) DrawBatchResults();
        }

        private void DrawSearchSettings()
        {
            _showSearchSettings = EditorGUILayout.Foldout(_showSearchSettings, "探索設定（プロジェクト内 / .unitypackage / ごみ箱）", true);
            if (!_showSearchSettings) return;
            EditorGUI.indentLevel++;
            var settings = AsnSettings.GetOrCreate();
            EditorGUI.BeginChangeCheck();
            settings.projectSearchEnabled = EditorGUILayout.ToggleLeft("モード 1: プロジェクト内の類似候補", settings.projectSearchEnabled);
            settings.unityPackageSearchEnabled = EditorGUILayout.ToggleLeft("モード 2: .unitypackage の GUID 索引", settings.unityPackageSearchEnabled);
            settings.recycleBinSearchEnabled = EditorGUILayout.ToggleLeft("モード 3: OS ごみ箱", settings.recycleBinSearchEnabled);
            if (EditorGUI.EndChangeCheck()) settings.Save();

            EditorGUILayout.LabelField(".unitypackage 探索フォルダ", EditorStyles.boldLabel);
            for (var index = 0; index < settings.unityPackageFolders.Count; index++)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.SelectableLabel(settings.unityPackageFolders[index], GUILayout.Height(EditorGUIUtility.singleLineHeight));
                if (GUILayout.Button("削除", GUILayout.Width(52f)))
                {
                    settings.unityPackageFolders.RemoveAt(index--);
                    settings.Save();
                }
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("フォルダを追加", GUILayout.Width(110f)))
            {
                var folder = EditorUtility.OpenFolderPanel(".unitypackage を含むフォルダ", string.Empty, string.Empty);
                if (!string.IsNullOrEmpty(folder) && !settings.unityPackageFolders.Contains(folder, StringComparer.OrdinalIgnoreCase))
                {
                    settings.unityPackageFolders.Add(folder);
                    settings.Save();
                }
            }
            using (new EditorGUI.DisabledScope(settings.unityPackageFolders.Count == 0))
                if (GUILayout.Button("索引を構築 / 更新", GUILayout.Width(130f))) UnityPackageIndexStore.BuildOrUpdate(settings.unityPackageFolders, out _status);
            if (GUILayout.Button("ごみ箱を再走査", GUILayout.Width(110f)))
            {
                var count = RecycleBinScanner.Scan(true).Count;
                _status = "ごみ箱を走査しました: " + count + " 件。" + (RecycleBinScanner.Warnings.Count > 0 ? "\n" + string.Join("\n", RecycleBinScanner.Warnings.ToArray()) : string.Empty);
            }
            EditorGUILayout.EndHorizontal();
            var status = UnityPackageIndexStore.GetStatus();
            EditorGUILayout.LabelField(string.IsNullOrEmpty(status.LastScanTime)
                ? ".unitypackage 索引: 未構築"
                : ".unitypackage 索引: " + status.PackageCount + " package / " + status.GuidCount + " GUID / 最終走査 " + status.LastScanTime + (status.Completed ? string.Empty : "（未完了）"), EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.LabelField("ごみ箱: " + RecycleBinScanner.SupportDescription, EditorStyles.miniLabel);
            EditorGUI.indentLevel--;
        }

        private static void DrawVersionWarning()
        {
            if (!Application.unityVersion.StartsWith("2022.3", StringComparison.Ordinal))
                EditorGUILayout.HelpBox("主な対応環境は Unity 2022.3 です。現在: " + Application.unityVersion + "。検査は続行できます。", MessageType.Warning);
        }

        private void DrawForceTextGuide()
        {
            EditorGUILayout.HelpBox("現在: " + EditorSettings.serializationMode + "\n" + AsnText.ForceTextReason + "\n\n" + AsnText.ForceTextCost, MessageType.Error);
            if (GUILayout.Button("このプロジェクトを Force Text に変換する") &&
                EditorUtility.DisplayDialog("Force Text へ変換", AsnText.ForceTextCost + "\n\n本当に変換しますか？", "変換する", "キャンセル"))
            {
                EditorSettings.serializationMode = SerializationMode.ForceText;
                AssetDatabase.SaveAssets();
                _status = "シリアライズモードを Force Text に変更しました。Unity の再シリアライズ完了を確認してください。";
                Repaint();
            }
        }

        private void DrawTargetPicker()
        {
            EditorGUILayout.BeginHorizontal();
            var selected = EditorGUILayout.ObjectField("対象 Prefab / フォルダ", _targetAsset, typeof(UnityEngine.Object), false);
            if (selected != _targetAsset) SetTarget(selected);
            if (GUILayout.Button("選択中を使用", GUILayout.Width(110f))) TryUseSelection(true);
            EditorGUILayout.EndHorizontal();
            if (_targetAssetPaths.Count == 0)
                EditorGUILayout.HelpBox("Project ウィンドウで Prefab（複数可）またはフォルダを選ぶか、ここへドラッグ＆ドロップしてください。", MessageType.Info);
            else
                EditorGUILayout.LabelField(_targetAssetPaths.Count == 1 ? "アセットパス: " + _targetAssetPath : "対象 Prefab: " + _targetAssetPaths.Count + " 件", EditorStyles.miniLabel);
        }

        private void SetTarget(UnityEngine.Object candidate)
        {
            if (candidate == null) { ClearTarget(); return; }
            var path = AssetDatabase.GetAssetPath(candidate);
            SetTargetPath(path, candidate);
        }

        private void SetTargetPath(string path, UnityEngine.Object displayAsset)
        {
            var paths = CollectPrefabPaths(new[] { path });
            if (paths.Count == 0)
            {
                _status = "指定対象に Prefab がありません。";
                return;
            }
            _targetAssetPaths = paths;
            _targetAssetPath = paths[0];
            _targetAsset = displayAsset != null ? displayAsset : AssetDatabase.LoadMainAssetAtPath(path);
            _result = null;
            _batchResult = null;
            _selectedIssue = null;
            _selectedPrefabGroup = null;
            _selectedInspectionRootPath = null;
            _candidates = null;
            _rootMissingCandidates = null;
        }

        private void ClearTarget()
        {
            _targetAsset = null;
            _targetAssetPath = string.Empty;
            _targetAssetPaths.Clear();
            _result = null;
            _batchResult = null;
            _selectedIssue = null;
            _selectedPrefabGroup = null;
            _selectedInspectionRootPath = null;
            _candidates = null;
            _rootMissingCandidates = null;
        }

        private void TryUseSelection(bool showError = false)
        {
            var selectedPaths = new List<string>();
            if (Selection.assetGUIDs != null)
                foreach (var guid in Selection.assetGUIDs) selectedPaths.Add(AssetDatabase.GUIDToAssetPath(guid));
            if (selectedPaths.Count == 0)
                foreach (var selected in Selection.objects) selectedPaths.Add(AssetDatabase.GetAssetPath(selected));
            var prefabs = CollectPrefabPaths(selectedPaths);
            if (prefabs.Count == 0)
            {
                if (showError) _status = "選択対象に Prefab がありません。";
                return;
            }
            _targetAssetPaths = prefabs;
            _targetAssetPath = prefabs[0];
            _targetAsset = prefabs.Count == 1 ? AssetDatabase.LoadMainAssetAtPath(prefabs[0]) : Selection.activeObject;
            _result = null;
            _batchResult = null;
            _selectedIssue = null;
            _selectedPrefabGroup = null;
            _selectedInspectionRootPath = null;
            _candidates = null;
            _rootMissingCandidates = null;
        }

        private void Inspect()
        {
            if (_targetAssetPaths.Count > 1)
            {
                if (!EditorUtility.DisplayDialog(AsnText.WindowTitle, _targetAssetPaths.Count + " 件の Prefab を検査します。完了まで時間がかかる場合があります。", "検査する", "キャンセル")) return;
                _batchResult = PrefabReferenceScanner.InspectBatch(_targetAssetPaths);
                _result = null;
                ExecutionLogger.WriteBatchInspection(_batchResult);
                _status = (_batchResult.Cancelled ? "一括検査をキャンセルしました。走査済みの結果を表示します。" : "一括検査が完了しました。") + " ログを 1 件出力しました。";
            }
            else
            {
                _result = PrefabReferenceScanner.Inspect(_targetAssetPath, true);
                _batchResult = null;
                ExecutionLogger.WriteInspection(_result);
                _status = _result.Errors.Count == 0 ? "検査が完了し、ログを出力しました。" : string.Join("\n", _result.Errors.ToArray());
            }
            _selectedIssue = null;
            _selectedPrefabGroup = null;
            _selectedInspectionRootPath = null;
            _candidates = null;
            _rootMissingCandidates = null;
            _foldouts.Clear();
        }

        private static List<string> CollectPrefabPaths(IEnumerable<string> paths)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths ?? new string[0])
            {
                if (string.IsNullOrEmpty(path)) continue;
                if (path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) result.Add(path);
                else if (AssetDatabase.IsValidFolder(path))
                    foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { path }))
                    {
                        var prefabPath = AssetDatabase.GUIDToAssetPath(guid);
                        if (prefabPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) result.Add(prefabPath);
                    }
            }
            return result.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private void DrawResults()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("検査結果", EditorStyles.boldLabel);
            if (_result.RootFileMissing)
            {
                DrawRootFileMissing();
                return;
            }
            if (_result.Errors.Count > 0) EditorGUILayout.HelpBox(string.Join("\n", _result.Errors.ToArray()), MessageType.Error);
            var issues = _result.Issues.ToList();
            if (issues.Count == 0)
            {
                EditorGUILayout.HelpBox("問題ありません", MessageType.Info);
                return;
            }
            DrawSummary(issues);
            DrawFilters();
            var visibleIssues = issues.Where(IsVisible).ToList();
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("結果を Markdown でコピー", GUILayout.Width(190f))) EditorGUIUtility.systemCopyBuffer = ToMarkdown(issues);
            if (GUILayout.Button("すべて展開", GUILayout.Width(100f))) SetAllFoldouts(visibleIssues, true);
            if (GUILayout.Button("すべて折りたたむ", GUILayout.Width(120f))) SetAllFoldouts(visibleIssues, false);
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            var expandGroupsByDefault = visibleIssues.Count <= CollapseGroupThreshold;
            var missingPrefabGroups = BuildMissingPrefabGroups(_result, visibleIssues);
            var groupedIssues = new HashSet<ReferenceRecord>(missingPrefabGroups.SelectMany(group => group.References));

            if (missingPrefabGroups.Count > 0)
            {
                EditorGUILayout.LabelField("欠落 Prefab", EditorStyles.boldLabel);
                foreach (var group in missingPrefabGroups) DrawMissingPrefabGroup(group);
                EditorGUILayout.Space();
            }

            var otherIssues = visibleIssues.Where(item => !groupedIssues.Contains(item)).ToList();
            if (otherIssues.Count > 0 && missingPrefabGroups.Count > 0)
                EditorGUILayout.LabelField("その他の問題", EditorStyles.boldLabel);
            foreach (var pathGroup in otherIssues.GroupBy(item => item.DisplayPath))
            {
                if (!GroupFoldout(GameObjectKey(pathGroup.Key), pathGroup.Key, pathGroup, expandGroupsByDefault)) continue;
                EditorGUI.indentLevel++;
                foreach (var componentGroup in pathGroup.GroupBy(item => item.ComponentType))
                {
                    if (!GroupFoldout(ComponentKey(pathGroup.Key, componentGroup.Key), componentGroup.Key, componentGroup, expandGroupsByDefault)) continue;
                    EditorGUI.indentLevel++;
                    foreach (var issue in componentGroup) DrawIssue(issue);
                    EditorGUI.indentLevel--;
                }
                EditorGUI.indentLevel--;
            }
            if (_selectedIssue != null)
            {
                EditorGUILayout.Space();
                DrawCandidatePanel();
            }
            EditorGUILayout.EndScrollView();
        }

        private void DrawBatchResults()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("一括検査結果", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("検査 " + _batchResult.InspectedCount + " 件 / 問題のある Prefab " + _batchResult.ProblemPrefabCount + " 件 / 問題 " + _batchResult.IssueCount + " 件" +
                (_batchResult.Cancelled ? "\nキャンセル時点までの結果です。" : string.Empty), _batchResult.IssueCount == 0 ? MessageType.Info : MessageType.Warning);
            if (_batchResult.IssueCount == 0) EditorGUILayout.HelpBox("問題ありません", MessageType.Info);
            if (_batchResult.Errors.Count > 0) EditorGUILayout.HelpBox(string.Join("\n", _batchResult.Errors.ToArray()), MessageType.Error);
            DrawFilters();
            if (GUILayout.Button("一括結果を Markdown でコピー", GUILayout.Width(220f)))
                EditorGUIUtility.systemCopyBuffer = BatchToMarkdown(_batchResult);
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var result in _batchResult.Results)
            {
                var allIssues = result.Issues.ToList();
                var issues = allIssues.Where(IsVisible).ToList();
                var hasProblems = allIssues.Count > 0 || result.Errors.Count > 0;
                var key = "batch:" + result.RootAssetPath;
                if (!Foldout(key, (hasProblems ? "▲ " : "✓ ") + result.RootAssetPath + "  (" + allIssues.Count + " 件)", hasProblems)) continue;
                EditorGUI.indentLevel++;
                if (result.Errors.Count > 0) EditorGUILayout.HelpBox(string.Join("\n", result.Errors.ToArray()), MessageType.Error);
                if (!hasProblems) EditorGUILayout.LabelField("問題ありません", EditorStyles.miniLabel);
                else if (issues.Count == 0) EditorGUILayout.LabelField("現在のフィルタで表示する問題はありません。", EditorStyles.miniLabel);
                DrawBatchIssueGroups(result.RootAssetPath, issues);
                EditorGUI.indentLevel--;
            }
            if (_selectedIssue != null)
            {
                EditorGUILayout.Space();
                DrawCandidatePanel();
            }
            EditorGUILayout.EndScrollView();
        }

        private void DrawBatchIssueGroups(string rootAssetPath, List<ReferenceRecord> visibleIssues)
        {
            _drawingInspectionRootPath = rootAssetPath;
            var ownerResult = _batchResult.Results.FirstOrDefault(item => string.Equals(item.RootAssetPath, rootAssetPath, StringComparison.OrdinalIgnoreCase));
            var missingPrefabGroups = BuildMissingPrefabGroups(ownerResult, visibleIssues);
            var groupedIssues = new HashSet<ReferenceRecord>(missingPrefabGroups.SelectMany(group => group.References));
            var prefix = "batch:" + rootAssetPath + ":";
            foreach (var group in missingPrefabGroups) DrawMissingPrefabGroup(group, prefix);
            foreach (var pathGroup in visibleIssues.Where(item => !groupedIssues.Contains(item)).GroupBy(item => item.DisplayPath))
            {
                if (!GroupFoldout(prefix + GameObjectKey(pathGroup.Key), pathGroup.Key, pathGroup, false)) continue;
                EditorGUI.indentLevel++;
                foreach (var componentGroup in pathGroup.GroupBy(item => item.ComponentType))
                {
                    if (!GroupFoldout(prefix + ComponentKey(pathGroup.Key, componentGroup.Key), componentGroup.Key, componentGroup, false)) continue;
                    EditorGUI.indentLevel++;
                    foreach (var issue in componentGroup) DrawIssue(issue);
                    EditorGUI.indentLevel--;
                }
                EditorGUI.indentLevel--;
            }
            _drawingInspectionRootPath = null;
        }

        private void DrawMissingPrefabGroup(PrefabReferenceGroup group, string keyPrefix = "")
        {
            var issues = group.References;
            var guid = group.Guid;
            var representative = issues.FirstOrDefault(item => item.PropertyName == "m_SourcePrefab" && !string.IsNullOrEmpty(item.ReferencedName))
                ?? issues.FirstOrDefault(item => item.PropertyName == "m_SourcePrefab")
                ?? issues[0];
            var name = !string.IsNullOrEmpty(representative.ReferencedName) ? representative.ReferencedName : guid;
            var worst = issues.Any(item => item.Severity == IssueSeverity.Error) ? IssueSeverity.Error : IssueSeverity.Warning;
            var oldColor = GUI.color;
            GUI.color = SeverityColor(worst);
            var expanded = Foldout(keyPrefix + MissingPrefabKey(group.SourceAssetPath, guid), "Prefab 欠落: " + name + "  (" + issues.Count + " 件)", false);
            GUI.color = oldColor;
            if (!expanded) return;

            EditorGUI.indentLevel++;
            EditorGUILayout.SelectableLabel("guid: " + guid, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            EditorGUILayout.SelectableLabel("対象ファイル: " + group.SourceAssetPath, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            EditorGUILayout.LabelField("内訳: m_SourcePrefab " + group.SourcePrefabCount + " 件 / override target " + group.ModificationTargetCount +
                " 件 / objectReference " + group.ObjectReferenceCount + " 件 / 合計 " + group.References.Count + " 件", EditorStyles.wordWrappedMiniLabel);
            if (!string.IsNullOrEmpty(representative.ResolvedAssetPath))
                EditorGUILayout.SelectableLabel("元パス: " + representative.ResolvedAssetPath, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            if (group.OtherSourceAssetPaths.Count > 0)
                EditorGUILayout.HelpBox("同じ GUID は他の Prefab からも参照されています。この操作では書き換えません。各 Prefab を個別に検査・修復してください:\n" +
                    string.Join("\n", group.OtherSourceAssetPaths.ToArray()), MessageType.Warning);
            if (GUILayout.Button("この Prefab をまとめて修復", GUILayout.Width(210f)))
                OpenPrefabGroupCandidates(group, representative);
            EditorGUILayout.Space(2f);
            foreach (var issue in issues)
                DrawIssue(issue, issue.DisplayPath + " > " + issue.ComponentType, false, false);
            EditorGUI.indentLevel--;
        }

        private void DrawRootFileMissing()
        {
            EditorGUILayout.HelpBox("検査対象のファイル本体が見つかりません: " + _result.RootAssetPath +
                "\n元ファイル名から同名・類似の Prefab を探せますが、自動的な差し替えは行いません。候補を確認して手動で対象を選び直してください。", MessageType.Warning);
            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(_targetAsset == null))
                if (GUILayout.Button("対象を表示", GUILayout.Width(100f))) EditorGUIUtility.PingObject(_targetAsset);
            if (GUILayout.Button("同名候補を探す", GUILayout.Width(120f)))
                _rootMissingCandidates = CandidateAggregator.Find(new ReferenceRecord
                {
                    SourceAssetPath = _result.RootAssetPath,
                    ResolvedAssetPath = _result.RootAssetPath,
                    PropertyName = "m_SourcePrefab",
                    ExpectedType = typeof(GameObject)
                });
            EditorGUILayout.EndHorizontal();
            if (_rootMissingCandidates == null) return;
            if (_rootMissingCandidates.Count == 0)
            {
                EditorGUILayout.LabelField("同名・類似の Prefab は見つかりませんでした。");
                return;
            }
            EditorGUILayout.LabelField("手動対応の候補（推測）", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(AsnText.GuessWarning, MessageType.Warning);
            foreach (var candidate in _rootMissingCandidates)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.BeginHorizontal();
                if (candidate.Asset != null) EditorGUILayout.ObjectField(candidate.Asset, typeof(UnityEngine.Object), false);
                else EditorGUILayout.LabelField(candidate.OriginalAssetPath ?? candidate.AssetPath);
                EditorGUILayout.LabelField(candidate.AssetPath, EditorStyles.miniLabel);
                using (new EditorGUI.DisabledScope(candidate.Asset == null))
                    if (GUILayout.Button("表示", GUILayout.Width(60f))) EditorGUIUtility.PingObject(candidate.Asset);
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.LabelField(new GUIContent("推測 / score " + candidate.Score.ToString("0") + " ・ " + candidate.ScoreReason, candidate.ScoreReason), EditorStyles.wordWrappedMiniLabel);
                EditorGUILayout.EndVertical();
            }
        }

        private void DrawFilters()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("表示:", GUILayout.Width(38f));
            foreach (IssueKind kind in Enum.GetValues(typeof(IssueKind)))
            {
                if (kind == IssueKind.None || kind == IssueKind.EmptyReference) continue;
                _filters[kind] = GUILayout.Toggle(_filters[kind], new GUIContent(ShortLabel(kind), ReferenceClassifier.Description(kind)), EditorStyles.miniButton);
            }
            _showEmptyReferences = GUILayout.Toggle(_showEmptyReferences, new GUIContent("T-D 空参照（誤検知を含む）", ReferenceClassifier.Description(IssueKind.EmptyReference)), EditorStyles.miniButton);
            EditorGUILayout.EndHorizontal();
        }

        private bool IsVisible(ReferenceRecord issue) => issue.Issue == IssueKind.EmptyReference ? _showEmptyReferences : _filters[issue.Issue];

        private void DrawIssue(ReferenceRecord issue, string location = null, bool showPrefabContext = true, bool allowCandidateSearch = true)
        {
            var oldColor = GUI.color;
            GUI.color = SeverityColor(issue.Severity);
            var heading = showPrefabContext ? IssueHeading(issue) : issue.PropertyName + " — " + ReferenceClassifier.Label(issue);
            if (!string.IsNullOrEmpty(location)) heading = location + " > " + heading;
            var expanded = Foldout(IssueKey(issue), SeveritySymbol(issue.Severity) + " " + heading, false);
            GUI.color = oldColor;
            if (!expanded) return;
            EditorGUI.indentLevel++;
            EditorGUILayout.SelectableLabel($"guid: {issue.Guid}   fileID: {issue.FileId}\n{issue.SourceAssetPath}:{issue.LineNumber}", GUILayout.Height(34f));
            if (IsMissingPrefab(issue))
            {
                if (string.IsNullOrEmpty(issue.ReferencedName))
                    EditorGUILayout.HelpBox("欠落 Prefab 名は不明です（元名の手がかりなし）。.unitypackage 索引またはごみ箱探索を確認してください。", MessageType.Info);
                else
                    EditorGUILayout.LabelField("欠落 Prefab 名", issue.ReferencedName);
                if (!string.IsNullOrEmpty(issue.ResolvedAssetPath))
                    EditorGUILayout.SelectableLabel("元パス: " + issue.ResolvedAssetPath, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            }
            if (!string.IsNullOrEmpty(issue.TypeAssessment)) EditorGUILayout.LabelField(issue.TypeAssessment, EditorStyles.miniLabel);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("対象を表示", GUILayout.Width(90f))) Highlight(issue);
            if (allowCandidateSearch)
            {
                using (new EditorGUI.DisabledScope(issue.Issue == IssueKind.EmptyReference))
                    if (GUILayout.Button("候補を探す", GUILayout.Width(90f))) OpenCandidates(issue);
            }
            else EditorGUILayout.LabelField("修復は上のグループ操作から行います。", EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();
            EditorGUI.indentLevel--;
        }

        private static string IssueHeading(ReferenceRecord issue)
        {
            if (!IsMissingPrefab(issue)) return issue.PropertyName + " — " + ReferenceClassifier.Label(issue);
            var name = string.IsNullOrEmpty(issue.ReferencedName) ? "名前不明" : issue.ReferencedName;
            return "Prefab 欠落: " + name + " — " + ReferenceClassifier.Label(issue);
        }

        private static bool IsMissingPrefab(ReferenceRecord issue)
        {
            var prefabReference = issue.PropertyName == "m_SourcePrefab" ||
                (!string.IsNullOrEmpty(issue.ResolvedAssetPath) && issue.ResolvedAssetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase));
            return prefabReference && (issue.Issue == IssueKind.GuidMissing || issue.Issue == IssueKind.FileIdMissing);
        }

        private static List<PrefabReferenceGroup> BuildMissingPrefabGroups(InspectionResult result, IEnumerable<ReferenceRecord> visibleIssues)
        {
            var groups = new List<PrefabReferenceGroup>();
            if (result == null) return groups;
            var anchors = visibleIssues
                .Where(item => item.PropertyName == "m_SourcePrefab" && IsMissingPrefab(item) && !string.IsNullOrEmpty(item.Guid))
                .GroupBy(item => item.SourceAssetPath + "\n" + item.Guid, StringComparer.OrdinalIgnoreCase)
                .Select(item => item.First());
            foreach (var anchor in anchors)
            {
                var group = new PrefabReferenceGroup { SourceAssetPath = anchor.SourceAssetPath, Guid = anchor.Guid };
                group.References.AddRange(result.References
                    .Where(item => string.Equals(item.SourceAssetPath, anchor.SourceAssetPath, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(item.Guid, anchor.Guid, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(item => item.LineNumber).ThenBy(item => item.ReferenceColumn));
                group.OtherSourceAssetPaths.AddRange(result.References
                    .Where(item => !string.Equals(item.SourceAssetPath, anchor.SourceAssetPath, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(item.Guid, anchor.Guid, StringComparison.OrdinalIgnoreCase))
                    .Select(item => item.SourceAssetPath).Distinct(StringComparer.OrdinalIgnoreCase));
                groups.Add(group);
            }
            return groups;
        }

        private static void DrawSummary(IReadOnlyCollection<ReferenceRecord> issues)
        {
            var errors = issues.Count(item => item.Severity == IssueSeverity.Error);
            var warnings = issues.Count(item => item.Severity == IssueSeverity.Warning);
            var missingPrefabs = issues.Where(item => item.PropertyName == "m_SourcePrefab" && IsMissingPrefab(item) && !string.IsNullOrEmpty(item.Guid))
                .Select(item => item.SourceAssetPath + "\n" + item.Guid).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
            DrawColoredLabel("Error " + errors, SeverityColor(IssueSeverity.Error), 72f);
            DrawColoredLabel("Warning " + warnings, SeverityColor(IssueSeverity.Warning), 88f);
            foreach (IssueKind kind in Enum.GetValues(typeof(IssueKind)))
            {
                if (kind == IssueKind.None) continue;
                var count = issues.Count(item => item.Issue == kind);
                if (count > 0) EditorGUILayout.LabelField(ShortLabel(kind) + "×" + count, EditorStyles.miniLabel, GUILayout.Width(54f));
            }
            if (missingPrefabs > 0) EditorGUILayout.LabelField("欠落Prefab×" + missingPrefabs, EditorStyles.miniLabel, GUILayout.Width(82f));
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        private static void DrawColoredLabel(string text, Color color, float width)
        {
            var style = new GUIStyle(EditorStyles.boldLabel);
            style.normal.textColor = color;
            EditorGUILayout.LabelField(text, style, GUILayout.Width(width));
        }

        private bool GroupFoldout(string key, string label, IEnumerable<ReferenceRecord> issues, bool initial)
        {
            var list = issues as ICollection<ReferenceRecord> ?? issues.ToList();
            var worst = list.Any(item => item.Severity == IssueSeverity.Error) ? IssueSeverity.Error : IssueSeverity.Warning;
            var oldColor = GUI.color;
            GUI.color = SeverityColor(worst);
            var expanded = Foldout(key, label + "  (" + list.Count + ")", initial);
            GUI.color = oldColor;
            return expanded;
        }

        private void SetAllFoldouts(IEnumerable<ReferenceRecord> issues, bool expanded)
        {
            var issueList = issues as IList<ReferenceRecord> ?? issues.ToList();
            foreach (var item in issueList.Where(item => item.PropertyName == "m_SourcePrefab" && IsMissingPrefab(item) && !string.IsNullOrEmpty(item.Guid))
                .GroupBy(item => item.SourceAssetPath + "\n" + item.Guid, StringComparer.OrdinalIgnoreCase).Select(item => item.First()))
                _foldouts[MissingPrefabKey(item.SourceAssetPath, item.Guid)] = expanded;
            foreach (var pathGroup in issueList.GroupBy(item => item.DisplayPath))
            {
                _foldouts[GameObjectKey(pathGroup.Key)] = expanded;
                foreach (var componentGroup in pathGroup.GroupBy(item => item.ComponentType))
                {
                    _foldouts[ComponentKey(pathGroup.Key, componentGroup.Key)] = expanded;
                    foreach (var issue in componentGroup) _foldouts[IssueKey(issue)] = expanded;
                }
            }
        }

        private static string GameObjectKey(string path) => "go:" + path;
        private static string MissingPrefabKey(string sourceAssetPath, string guid) => "missing-prefab:" + sourceAssetPath + ":" + guid;
        private static string ComponentKey(string path, string component) => "component:" + path + "\n" + component;
        private static string IssueKey(ReferenceRecord issue) => "issue:" + issue.SourceAssetPath + ":" + issue.LineNumber + ":" + issue.ReferenceColumn;
        private static Color SeverityColor(IssueSeverity severity) => severity == IssueSeverity.Error ? new Color(1f, .45f, .45f) : new Color(1f, .72f, .15f);
        private static string SeveritySymbol(IssueSeverity severity) => severity == IssueSeverity.Error ? "●" : "▲";

        private void DrawCandidatePanel()
        {
            // 修復・回収は同じ OnGUI フレーム内でフィールドを無効化し得るため、描画対象を退避する。
            var selectedIssue = _selectedIssue;
            var selectedPrefabGroup = _selectedPrefabGroup;
            var candidates = _candidates;
            var manualCandidateType = selectedIssue.IsScript ? typeof(MonoScript) : typeof(UnityEngine.Object);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(selectedPrefabGroup != null ? "Prefab グループ修復候補" : "修復候補: " + selectedIssue.PropertyName, EditorStyles.boldLabel);
            if (GUILayout.Button("閉じる", GUILayout.Width(60f)))
            {
                _selectedIssue = null;
                _selectedPrefabGroup = null;
                _selectedInspectionRootPath = null;
                _candidates = null;
                _candidateDiagnostics = null;
                _manualCandidate = null;
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.EndVertical();
                return;
            }
            EditorGUILayout.EndHorizontal();
            if (selectedPrefabGroup != null)
                EditorGUILayout.HelpBox("同一ファイル内の同じ GUID をまとめて修復します。m_SourcePrefab " + selectedPrefabGroup.SourcePrefabCount +
                    " 件 / override target " + selectedPrefabGroup.ModificationTargetCount + " 件 / objectReference " +
                    selectedPrefabGroup.ObjectReferenceCount + " 件 / 合計 " + selectedPrefabGroup.References.Count + " 件。fileID と type は保持します。", MessageType.Info);
            if (selectedIssue.Issue == IssueKind.FileIdMissing)
            {
                if (selectedIssue.BackingFileMissing)
                    EditorGUILayout.HelpBox("参照先ファイルは物理的に存在しません: " + selectedIssue.ResolvedAssetPath +
                        "\nAssetDatabase のキャッシュ上では GUID が解決していますが、名前・.unitypackage・ごみ箱を手がかりに別のアセットを探します。候補はすべて確認してください。", MessageType.Error);
                else
                    EditorGUILayout.HelpBox("ファイルは見つかっています: " + selectedIssue.ResolvedAssetPath +
                        "\n同一アセット内のサブアセットから fileID を選び直します。候補はすべて推測です。", MessageType.Info);
            }
            if (selectedIssue.IsScript) EditorGUILayout.HelpBox(AsnText.MissingScriptRepairWarning, MessageType.Warning);
            if (candidates != null && candidates.Any(item => item.Certainty == CandidateCertainty.Guess))
                EditorGUILayout.HelpBox(AsnText.GuessWarning, MessageType.Warning);
            if (_candidateDiagnostics != null)
                EditorGUILayout.HelpBox(_candidateDiagnostics.ToDisplayText(), MessageType.Info);
            if (candidates == null || candidates.Count == 0)
            {
                EditorGUILayout.LabelField("該当する候補がありません。名前として意味のある一致に満たない候補は除外しました。", EditorStyles.wordWrappedLabel);
                if (selectedIssue.Issue == IssueKind.GuidMissing || selectedIssue.Issue == IssueKind.MissingScript || selectedIssue.BackingFileMissing)
                {
                    EditorGUILayout.HelpBox(".unitypackage 索引が未構築なら、探索設定からフォルダを登録して索引を構築してください。\n" + AsnText.RecycleBinLimitWarning, MessageType.Info);
                }
            }
            else foreach (var candidate in candidates)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.BeginHorizontal();
                if (candidate.Asset != null) EditorGUILayout.ObjectField(candidate.Asset, typeof(UnityEngine.Object), false);
                else EditorGUILayout.LabelField(candidate.OriginalAssetPath ?? candidate.AssetPath, EditorStyles.boldLabel);
                using (new EditorGUI.DisabledScope(!candidate.CanRepair || candidate.Asset == null))
                    if (GUILayout.Button(selectedPrefabGroup != null ? "まとめて修復" : "選択して修復", GUILayout.Width(110f))) Repair(candidate);
                EditorGUILayout.EndHorizontal();
                if (_selectedIssue == null)
                {
                    EditorGUILayout.EndVertical();
                    EditorGUILayout.EndVertical();
                    return;
                }
                var certainty = candidate.Certainty == CandidateCertainty.Certain ? "確実な復元" : "推測";
                var origins = candidate.SourceKinds.Count == 0 ? candidate.SourceLabel : string.Join(" / ", candidate.SourceKinds.Select(SourceName).ToArray());
                var scoreText = certainty + " / " + origins + " / score " + candidate.Score.ToString("0") + " ・ " + candidate.ScoreReason;
                EditorGUILayout.LabelField(new GUIContent(scoreText, candidate.ScoreReason), EditorStyles.wordWrappedMiniLabel);
                if (!string.IsNullOrEmpty(candidate.OriginDescription)) EditorGUILayout.LabelField(candidate.OriginDescription, EditorStyles.wordWrappedMiniLabel);
                if (candidate.SourceKind == CandidateSourceKind.UnityPackage)
                {
                    EditorGUILayout.HelpBox(candidate.Certainty == CandidateCertainty.Certain
                        ? AsnText.UnityPackageImportGuide
                        : AsnText.UnityPackageNameMatchGuide,
                        candidate.Certainty == CandidateCertainty.Certain ? MessageType.Info : MessageType.Warning);
                    foreach (var packagePath in candidate.PackagePaths)
                    {
                        EditorGUILayout.BeginHorizontal();
                        EditorGUILayout.SelectableLabel(Path.GetFileName(packagePath) + " — " + packagePath, GUILayout.Height(EditorGUIUtility.singleLineHeight));
                        if (GUILayout.Button("場所", GUILayout.Width(48f))) EditorUtility.RevealInFinder(packagePath);
                        if (GUILayout.Button("コピー", GUILayout.Width(52f))) EditorGUIUtility.systemCopyBuffer = packagePath;
                        EditorGUILayout.EndHorizontal();
                    }
                }
                else if (candidate.SourceKind == CandidateSourceKind.RecycleBin && candidate.RecycleEntry != null)
                {
                    var entry = candidate.RecycleEntry;
                    EditorGUILayout.LabelField("元ファイル名: " + entry.OriginalFileName + " / 削除日時: " + (entry.DeletedUtc.HasValue ? entry.DeletedUtc.Value.ToLocalTime().ToString("G") : "不明"), EditorStyles.wordWrappedMiniLabel);
                    if (!string.IsNullOrEmpty(entry.OriginalPath)) EditorGUILayout.SelectableLabel("元パス: " + entry.OriginalPath, GUILayout.Height(EditorGUIUtility.singleLineHeight));
                    if (!entry.HasAsset) EditorGUILayout.HelpBox(".meta はありますが、本体が無いため回収しても参照は直りません。", MessageType.Warning);
                    using (new EditorGUI.DisabledScope(!entry.HasAsset))
                        if (GUILayout.Button("ごみ箱からコピーして復元"))
                        {
                            if (RecycleBinRecovery.Recover(entry, out var recoveryStatus))
                            {
                                if (ReinspectAfterMutation(_selectedInspectionRootPath, out var reinspectionStatus))
                                    _status = recoveryStatus + "\n" + reinspectionStatus;
                                else
                                    _status = recoveryStatus + "\n修復後の再検査を実行できませんでした。結果は更新されていません。";
                            }
                            else _status = recoveryStatus;
                        }
                    if (_selectedIssue == null)
                    {
                        EditorGUILayout.EndVertical();
                        EditorGUILayout.EndVertical();
                        return;
                    }
                    EditorGUILayout.HelpBox(AsnText.RecycleBinLimitWarning, MessageType.Info);
                }
                EditorGUILayout.EndVertical();
            }
            _manualCandidate = EditorGUILayout.ObjectField("手動指定（推測）", _manualCandidate, manualCandidateType, false);
            using (new EditorGUI.DisabledScope(_manualCandidate == null))
                if (GUILayout.Button("手動指定した候補で修復"))
                {
                    if (TryCreateCandidate(_manualCandidate, out var candidate)) Repair(candidate);
                    else _status = "プロジェクト内アセットを指定してください。";
                }
            EditorGUILayout.EndVertical();
        }

        private void OpenCandidates(ReferenceRecord issue)
        {
            _selectedIssue = issue;
            _selectedPrefabGroup = null;
            _selectedInspectionRootPath = !string.IsNullOrEmpty(_drawingInspectionRootPath)
                ? _drawingInspectionRootPath
                : _result != null ? _result.RootAssetPath : issue.SourceAssetPath;
            if (_batchResult != null && string.IsNullOrEmpty(_drawingInspectionRootPath))
            {
                var owner = _batchResult.Results.FirstOrDefault(item => item.References.Contains(issue));
                if (owner != null) _selectedInspectionRootPath = owner.RootAssetPath;
            }
            _candidateDiagnostics = null;
            if (issue.Issue == IssueKind.FileIdMissing && !issue.BackingFileMissing)
                _candidates = SubAssetCandidateFinder.Find(issue);
            else if (issue.IsScript)
            {
                _candidates = new List<RepairCandidate>();
                if (AsnSettings.GetOrCreate().unityPackageSearchEnabled) _candidates.AddRange(new UnityPackageCandidateSource().Find(issue));
                if (AsnSettings.GetOrCreate().recycleBinSearchEnabled) _candidates.AddRange(new RecycleBinCandidateSource().Find(issue));
                if (AsnSettings.GetOrCreate().projectSearchEnabled) _candidates.AddRange(MonoScriptCandidateFinder.Find(issue));
                _candidates = _candidates.OrderBy(item => item.Certainty).ThenByDescending(item => item.Score).ToList();
            }
            else _candidates = CandidateAggregator.Find(issue, out _candidateDiagnostics);
            _manualCandidate = null;
            _scroll.y = float.MaxValue;
        }

        private void OpenPrefabGroupCandidates(PrefabReferenceGroup group, ReferenceRecord representative)
        {
            OpenCandidates(representative);
            _selectedPrefabGroup = group;
        }

        private static string SourceName(CandidateSourceKind kind)
        {
            switch (kind)
            {
                case CandidateSourceKind.Project: return "プロジェクト内";
                case CandidateSourceKind.UnityPackage: return ".unitypackage";
                case CandidateSourceKind.RecycleBin: return "ごみ箱";
                case CandidateSourceKind.SubAsset: return "同一アセット内";
                case CandidateSourceKind.MonoScript: return "MonoScript";
                default: return kind.ToString();
            }
        }

        private void Repair(RepairCandidate candidate)
        {
            var issue = _selectedIssue;
            var prefabGroup = _selectedPrefabGroup;
            var inspectionRootPath = _selectedInspectionRootPath;
            var repaired = prefabGroup != null
                ? PrefabReferenceRepairer.RepairPrefabGroup(prefabGroup, candidate)
                : PrefabReferenceRepairer.Repair(issue, candidate);
            var repairStatus = repaired.Message + (string.IsNullOrEmpty(repaired.BackupDirectory) ? string.Empty : "\nバックアップ: " + repaired.BackupDirectory);
            if (repaired.Success)
            {
                if (ReinspectAfterMutation(inspectionRootPath, out var reinspectionStatus))
                {
                    repairStatus += "\n" + reinspectionStatus;
                    if (prefabGroup != null && IsPrefabGroupStillBroken(prefabGroup, inspectionRootPath))
                        repairStatus += "\n警告: 再検査後も同じ Prefab グループの問題が残っています。修復先が別物であるか、一部参照を解決できませんでした。バックアップと結果を確認してください。";
                }
                else
                    repairStatus += "\n修復後の再検査を実行できませんでした。結果は更新されていません。";
            }
            _status = repairStatus;
        }

        private bool ReinspectAfterMutation(string inspectionRootPath, out string status)
        {
            status = string.Empty;
            _selectedIssue = null;
            _selectedPrefabGroup = null;
            _selectedInspectionRootPath = null;
            _candidates = null;
            _manualCandidate = null;

            if (string.IsNullOrEmpty(inspectionRootPath)) return false;
            if (_batchResult == null)
            {
                // 単体検査は 0.1.0 以来のログ出力・表示リセットを含む既存経路を維持する。
                Inspect();
                status = _status;
                return _result != null;
            }

            var index = _batchResult.Results.FindIndex(item =>
                string.Equals(item.RootAssetPath, inspectionRootPath, StringComparison.OrdinalIgnoreCase));
            if (index < 0) return false;
            var refreshed = PrefabReferenceScanner.Inspect(inspectionRootPath, true);
            _batchResult.Results[index] = refreshed;
            status = refreshed.Errors.Count == 0
                ? "修復した Prefab 1 件を再検査し、一括結果を更新しました。"
                : "修復した Prefab 1 件を再検査しました。確認事項:\n" + string.Join("\n", refreshed.Errors.ToArray());
            return true;
        }

        private bool IsPrefabGroupStillBroken(PrefabReferenceGroup group, string inspectionRootPath)
        {
            InspectionResult result = null;
            if (_batchResult != null)
                result = _batchResult.Results.FirstOrDefault(item => string.Equals(item.RootAssetPath, inspectionRootPath, StringComparison.OrdinalIgnoreCase));
            else result = _result;
            return result != null && result.References.Any(item =>
                string.Equals(item.SourceAssetPath, group.SourceAssetPath, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Guid, group.Guid, StringComparison.OrdinalIgnoreCase) && item.Issue != IssueKind.None);
        }

        private static void Highlight(ReferenceRecord issue)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(issue.SourceAssetPath);
            if (prefab == null)
            {
                var asset = AssetDatabase.LoadMainAssetAtPath(issue.SourceAssetPath);
                if (asset != null) EditorGUIUtility.PingObject(asset);
                return;
            }
            AssetDatabase.OpenAsset(prefab);
            EditorApplication.delayCall += () =>
            {
                var stage = PrefabStageUtility.GetCurrentPrefabStage();
                if (stage == null || stage.prefabContentsRoot == null) { EditorGUIUtility.PingObject(prefab); return; }
                var root = stage.prefabContentsRoot.transform;
                var relative = issue.GameObjectPath;
                if (!string.IsNullOrEmpty(relative) && relative.StartsWith(root.name + "/", StringComparison.Ordinal)) relative = relative.Substring(root.name.Length + 1);
                var target = string.IsNullOrEmpty(relative) || relative == root.name ? root : root.Find(relative);
                Selection.activeGameObject = target != null ? target.gameObject : stage.prefabContentsRoot;
                EditorGUIUtility.PingObject(Selection.activeGameObject);
            };
        }

        private static bool TryCreateCandidate(UnityEngine.Object asset, out RepairCandidate candidate)
        {
            candidate = null;
            if (asset == null || !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string guid, out long fileId)) return false;
            candidate = new RepairCandidate { Asset = asset, AssetPath = AssetDatabase.GetAssetPath(asset), Guid = guid, FileId = fileId, Certainty = CandidateCertainty.Guess };
            return !string.IsNullOrEmpty(candidate.AssetPath);
        }

        private bool Foldout(string key, string label, bool initial)
        {
            if (!_foldouts.TryGetValue(key, out var value)) value = initial;
            value = EditorGUILayout.Foldout(value, label, true);
            _foldouts[key] = value;
            return value;
        }

        private static string ShortLabel(IssueKind kind) => ReferenceClassifier.Label(kind).Split(' ')[0];

        private static string ToMarkdown(IEnumerable<ReferenceRecord> issues)
        {
            var text = new StringBuilder("| GameObject | Component | Property | 種別 | Severity | GUID | fileID | File:Line |\n|---|---|---|---|---|---|---:|---|\n");
            foreach (var item in issues)
                text.AppendLine($"| {Escape(item.DisplayPath)} | {Escape(item.ComponentType)} | {Escape(item.PropertyName)} | {ReferenceClassifier.Label(item)} | {item.Severity} | `{item.Guid}` | {item.FileId} | {Escape(item.SourceAssetPath)}:{item.LineNumber} |");
            return text.ToString();
        }

        private static string BatchToMarkdown(BatchInspectionResult batch)
        {
            var text = new StringBuilder("# ASN 一括検査結果\n\n検査: " + batch.InspectedCount + " / 問題のある Prefab: " + batch.ProblemPrefabCount + " / 問題: " + batch.IssueCount + "\n\n");
            foreach (var result in batch.Results)
            {
                text.AppendLine("## " + result.RootAssetPath);
                var issues = result.Issues.ToList();
                text.AppendLine(issues.Count == 0 ? "問題ありません\n" : ToMarkdown(issues) + "\n");
                foreach (var error in result.Errors) text.AppendLine("- Error: " + error);
            }
            foreach (var error in batch.Errors) text.AppendLine("- Error: " + error);
            return text.ToString();
        }

        private static string Escape(string value) => (value ?? string.Empty).Replace("|", "\\|");
    }
}
