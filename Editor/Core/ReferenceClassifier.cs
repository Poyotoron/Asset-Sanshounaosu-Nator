using System.Collections.Generic;

namespace Maaaaa.Asn.Editor.Core
{
    internal static class ReferenceClassifier
    {
        public static void ClassifyAll(IEnumerable<ReferenceRecord> records)
        {
            foreach (var record in records) Classify(record);
        }

        private static void Classify(ReferenceRecord record)
        {
            record.Issue = IssueKind.None;
            record.Severity = IssueSeverity.None;
            if (record.FileId == 0)
            {
                record.Issue = IssueKind.EmptyReference;
                record.Severity = IssueSeverity.Warning;
                record.CollapsedByDefault = true;
            }
            else if (!record.GuidResolved)
            {
                record.Issue = record.IsScript ? IssueKind.MissingScript : IssueKind.GuidMissing;
                record.Severity = IssueSeverity.Error;
            }
            else if (record.BackingFileMissing)
            {
                record.Issue = IssueKind.FileIdMissing;
                record.Severity = IssueSeverity.Error;
            }
            else if (!record.FileIdResolved)
            {
                record.Issue = IssueKind.FileIdMissing;
                // Unity 内部で吸収される可能性があるため、F-3-2 に従い警告とする。
                record.Severity = IssueSeverity.Warning;
            }
            else if (record.ExpectedType != null && record.ResolvedType != null && !record.ExpectedType.IsAssignableFrom(record.ResolvedType))
            {
                record.Issue = IssueKind.TypeMismatch;
                record.Severity = IssueSeverity.Warning;
            }
        }

        public static string Label(IssueKind kind)
        {
            switch (kind)
            {
                case IssueKind.GuidMissing: return "T-A GUID 解決不可";
                case IssueKind.FileIdMissing: return "T-B fileID 解決不可";
                case IssueKind.MissingScript: return "T-C Missing Script";
                case IssueKind.EmptyReference: return "T-D 空参照";
                case IssueKind.TypeMismatch: return "T-E 型不一致";
                default: return "正常";
            }
        }

        public static string Label(ReferenceRecord record)
        {
            return record.BackingFileMissing ? "T-B 参照先ファイル本体欠落" : Label(record.Issue);
        }

        public static string Description(ReferenceRecord record)
        {
            return record.BackingFileMissing
                ? "T-B 参照先ファイル本体欠落: GUID のパスは判明していますが、Assets/ または Packages/ 配下の実ファイルが存在しません。"
                : Description(record.Issue);
        }

        public static string Description(IssueKind kind)
        {
            switch (kind)
            {
                case IssueKind.GuidMissing: return "T-A GUID 解決不可: guid がプロジェクト内に存在しません（パッケージ未導入、meta 再生成など）。";
                case IssueKind.FileIdMissing: return "T-B fileID 解決不可: guid は解決しますが fileID がアセット内にありません（FBX の再インポート等でサブアセットの構成が変わり、fileID が変化した場合など）。";
                case IssueKind.MissingScript: return "T-C Missing Script: m_Script の guid が解決できません（スクリプトやパッケージの消失、Dynamic Bone の残骸など）。";
                case IssueKind.EmptyReference: return "T-D 空参照: fileID が 0 です（意図的な未設定の可能性があり、誤検知を多く含みます）。";
                case IssueKind.TypeMismatch: return "T-E 型不一致: 解決したアセットの型がフィールドの期待型と異なります（手動書き換えの失敗など）。";
                default: return "問題は検出されていません。";
            }
        }
    }
}
