using UnityEngine;

/// <summary>
/// Gives a pick-up-able object its own rotation while held by FirstPersonGrab, e.g. a bottle held upside
/// down by the neck. Put it on the object with the Rigidbody. Without it, held objects stay upright.
/// </summary>
public class HeldPose : MonoBehaviour
{
    [Tooltip("Rotation while held, relative to the way you're facing (degrees). " +
             "X tips it forward/back (180 = upside down), Y turns it, Z tilts it sideways.")]
    [SerializeField] Vector3 heldRotation = new Vector3(160f, 0f, 15f);

    public Quaternion Rotation => Quaternion.Euler(heldRotation);

    public void SetRotation(Vector3 eulerDegrees) => heldRotation = eulerDegrees;
}
