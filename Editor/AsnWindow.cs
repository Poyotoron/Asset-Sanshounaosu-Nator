using System;
using System.Collections.Generic;
using System.Linq;
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
        private InspectionResult _result;
        private Vector2 _scroll;
        private bool _showEmptyReferences;
        private readonly Dictionary<IssueKind, bool> _filters = new Dictionary<IssueKind, bool>();
        private readonly Dictionary<string, bool> _foldouts = new Dictionary<string, bool>();
        private ReferenceRecord _selectedIssue;
        private List<RepairCandidate> _candidates;
        private List<RepairCandidate> _rootMissingCandidates;
        private UnityEngine.Object _manualCandidate;
        private string _status;

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
            DrawTargetPicker();
            using (new EditorGUI.DisabledScope(!forceText || string.IsNullOrEmpty(_targetAssetPath)))
                if (GUILayout.Button("参照を検査", GUILayout.Height(30f))) Inspect();
            if (!string.IsNullOrEmpty(_status)) EditorGUILayout.HelpBox(_status, MessageType.Info);
            if (_result != null) DrawResults();
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
            var selected = EditorGUILayout.ObjectField("対象 Prefab", _targetAsset, typeof(UnityEngine.Object), false);
            if (selected != _targetAsset) SetTarget(selected);
            if (GUILayout.Button("選択中を使用", GUILayout.Width(110f))) TryUseSelection(true);
            EditorGUILayout.EndHorizontal();
            if (string.IsNullOrEmpty(_targetAssetPath))
                EditorGUILayout.HelpBox("Project ウィンドウで Prefab を選ぶか、ここへドラッグ＆ドロップしてください。", MessageType.Info);
            else
                EditorGUILayout.LabelField("アセットパス", _targetAssetPath, EditorStyles.miniLabel);
        }

        private void SetTarget(UnityEngine.Object candidate)
        {
            if (candidate == null) { ClearTarget(); return; }
            var path = AssetDatabase.GetAssetPath(candidate);
            SetTargetPath(path, candidate);
        }

        private void SetTargetPath(string path, UnityEngine.Object displayAsset)
        {
            if (string.IsNullOrEmpty(path) || !path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                _status = "Prefab アセットだけを指定できます。";
                return;
            }
            _targetAssetPath = path;
            _targetAsset = displayAsset != null ? displayAsset : AssetDatabase.LoadMainAssetAtPath(path);
            _result = null;
            _selectedIssue = null;
            _candidates = null;
            _rootMissingCandidates = null;
        }

        private void ClearTarget()
        {
            _targetAsset = null;
            _targetAssetPath = string.Empty;
            _result = null;
            _selectedIssue = null;
            _candidates = null;
            _rootMissingCandidates = null;
        }

        private void TryUseSelection(bool showError = false)
        {
            var selected = Selection.activeObject;
            var path = selected != null ? AssetDatabase.GetAssetPath(selected) : string.Empty;
            if (string.IsNullOrEmpty(path) && Selection.activeInstanceID != 0)
                path = AssetDatabase.GetAssetPath(Selection.activeInstanceID);
            if (string.IsNullOrEmpty(path) && Selection.assetGUIDs != null && Selection.assetGUIDs.Length > 0)
                path = AssetDatabase.GUIDToAssetPath(Selection.assetGUIDs[0]);
            if (string.IsNullOrEmpty(path))
            {
                if (showError) _status = "Project ウィンドウで Prefab を選択してください。";
                return;
            }
            SetTargetPath(path, selected);
        }

        private void Inspect()
        {
            _result = PrefabReferenceScanner.Inspect(_targetAssetPath, true);
            ExecutionLogger.WriteInspection(_result);
            _status = _result.Errors.Count == 0 ? "検査が完了し、ログを出力しました。" : string.Join("\n", _result.Errors.ToArray());
            _selectedIssue = null;
            _candidates = null;
            _rootMissingCandidates = null;
            _foldouts.Clear();
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
            var missingPrefabGuids = new HashSet<string>(visibleIssues
                .Where(item => item.PropertyName == "m_SourcePrefab" && IsMissingPrefab(item) && !string.IsNullOrEmpty(item.Guid))
                .Select(item => item.Guid), StringComparer.OrdinalIgnoreCase);
            var missingPrefabGroups = visibleIssues
                .Where(item => missingPrefabGuids.Contains(item.Guid) && (item.PropertyName == "m_SourcePrefab" || item.IsModificationTarget))
                .GroupBy(item => item.Guid, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var groupedIssues = new HashSet<ReferenceRecord>(missingPrefabGroups.SelectMany(group => group));

            if (missingPrefabGroups.Count > 0)
            {
                EditorGUILayout.LabelField("欠落 Prefab", EditorStyles.boldLabel);
                foreach (var group in missingPrefabGroups) DrawMissingPrefabGroup(group.Key, group.ToList());
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

        private void DrawMissingPrefabGroup(string guid, List<ReferenceRecord> issues)
        {
            var representative = issues.FirstOrDefault(item => item.PropertyName == "m_SourcePrefab" && !string.IsNullOrEmpty(item.ReferencedName))
                ?? issues.FirstOrDefault(item => item.PropertyName == "m_SourcePrefab")
                ?? issues[0];
            var name = !string.IsNullOrEmpty(representative.ReferencedName) ? representative.ReferencedName : guid;
            var worst = issues.Any(item => item.Severity == IssueSeverity.Error) ? IssueSeverity.Error : IssueSeverity.Warning;
            var oldColor = GUI.color;
            GUI.color = SeverityColor(worst);
            var expanded = Foldout(MissingPrefabKey(guid), "Prefab 欠落: " + name + "  (" + issues.Count + " 件)", false);
            GUI.color = oldColor;
            if (!expanded) return;

            EditorGUI.indentLevel++;
            EditorGUILayout.SelectableLabel("guid: " + guid, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            if (!string.IsNullOrEmpty(representative.ResolvedAssetPath))
                EditorGUILayout.SelectableLabel("元パス: " + representative.ResolvedAssetPath, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            if (GUILayout.Button("この Prefab の候補を探す", GUILayout.Width(180f)))
                OpenCandidates(representative);
            EditorGUILayout.Space(2f);
            foreach (var issue in issues)
                DrawIssue(issue, issue.DisplayPath + " > " + issue.ComponentType, false);
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
                _rootMissingCandidates = SimilarAssetFinder.FindRootReplacement(_result.RootAssetPath);
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
                EditorGUILayout.ObjectField(candidate.Asset, typeof(UnityEngine.Object), false);
                EditorGUILayout.LabelField(candidate.AssetPath, EditorStyles.miniLabel);
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

        private void DrawIssue(ReferenceRecord issue, string location = null, bool showPrefabContext = true)
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
                    EditorGUILayout.HelpBox("欠落 Prefab 名は不明です（元名の手がかりなし）。Phase 2 で追加予定の unitypackage / ごみ箱探索が必要です。", MessageType.Info);
                else
                    EditorGUILayout.LabelField("欠落 Prefab 名", issue.ReferencedName);
                if (!string.IsNullOrEmpty(issue.ResolvedAssetPath))
                    EditorGUILayout.SelectableLabel("元パス: " + issue.ResolvedAssetPath, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            }
            if (!string.IsNullOrEmpty(issue.TypeAssessment)) EditorGUILayout.LabelField(issue.TypeAssessment, EditorStyles.miniLabel);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("対象を表示", GUILayout.Width(90f))) Highlight(issue);
            using (new EditorGUI.DisabledScope(issue.Issue == IssueKind.EmptyReference))
                if (GUILayout.Button("候補を探す", GUILayout.Width(90f))) OpenCandidates(issue);
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

        private static void DrawSummary(IReadOnlyCollection<ReferenceRecord> issues)
        {
            var errors = issues.Count(item => item.Severity == IssueSeverity.Error);
            var warnings = issues.Count(item => item.Severity == IssueSeverity.Warning);
            var missingPrefabs = issues.Where(item => item.PropertyName == "m_SourcePrefab" && IsMissingPrefab(item) && !string.IsNullOrEmpty(item.Guid))
                .Select(item => item.Guid).Distinct(StringComparer.OrdinalIgnoreCase).Count();
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
            foreach (var guid in issueList.Where(item => item.PropertyName == "m_SourcePrefab" && IsMissingPrefab(item) && !string.IsNullOrEmpty(item.Guid))
                .Select(item => item.Guid).Distinct(StringComparer.OrdinalIgnoreCase))
                _foldouts[MissingPrefabKey(guid)] = expanded;
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
        private static string MissingPrefabKey(string guid) => "missing-prefab:" + guid;
        private static string ComponentKey(string path, string component) => "component:" + path + "\n" + component;
        private static string IssueKey(ReferenceRecord issue) => "issue:" + issue.SourceAssetPath + ":" + issue.LineNumber + ":" + issue.ReferenceColumn;
        private static Color SeverityColor(IssueSeverity severity) => severity == IssueSeverity.Error ? new Color(1f, .45f, .45f) : new Color(1f, .72f, .15f);
        private static string SeveritySymbol(IssueSeverity severity) => severity == IssueSeverity.Error ? "●" : "▲";

        private void DrawCandidatePanel()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("修復候補: " + _selectedIssue.PropertyName, EditorStyles.boldLabel);
            if (GUILayout.Button("閉じる", GUILayout.Width(60f)))
            {
                _selectedIssue = null;
                _candidates = null;
                _manualCandidate = null;
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.EndVertical();
                return;
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.HelpBox(AsnText.GuessWarning, MessageType.Warning);
            if (SimilarAssetFinder.BuildHints(_selectedIssue).Length == 0)
                EditorGUILayout.HelpBox("名前の手がかりがありません。Phase 2 で追加予定の unitypackage / ごみ箱探索が必要です。", MessageType.Info);
            if (_candidates == null || _candidates.Count == 0) EditorGUILayout.LabelField("類似候補は見つかりませんでした。");
            else foreach (var candidate in _candidates)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.ObjectField(candidate.Asset, typeof(UnityEngine.Object), false);
                if (GUILayout.Button("選択して修復", GUILayout.Width(110f))) Repair(candidate);
                EditorGUILayout.EndHorizontal();
                var scoreText = "推測 / score " + candidate.Score.ToString("0") + " ・ " + candidate.ScoreReason;
                EditorGUILayout.LabelField(new GUIContent(scoreText, candidate.ScoreReason), EditorStyles.wordWrappedMiniLabel);
                EditorGUILayout.EndVertical();
            }
            _manualCandidate = EditorGUILayout.ObjectField("手動指定（推測）", _manualCandidate, typeof(UnityEngine.Object), false);
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
            _candidates = SimilarAssetFinder.Find(issue);
            _manualCandidate = null;
            _scroll.y = float.MaxValue;
        }

        private void Repair(RepairCandidate candidate)
        {
            var repaired = PrefabReferenceRepairer.Repair(_selectedIssue, candidate);
            var repairStatus = repaired.Message + (string.IsNullOrEmpty(repaired.BackupDirectory) ? string.Empty : "\nバックアップ: " + repaired.BackupDirectory);
            if (repaired.Success) Inspect(); // F-7-7: 自動再検査。
            _status = repairStatus;
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

        private static string Escape(string value) => (value ?? string.Empty).Replace("|", "\\|");
    }
}
