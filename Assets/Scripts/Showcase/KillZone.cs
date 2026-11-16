using UnityEngine;

/// <summary>
/// Trigger volume that sends the player back to the last Checkpoint (or a default spawn).
/// Works with any CharacterController-based player. Needs a Collider set to "Is Trigger".
/// </summary>
[RequireComponent(typeof(Collider))]
public class KillZone : MonoBehaviour
{
    [Tooltip("Used until the player touches a Checkpoint.")]
    [SerializeField] Transform defaultSpawn;

    public void SetDefaultSpawn(Transform spawn) => defaultSpawn = spawn;

    void Reset() => GetComponent<Collider>().isTrigger = true;

    void OnTriggerEnter(Collider other)
    {
        CharacterController controller = other.GetComponentInParent<CharacterController>();
        if (controller == null) return;

        Vector3 position;
        Quaternion rotation;
        if (Checkpoint.HasCurrent)
        {
            position = Checkpoint.CurrentPosition;
            rotation = Checkpoint.CurrentRotation;
        }
        else if (defaultSpawn != null)
        {
            position = defaultSpawn.position;
            rotation = defaultSpawn.rotation;
        }
        else return;

        // A CharacterController overrides transform changes while enabled.
        controller.enabled = false;
        controller.transform.SetPositionAndRotation(position, rotation);
        controller.enabled = true;
    }
}
