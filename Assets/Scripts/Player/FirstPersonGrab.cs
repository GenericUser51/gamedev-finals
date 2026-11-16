using System;
using System.Collections;
using StarterAssets;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Pick up and throw physics objects, for the Starter Assets FirstPersonController. Put it on PlayerCapsule.
///
/// Look at any non-kinematic Rigidbody (up to Max Mass) within reach and press Interact (E / gamepad X) to
/// pick it up. It floats in front of you and keeps up when you run. Press Throw (left click / right trigger)
/// to throw it, or Interact again to drop it. Heavier objects fly slower; throws add your own movement speed.
///
/// Uses the "Interact" and "Throw" actions from the PlayerInput's actions asset.
/// </summary>
[RequireComponent(typeof(FirstPersonController))]
public class FirstPersonGrab : MonoBehaviour
{
    [Header("Picking Up")]
    [Tooltip("How far away you can grab objects (m).")]
    [SerializeField] float pickupRange = 3.5f;
    [Tooltip("Heaviest object you can pick up (kg).")]
    [SerializeField] float maxMass = 25f;
    [Tooltip("Layers that can be picked up. Defaults to everything.")]
    [SerializeField] LayerMask grabLayers = ~0;

    [Header("Holding")]
    [Tooltip("Where a held object sits relative to the camera: right, up, forward (m). Like holding it in your " +
             "right hand. Bigger objects are pushed further forward so they don't clip into the camera.")]
    [SerializeField] Vector3 heldPosition = new Vector3(0.35f, -0.15f, 0.7f);
    [Tooltip("How tightly the object follows your hand. Higher = snappier, lower = floatier.")]
    [SerializeField] float holdStiffness = 25f;
    [Tooltip("How quickly the object turns to stay upright and face the way you look. Higher = snappier.")]
    [SerializeField] float rotationStiffness = 15f;
    [Tooltip("Fastest the object moves to catch up with the hold point (m/s).")]
    [SerializeField] float maxFollowSpeed = 25f;
    [Tooltip("If the object gets snagged this far from the hold point (e.g. on a wall), you drop it (m).")]
    [SerializeField] float dropDistance = 2.5f;

    [Header("Throwing")]
    [Tooltip("Throw speed for light objects (m/s).")]
    [SerializeField] float throwPower = 48f;
    [Tooltip("Throws fly toward what the crosshair is on, within this distance (m); beyond it, straight ahead.")]
    [SerializeField] float aimDistance = 100f;
    [Tooltip("Objects up to this mass get the full throw speed; heavier ones fly proportionally slower (kg).")]
    [SerializeField] float fullSpeedMass = 5f;
    [Tooltip("Slowest a heavy throw can be, as a fraction of Throw Speed.")]
    [SerializeField, Range(0f, 1f)] float minThrowSpeedFraction = 0.3f;
    [Tooltip("End-over-end spin given to thrown objects (radians/sec; 12 ≈ 2 turns per second). " +
             "Heavy objects spin proportionally slower, like their throw speed. 0 = no spin.")]
    [SerializeField] float throwSpin = 12f;
    [Tooltip("How much the spin axis wobbles randomly, so throws don't all tumble identically. 0 = always clean end-over-end.")]
    [SerializeField, Range(0f, 1f)] float throwSpinRandomness = 0.3f;
    [Tooltip("Add your own movement speed to throws and drops, so sprinting throws go further.")]
    [SerializeField] bool inheritPlayerVelocity = true;

    [Header("On-Screen")]
    [Tooltip("Show a small dot in the middle of the screen.")]
    [SerializeField] bool showCrosshair = true;
    [Tooltip("Show which key picks up / throws when looking at or holding something.")]
    [SerializeField] bool showPrompts = true;

    public Rigidbody HeldObject => held;
    public bool IsHolding => held != null;
    public event Action<Rigidbody> PickedUp;
    public event Action<Rigidbody> Thrown;
    public event Action<Rigidbody> Dropped;

    FirstPersonController fpc;
    CharacterController controller;
    PlayerInput playerInput;
    InputAction interactAction, throwAction;
    Transform cameraTransform;

    Rigidbody held;
    Collider[] heldColliders;
    float heldSize;          // half the object's largest dimension, to keep it clear of the camera
    float heldYawOffset;     // its facing relative to ours when picked up, kept while held
    HeldPose heldPose;       // optional custom in-hand rotation
    bool heldUsedGravity;
    RigidbodyInterpolation heldInterpolation;
    Rigidbody lookTarget; // what the crosshair is on (for the prompt)

    GUIStyle promptStyle;

    void Awake()
    {
        fpc = GetComponent<FirstPersonController>();
        controller = GetComponent<CharacterController>();
        playerInput = GetComponent<PlayerInput>();
    }

