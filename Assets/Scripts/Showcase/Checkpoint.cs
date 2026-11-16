using UnityEngine;

/// <summary>
/// Trigger volume that saves where the player respawns after falling into a KillZone.
/// Needs a Collider set to "Is Trigger".
/// </summary>
[RequireComponent(typeof(Collider))]
public class Checkpoint : MonoBehaviour
{
    [Tooltip("Where the player's feet are placed on respawn, relative to this object.")]
    [SerializeField] Vector3 spawnOffset = Vector3.zero;

    public static bool HasCurrent { get; private set; }
    public static Vector3 CurrentPosition { get; private set; }
    public static Quaternion CurrentRotation { get; private set; }

    public Vector3 SpawnPosition => transform.TransformPoint(spawnOffset);

    // Statics survive between Play sessions when domain reload is off, so clear them on each start.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => HasCurrent = false;

    void Reset() => GetComponent<Collider>().isTrigger = true;

    void OnTriggerEnter(Collider other)
    {
        if (other.GetComponentInParent<CharacterController>() == null) return;
        HasCurrent = true;
        CurrentPosition = SpawnPosition;
        CurrentRotation = Quaternion.Euler(0f, transform.eulerAngles.y, 0f);
    }

    void OnDrawGizmos()
    {
        Gizmos.color = new Color(0.2f, 1f, 0.4f, 0.8f);
        Gizmos.DrawWireSphere(SpawnPosition + Vector3.up * 0.5f, 0.5f);
    }
}
