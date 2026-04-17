// Input System compatibility
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

using UnityEngine;

// =============================================================================
//  LMGTracker.cs  -  Unity 6
//
//  Two-stage mount tracker for machine guns.
//  Behaviour depends on camera mode (read from TankCameraController):
//
//    Third Person / Gun Sight
//      Mount horizontal: screen-centre raycast (camera is free to look around).
//      Vertical: mouse Y two-stage elevation.
//
//    LMG Sight
//      Mount horizontal: mouse X directly.
//      Vertical: mouse Y two-stage elevation.
//      Raycast cannot be used here because the LMG sight camera is parented
//      to the LMG mount - it always points where the gun already points,
//      so the raycast never produces rotation.
//
//  If no TankCameraController is assigned, always uses raycast (Third Person).
//
//  Two-stage elevation:
//    Stage 1 - Secondary Pivot (mid-body): fills its range first.
//    Stage 2 - Barrel: picks up the remainder once secondary is maxed.
//
//  Recommended hierarchy:  Mount > SecondaryPivot > Barrel
// =============================================================================

public class LMGTracker : MonoBehaviour
{
    // ── References ────────────────────────────────────────────────────────────
    [Header("References")]
    [Tooltip("The LMG mount pivot that rotates left and right.")]
    public Transform mount;

    [Tooltip("The barrel (stage 2 vertical). Make this a child of Secondary Pivot.")]
    public Transform barrel;

    [Tooltip("The mid-body section (stage 1 vertical). Leave empty for single-stage.")]
    public Transform secondaryPivot;

    [Tooltip("Parent transform for local-space yaw calculations. Auto-fills from mount.parent.")]
    public Transform relativeTo;

    [Tooltip("Camera the player looks through. Leave empty to use Camera.main.")]
    public Camera aimCamera;

    [Tooltip("Assign TankCameraController to enable LMG Sight direct mouse control.")]
    public TankCameraController cameraController;

    // ── Aiming ────────────────────────────────────────────────────────────────
    [Header("Aiming  (Third Person / Gun Sight)")]
    [Tooltip("Raycast range (metres).")]
    public float aimRange = 300f;

    [Tooltip("Layers the aim ray can hit.")]
    public LayerMask aimLayerMask = Physics.DefaultRaycastLayers;

    // ── Smoothing ─────────────────────────────────────────────────────────────
    [Header("Smoothing")]
    [Range(0.5f, 20f)]
    public float mountSmoothing = 8f;

    [Range(0.5f, 20f)]
    public float barrelSmoothing = 8f;

    // ── Mouse Speeds ──────────────────────────────────────────────────────────
    [Header("Mouse Speeds")]
    [Range(0.5f, 15f)]
    [Tooltip("How fast mouse X rotates the mount in LMG Sight mode.")]
    public float mountMouseSpeed = 3f;

    [Range(0.5f, 15f)]
    [Tooltip("How fast mouse Y moves the elevation target.")]
    public float barrelSpeed = 3f;

    // ── Secondary Pivot Limits (Stage 1) ──────────────────────────────────────
    [Header("Secondary Pivot Elevation Limits  (Stage 1)")]
    public bool trackVertical = true;

    [Range(0f, 45f)]
    public float secondaryMaxDepression = 10f;

    [Range(0f, 80f)]
    public float secondaryMaxElevation = 20f;

    // ── Barrel Extra Limits (Stage 2) ─────────────────────────────────────────
    [Header("Barrel Extra Elevation Limits  (Stage 2)")]
    [Range(0f, 45f)]
    public float barrelExtraDepression = 5f;

    [Range(0f, 80f)]
    public float barrelExtraElevation = 15f;

    // ─────────────────────────────────────────────────────────────────────────
    //  Private state
    // ─────────────────────────────────────────────────────────────────────────

    private float mountTargetYaw    = 0f;
    private float mountYaw          = 0f;
    private float mountYawVel       = 0f;
    private float targetPitch       = 0f;
    private float secondaryPitch    = 0f;
    private float barrelPitch       = 0f;
    private float secondaryPitchVel = 0f;
    private float barrelPitchVel    = 0f;

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
        {
            mountYaw       = mount.localEulerAngles.y;
            mountTargetYaw = mountYaw;
        }

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

        TankCameraMode mode = cameraController != null
            ? cameraController.CurrentMode
            : TankCameraMode.ThirdPerson;

        // ── Horizontal: how the mount target yaw is set depends on mode ───────
        if (mode == TankCameraMode.LMGSight)
        {
            // Camera is parented to the LMG so a raycast would never produce
            // rotation. Drive the mount directly with mouse X instead.
            ReadMouseX(out float mouseX);
            mountTargetYaw += mouseX * mountMouseSpeed * InputScale;
        }
        else
        {
            // Third Person / Gun Sight: camera is free, raycast works correctly
            Vector3 aimPoint  = GetAimPoint();
            Vector3 toAimFlat = Vector3.ProjectOnPlane(aimPoint - mount.position, relativeTo.up);

            if (toAimFlat.sqrMagnitude > 0.001f)
            {
                Vector3 localDir = relativeTo.InverseTransformDirection(toAimFlat.normalized);
                mountTargetYaw   = Mathf.Atan2(localDir.x, localDir.z) * Mathf.Rad2Deg;
            }
        }

        mountYaw = Mathf.SmoothDampAngle(mountYaw, mountTargetYaw, ref mountYawVel,
                                          Mathf.Max(0.01f, 1f / mountSmoothing));
        mount.localRotation = Quaternion.Euler(0f, mountYaw, 0f);

        // ── Vertical: mouse Y two-stage elevation (same in all modes) ─────────
        if (!trackVertical) return;

        ReadMouseY(out float mouseY);

        float totalMaxElevation  = secondaryMaxElevation  + barrelExtraElevation;
        float totalMaxDepression = secondaryMaxDepression + barrelExtraDepression;

        bool pushingPastTop    = mouseY > 0f && targetPitch <= -totalMaxElevation;
        bool pushingPastBottom = mouseY < 0f && targetPitch >=  totalMaxDepression;
        if (!pushingPastTop && !pushingPastBottom)
            targetPitch -= mouseY * barrelSpeed * InputScale;

        targetPitch = Mathf.Clamp(targetPitch, -totalMaxElevation, totalMaxDepression);

        // Stage 1: secondary pivot
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

        // Stage 2: barrel picks up what secondary could not cover
        if (barrel != null)
        {
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

    private void ReadMouseX(out float mouseX)
    {
        mouseX = 0f;
        if (Cursor.lockState != CursorLockMode.Locked) return;
#if ENABLE_INPUT_SYSTEM
        var mouse = Mouse.current;
        if (mouse != null)
            mouseX = mouse.delta.ReadValue().x;
#else
        mouseX = Input.GetAxis("Mouse X");
#endif
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
