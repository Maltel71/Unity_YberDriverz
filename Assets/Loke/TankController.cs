// Input System compatibility
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

using System.Collections;
using UnityEngine;

// =============================================================================
//  TankController.cs  -  Unity 6  -  8-wheel differential tank
//  RAYCAST SUSPENSION (no WheelColliders)
//
//  Setup:
//    1. Put a Rigidbody + this script on your tank root.
//    2. Add a single Box Collider to the tank root that covers the hull.
//       (No mesh colliders! They fight Rigidbody physics.)
//    3. Create 8 empty GameObjects, one roughly at each wheel position.
//       Exact height does not matter - the spring will find the ground.
//    4. Assign them in the Inspector.
//    5. Optionally assign a visual mesh Transform per wheel to see them spin.
// =============================================================================

[System.Serializable]
public class WheelPoint
{
    [Tooltip("Empty GameObject at roughly the wheel hub position. Height is not critical.")]
    public Transform anchor;

    [Tooltip("Visual wheel mesh to spin. Leave null if you have no mesh yet.")]
    public Transform mesh;

    // Runtime state - do not set these in the Inspector
    [System.NonSerialized] public float spinAngle;
    [System.NonSerialized] public bool  grounded;
    [System.NonSerialized] public float compression;
    // Distance from anchor down to wheel centre (used for mesh placement)
    [System.NonSerialized] public float suspensionLength;
}

[RequireComponent(typeof(Rigidbody))]
public class TankController : MonoBehaviour
{
    // ── Wheels ────────────────────────────────────────────────────────────────
    [Header("Wheels - Left Side (front to rear)")]
    public WheelPoint leftFront;
    public WheelPoint leftFrontMid;
    public WheelPoint leftRearMid;
    public WheelPoint leftRear;

    [Header("Wheels - Right Side (front to rear)")]
    public WheelPoint rightFront;
    public WheelPoint rightFrontMid;
    public WheelPoint rightRearMid;
    public WheelPoint rightRear;

    // ── Rigidbody ─────────────────────────────────────────────────────────────
    [Header("Rigidbody")]
    public float mass        = 2000f;
    public float drag        = 1.5f;
    public float angularDrag = 5f;
    [Tooltip("Lower centre of mass prevents tipping. Negative Y moves it down.")]
    public Vector3 centerOfMassOffset = new Vector3(0f, -0.5f, 0f);

    // ── Suspension ────────────────────────────────────────────────────────────
    [Header("Suspension")]
    [Tooltip("Wheel radius in metres. Used for ground raycast and mesh placement.")]
    public float wheelRadius = 0.3f;

    [Tooltip("How far below the wheel anchor the suspension rests at equilibrium (metres). " +
             "Roughly: the distance from the anchor down to the axle center when sitting on flat ground.")]
    public float springRestLength = 0.4f;

    [Tooltip("How much the spring can compress or extend beyond the rest length (metres).")]
    public float springTravel = 0.15f;

    [Tooltip("Spring stiffness (N/m). " +
             "Rough formula: mass * 9.81 / wheelCount / (0.35 * springTravel). " +
             "Default (2000 kg, 8 wheels, 0.15 m travel) ~ 46500.")]
    public float springStrength = 46500f;

    [Tooltip("Spring damper (N*s/m). Aim for 10-15% of spring strength. Too low = bouncing.")]
    public float springDamper = 5000f;

    // ── Drive ─────────────────────────────────────────────────────────────────
    [Header("Drive")]
    [Tooltip("Forward drive force (N) applied at each grounded wheel.")]
    public float motorForce = 5000f;

    [Tooltip("Max speed (m/s) before drive force cuts out.")]
    public float maxSpeed = 15f;

    [Range(0.1f, 5f)]
    [Tooltip("Acceleration curve sharpness. Higher = more gradual ramp up.")]
    public float accelerationCurveSharpness = 1.5f;

    [Tooltip("Differential torque force for turning. Higher = faster pivots.")]
    public float turnForce = 4000f;

    [Range(0.1f, 3f)]
    public float turnSensitivity = 1f;

    [Tooltip("Allow pivot turn (spin on the spot) with no throttle.")]
    public bool allowPivotTurn = true;

    // ── Brakes ────────────────────────────────────────────────────────────────
    [Header("Brakes")]
    [Tooltip("Braking force (N) while the brake key is held.")]
    public float brakeForce = 20000f;

    [Tooltip("Gentle braking force applied automatically when coasting (no input).")]
    public float coastBrakeForce = 5000f;

    // ── Lateral Friction ──────────────────────────────────────────────────────
    [Header("Lateral Friction")]
    [Tooltip("How strongly each grounded wheel resists sideways sliding. " +
             "Higher = snappier turning but can feel rigid. 0.3-0.8 is a good range.")]
    [Range(0f, 1f)]
    public float lateralFriction = 0.5f;

