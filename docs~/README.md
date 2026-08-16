# ドキュメントサイトのローカルプレビュー

以下は PowerShell で、リポジトリのルートから実行します。ドキュメント用の Python 環境は兄弟リポジトリと共用し、Unity プロジェクト内に仮想環境を作らないでください。

```powershell
& "$env:USERPROFILE\.venvs\vpm-docs\Scripts\Activate.ps1"
uv pip install -r "docs~/requirements.txt"
mkdocs serve -f "docs~/mkdocs.yml"
```

`uv` を使わない場合は、依存の更新に `pip install -r "docs~/requirements.txt"` を使用できます。

公開前は CI と同じ厳密な条件でビルドします。`ASN_VERSION` を指定しなければ、フッタの対応バージョンは `dev` になります。

```powershell
mkdocs build --strict -f "docs~/mkdocs.yml"
```

ビルド成果物の `docs~/site/` はコミットしないでください。また、`docs~/` の末尾の `~` は消さないでください。消すと Unity が配下に `.meta` を作り、配布物にも混ざります。
