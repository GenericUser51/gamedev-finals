using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Puts the player at the walled city's start whenever a scene containing the city loads (entering Play mode, or
/// loading the scene in a build), no matter where the player was left in the editor. Nothing to set up: it looks for
/// the KowloonStage built by Tools > Kowloon Stage, and does nothing in scenes without one.
/// </summary>
public static class KowloonSpawn
{
    const string StageName = "KowloonStage";
    const string SpawnName = "PlayerSpawn";

    // After the scene's objects have woken up but before their Start, so the player begins there on the first frame.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void OnFirstSceneLoaded()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;   // domain reload may be off: don't subscribe twice
        SceneManager.sceneLoaded += OnSceneLoaded;
        MovePlayerToStart();
    }

    static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (mode == LoadSceneMode.Single) MovePlayerToStart();
    }

    static void MovePlayerToStart()
    {
        GameObject stage = GameObject.Find(StageName);
        Transform spawn = stage != null ? stage.transform.Find(SpawnName) : null;
        if (spawn == null) return;   // no walled city in this scene

        CharacterController player = FindPlayer();
        if (player == null)
        {
            Debug.LogWarning("KowloonSpawn: no CharacterController player found to move to the walled city's start.");
            return;
        }

        // A CharacterController overrides transform changes while enabled.
        player.enabled = false;
        player.transform.SetPositionAndRotation(spawn.position, spawn.rotation);
        player.enabled = true;
        Physics.SyncTransforms();
    }

    static CharacterController FindPlayer()
    {
        GameObject tagged = GameObject.FindWithTag("Player");
        if (tagged != null)
        {
            CharacterController controller = tagged.GetComponentInChildren<CharacterController>();
            if (controller != null) return controller;
        }
        return Object.FindAnyObjectByType<CharacterController>();
    }
}
