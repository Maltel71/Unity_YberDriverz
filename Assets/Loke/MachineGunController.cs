// Input System compatibility
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

using System.Collections;
using UnityEngine;
using UnityEngine.VFX;

// =============================================================================
//  MachineGunController.cs  -  Unity 6
//
//  Configurable raycast machine gun that supports multiple barrels / shoot points.
//  Each shoot point fires in sequence (barrel cycling). Every shoot point
//  should have its muzzle-flash ParticleSystem placed directly on it.
//
//  Setup:
//    1. Attach this script to any GameObject (e.g. the turret or gun mount).
//    2. Create one or more empty GameObjects at each gun barrel tip.
//       Add a muzzle-flash ParticleSystem directly onto each of those objects.
//    3. Assign all barrel shoot-point Transforms in the Shoot Points array.
//    4. Assign the hull Rigidbody for recoil.
//    5. Optionally assign impact / explosion VFX prefabs and an AudioSource.
//
//  Multiple guns on one tank:
//    Add one MachineGunController per gun mount (e.g. turret MG + hull MG).
//    Each instance has its own fire rate, damage, and shoot points - fully
//    independent. Just assign different shoot points and fire keys.
// =============================================================================

[System.Serializable]
public class MGShootPoint
{
    [Tooltip("Empty GameObject at the barrel tip. " +
             "The muzzle-flash ParticleSystem should be on this object.")]
    public Transform transform;

    [Tooltip("Muzzle flash Visual Effect. Leave empty to auto-find on the Transform above.")]
    public VisualEffect muzzleVFX;
}

public enum MGFireButton  { LeftMouse, RightMouse, MiddleMouse }
public enum MGInputType   { MouseButton, Key }

public class MachineGunController : MonoBehaviour
{
    // ── Shoot Points ──────────────────────────────────────────────────────────
    [Header("Shoot Points  (one entry per barrel)")]
    [Tooltip("Add one entry per barrel. Each fires in sequence (cycled). " +
             "For a single gun just add one entry.")]
    public MGShootPoint[] shootPoints;

    // ── Raycast ───────────────────────────────────────────────────────────────
    [Header("Raycast")]
    [Tooltip("Maximum range of each bullet (metres).")]
    public float range = 300f;

    [Tooltip("Layers bullets can hit. Exclude the tank's own layer.")]
    public LayerMask hitLayers = Physics.DefaultRaycastLayers;

    // ── Damage ────────────────────────────────────────────────────────────────
    [Header("Damage")]
    [Tooltip("Damage per bullet sent via SendMessage(\"TakeDamage\", damage) " +
             "to the hit object. Requires a TakeDamage(float) method on the target.")]
    public float damage = 15f;

    // ── Impact & Explosion ────────────────────────────────────────────────────
    [Header("Impact and Explosion")]
    [Tooltip("Radius around the hit point for area damage and force. " +
             "0 = point hit only.")]
    public float impactRadius = 0f;

    [Tooltip("Outward force applied to Rigidbodies within impactRadius. " +
             "0 = no physics push.")]
    public float explosionForce = 0f;

    [Tooltip("Upward bias for the explosion force. 1 = standard kick. 0 = purely outward.")]
    [Range(0f, 3f)]
    public float explosionUpwardModifier = 0.5f;

    [Tooltip("VFX prefab instantiated at the hit point (e.g. sparks / dust).")]
    public GameObject impactVFXPrefab;

    [Tooltip("How long before the impact VFX is destroyed (seconds).")]
    public float impactVFXLifetime = 1f;

    [Tooltip("Enable a separate explosion VFX when impactRadius > 0.")]
    public bool useExplosionVFX = false;

    [Tooltip("Explosion VFX prefab, used when useExplosionVFX is true and impactRadius > 0.")]
    public GameObject explosionVFXPrefab;

    [Tooltip("How long before the explosion VFX is destroyed (seconds).")]
    public float explosionVFXLifetime = 2f;

