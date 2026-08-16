using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Keeps the authored map scene explicit: the city presentation component is
/// visible and editable in the hierarchy instead of existing only at runtime.
/// </summary>
[InitializeOnLoad]
public static class HexCityLayerSceneInstaller
{
	static HexCityLayerSceneInstaller()
	{
		EditorApplication.delayCall += EnsureCityLayerInOpenScene;
	}

	static void EnsureCityLayerInOpenScene()
	{
		if (EditorApplication.isPlayingOrWillChangePlaymode)
		{
			return;
		}

		HexGrid grid = Object.FindObjectOfType<HexGrid>();
		if (!grid || grid.GetComponentInChildren<HexCityLayer>(true))
		{
			return;
		}

		GameObject cityObject = new("World Cities");
		Undo.RegisterCreatedObjectUndo(cityObject, "Install World Cities Layer");
		cityObject.transform.SetParent(grid.transform, false);
		Undo.AddComponent<HexCityLayer>(cityObject);
		EditorSceneManager.MarkSceneDirty(grid.gameObject.scene);
		EditorSceneManager.SaveScene(grid.gameObject.scene);
		Debug.Log(
			"Installed Hex Grid/World Cities with HexCityLayer in the open map scene.",
			cityObject);
	}
}
