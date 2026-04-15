// Input System compatibility
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

using UnityEngine;

// =============================================================================
//  TankCameraController.cs  -  Unity 6
//
//  Switches between three Cinemachine Virtual Cameras.
//  Cinemachine handles all blending, zoom, and FOV - this script only
//  activates and deactivates the virtual camera GameObjects.
//
//  Key bindings:
//    V      - cycle forward through all three modes
//    Shift  - toggle between Third Person and Gun Sight
//    L      - toggle between Third Person and LMG Sight
//
//  Setup:
//    1. Attach to any GameObject (e.g. the tank root).
//    2. Assign the three Cinemachine Virtual Camera GameObjects.
// =============================================================================

public enum TankCameraMode { ThirdPerson, GunSight, LMGSight }

public class TankCameraController : MonoBehaviour
{
    [Header("Cinemachine Virtual Cameras")]
    public GameObject thirdPersonCamera;
    public GameObject gunSightCamera;
    public GameObject lmgSightCamera;

    [Header("Input")]
#if ENABLE_INPUT_SYSTEM
    public Key cycleKey    = Key.V;
    public Key gunSightKey = Key.LeftShift;
    public Key lmgSightKey = Key.L;
#else
    public KeyCode cycleKey    = KeyCode.V;
    public KeyCode gunSightKey = KeyCode.LeftShift;
    public KeyCode lmgSightKey = KeyCode.L;
#endif

    private TankCameraMode currentMode = TankCameraMode.ThirdPerson;
    public TankCameraMode CurrentMode => currentMode;

    private void Start()
    {
        SetMode(TankCameraMode.ThirdPerson);
    }

    private void Update()
    {
#if ENABLE_INPUT_SYSTEM
        var kb = Keyboard.current;
        if (kb == null) return;

        if (kb[cycleKey].wasPressedThisFrame)
            SetMode((TankCameraMode)(((int)currentMode + 1) % 3));

        if (kb[gunSightKey].wasPressedThisFrame)
            SetMode(currentMode == TankCameraMode.GunSight ? TankCameraMode.ThirdPerson : TankCameraMode.GunSight);

        if (kb[lmgSightKey].wasPressedThisFrame)
            SetMode(currentMode == TankCameraMode.LMGSight ? TankCameraMode.ThirdPerson : TankCameraMode.LMGSight);
#else
        if (Input.GetKeyDown(cycleKey))
            SetMode((TankCameraMode)(((int)currentMode + 1) % 3));

        if (Input.GetKeyDown(gunSightKey))
            SetMode(currentMode == TankCameraMode.GunSight ? TankCameraMode.ThirdPerson : TankCameraMode.GunSight);

        if (Input.GetKeyDown(lmgSightKey))
            SetMode(currentMode == TankCameraMode.LMGSight ? TankCameraMode.ThirdPerson : TankCameraMode.LMGSight);
#endif
    }

    public void SetMode(TankCameraMode mode)
    {
        currentMode = mode;
        if (thirdPersonCamera != null) thirdPersonCamera.SetActive(mode == TankCameraMode.ThirdPerson);
        if (gunSightCamera    != null) gunSightCamera.SetActive(mode == TankCameraMode.GunSight);
        if (lmgSightCamera    != null) lmgSightCamera.SetActive(mode == TankCameraMode.LMGSight);
    }
}
