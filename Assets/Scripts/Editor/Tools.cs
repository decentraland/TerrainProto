using UnityEditor;

namespace Decentraland.Terrain
{
    internal static class Tools
    {
        [MenuItem("Tools/Reload Scripts")]
        private static void ReloadScripts() =>
            EditorUtility.RequestScriptReload();
    }
}
