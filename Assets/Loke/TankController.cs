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

    [Tooltip("Tick to make this wheel visually steer (rotate on Y axis with steering input).")]
    public bool steerable = false;

    [Tooltip("Tick to apply motor force to this wheel when throttle is pressed.")]
    public bool driven = true;

    [Tooltip("Tick to include this wheel in the differential turning torque. " +
             "Fewer grounded differential wheels = weaker turning.")]
    public bool differential = true;

    // Runtime state - do not set these in the Inspector
    [System.NonSerialized] public float spinAngle;
    [System.NonSerialized] public float steerAngle;
    [System.NonSerialized] public bool  grounded;
    [System.NonSerialized] public float compression;
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
    public float angularDrag = 1.5f;
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

    [Tooltip("Steering yaw torque (N*m). The tank rotates by this torque multiplied by " +
             "forward speed, so turning is naturally proportional to how fast you are moving. " +
             "Increase if the tank feels sluggish to steer; decrease if it oversteers.")]
    public float turnForce = 8000f;

    [Range(0.1f, 3f)]
    public float turnSensitivity = 1f;

    [Tooltip("Allow pivot turn (spin on the spot with A/D and no throttle). " +
             "Uses differential force only when stationary.")]
    public bool allowPivotTurn = true;

    [Range(0f, 1f)]
    [Tooltip("How much steering torque falls off at max speed.\n" +
             "0 = same torque at any speed.\n" +
             "0.4 = gentle reduction at top speed (recommended).\n" +
             "1 = almost no turning at max speed.")]
    public float turnSpeedFalloff = 0.4f;

    // ── Visual Steering ───────────────────────────────────────────────────────
    [Header("Visual Steering")]
    [Tooltip("Max angle (degrees) the steerable wheel meshes rotate when turning.")]
    [Range(0f, 45f)]
    public float maxSteerAngle = 25f;

    [Tooltip("How quickly the steerable wheels rotate to the target angle. Higher = snappier.")]
    [Range(1f, 20f)]
    public float steerAngleSpeed = 8f;


    // ── Brakes & Rolling ──────────────────────────────────────────────────────
    [Header("Brakes and Rolling")]
    [Tooltip("Braking force (N) applied when the brake key or S-as-brake is active.")]
    public float brakeForce = 20000f;

    [Tooltip("Speed (km/h) below which S switches from braking to reverse. " +
             "Above this speed S only brakes.")]
    public float brakeToReverseSpeedKph = 15f;

    [Tooltip("Maximum rolling resistance force (N) applied at low speed when coasting. " +
             "The actual force is scaled down at high speed by the curve below.")]
    public float rollingResistance = 1200f;

    [Range(0.1f, 5f)]
    [Tooltip("How quickly resistance drops off as speed increases.\n" +
             "1 = linear (same reduction across all speeds).\n" +
             "2 = quadratic - coasts long at speed, scrubs off quickly when slow.\n" +
             "Higher values = even more coast-friendly at high speed.")]
    public float rollingResistanceCurve = 2f;

    // ── Lateral Friction ──────────────────────────────────────────────────────
    [Header("Lateral Friction")]
    [Tooltip("How strongly each grounded wheel resists sideways sliding. " +
             "Lower = more drift and slide. 0.1-0.25 for drifty, 0.4-0.8 for grippy.")]
    [Range(0f, 1f)]
    public float lateralFriction = 0.18f;

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

    private Rigidbody    rb;
    private WheelPoint[] leftWheels;
    private WheelPoint[] rightWheels;
    private WheelPoint[] allWheels;
    private float        currentSteer; // tracked each FixedUpdate, read in Update

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
        // ── Raw input ─────────────────────────────────────────────────────────
        float forwardInput = 0f;
        float backInput    = 0f;
        float steer        = 0f;
        bool  brakeKey     = false;

#if ENABLE_INPUT_SYSTEM
        var kb = Keyboard.current;
        if (kb != null)
        {
            forwardInput = kb[forwardKey].isPressed ? 1f : 0f;
            backInput    = kb[backKey].isPressed    ? 1f : 0f;
            steer        = (kb[rightKey].isPressed  ? 1f : 0f) - (kb[leftKey].isPressed ? 1f : 0f);
            brakeKey     = kb[this.brakeKey].isPressed;
        }
#else
        float rawAxis = Input.GetAxis(throttleAxis);
        forwardInput  = Mathf.Max(0f,  rawAxis);
        backInput     = Mathf.Max(0f, -rawAxis);
        steer         = Input.GetAxis(steerAxis);
        brakeKey      = Input.GetKey(this.brakeKey);
