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
    }
}
