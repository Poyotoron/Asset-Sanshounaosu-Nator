# Changelog

このプロジェクトの変更履歴。バージョニングは [Semantic Versioning](https://semver.org/lang/ja/) に従う。

## [0.1.0] - 2026-07-16

初回リリース (MVP)。Prefab の壊れた参照 (Missing Reference) を **検査 → 診断 → 提案 → 修復** する Editor 拡張。破壊的操作を含むため、**承認制・バックアップ必須・推測の明示**を設計原則とする。

### Added
- **前提条件チェックと Force Text 変換誘導 (F-0)**: 起動時に Asset Serialization Mode を検査し、`Force Text` 以外なら検査を止めつつ、ワンクリックで Force Text へ変換する導線を出す。変換は確認ダイアログ＋明示的承認時のみ実行し、再シリアライズのコストと VCS 差分を事前に警告する。
- **Prefab 選択 UI (F-1)**: `Tools/参照直すネーター` から開く EditorWindow。Project 選択の取り込み・ObjectField への D&D に対応。**参照が壊れて GameObject として開けない Prefab（Broken Prefab）も対象に指定できる。**
- **参照走査 (F-2)**: `.prefab` を YAML テキストとして直読みし、`{fileID, guid, type}` 参照を抽出。`m_Modifications` 内の `target`・`m_Script`・`m_SourcePrefab`（Prefab Variant / Nested の親）も対象に含め、親 Prefab を再帰走査する。
- **問題の分類 (F-3)**: T-A GUID 解決不可 / T-B fileID 解決不可 / T-C Missing Script / T-D 空参照 / T-E 型不一致 に分類し深刻度を割り当てる。**参照先ファイル本体の物理欠落**は Unity のキャッシュ状態に関わらず Error として検出する。
- **結果表示 (F-4)**: 階層ツリー・深刻度の色分け・種別フィルタ・件数サマリ・Markdown コピー。表示選択ボタンには種別説明のツールチップを備える。件数が多い場合は既定で折りたたみ、「すべて展開 / 折りたたむ」で一括切替。
- **欠落 Prefab のまとめ表示**: 同じ欠落 Prefab に起因する参照（`m_SourcePrefab` ＋ その override `target`）を **Prefab 単位でグルーピング**し、代表ノードに欠落 Prefab 名（YAML から復元）・元パス・候補探索を集約。複数 Prefab の欠落を区別しやすくした。
- **探索モード 1: 類似の名前探索 (F-5-1)**: 未解決参照に対し、プロジェクト内から名前の近いアセットを候補提示する。元ファイル名を最優先の手がかりにし、**スコアの根拠（完全一致 / 前方一致 / 部分一致 / 編集距離、一致したヒントと対象）を可視化**する。候補は常に「推測」と明示し、自動適用しない。手動指定の経路も備える。
- **修復の実行 (F-7)**: 承認制。API で代入可能なら SerializedProperty 代入 (R-1)、それ以外（Missing Script / 親参照 / サブアセット fileID）は YAML 直接書き換え (R-2)。**いかなる修復の前にも対象を退避**し、R-2 は Diff プレビュー後に承認を求める。YAML 置換は元参照の `type` を保持する。修復後は再インポートと自動再検査を行う。
- **実行ログ (F-8)**: 検査結果と修復操作（どの GUID をどのファイルの何行で、どの方式で書き換えたか）をタイムスタンプ付きで記録する。
- **バックアップ / ログ出力先**: `<Project>/AssetSanshounaosuNator_Backup/<yyyyMMdd_HHmmss>/`（`Assets/` 外、Unity のインポート対象外）。

### 既知の制限
- **Force Text シリアライズ前提**。Binary は非対応（変換を誘導する）。
- 探索モード 2（`.unitypackage` 逆引き）・モード 3（OS ごみ箱回収）は **Phase 2 で対応予定（本リリース未対応）**。
- 名前一致による修復は**推測**であり、誤った紐付けが起こり得る。
- 修復してもコンポーネントの設定値が完全に復元される保証はない。
- 元アセットがどこにも存在しない場合は解決できない。
