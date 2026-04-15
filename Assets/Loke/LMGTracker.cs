// Input System compatibility
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

using UnityEngine;

// =============================================================================
//  LMGTracker.cs  -  Unity 6
//
//  Lightweight two-stage mount tracker for machine guns.
//
//  Mount (horizontal): follows the camera via a screen-centre raycast,
//  identical to how TurretController handles turret yaw.
//
//  Vertical: driven by mouse Y delta, identical to TurretController's barrel.
//  Two-stage elevation:
//    Stage 1 - Secondary Pivot (mid-body / receiver): moves first within its limits.
//    Stage 2 - Barrel: once the secondary pivot is maxed, the barrel continues.
//
//  Recommended hierarchy:
//    Mount
//    └── SecondaryPivot   (tilts up/down - stage 1)
//        └── Barrel       (tilts further up/down - stage 2)
//
//  If you only have one vertical pivot, leave Secondary Pivot empty and the
//  barrel alone covers the full elevation range.
//
//  Pair with MachineGunController on the same or child GameObject for shooting.
//  This script only handles rotation.
// =============================================================================

public class LMGTracker : MonoBehaviour
{
    // ── References ────────────────────────────────────────────────────────────
    [Header("References")]
    [Tooltip("The LMG mount pivot that rotates left and right. " +
             "Should be a child of the hull or turret.")]
    public Transform mount;

    [Tooltip("The barrel that tilts up and down (stage 2 vertical). " +
             "For two-stage tracking, make this a child of Secondary Pivot.")]
    public Transform barrel;

    [Tooltip("The mid-body section that tilts first (stage 1 vertical). " +
             "Exhausts its own elevation range before the barrel starts moving. " +
             "Leave empty to use single-stage tracking on the barrel only.")]
    public Transform secondaryPivot;

    [Tooltip("Parent transform used for local-space yaw calculations (usually the hull or turret). " +
             "Auto-fills from mount.parent on Start - only set manually if the mount's direct " +
             "parent is not the correct reference frame.")]
    public Transform relativeTo;

    [Tooltip("Camera the player looks through. Leave empty to use Camera.main.")]
    public Camera aimCamera;

    // ── Aiming ────────────────────────────────────────────────────────────────
    [Header("Aiming")]
    [Tooltip("How far the aim ray reaches (metres).")]
    public float aimRange = 300f;

    [Tooltip("Layers the aim ray can hit.")]
    public LayerMask aimLayerMask = Physics.DefaultRaycastLayers;

    // ── Mount Smoothing ───────────────────────────────────────────────────────
    [Header("Smoothing")]
    [Range(0.5f, 20f)]
    [Tooltip("How quickly the mount rotates to face the aim direction. Higher = snappier.")]
    public float mountSmoothing = 8f;

    [Range(0.5f, 20f)]
    [Tooltip("How quickly the vertical pivots ease toward their targets. Higher = snappier.")]
    public float barrelSmoothing = 8f;

    // ── Barrel Mouse Speed ────────────────────────────────────────────────────
    [Header("Barrel - Mouse Control")]
    [Range(0.5f, 15f)]
    [Tooltip("How fast mouse Y moves the barrel target angle. Same behaviour as TurretController.")]
    public float barrelSpeed = 3f;

    // ── Secondary Pivot Limits (Stage 1) ──────────────────────────────────────
    [Header("Secondary Pivot Elevation Limits  (Stage 1)")]
    [Tooltip("Enable vertical tracking. Uncheck for a fixed-elevation mount.")]
    public bool trackVertical = true;

    [Range(0f, 45f)]
    [Tooltip("How far the secondary pivot (mid-body) can depress downward (degrees).")]
    public float secondaryMaxDepression = 10f;

    [Range(0f, 80f)]
    [Tooltip("How far the secondary pivot (mid-body) can elevate upward (degrees).")]
    public float secondaryMaxElevation = 20f;

    // ── Barrel Extra Limits (Stage 2) ─────────────────────────────────────────
    [Header("Barrel Extra Elevation Limits  (Stage 2)")]
    [Tooltip("Additional degrees the barrel can depress BEYOND the secondary pivot. " +
             "If no secondary pivot is assigned this is the barrel's total downward limit.")]
    [Range(0f, 45f)]
    public float barrelExtraDepression = 5f;

    [Range(0f, 80f)]
    [Tooltip("Additional degrees the barrel can elevate BEYOND the secondary pivot. " +
             "If no secondary pivot is assigned this is the barrel's total upward limit.")]
    public float barrelExtraElevation = 15f;

    // ─────────────────────────────────────────────────────────────────────────
    //  Private state
    // ─────────────────────────────────────────────────────────────────────────

    private float mountYaw          = 0f;
    private float targetPitch       = 0f;   // accumulated mouse Y - the desired total pitch
    private float secondaryPitch    = 0f;   // smoothed stage-1 pitch
    private float barrelPitch       = 0f;   // smoothed stage-2 pitch (relative to secondary)
    private float mountYawVel       = 0f;
    private float secondaryPitchVel = 0f;
    private float barrelPitchVel    = 0f;

    // New Input System mouse.delta gives raw pixels; scale so barrelSpeed feels
    // the same as TurretController with the legacy Input normalised axis.
#if ENABLE_INPUT_SYSTEM
    private const float InputScale = 0.05f;
#else
    private const float InputScale = 1f;
#endif

    // ─────────────────────────────────────────────────────────────────────────
    //  Lifecycle
    // ─────────────────────────────────────────────────────────────────────────