    // ── Input ─────────────────────────────────────────────────────────────────
#if ENABLE_INPUT_SYSTEM
    [Header("Input Keys (New Input System)")]
    public Key forwardKey = Key.W;
    public Key backKey    = Key.S;
    public Key leftKey    = Key.A;
    public Key rightKey   = Key.D;
    public Key brakeKey   = Key.Space;
#else
    [Header("Input (Legacy Input Manager)")]
    public string  throttleAxis = "Vertical";
    public string  steerAxis    = "Horizontal";
    public KeyCode brakeKey     = KeyCode.Space;
#endif

    // ── Debug ─────────────────────────────────────────────────────────────────
    [Header("Debug")]
    public bool showSpeedGizmo  = true;
    [Tooltip("GREEN sphere = grounded wheel contact. RED = wheel in air.")]
    public bool showWheelGizmos = true;

    // ─────────────────────────────────────────────────────────────────────────
    //  Private
    // ─────────────────────────────────────────────────────────────────────────

    private Rigidbody  rb;
    private WheelPoint[] leftWheels;
    private WheelPoint[] rightWheels;
    private WheelPoint[] allWheels;

    // ─────────────────────────────────────────────────────────────────────────
    //  Lifecycle
    // ─────────────────────────────────────────────────────────────────────────

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();
        rb.mass           = mass;
        rb.linearDamping  = drag;
        rb.angularDamping = angularDrag;
        rb.centerOfMass   = centerOfMassOffset;

