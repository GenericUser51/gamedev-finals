using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Platform that moves back and forth and carries CharacterControllers standing on it.
///
/// Setup: a kinematic Rigidbody on this object, a solid collider (on this object or a child) to stand on,
/// and a trigger BoxCollider on this object covering the space just above the surface to detect riders.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
[DefaultExecutionOrder(-100)] // move before players so they follow without lag
public class MovingPlatform : MonoBehaviour
{
    [Tooltip("How far the platform travels from its start position (world space).")]
    [SerializeField] Vector3 travel = new Vector3(0f, 0f, 8f);
    [Tooltip("Travel speed (m/s).")]
    [SerializeField] float speed = 2.5f;
    [Tooltip("Pause at each end (sec).")]
    [SerializeField] float pauseTime = 1f;

    readonly List<CharacterController> riders = new List<CharacterController>();
    Vector3 startPosition;
    float progress;      // 0 = start, 1 = end
    int direction = 1;
    float pauseTimer;

    public void Configure(Vector3 newTravel, float newSpeed, float newPause)
    {
        travel = newTravel;
        speed = newSpeed;
        pauseTime = newPause;
    }

    void Reset()
    {
        var body = GetComponent<Rigidbody>();
        body.isKinematic = true;
        body.useGravity = false;
    }

    void Awake()
    {
        startPosition = transform.position;
        GetComponent<Rigidbody>().isKinematic = true;
    }

    void Update()
    {
        float dt = Time.deltaTime;
        if (dt <= 0f || travel.sqrMagnitude < 0.0001f) return;

        if (pauseTimer > 0f)
        {
            pauseTimer -= dt;
            return;
        }

        progress += direction * speed * dt / travel.magnitude;
        if (progress >= 1f || progress <= 0f)
        {
            progress = Mathf.Clamp01(progress);
            direction = -direction;
            pauseTimer = pauseTime;
        }

        // Ease in/out so riders aren't jerked at the ends.
        float eased = Mathf.SmoothStep(0f, 1f, progress);
        Vector3 newPosition = startPosition + travel * eased;
        Vector3 delta = newPosition - transform.position;
        transform.position = newPosition;

        for (int i = riders.Count - 1; i >= 0; i--)
        {
            CharacterController rider = riders[i];
            if (rider == null) { riders.RemoveAt(i); continue; }
            if (rider.enabled) rider.Move(delta);
        }
    }

    void OnTriggerEnter(Collider other)
    {
        if (other is CharacterController rider && !riders.Contains(rider)) riders.Add(rider);
    }

    void OnTriggerExit(Collider other)
    {
        if (other is CharacterController rider) riders.Remove(rider);
    }
}
