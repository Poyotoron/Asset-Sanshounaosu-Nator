using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Maaaaa.Asn.Editor.Core
{
    /// <summary>プロジェクト単位の探索設定。兄弟ツールと同じ zzz_pytr 配下へ保存する。</summary>
    internal sealed class AsnSettings : ScriptableObject
    {
        public const string SettingsFolder = "Assets/zzz_pytr/Asset-Sanshounaosu-Nator";
        public const string SettingsPath = SettingsFolder + "/Settings.asset";

        public List<string> unityPackageFolders = new List<string>();
        public bool projectSearchEnabled = true;
        public bool unityPackageSearchEnabled = true;
        public bool recycleBinSearchEnabled = true;

        private static AsnSettings _cached;

        public static AsnSettings GetOrCreate()
        {
            if (_cached != null) return _cached;
            _cached = AssetDatabase.LoadAssetAtPath<AsnSettings>(SettingsPath);
            if (_cached != null) return _cached;
            EnsureFolder();
            _cached = CreateInstance<AsnSettings>();
            AssetDatabase.CreateAsset(_cached, SettingsPath);
            AssetDatabase.SaveAssets();
            return _cached;
        }

        private static void EnsureFolder()
        {
            const string authorFolder = "Assets/zzz_pytr";
            if (!AssetDatabase.IsValidFolder(authorFolder)) AssetDatabase.CreateFolder("Assets", "zzz_pytr");
            if (!AssetDatabase.IsValidFolder(SettingsFolder)) AssetDatabase.CreateFolder(authorFolder, "Asset-Sanshounaosu-Nator");
        }

        public void Save()
        {
            EditorUtility.SetDirty(this);
            AssetDatabase.SaveAssets();
        }
    }
}