        leftWheels  = new[] { leftFront, leftFrontMid, leftRearMid, leftRear };
        rightWheels = new[] { rightFront, rightFrontMid, rightRearMid, rightRear };
        allWheels   = new[]
        {
            leftFront, leftFrontMid, leftRearMid, leftRear,
            rightFront, rightFrontMid, rightRearMid, rightRear
        };
    }

    private void FixedUpdate()
    {
        float throttle = 0f;
        float steer    = 0f;
        bool  braking  = false;

#if ENABLE_INPUT_SYSTEM
        var kb = Keyboard.current;
        if (kb != null)
        {
            throttle = (kb[forwardKey].isPressed ? 1f : 0f) - (kb[backKey].isPressed  ? 1f : 0f);
            steer    = (kb[rightKey].isPressed   ? 1f : 0f) - (kb[leftKey].isPressed  ? 1f : 0f);
            braking  =  kb[brakeKey].isPressed;
        }
#else
        throttle = Input.GetAxis(throttleAxis);
        steer    = Input.GetAxis(steerAxis);
        braking  = Input.GetKey(brakeKey);
#endif
        steer *= turnSensitivity;

        // Differential: left and right sides get independent drive throttle
        float leftThrottle  = throttle + steer;
        float rightThrottle = throttle - steer;

        if (!allowPivotTurn && Mathf.Abs(throttle) < 0.01f)
            leftThrottle = rightThrottle = 0f;

        bool isCoasting = Mathf.Abs(throttle) < 0.01f && Mathf.Abs(steer) < 0.01f;
        float brake = braking ? brakeForce : isCoasting ? coastBrakeForce : 0f;

        float speed        = GetSpeedMS();
        float speedRatio   = Mathf.Clamp01(speed / maxSpeed);
        float torqueScalar = 1f - Mathf.Pow(speedRatio, accelerationCurveSharpness);

        ProcessWheelSide(leftWheels,  leftThrottle,  torqueScalar, brake);
        ProcessWheelSide(rightWheels, rightThrottle, torqueScalar, brake);
    }

    private void Update()
    {
        foreach (var wp in allWheels)
            UpdateWheelMesh(wp);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Suspension + Drive per wheel
    // ─────────────────────────────────────────────────────────────────────────

    private void ProcessWheelSide(WheelPoint[] wheels, float throttle,
                                   float torqueScalar, float brake)
    {
        foreach (var wp in wheels)
        {
            if (wp?.anchor == null) continue;

            Vector3 origin  = wp.anchor.position;
            float   maxDist = springRestLength + springTravel + wheelRadius;

            if (Physics.Raycast(origin, -transform.up, out RaycastHit hit, maxDist,
                                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            {
                wp.grounded = true;

                // Clamp suspensionLength so the visual wheel can never go below the floor
                // or above the anchor, regardless of where the anchor is placed.
                wp.suspensionLength = Mathf.Clamp(hit.distance - wheelRadius,
                                                   0f,
                                                   springRestLength + springTravel);

                // ── Suspension spring ──────────────────────────────────────
                float naturalLength = springRestLength + wheelRadius;
                wp.compression = Mathf.Clamp(naturalLength - hit.distance,
                                             -springTravel, springTravel);

                float velOnSpring = Vector3.Dot(rb.GetPointVelocity(origin), transform.up);
                float suspForce   = wp.compression * springStrength
                                    - velOnSpring   * springDamper;

                // Cap so the spring can never push harder than 3x the share of vehicle weight.
                // This prevents badly-placed anchors from launching the tank.
                float maxForce = rb.mass * Mathf.Abs(Physics.gravity.y) * 3f / allWheels.Length;
                suspForce = Mathf.Clamp(suspForce, 0f, maxForce);

                rb.AddForceAtPosition(transform.up * suspForce, origin, ForceMode.Force);

                // ── Drive force ────────────────────────────────────────────
                float   drive        = throttle * motorForce * torqueScalar;
                Vector3 contactWorld = wp.anchor.position - transform.up * hit.distance;
                rb.AddForceAtPosition(transform.forward * drive, contactWorld, ForceMode.Force);

                // ── Braking ────────────────────────────────────────────────
                if (brake > 0f)
                {
                    Vector3 vel      = rb.GetPointVelocity(contactWorld);
                    Vector3 flatVel  = Vector3.ProjectOnPlane(vel, transform.up);
                    Vector3 brakeVec = -flatVel.normalized * Mathf.Min(brake, flatVel.magnitude * rb.mass);
                    rb.AddForceAtPosition(brakeVec, contactWorld, ForceMode.Force);
                }

                // ── Lateral friction (stops sideways sliding) ──────────────
                Vector3 pointVel = rb.GetPointVelocity(contactWorld);
                float   latSpeed = Vector3.Dot(pointVel, transform.right);
                Vector3 latForce = -transform.right * (latSpeed * lateralFriction
                                   * rb.mass / allWheels.Length);
                rb.AddForceAtPosition(latForce, contactWorld, ForceMode.Force);
            }
            else
            {
                wp.grounded         = false;
                wp.compression      = 0f;
                wp.suspensionLength = springRestLength + springTravel; // hang at full extension
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Wheel mesh animation
    // ─────────────────────────────────────────────────────────────────────────

    private void UpdateWheelMesh(WheelPoint wp)
    {
        if (wp?.mesh == null || wp.anchor == null) return;

        // Position: offset downward from the anchor by the current suspension length.
        // Using anchor.position (not a stored contact point) means the mesh always
        // moves correctly with the hull every frame, with no physics-timing lag.
        wp.mesh.position = wp.anchor.position - transform.up * wp.suspensionLength;

        // Rotation: spin around the wheel's local X axis based on forward speed.
        float fwdSpeed        = Vector3.Dot(rb.linearVelocity, transform.forward);
        float degreesPerMeter = 360f / (2f * Mathf.PI * wheelRadius);
        wp.spinAngle         += fwdSpeed * degreesPerMeter * Time.deltaTime;

        wp.mesh.rotation = transform.rotation * Quaternion.Euler(wp.spinAngle, 0f, 0f);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Utilities
    // ─────────────────────────────────────────────────────────────────────────

    public float GetSpeedMS()  => rb.linearVelocity.magnitude;
    public float GetSpeedKPH() => GetSpeedMS() * 3.6f;

    // ─────────────────────────────────────────────────────────────────────────
    //  Editor / Gizmos
    // ─────────────────────────────────────────────────────────────────────────

#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        if (rb == null) rb = GetComponent<Rigidbody>();

        if (showSpeedGizmo && Application.isPlaying)
        {
            UnityEditor.Handles.Label(
                transform.position + Vector3.up * 2.5f,
                string.Format("{0:F1} km/h", GetSpeedKPH()));
        }

        if (!showWheelGizmos) return;

        WheelPoint[] gizmoWheels =
        {
            leftFront, leftFrontMid, leftRearMid, leftRear,
            rightFront, rightFrontMid, rightRearMid, rightRear
        };

        foreach (var wp in gizmoWheels)
        {
            if (wp?.anchor == null) continue;

            Vector3 origin  = wp.anchor.position;
            float   maxDist = springRestLength + springTravel + wheelRadius;

            bool hit = Physics.Raycast(origin, -transform.up, out RaycastHit rayHit, maxDist);

            // Anchor point
            Gizmos.color = Color.white;
            Gizmos.DrawWireSphere(origin, 0.05f);

            // Suspension ray
            Gizmos.color = hit ? Color.green : Color.red;
            Gizmos.DrawLine(origin, origin - transform.up * maxDist);

            // Wheel centre position
            Vector3 endpoint = hit
                ? origin - transform.up * (rayHit.distance - wheelRadius)
                : origin - transform.up * (springRestLength + springTravel);
            Gizmos.DrawWireSphere(endpoint, wheelRadius);

            // Label
            UnityEditor.Handles.color = Color.white;
            if (Application.isPlaying)
            {
                string label = wp.grounded
                    ? string.Format("{0:F0}% comp", (wp.compression / springTravel) * 100f)
                    : "air";
                UnityEditor.Handles.Label(origin + Vector3.up * 0.1f, label);
            }
            else
            {
                UnityEditor.Handles.Label(origin + Vector3.up * 0.1f, wp.anchor.name);
            }
        }
    }

    private void OnValidate()
    {
        if (rb == null) rb = GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.mass           = mass;
            rb.linearDamping  = drag;
            rb.angularDamping = angularDrag;
            rb.centerOfMass   = centerOfMassOffset;
        }
    }
#endif
}