    // ── Fire Rate ─────────────────────────────────────────────────────────────
    [Header("Fire Rate")]
    [Tooltip("Rounds per second. 10 = fast MG, 3 = slow cannon-style auto.")]
    [Range(0.5f, 30f)]
    public float fireRate = 10f;

    // ── Recoil ────────────────────────────────────────────────────────────────
    [Header("Recoil")]
    [Tooltip("Rigidbody that receives the recoil impulse per shot (usually the tank hull). " +
             "Leave empty to auto-find on this GameObject or its parents.")]
    public Rigidbody recoilBody;

    [Tooltip("Recoil impulse force (N*s) applied backwards along the barrel per shot. " +
             "Keep this low for machine guns - it adds up fast at high fire rates.")]
    public float recoilForce = 200f;

    // ── Audio ─────────────────────────────────────────────────────────────────
    [Header("Audio")]
    [Tooltip("AudioSource for firing sounds. Leave empty to auto-find on this GameObject.")]
    public AudioSource audioSource;

    [Tooltip("Sound played each time a round fires.")]
    public AudioClip fireSound;

    [Range(0f, 1f)]
    public float fireSoundVolume = 0.8f;

    // ── Input ─────────────────────────────────────────────────────────────────
    [Header("Input")]
    [Tooltip("Choose whether to fire with a mouse button or a keyboard key.")]
    public MGInputType inputType = MGInputType.Key;

    [Tooltip("Mouse button to hold (used when Input Type is MouseButton).")]
    public MGFireButton fireButton = MGFireButton.RightMouse;

#if ENABLE_INPUT_SYSTEM
    [Tooltip("Keyboard key to hold (used when Input Type is Key).")]
    public Key fireKey = Key.Space;
#else
    [Tooltip("Keyboard key to hold (used when Input Type is Key).")]
    public KeyCode fireKey = KeyCode.Space;
#endif

    // ─────────────────────────────────────────────────────────────────────────
    //  Private
    // ─────────────────────────────────────────────────────────────────────────

    private float   nextFireTime   = 0f;
    private int     currentBarrel  = 0;   // index into shootPoints, cycles each shot
    private bool    isFiring       = false;

    // ─────────────────────────────────────────────────────────────────────────
    //  Lifecycle
    // ─────────────────────────────────────────────────────────────────────────

    private void Start()
    {
        // Auto-resolve muzzle VFX on each shoot point
        if (shootPoints != null)
        {
            foreach (var sp in shootPoints)
            {
                if (sp.muzzleVFX == null && sp.transform != null)
                    sp.muzzleVFX = sp.transform.GetComponentInChildren<VisualEffect>();
            }
        }

        if (audioSource == null)
            audioSource = GetComponent<AudioSource>();

        if (recoilBody == null)
            recoilBody = GetComponentInParent<Rigidbody>();
    }