    private void Start()
    {
        if (relativeTo == null && mount != null)
            relativeTo = mount.parent;

        if (aimCamera == null)
            aimCamera = Camera.main;

        if (mount != null)
            mountYaw = mount.localEulerAngles.y;

        // Seed targetPitch from whatever the model rests at so nothing snaps on frame 1
        Transform seedFrom = secondaryPivot != null ? secondaryPivot : barrel;
        if (seedFrom != null)
        {
            float p = seedFrom.localEulerAngles.x;
            targetPitch = p > 180f ? p - 360f : p;
        }

        secondaryPitch = targetPitch;
        barrelPitch    = 0f;
    }

    private void Update()
    {
        if (mount == null || relativeTo == null) return;

        Vector3 aimPoint = GetAimPoint();

        // ── Horizontal: mount follows camera in local space ───────────────────
        Vector3 toAimFlat = Vector3.ProjectOnPlane(aimPoint - mount.position, relativeTo.up);
        float targetYaw   = mountYaw;

        if (toAimFlat.sqrMagnitude > 0.001f)
        {
            Vector3 localDir = relativeTo.InverseTransformDirection(toAimFlat.normalized);
            targetYaw = Mathf.Atan2(localDir.x, localDir.z) * Mathf.Rad2Deg;
        }

        mountYaw = Mathf.SmoothDampAngle(mountYaw, targetYaw, ref mountYawVel,
                                          Mathf.Max(0.01f, 1f / mountSmoothing));
        mount.localRotation = Quaternion.Euler(0f, mountYaw, 0f);

        // ── Vertical: mouse delta, same pattern as TurretController barrel ─────
        if (!trackVertical) return;

        ReadMouseY(out float mouseY);

        // Combined range across both stages
        float totalMaxElevation  = secondaryMaxElevation  + barrelExtraElevation;
        float totalMaxDepression = secondaryMaxDepression + barrelExtraDepression;

        // Block input that would push past the combined limits
        bool pushingPastTop    = mouseY > 0f && targetPitch <= -totalMaxElevation;
        bool pushingPastBottom = mouseY < 0f && targetPitch >=  totalMaxDepression;
        if (!pushingPastTop && !pushingPastBottom)
            targetPitch -= mouseY * barrelSpeed * InputScale;

        targetPitch = Mathf.Clamp(targetPitch, -totalMaxElevation, totalMaxDepression);

        // ── Stage 1: secondary pivot fills its range first ────────────────────
        if (secondaryPivot != null)
        {
            float secondaryTarget = Mathf.Clamp(targetPitch,
                                                 -secondaryMaxElevation,
                                                  secondaryMaxDepression);

            secondaryPitch = Mathf.SmoothDampAngle(secondaryPitch, secondaryTarget,
                                                    ref secondaryPitchVel,
                                                    Mathf.Max(0.01f, 1f / barrelSmoothing));

            if (secondaryPitch <= -secondaryMaxElevation || secondaryPitch >= secondaryMaxDepression)
                secondaryPitchVel = 0f;
            secondaryPitch = Mathf.Clamp(secondaryPitch, -secondaryMaxElevation, secondaryMaxDepression);

            secondaryPivot.localRotation = Quaternion.Euler(secondaryPitch, 0f, 0f);
        }

        // ── Stage 2: barrel picks up whatever secondary pivot could not cover ──
        if (barrel != null)
        {
            // If there is a secondary pivot, barrel only moves once secondary is maxed.
            // If no secondary pivot, barrel covers the entire range on its own.
            float remainder    = secondaryPivot != null ? targetPitch - secondaryPitch : targetPitch;
            float barrelTarget = Mathf.Clamp(remainder, -barrelExtraElevation, barrelExtraDepression);

            barrelPitch = Mathf.SmoothDampAngle(barrelPitch, barrelTarget,
                                                 ref barrelPitchVel,
                                                 Mathf.Max(0.01f, 1f / barrelSmoothing));

            if (barrelPitch <= -barrelExtraElevation || barrelPitch >= barrelExtraDepression)
                barrelPitchVel = 0f;
            barrelPitch = Mathf.Clamp(barrelPitch, -barrelExtraElevation, barrelExtraDepression);

            barrel.localRotation = Quaternion.Euler(barrelPitch, 0f, 0f);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private Vector3 GetAimPoint()
    {
        if (aimCamera == null)
            return mount.position + mount.forward * aimRange;

        Ray ray = aimCamera.ScreenPointToRay(
            new Vector3(Screen.width * 0.5f, Screen.height * 0.5f, 0f));

        if (Physics.Raycast(ray, out RaycastHit hit, aimRange, aimLayerMask,
                            QueryTriggerInteraction.Ignore))
            return hit.point;

        return ray.origin + ray.direction * aimRange;
    }

    private void ReadMouseY(out float mouseY)
    {
        mouseY = 0f;
        if (Cursor.lockState != CursorLockMode.Locked) return;

#if ENABLE_INPUT_SYSTEM
        var mouse = Mouse.current;
        if (mouse != null)
            mouseY = mouse.delta.ReadValue().y;
#else
        mouseY = Input.GetAxis("Mouse Y");
#endif
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Gizmos
    // ─────────────────────────────────────────────────────────────────────────

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        if (mount == null) return;

        Gizmos.color = Color.green;
        Gizmos.DrawRay(mount.position, mount.forward * aimRange);

        if (secondaryPivot != null)
        {
            Gizmos.color = new Color(1f, 0.6f, 0f);
            Gizmos.DrawRay(secondaryPivot.position, secondaryPivot.forward * (aimRange * 0.6f));
        }

        if (barrel != null)
        {
            Gizmos.color = Color.cyan;
            Gizmos.DrawRay(barrel.position, barrel.forward * aimRange);
        }
    }
#endif
}