    void Start()
    {
        // PlayerInput sets up its actions by Start.
        if (playerInput != null && playerInput.actions != null)
        {
            interactAction = playerInput.actions.FindAction("Interact", false);
            throwAction = playerInput.actions.FindAction("Throw", false);
        }

        if (interactAction == null || throwAction == null)
        {
            Debug.LogWarning("FirstPersonGrab: the PlayerInput's actions asset needs 'Interact' and 'Throw' " +
                             "actions. Picking up is disabled.", this);
            enabled = false;
        }
    }

    void OnDisable()
    {
        if (held != null) Release(throwIt: false);
    }

    void Update()
    {
        if (Camera.main != null) cameraTransform = Camera.main.transform;
        if (cameraTransform == null) return;

        // The held object was destroyed or disabled by something else.
        if (held != null && !held.gameObject.activeInHierarchy) Release(throwIt: false);

        lookTarget = held == null ? FindGrabbable() : null;

        if (interactAction.WasPressedThisFrame())
        {
            if (held != null) Release(throwIt: false);
            else if (lookTarget != null) PickUp(lookTarget);
        }
        else if (held != null && throwAction.WasPressedThisFrame())
        {
            Release(throwIt: true);
        }
    }

    void FixedUpdate()
    {
        if (held == null || cameraTransform == null) return;

        // Hand position: lower right of the view, pushed out by the object's size so it doesn't clip the camera.
        Vector3 offset = heldPosition + Vector3.forward * heldSize;
        Vector3 holdPoint = cameraTransform.TransformPoint(offset);
        Vector3 toHoldPoint = holdPoint - held.worldCenterOfMass;

        if (toHoldPoint.magnitude > dropDistance)
        {
            Release(throwIt: false); // snagged on something
            return;
        }

        // Pull toward the hand, plus our own velocity so it keeps up when we run.
        Vector3 velocity = Vector3.ClampMagnitude(toHoldPoint * holdStiffness, maxFollowSpeed);
        held.linearVelocity = velocity + fpc.LastMoveVelocity;

        // Turn with our view. Objects with a HeldPose use their own pose (e.g. a bottle upside down);
        // others stay upright, keeping whichever way they faced when we grabbed them.
        Quaternion targetRotation = heldPose != null
            ? Quaternion.Euler(0f, CameraYaw(), 0f) * heldPose.Rotation
            : Quaternion.Euler(0f, CameraYaw() + heldYawOffset, 0f);
        Quaternion delta = targetRotation * Quaternion.Inverse(held.rotation);
        delta.ToAngleAxis(out float angle, out Vector3 axis);
        if (angle > 180f) angle -= 360f; // shortest way round
        held.angularVelocity = Mathf.Abs(angle) > 0.01f && !float.IsNaN(axis.x)
            ? axis * (angle * Mathf.Deg2Rad * rotationStiffness)
            : Vector3.zero;
    }

    float CameraYaw() => cameraTransform != null ? cameraTransform.eulerAngles.y : transform.eulerAngles.y;

    Rigidbody FindGrabbable()
    {
        if (!Physics.Raycast(cameraTransform.position, cameraTransform.forward, out RaycastHit hit, pickupRange,
                             grabLayers, QueryTriggerInteraction.Ignore))
            return null;

        Rigidbody body = hit.rigidbody;
        if (body == null || body.isKinematic || body.mass > maxMass) return null;
        if (body.transform.IsChildOf(transform)) return null; // part of the player
        return body;
    }

    void PickUp(Rigidbody body)
    {
        held = body;
        heldColliders = body.GetComponentsInChildren<Collider>();
        foreach (Collider c in heldColliders) Physics.IgnoreCollision(c, controller, true); // no prop surfing

        // Size, so bigger objects are held further out.
        heldSize = 0f;
        if (heldColliders.Length > 0)
        {
            Bounds bounds = heldColliders[0].bounds;
            for (int i = 1; i < heldColliders.Length; i++) bounds.Encapsulate(heldColliders[i].bounds);
            heldSize = Mathf.Max(bounds.extents.x, bounds.extents.y, bounds.extents.z);
        }
        heldYawOffset = Mathf.DeltaAngle(CameraYaw(), body.rotation.eulerAngles.y);
        heldPose = body.GetComponent<HeldPose>();

        heldUsedGravity = body.useGravity;
        heldInterpolation = body.interpolation;
        body.useGravity = false;
        body.interpolation = RigidbodyInterpolation.Interpolate; // smooth while it follows the camera
        // Fast throws shouldn't pass through thin walls.
        if (body.collisionDetectionMode == CollisionDetectionMode.Discrete)
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;

        PickedUp?.Invoke(body);
    }

