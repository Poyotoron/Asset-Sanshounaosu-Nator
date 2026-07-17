namespace Maaaaa.Asn.Editor
{
    internal static class AsnText
    {
        public const string WindowTitle = "参照直すネーター";
        public const string FirstRunWarning = "本ツールは Prefab を書き換える可能性があります。修復前にはバックアップを作成しますが、VCS へのコミットまたは手動バックアップも推奨します。";
        public const string GuessWarning = "これは推測です。誤ったアセットを紐付ける可能性があります。";
        public const string ForceTextReason = "本ツールは壊れた参照の GUID を調べるため Prefab の YAML を直接読みます。Binary / Mixed には対応していません。";
        public const string ForceTextCost = "変換は全アセットの再シリアライズを伴います。大規模プロジェクトでは時間がかかり、VCS に大量の差分が生じます。先にコミットまたはバックアップしてください。";
        public const string RepairWarning = "修復前にバックアップを作成します。VCS へのコミットも推奨します。修復してもコンポーネントの設定値が完全に復元される保証はありません。";
        public const string MissingScriptRepairWarning = "Missing Script の m_Script を YAML で直接書き換えます。本ツールでは、同じ型へ戻した場合や別の型へ差し替えた場合に、既存の設定値がどう扱われるかを実機で検証していません。YAML に残る値が読み込まれる可能性はありますが、対応しないフィールドを含め値が失われるおそれがあります。設定値の完全な復元は保証できません。必ずバックアップと Diff を確認してください。";
        public const string RecycleBinLimitWarning = "ごみ箱探索は万能ではありません。永久削除、ごみ箱を空にした場合、OS による自動削除後は回収できません。.meta が無い場合は GUID を保持できず、名前一致の推測へ縮退します。";
        public const string UnityPackageImportGuide = "この .unitypackage をユーザー自身でインポートすれば、元の GUID が復活して解決する可能性があります。本ツールは影響範囲を予測できないため自動インポートしません。インポート後に再検査してください。";
        public const string UnityPackageNameMatchGuide = "この .unitypackage に同名または部分一致のアセットがあります。名前一致は推測であり、GUID が異なるためインポートだけでは参照は直りません。インポート後、再検査してプロジェクト内の候補として選び直してください。";
    }
}
