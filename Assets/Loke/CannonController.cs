// Input System compatibility
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

using UnityEngine;
using UnityEngine.VFX;

// =============================================================================
//  CannonController.cs  -  Unity 6
//
//  Physical-projectile cannon with multi-ammo-type support.
//  Press Q (configurable) to cycle ammo types.
//
//  Ammo types are defined in the Inspector as an array of CannonAmmoType.
//  Each type carries its own projectile prefab, muzzle velocity, damage,
//  impact radius, explosion force, and VFX settings.
//
//  The projectile prefab must have CannonProjectile attached.
//  CannonController sets all CannonProjectile fields right after Instantiate,
//  so you can share one prefab across ammo types and the behaviour differs
//  purely from the ammo data.
//
//  Setup:
//    1. Attach this script to any GameObject (e.g. the turret or barrel).
//    2. Create an empty GameObject at the barrel tip — this is the Shoot Point.
//       Add your muzzle-flash VisualEffect directly onto that object.
//    3. Assign the Shoot Point in the Inspector.
//    4. Create at least one CannonAmmoType entry and assign a projectile prefab.
//    5. Assign the hull Rigidbody for recoil.
// =============================================================================

[System.Serializable]
public class CannonAmmoType
{
    [Tooltip("Name shown in the console when you cycle to this ammo type.")]
    public string displayName = "AP";

    [Tooltip("Projectile prefab. Must have CannonProjectile attached.")]
    public CannonProjectile projectilePrefab;

    [Tooltip("Speed at which the projectile is launched (metres per second).")]
    public float muzzleVelocity = 800f;

    [Tooltip("Damage dealt on direct hit.")]
    public float damage = 150f;

    [Tooltip("Radius around the hit point for area damage and explosion force. 0 = point hit only.")]
    public float impactRadius = 0f;

    [Tooltip("Outward force applied to Rigidbodies inside impactRadius. 0 = no push.")]
    public float explosionForce = 0f;

    [Range(0f, 3f)]
    [Tooltip("Upward bias for the explosion force. 1 = standard kick, 0 = purely outward.")]
    public float explosionUpwardModifier = 1f;

    [Tooltip("VFX instantiated at the hit point on impact. Leave empty for none.")]
    public VisualEffect impactVFXPrefab;

    [Tooltip("How long before the impact VFX is destroyed (seconds).")]
    public float impactVFXLifetime = 2f;

    [Tooltip("Enable a separate explosion VFX when impactRadius > 0.")]
    public bool useExplosionVFX = false;

    [Tooltip("Explosion VFX instantiated at the hit point. Only used when useExplosionVFX is true.")]
    public VisualEffect explosionVFXPrefab;

    [Tooltip("How long before the explosion VFX is destroyed (seconds).")]
    public float explosionVFXLifetime = 3f;

    [Tooltip("How many seconds before the projectile self-destructs if it hits nothing.")]
    public float projectileLifetime = 10f;
}

public enum CannonFireButton { LeftMouse, RightMouse, MiddleMouse }

public class CannonController : MonoBehaviour
{
    // ── Ammo Types ────────────────────────────────────────────────────────────
    [Header("Ammo Types")]
    [Tooltip("Define one entry per ammo type. Cycle with the Cycle Ammo Key (default Q).")]
    public CannonAmmoType[] ammoTypes;

    // ── Shoot Point ───────────────────────────────────────────────────────────
    [Header("Shoot Point")]
    [Tooltip("Empty GameObject at the barrel tip. " +
             "The muzzle-flash VFX should live on or under this object.")]
    public Transform shootPoint;

    [Tooltip("Muzzle flash Visual Effect. Leave empty to auto-find it on the Shoot Point.")]
    public VisualEffect muzzleVFX;

    // ── Hit Layers ────────────────────────────────────────────────────────────
    [Header("Physics")]
    [Tooltip("Layers the projectile can collide with. Exclude the tank's own layer.")]
    public LayerMask hitLayers = Physics.DefaultRaycastLayers;

    // ── Recoil ────────────────────────────────────────────────────────────────
    [Header("Recoil")]
    [Tooltip("Rigidbody that receives the recoil impulse (usually the tank hull). " +
             "Leave empty to auto-find on this GameObject or its parents.")]
    public Rigidbody recoilBody;

    [Tooltip("Recoil impulse (N*s) applied backwards along the barrel when firing.")]
    public float recoilForce = 12000f;

    // ── Audio ─────────────────────────────────────────────────────────────────
    [Header("Audio")]
    [Tooltip("AudioSource used for the fire sound. Leave empty to auto-find on this GameObject.")]
    public AudioSource audioSource;

    [Tooltip("Sound played when the cannon fires.")]
    public AudioClip fireSound;

    [Range(0f, 1f)]
    public float fireSoundVolume = 1f;

    // ── Reload ────────────────────────────────────────────────────────────────
    [Header("Reload")]
    [Tooltip("Time in seconds between shots (reload time).")]
    public float reloadTime = 3f;

    // ── Input ─────────────────────────────────────────────────────────────────
    [Header("Input")]
    [Tooltip("Mouse button that fires the cannon.")]
    public CannonFireButton fireButton = CannonFireButton.LeftMouse;

#if ENABLE_INPUT_SYSTEM
    [Tooltip("Key that cycles through ammo types.")]
    public Key cycleAmmoKey = Key.Q;
#else
    [Tooltip("Key that cycles through ammo types.")]
    public KeyCode cycleAmmoKey = KeyCode.Q;
#endif