#endif

        steer        *= turnSensitivity;
        currentSteer  = steer;

        // ── S key dual-purpose: brake above threshold, reverse below ──────────
        float forwardSpeedMS  = Vector3.Dot(rb.linearVelocity, transform.forward);
        float thresholdMS     = brakeToReverseSpeedKph / 3.6f;

        float throttle    = forwardInput;
        float sBrake      = 0f;

        if (backInput > 0f)
        {
            if (forwardSpeedMS > thresholdMS)
                sBrake  = brakeForce;   // moving forward fast enough: S = brake
            else
                throttle -= backInput;  // slow or stopped: S = reverse
        }

        // ── Brake force: dedicated brake key OR S-as-brake ────────────────────
        float brake = brakeKey ? brakeForce : sBrake;

        // ── Rolling resistance: replaces the hard coast brake ─────────────────
        // Applied as a whole-body force so the tank glides to a natural stop.
        // Resistance is highest at low speed and tapers off at high speed,
        // so the tank coasts freely when fast but stops quickly from a crawl.
        bool hasThrottle = Mathf.Abs(throttle) > 0.01f;
        if (!hasThrottle && brake < 0.01f)
        {
            Vector3 flatVel   = Vector3.ProjectOnPlane(rb.linearVelocity, transform.up);
            float   flatSpeed = flatVel.magnitude;
            if (flatSpeed > 0.01f)
            {
                // Scale resistance by (1 - speedRatio)^curve so it nearly vanishes at high speed
                float flatSpeedRatio   = Mathf.Clamp01(flatSpeed / maxSpeed);
                float resistMultiplier = Mathf.Pow(1f - flatSpeedRatio, rollingResistanceCurve);
                // Cap so resistance can never reverse the velocity in one step
                float resistN = Mathf.Min(rollingResistance * resistMultiplier,
                                          flatSpeed * rb.mass / Time.fixedDeltaTime);
                rb.AddForce(-flatVel.normalized * resistN, ForceMode.Force);
            }
        }

        // ── Speed scalars ─────────────────────────────────────────────────────
        float speed      = GetSpeedMS();
        float speedRatio = Mathf.Clamp01(speed / maxSpeed);

        // Drive: full force at low speed, fades out as you approach maxSpeed
        float driveScalar = 1f - Mathf.Pow(speedRatio, accelerationCurveSharpness);

        // Turn: slightly reduced at top speed via turnSpeedFalloff
        float turnScalar = 1f - speedRatio * turnSpeedFalloff;

        // ── Car-like steering: yaw torque proportional to steer × forward speed ──
        // At zero speed torque is zero (no ballerina spinning).
        // As you gain speed the torque grows, steering the tank naturally like a car.
        // The front wheel angle is what steers - the lateral friction on angled wheels
        // (see ProcessWheelSide) generates the actual cornering force per wheel.
        float steeringTorque = steer * Mathf.Abs(forwardSpeedMS) * turnForce * turnScalar;
        rb.AddTorque(transform.up * steeringTorque, ForceMode.Force);

        // ── Differential: only for pivot turns when stationary ────────────────
        float driveForce  = throttle * motorForce * driveScalar;
        bool  isStationary = speed < 0.5f;
        float diffForce   = (allowPivotTurn && isStationary)
                          ? steer * turnForce * 0.4f
                          : 0f;

        ProcessWheelSide(leftWheels,  driveForce,  diffForce, brake);
        ProcessWheelSide(rightWheels, driveForce, -diffForce, brake);
    }

    private void Update()
    {
        foreach (var wp in allWheels)
            UpdateWheelMesh(wp);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Suspension + Drive per wheel
    // ─────────────────────────────────────────────────────────────────────────

    private void ProcessWheelSide(WheelPoint[] wheels, float driveForce, float diffForce, float brake)
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

                Vector3 contactWorld = wp.anchor.position - transform.up * hit.distance;

                // ── Drive force (only on driven wheels) ───────────────────
                if (wp.driven)
                    rb.AddForceAtPosition(transform.forward * driveForce, contactWorld, ForceMode.Force);

                // ── Differential turning (only on differential wheels) ────
                if (wp.differential)
                    rb.AddForceAtPosition(transform.forward * diffForce, contactWorld, ForceMode.Force);

                // ── Braking ────────────────────────────────────────────────
                if (brake > 0f)
                {
                    Vector3 vel      = rb.GetPointVelocity(contactWorld);
                    Vector3 flatVel  = Vector3.ProjectOnPlane(vel, transform.up);
                    Vector3 brakeVec = -flatVel.normalized * Mathf.Min(brake, flatVel.magnitude * rb.mass);
                    rb.AddForceAtPosition(brakeVec, contactWorld, ForceMode.Force);
                }

                // ── Lateral friction ───────────────────────────────────────
                // Steerable wheels resist sliding along their OWN lateral axis
                // (perpendicular to the wheel's rolling direction), not the tank's axis.
                // This means an angled front wheel naturally pushes the contact point
                // sideways as the tank moves forward - exactly how real car steering works.
                // Non-steerable wheels simply resist the tank's lateral sliding as before.
                Vector3 pointVel   = rb.GetPointVelocity(contactWorld);
                Vector3 wheelLateral;
                if (wp.steerable && Mathf.Abs(wp.steerAngle) > 0.5f)
                {
                    float steerRad = wp.steerAngle * Mathf.Deg2Rad;
                    wheelLateral = transform.right   * Mathf.Cos(steerRad)
                                 - transform.forward * Mathf.Sin(steerRad);
                }
                else
                {
                    wheelLateral = transform.right;
                }

                float   latSpeed = Vector3.Dot(pointVel, wheelLateral);
                Vector3 latForce = -wheelLateral * (latSpeed * lateralFriction * rb.mass / allWheels.Length);
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
        // Anchored to anchor.position each frame so the wheel always moves with the hull.
        wp.mesh.position = wp.anchor.position - transform.up * wp.suspensionLength;

        // Spin: accumulate rotation based on forward speed
        float fwdSpeed        = Vector3.Dot(rb.linearVelocity, transform.forward);
        float degreesPerMeter = 360f / (2f * Mathf.PI * wheelRadius);
        wp.spinAngle         += fwdSpeed * degreesPerMeter * Time.deltaTime;

        // Visual steering: smoothly rotate steerable wheels toward the target steer angle
        if (wp.steerable)
        {
            float targetSteer = currentSteer * maxSteerAngle;
            wp.steerAngle     = Mathf.Lerp(wp.steerAngle, targetSteer, steerAngleSpeed * Time.deltaTime);
        }

        // Apply rotation: body yaw -> steer angle (Y) -> wheel spin (X)
        wp.mesh.rotation = transform.rotation
                         * Quaternion.Euler(0f, wp.steerAngle, 0f)
                         * Quaternion.Euler(wp.spinAngle, 0f, 0f);
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