    void Release(bool throwIt)
    {
        Rigidbody body = held;
        Collider[] colliders = heldColliders;
        held = null;
        heldColliders = null;
        if (body == null) return;

        body.useGravity = heldUsedGravity;
        body.interpolation = heldInterpolation;

        Vector3 baseVelocity = inheritPlayerVelocity ? fpc.LastMoveVelocity : Vector3.zero;
        if (throwIt && cameraTransform != null)
        {
            float massFactor = Mathf.Clamp(fullSpeedMass / Mathf.Max(body.mass, 0.01f), minThrowSpeedFraction, 1f);
            body.linearVelocity = baseVelocity + AimDirection(body, colliders) * (throwPower * massFactor);

            // Tumble forward end over end (around the camera's right axis), with a little random wobble.
            if (throwSpin > 0f)
            {
                Vector3 axis = (cameraTransform.right + UnityEngine.Random.insideUnitSphere * throwSpinRandomness).normalized;
                float spin = throwSpin * massFactor;
                body.maxAngularVelocity = Mathf.Max(body.maxAngularVelocity, spin * 1.5f);
                body.angularVelocity = axis * spin;
            }
            Thrown?.Invoke(body);
        }
        else
        {
            body.linearVelocity = baseVelocity;
            Dropped?.Invoke(body);
        }

        // Collide with the player again once it has had a moment to get clear.
        if (isActiveAndEnabled) StartCoroutine(RestoreCollisionAfterDelay(colliders, 0.3f));
        else RestoreCollision(colliders);
    }

    // The object is held off to the side, so aim it at what the crosshair is on rather than straight ahead.
    Vector3 AimDirection(Rigidbody body, Collider[] bodyColliders)
    {
        Vector3 aimPoint = cameraTransform.position + cameraTransform.forward * aimDistance;
        RaycastHit[] hits = Physics.RaycastAll(cameraTransform.position, cameraTransform.forward, aimDistance,
                                               ~0, QueryTriggerInteraction.Ignore);
        float closest = float.MaxValue;
        foreach (RaycastHit hit in hits)
        {
            // Skip ourselves and the object being thrown.
            if (hit.collider == controller || hit.collider.transform.IsChildOf(transform)) continue;
            if (hit.rigidbody == body) continue;
            if (hit.distance < closest)
            {
                closest = hit.distance;
                aimPoint = hit.point;
            }
        }

        Vector3 direction = aimPoint - body.worldCenterOfMass;
        // Something right in front of us: just throw along the view.
        return direction.sqrMagnitude > 0.25f ? direction.normalized : cameraTransform.forward;
    }

    IEnumerator RestoreCollisionAfterDelay(Collider[] colliders, float delay)
    {
        yield return new WaitForSeconds(delay);
        RestoreCollision(colliders);
    }

    void RestoreCollision(Collider[] colliders)
    {
        if (colliders == null || controller == null) return;
        foreach (Collider c in colliders)
            if (c != null) Physics.IgnoreCollision(c, controller, false);
    }

    // ---- On-screen crosshair and prompts ----

    void OnGUI()
    {
        if (!showCrosshair && !showPrompts) return;
        float cx = Screen.width * 0.5f, cy = Screen.height * 0.5f;

        if (showCrosshair)
        {
            Color old = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, held != null || lookTarget != null ? 0.95f : 0.5f);
            GUI.DrawTexture(new Rect(cx - 2f, cy - 2f, 4f, 4f), Texture2D.whiteTexture);
            GUI.color = old;
        }

        if (!showPrompts) return;
        string text = null;
        if (held != null) text = $"[{KeyName(throwAction)}] Throw     [{KeyName(interactAction)}] Drop";
        else if (lookTarget != null) text = $"[{KeyName(interactAction)}] Pick up";
        if (text == null) return;

        if (promptStyle == null)
        {
            promptStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 18,
                fontStyle = FontStyle.Bold
            };
        }

        var rect = new Rect(cx - 200f, cy + 24f, 400f, 30f);
        promptStyle.normal.textColor = new Color(0f, 0f, 0f, 0.6f); // shadow
        GUI.Label(new Rect(rect.x + 1f, rect.y + 1f, rect.width, rect.height), text, promptStyle);
        promptStyle.normal.textColor = Color.white;
        GUI.Label(rect, text, promptStyle);
    }

    // Key name for the current device, e.g. "E" on keyboard or "X" on an Xbox pad.
    string KeyName(InputAction action)
    {
        if (action == null) return "?";
        string scheme = playerInput != null ? playerInput.currentControlScheme : null;
        string name = string.IsNullOrEmpty(scheme)
            ? action.GetBindingDisplayString()
            : action.GetBindingDisplayString(InputBinding.MaskByGroup(scheme));
        return string.IsNullOrEmpty(name) ? action.GetBindingDisplayString() : name;
    }
}
