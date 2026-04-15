// Input System compatibility
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

using System.Collections;
using UnityEngine;
using UnityEngine.VFX;

// =============================================================================
//  CannonController.cs  -  Unity 6
//
//  Single-shot raycast cannon with muzzle flash, impact / explosion VFX,
//  area force, audio, and barrel recoil.
//
//  Setup:
//    1. Attach this script to any GameObject (e.g. the turret or barrel).
//    2. Create an empty GameObject at the barrel tip - this is the Shoot Point.
//       Add your muzzle-flash ParticleSystem directly onto that object.
//    3. Assign the Shoot Point in the Inspector.
//    4. Assign the hull / barrel Rigidbody for recoil.
//    5. Optionally assign impact and explosion VFX prefabs.
// =============================================================================

public enum CannonFireButton { LeftMouse, RightMouse, MiddleMouse }

public class CannonController : MonoBehaviour
{
    // ── Shoot Point ───────────────────────────────────────────────────────────
    [Header("Shoot Point")]
    [Tooltip("Empty GameObject at the barrel tip. " +
             "The muzzle-flash ParticleSystem should live on this object.")]
    public Transform shootPoint;

    [Tooltip("Muzzle flash Visual Effect. Leave empty to auto-find it on the Shoot Point.")]
    public VisualEffect muzzleVFX;

    // ── Raycast ───────────────────────────────────────────────────────────────
    [Header("Raycast")]
    [Tooltip("Maximum range of the cannon round (metres).")]
    public float range = 1000f;

    [Tooltip("Layers the cannon round can hit. Exclude the tank's own layer.")]
    public LayerMask hitLayers = Physics.DefaultRaycastLayers;

    // ── Damage ────────────────────────────────────────────────────────────────
    [Header("Damage")]
    [Tooltip("Direct-hit damage sent via SendMessage(\"TakeDamage\", damage) " +
             "to the hit object. Requires a TakeDamage(float) method on the target.")]
    public float damage = 150f;

    // ── Impact & Explosion ────────────────────────────────────────────────────
    [Header("Impact and Explosion")]
    [Tooltip("Radius around the impact point that also receives damage and force. " +
             "0 = point hit only (no area effect).")]
    public float impactRadius = 4f;

    [Tooltip("Outward force applied to every Rigidbody within impactRadius. " +
             "0 = no physics push.")]
    public float explosionForce = 8000f;

    [Tooltip("Upward bias added to the explosion force. " +
             "1 = standard upward kick; 0 = purely outward.")]
    [Range(0f, 3f)]
    public float explosionUpwardModifier = 1f;

    [Tooltip("VFX prefab instantiated at the exact hit point (e.g. a dust / sparks effect). " +
             "Leave empty for no impact VFX.")]
    public GameObject impactVFXPrefab;

    [Tooltip("How long before the instantiated impact VFX is destroyed (seconds).")]
    public float impactVFXLifetime = 2f;

    [Tooltip("Enable a separate, larger explosion VFX when impactRadius > 0.")]
    public bool useExplosionVFX = true;

    [Tooltip("Explosion VFX prefab instantiated at the hit point when useExplosionVFX is true.")]
    public GameObject explosionVFXPrefab;

    [Tooltip("How long before the instantiated explosion VFX is destroyed (seconds).")]
    public float explosionVFXLifetime = 3f;

    // ── Recoil ────────────────────────────────────────────────────────────────
    [Header("Recoil")]
    [Tooltip("Rigidbody that receives the recoil impulse (usually the tank hull). " +
             "Leave empty to auto-find on this GameObject or its parents.")]
    public Rigidbody recoilBody;

    [Tooltip("Recoil impulse force (N*s). Applied backwards along the barrel at the moment of firing.")]
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

    // ─────────────────────────────────────────────────────────────────────────
    //  Private
    // ─────────────────────────────────────────────────────────────────────────

    private float nextFireTime = 0f;

    // ─────────────────────────────────────────────────────────────────────────
    //  Lifecycle
    // ─────────────────────────────────────────────────────────────────────────

    private void Start()
    {
        // Auto-resolve optional references
        if (muzzleVFX == null && shootPoint != null)
            muzzleVFX = shootPoint.GetComponentInChildren<VisualEffect>();

        if (audioSource == null)
            audioSource = GetComponent<AudioSource>();

        if (recoilBody == null)
            recoilBody = GetComponentInParent<Rigidbody>();
    }

    private void Update()
    {
        if (FireKeyDown() && Time.time >= nextFireTime)
            Fire();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Firing
    // ─────────────────────────────────────────────────────────────────────────

    public void Fire()
    {
        if (shootPoint == null) return;

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

        // ── Raycast ───────────────────────────────────────────────────────────
        Ray ray = new Ray(shootPoint.position, shootPoint.forward);
        if (Physics.Raycast(ray, out RaycastHit hit, range, hitLayers,
                            QueryTriggerInteraction.Ignore))
        {
            HandleImpact(hit);
        }
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
                // Area damage
                col.SendMessageUpwards("TakeDamage", damage,
                                       SendMessageOptions.DontRequireReceiver);

                // Explosion force
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
        int btn = (int)fireButton;
        return Input.GetMouseButtonDown(btn);
#endif
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Gizmos
    // ─────────────────────────────────────────────────────────────────────────

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        if (shootPoint == null) return;

        // Draw fire direction ray
        Gizmos.color = Color.yellow;
        Gizmos.DrawRay(shootPoint.position, shootPoint.forward * range);

        // Draw impact radius sphere at range if area effect is on
        if (impactRadius > 0f)
        {
            Gizmos.color = new Color(1f, 0.4f, 0f, 0.25f);
            Gizmos.DrawSphere(shootPoint.position + shootPoint.forward * range, impactRadius);
            Gizmos.color = new Color(1f, 0.4f, 0f, 0.8f);
            Gizmos.DrawWireSphere(shootPoint.position + shootPoint.forward * range, impactRadius);
        }
    }
#endif
}
