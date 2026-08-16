# インストール

## 動作環境

| 項目 | 要件 |
|---|---|
| Unity | 2022.3 (LTS) を主対象 |
| Asset Serialization | **Force Text**（[事前準備](serialization.md)） |
| VRChat SDK | 無くても動きます |
| 種別 | Editor 専用（ランタイムコードなし） |

!!! note "VRChat SDK は必須ではありません"
    このツールは Prefab のファイルそのものを読むので、SDK の有無に関係なく動きます。SDK が入っていない環境でもコンパイルが通ります。

!!! info "ごみ箱探索だけは OS で差があります"
    Windows の標準ごみ箱に対応しています。macOS / Linux も実装はしていますが、実機で確認していません。詳しくは[ごみ箱から探す](search-trash.md)を参照してください。

## VCC / ALCOM から導入する（推奨）

1. VCC または ALCOM に、配布元のリポジトリを追加します。
2. 対象の Unity プロジェクトを開き、**Manage Packages** を選びます。
3. 一覧から「アセット参照直すネーター」を追加します。

## unitypackage から導入する

VCC を使っていない場合は、[リリースページ](https://github.com/Poyotoron/Asset-Sanshounaosu-Nator/releases)から `.unitypackage` をダウンロードし、Unity のプロジェクトへドラッグ＆ドロップしてインポートしてください。

## 導入できたか確認する

Unity のメニューに次の項目が増えていればインストール成功です。

```
Tools > 参照直すネーター
```

クリックすると、最初に確認ダイアログが出ます。

!!! warning "初回だけ確認を求められます"
    「本ツールは Prefab を書き換える可能性があります」という内容のダイアログが出ます。**「了承して開く」を押すとウィンドウが開きます。** 「閉じる」を選ぶとウィンドウは開きません。一度了承すると、次回以降は表示されません。

ウィンドウは 1 枚で、上から順に次の要素が並びます。

| 位置 | 内容 |
|---|---|
| 上部 | Force Text でない場合の警告と変換ボタン |
| 探索設定 | 3 つの探索モードの切り替え、`.unitypackage` フォルダの登録、ごみ箱の再走査 |
| 対象 | 検査する Prefab / フォルダの指定 |
| 参照を検査 | 検査の実行ボタン |
| 検査結果 | 問題の一覧と、候補・修復の操作 |

## 設定の保存先

有効にした探索モードと、登録した `.unitypackage` フォルダは、次の場所に保存されます。

```
Assets/zzz_pytr/Asset-Sanshounaosu-Nator/Settings.asset
```

初回にツールを開いたときに自動で作られます。めったに開かないフォルダなので、Project ウィンドウの末尾側に並ぶ名前にしてあります。

`.unitypackage` の索引はこれとは別で、次の場所に置かれます。

```
Library/AssetSanshounaosuNator/unitypackage-index.json
```

!!! tip "`Library/` を消すと索引も消えます"
    索引はキャッシュ扱いなので `Library/` の中にあります。`Library/` を削除したり、プロジェクトをクリーンな状態から開き直すと**索引は消えます。** 消えても登録したフォルダの設定は残っているので、「索引を構築 / 更新」を押し直せば作り直せます。

## アンインストール

VCC の Manage Packages から削除します。次のものは `Assets/` の外、またはツールの管理外にあるため残ります。不要なら手で削除してください。

- `Assets/zzz_pytr/Asset-Sanshounaosu-Nator/`（設定）
- `AssetSanshounaosuNator_Backup/`（プロジェクト直下のバックアップとログ）

!!! warning "バックアップフォルダを消す前に"
    `AssetSanshounaosuNator_Backup/` には、**修復前の `.prefab` のコピー**が入っています。修復結果に納得していない段階でこれを消すと、元に戻せなくなります。詳しくは[バックアップとログ](backup.md)を参照してください。