    // ─────────────────────────────────────────────────────────────────────────
    //  Private
    // ─────────────────────────────────────────────────────────────────────────

    private float nextFireTime    = 0f;
    private int   currentAmmoIndex = 0;

    // ─────────────────────────────────────────────────────────────────────────
    //  Lifecycle
    // ─────────────────────────────────────────────────────────────────────────

    private void Start()
    {
        if (muzzleVFX == null && shootPoint != null)
            muzzleVFX = shootPoint.GetComponentInChildren<VisualEffect>();

        if (audioSource == null)
            audioSource = GetComponent<AudioSource>();

        if (recoilBody == null)
            recoilBody = GetComponentInParent<Rigidbody>();
    }

    private void Update()
    {
        HandleAmmoSwitch();

        if (FireKeyDown() && Time.time >= nextFireTime)
            Fire();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Ammo cycling
    // ─────────────────────────────────────────────────────────────────────────

    private void HandleAmmoSwitch()
    {
        if (ammoTypes == null || ammoTypes.Length <= 1) return;

#if ENABLE_INPUT_SYSTEM
        var kb = Keyboard.current;
        if (kb != null && kb[cycleAmmoKey].wasPressedThisFrame)
            CycleAmmo();
#else
        if (Input.GetKeyDown(cycleAmmoKey))
            CycleAmmo();
#endif
    }

    private void CycleAmmo()
    {
        if (ammoTypes == null || ammoTypes.Length == 0) return;

        currentAmmoIndex = (currentAmmoIndex + 1) % ammoTypes.Length;
        CannonAmmoType ammo = ammoTypes[currentAmmoIndex];
        Debug.Log($"[CannonController] Ammo → {ammo.displayName}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Firing
    // ─────────────────────────────────────────────────────────────────────────

    public void Fire()
    {
        if (shootPoint == null) return;
        if (ammoTypes == null || ammoTypes.Length == 0) return;

        CannonAmmoType ammo = ammoTypes[currentAmmoIndex];
        if (ammo.projectilePrefab == null) return;

        nextFireTime = Time.time + reloadTime;

        // ── Muzzle flash ──────────────────────────────────────────────────────
        if (muzzleVFX != null)
        {
            muzzleVFX.Reinit();
            muzzleVFX.Play();
        }

        // ── Fire sound ────────────────────────────────────────────────────────
        if (audioSource != null && fireSound != null)
            audioSource.PlayOneShot(fireSound, fireSoundVolume);

        // ── Recoil ────────────────────────────────────────────────────────────
        if (recoilBody != null)
            recoilBody.AddForceAtPosition(
                -shootPoint.forward * recoilForce,
                shootPoint.position,
                ForceMode.Impulse);

        // ── Spawn projectile ──────────────────────────────────────────────────
        CannonProjectile proj = Instantiate(ammo.projectilePrefab,
                                            shootPoint.position,
                                            shootPoint.rotation);

        // Pass ammo data to the projectile
        proj.damage                  = ammo.damage;
        proj.impactRadius            = ammo.impactRadius;
        proj.explosionForce          = ammo.explosionForce;
        proj.explosionUpwardModifier = ammo.explosionUpwardModifier;
        proj.hitLayers               = hitLayers;
        proj.useExplosionVFX         = ammo.useExplosionVFX;
        proj.impactVFXPrefab         = ammo.impactVFXPrefab;
        proj.impactVFXLifetime       = ammo.impactVFXLifetime;
        proj.explosionVFXPrefab      = ammo.explosionVFXPrefab;
        proj.explosionVFXLifetime    = ammo.explosionVFXLifetime;
        proj.lifetime                = ammo.projectileLifetime;

        // Launch it
        Rigidbody rb = proj.GetComponent<Rigidbody>();
        if (rb != null)
            rb.linearVelocity = shootPoint.forward * ammo.muzzleVelocity;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Input helper
    // ─────────────────────────────────────────────────────────────────────────

    private bool FireKeyDown()
    {
#if ENABLE_INPUT_SYSTEM
        var mouse = Mouse.current;
        if (mouse == null) return false;
        switch (fireButton)
        {
            case CannonFireButton.LeftMouse:   return mouse.leftButton.wasPressedThisFrame;
            case CannonFireButton.RightMouse:  return mouse.rightButton.wasPressedThisFrame;
            case CannonFireButton.MiddleMouse: return mouse.middleButton.wasPressedThisFrame;
            default: return false;
        }
#else
        return Input.GetMouseButtonDown((int)fireButton);
#endif
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Gizmos
    // ─────────────────────────────────────────────────────────────────────────

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        if (shootPoint == null) return;

        CannonAmmoType ammo = (ammoTypes != null && ammoTypes.Length > 0)
            ? ammoTypes[currentAmmoIndex] : null;

        Gizmos.color = Color.yellow;
        Gizmos.DrawRay(shootPoint.position, shootPoint.forward * 5f);

        if (ammo != null && ammo.impactRadius > 0f)
        {
            Gizmos.color = new Color(1f, 0.4f, 0f, 0.25f);
            Gizmos.DrawSphere(shootPoint.position + shootPoint.forward * 5f, ammo.impactRadius);
            Gizmos.color = new Color(1f, 0.4f, 0f, 0.8f);
            Gizmos.DrawWireSphere(shootPoint.position + shootPoint.forward * 5f, ammo.impactRadius);
        }
    }
#endif
}
