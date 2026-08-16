# アセット参照直すネーター (Asset-Sanshounaosu-Nator)

VRChat のアバター改変用 Unity プロジェクトで頻発する **Prefab の壊れた参照 (Missing Reference)** を、**検査 → 診断 → 提案 → 修復**する Unity Editor 拡張です。BOOTH 由来 Prefab の依存パッケージ未導入、サブアセットの fileID 不一致、meta 再生成による GUID 変化、Missing Script などを機械的に洗い出し、承認のうえで復元します。

> **本ツールは Prefab を書き換える破壊的操作を含みます。** そのため、**自動修復はしない（承認制）／書き換え前に必ず退避／「確実な復元」と「推測」を明確に区別**、を設計原則としています。

**📖 使い方ドキュメント: <https://ghp.maaaaa.net/Asset-Sanshounaosu-Nator/>**

導入手順・画面の読み方・探索モードの使い分け・修復の手順は、上のドキュメントサイトにまとまっています。この README は概要と警告までです。

---

## 動作環境

| 項目 | 要件 |
|---|---|
| Unity | 2022.3 (LTS) を主対象 |
| Asset Serialization | **Force Text**（未設定時はツールから変換を誘導） |
| VRChat SDK | ソフト依存（未導入でも動作・コンパイル可） |
| 種別 | Editor 専用（ランタイムコードなし） |

---

## できること (0.2.0)

- **前提チェックと Force Text 変換誘導** — Binary / Mixed のときはワンクリック変換を案内（承認制）。
- **参照走査** — `.prefab` の YAML を直読みし、`m_Modifications` の `target`・`m_Script`・`m_SourcePrefab`（親 Prefab）まで走査。壊れて開けない Prefab も検査対象にできる。
- **分類と可視化** — T-A〜T-E に分類、深刻度で色分け、種別フィルタ、件数サマリ、Markdown コピー。欠落 Prefab は名前つきで **Prefab 単位にまとめて**表示。
- **候補提示（探索モード 1）** — プロジェクト内から名前の近いアセットを提示。スコアの根拠（一致の種類・一致した名前）も表示。候補は常に「推測」と明示。
- **探索モード 2: `.unitypackage`** — 登録フォルダ内を展開せず索引化し、欠落 GUID を含む package と元パスを確実な候補として案内する。GUID が変わっている場合は名前でも探索するが、名前一致は推測であり、インポート後にプロジェクト内候補として選び直す必要がある。インポートは自動実行しない。
- **探索モード 3: OS ごみ箱** — `.meta` の GUID 一致と本体のみの名前一致を区別して探索。承認後、ごみ箱の原本を残したまま Assets 配下へコピー。
- **fileID / Missing Script 修復** — 同一アセット内のサブアセット選択と、MonoScript 名・残存フィールド構成による候補提示に対応。
- **一括検査** — 複数選択またはフォルダ配下の Prefab を、進捗・キャンセル付きで検査。Prefab 単位の結果と横断 Markdown を出力。
- **修復（承認制）** — 可能なら SerializedProperty 代入、無理なら YAML 直接書き換え（Diff プレビュー付き）。**書き換え前に必ずバックアップ**し、修復後に再インポート＋自動再検査。
- **実行ログ** — 何をどのファイルの何行でどう書き換えたかを記録。

バックアップとログは `<Project>/AssetSanshounaosuNator_Backup/<日時>/`（`Assets/` 外）に出力されます。

## 使い方

メニュー `Tools/参照直すネーター` を開き、対象の Prefab またはフォルダを指定して「参照を検査」を押します。

詳しい手順（探索モードの準備、結果の読み方、修復の承認、元に戻す方法）は**[ドキュメントサイト](https://ghp.maaaaa.net/Asset-Sanshounaosu-Nator/)**を参照してください。

| 知りたいこと | ページ |
|---|---|
| 最短で 1 回試す | [クイックスタート](https://ghp.maaaaa.net/Asset-Sanshounaosu-Nator/quickstart/) |
| Force Text への変換 | [事前準備](https://ghp.maaaaa.net/Asset-Sanshounaosu-Nator/serialization/) |
| 検査結果の分類の意味 | [結果の読み方](https://ghp.maaaaa.net/Asset-Sanshounaosu-Nator/results/) |
| 修復の流れと注意点 | [修復する](https://ghp.maaaaa.net/Asset-Sanshounaosu-Nator/repair/) |
| 直したものを元に戻す | [バックアップとログ](https://ghp.maaaaa.net/Asset-Sanshounaosu-Nator/backup/) |

---

## 注意 / 警告

- **Force Text 以外では動作しません**（Binary は非対応。変換を誘導します）。変換は全アセットの再シリアライズを伴い、VCS に大量の差分を生みます。事前のコミット/バックアップを推奨します。
- **名前一致による修復は推測**であり、誤った紐付けが起こり得ます。
- **`.unitypackage` の索引構築は時間を要し得ます**。本ツールは package を自動インポートせず、対象の案内までを行います。
- `.unitypackage` の名前一致は推測です。ありふれた名前や多数のエントリに一致する名前はノイズ防止のため候補から除外され、名前一致した package をインポートしても参照は自動では直りません。
- **ごみ箱探索は万能ではありません**。永久削除、ごみ箱を空にした場合、OS の自動削除後は回収できません。`.meta` が無ければ GUID を保持できず、名前一致（推測）になります。Windows の標準ごみ箱に対応しています。macOS / Linux の走査も実装していますが、実機では検証していません。いずれの OS でも、アクセス権や OS の形式変更で列挙できない場合があります。
- **Missing Script の設定値が完全に復元される保証はありません**。同じ型へ戻す場合は YAML に残る値が読み込まれる可能性がありますが、本ツールでは実機で検証していません。別の型へ差し替える場合を含め、対応しないフィールドなどの値が失われるおそれがあります。必ずバックアップと Diff を確認してください。
- 一括機能は**検査のみ**です。一括修復は未対応で、修復は 1 件ずつ承認します。
- YAML 直接書き換えは Unity 非公式の手法で、将来のバージョンで破綻し得ます。
- 元アセットがどこにも存在しない場合は解決できません。

---

## ライセンス

MIT License（[LICENSE](LICENSE) 参照）。

## 三兄弟

整理 (Asset-Omatome-Nator) / 削除 (Asset-Keshichaumon-Nator) / **修復 (Asset-Sanshounaosu-Nator)** の 3 兄弟で「整理・削除・修復」を構成します。本ツールは「壊れた参照を直す」役割を担います。