    private void Update()
    {
        isFiring = FireKeyHeld();

        if (isFiring && Time.time >= nextFireTime)
            FireOnce();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Firing
    // ─────────────────────────────────────────────────────────────────────────

    private void FireOnce()
    {
        if (shootPoints == null || shootPoints.Length == 0) return;

        // Clamp and wrap barrel index in case the array changed at runtime
        currentBarrel = currentBarrel % shootPoints.Length;
        MGShootPoint sp = shootPoints[currentBarrel];

        if (sp?.transform == null)
        {
            currentBarrel = (currentBarrel + 1) % shootPoints.Length;
            return;
        }

        nextFireTime = Time.time + (1f / fireRate);

        // ── Muzzle flash ──────────────────────────────────────────────────────
        if (sp.muzzleVFX != null)
        {
            sp.muzzleVFX.Reinit();
            sp.muzzleVFX.Play();
        }

        // ── Fire sound ────────────────────────────────────────────────────────
        if (audioSource != null && fireSound != null)
            audioSource.PlayOneShot(fireSound, fireSoundVolume);

        // ── Recoil ────────────────────────────────────────────────────────────
        if (recoilBody != null)
            recoilBody.AddForceAtPosition(
                -sp.transform.forward * recoilForce,
                sp.transform.position,
                ForceMode.Impulse);

        // ── Raycast ───────────────────────────────────────────────────────────
        Ray ray = new Ray(sp.transform.position, sp.transform.forward);
        if (Physics.Raycast(ray, out RaycastHit hit, range, hitLayers,
                            QueryTriggerInteraction.Ignore))
        {
            HandleImpact(hit);
        }

        // Advance to next barrel
        currentBarrel = (currentBarrel + 1) % shootPoints.Length;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Impact handling
    // ─────────────────────────────────────────────────────────────────────────

    private void HandleImpact(RaycastHit hit)
    {
        Vector3 hitPoint = hit.point;

        // ── Direct hit damage ─────────────────────────────────────────────────
        hit.collider.SendMessageUpwards("TakeDamage", damage,
                                        SendMessageOptions.DontRequireReceiver);

        // ── Area effect ───────────────────────────────────────────────────────
        if (impactRadius > 0f)
        {
            Collider[] nearby = Physics.OverlapSphere(hitPoint, impactRadius, hitLayers);
            foreach (Collider col in nearby)
            {
                col.SendMessageUpwards("TakeDamage", damage,
                                       SendMessageOptions.DontRequireReceiver);

                if (explosionForce > 0f)
                {
                    Rigidbody rb = col.attachedRigidbody;
                    if (rb != null)
                        rb.AddExplosionForce(explosionForce, hitPoint,
                                             impactRadius, explosionUpwardModifier,
                                             ForceMode.Impulse);
                }
            }
        }

        // ── Impact VFX ────────────────────────────────────────────────────────
        if (impactVFXPrefab != null)
        {
            GameObject fx = Instantiate(impactVFXPrefab, hitPoint,
                                        Quaternion.LookRotation(hit.normal));
            Destroy(fx, impactVFXLifetime);
        }

        // ── Explosion VFX ─────────────────────────────────────────────────────
        if (useExplosionVFX && explosionVFXPrefab != null && impactRadius > 0f)
        {
            GameObject fx = Instantiate(explosionVFXPrefab, hitPoint,
                                        Quaternion.identity);
            Destroy(fx, explosionVFXLifetime);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Input helper
    // ─────────────────────────────────────────────────────────────────────────

    private bool FireKeyHeld()
    {
#if ENABLE_INPUT_SYSTEM
        if (inputType == MGInputType.Key)
        {
            var kb = Keyboard.current;
            return kb != null && kb[fireKey].isPressed;
        }
        else
        {
            var mouse = Mouse.current;
            if (mouse == null) return false;
            switch (fireButton)
            {
                case MGFireButton.LeftMouse:   return mouse.leftButton.isPressed;
                case MGFireButton.RightMouse:  return mouse.rightButton.isPressed;
                case MGFireButton.MiddleMouse: return mouse.middleButton.isPressed;
                default: return false;
            }
        }
#else
        if (inputType == MGInputType.Key)
            return Input.GetKey(fireKey);
        else
            return Input.GetMouseButton((int)fireButton);
#endif
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Gizmos
    // ─────────────────────────────────────────────────────────────────────────

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        if (shootPoints == null) return;

        foreach (var sp in shootPoints)
        {
            if (sp?.transform == null) continue;

            Gizmos.color = Color.cyan;
            Gizmos.DrawRay(sp.transform.position, sp.transform.forward * range);

            if (impactRadius > 0f)
            {
                Gizmos.color = new Color(0f, 1f, 1f, 0.15f);
                Gizmos.DrawSphere(sp.transform.position + sp.transform.forward * range,
                                  impactRadius);
                Gizmos.color = new Color(0f, 1f, 1f, 0.6f);
                Gizmos.DrawWireSphere(sp.transform.position + sp.transform.forward * range,
                                      impactRadius);
            }
        }
    }
#endif
}
