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
//  Each shoot point fires in sequence (barrel cycling).
//
//  Audio uses a pool of AudioSources so the last N shots keep their tail.
//  When a new shot is fired and all pool slots are busy, the oldest one gets
//  cut and reused. Adjust Audio Tail Count to change how many tails overlap.
//
//  Multiple guns on one tank:
//    Add one MachineGunController per gun mount.
//    Each instance has its own fire rate, damage, and shoot points.
// =============================================================================

[System.Serializable]
public class MGShootPoint
{
    [Tooltip("Empty GameObject at the barrel tip. " +
             "The muzzle-flash VFX should be on this object.")]
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
    [Tooltip("Add one entry per barrel. Barrels fire in strict sequence and cycle back:\n" +
             "1 barrel  → fires repeatedly from barrel 1.\n" +
             "2 barrels → barrel 1, barrel 2, barrel 1, barrel 2...\n" +
             "Fire Rate is the total rate across all barrels.")]
    public MGShootPoint[] shootPoints;

    // ── Raycast ───────────────────────────────────────────────────────────────
    [Header("Raycast")]
    [Tooltip("Maximum range of each bullet (metres).")]
    public float range = 300f;

    [Tooltip("Layers bullets can hit. Exclude the tank's own layer.")]
    public LayerMask hitLayers = Physics.DefaultRaycastLayers;

    // ── Damage ────────────────────────────────────────────────────────────────
    [Header("Damage")]
    [Tooltip("Damage per bullet sent via SendMessage(\"TakeDamage\", damage).")]
    public float damage = 15f;

    // ── Impact & Explosion ────────────────────────────────────────────────────
    [Header("Impact and Explosion")]
    [Tooltip("Radius around the hit point for area damage and force. 0 = point hit only.")]
    public float impactRadius = 0f;

    [Tooltip("Outward force applied to Rigidbodies within impactRadius. 0 = no push.")]
    public float explosionForce = 0f;

    [Range(0f, 3f)]
    [Tooltip("Upward bias for the explosion force.")]
    public float explosionUpwardModifier = 0.5f;

    [Tooltip("Visual Effect instantiated at the hit point. Leave empty for none.")]
    public VisualEffect impactVFXPrefab;

    [Tooltip("How long before the impact VFX is destroyed (seconds).")]
    public float impactVFXLifetime = 1f;

    [Tooltip("Enable a separate explosion VFX when impactRadius > 0.")]
    public bool useExplosionVFX = false;

    [Tooltip("Explosion VFX instantiated at the hit point when useExplosionVFX is true.")]
    public VisualEffect explosionVFXPrefab;

    [Tooltip("How long before the explosion VFX is destroyed (seconds).")]
    public float explosionVFXLifetime = 2f;

    // ── Fire Rate ─────────────────────────────────────────────────────────────
    [Header("Fire Rate")]
    [Range(0.5f, 30f)]
    [Tooltip("Rounds per second across all barrels.")]
    public float fireRate = 10f;

    // ── Recoil ────────────────────────────────────────────────────────────────
    [Header("Recoil")]
    [Tooltip("Rigidbody that receives the recoil impulse. Leave empty to auto-find.")]
    public Rigidbody recoilBody;

    [Tooltip("Recoil impulse (N*s) per shot along the barrel.")]
    public float recoilForce = 200f;

    // ── Audio ─────────────────────────────────────────────────────────────────
    [Header("Audio")]
    [Tooltip("The primary AudioSource. Spatial blend and mixer settings are copied " +
             "to the extra pool sources automatically.")]
    public AudioSource audioSource;

    [Tooltip("Sound played each time a round fires.")]
    public AudioClip fireSound;

    [Range(0f, 1f)]
    public float fireSoundVolume = 0.8f;

    [Range(1, 8)]
    [Tooltip("How many shot tails can overlap at once. " +
             "3 means the last 3 shots keep their full tail; the 4th cuts the oldest.\n" +
             "Extra AudioSources are created automatically at startup.")]
    public int audioTailCount = 3;

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

    private float         nextFireTime  = 0f;
    private int           currentBarrel = 0;
    private bool          isFiring      = false;

    private AudioSource[] audioPool;
    private int           audioPoolIndex = 0;

    // ─────────────────────────────────────────────────────────────────────────
    //  Lifecycle
    // ─────────────────────────────────────────────────────────────────────────

    private void Start()
    {
        // Auto-resolve muzzle VFX
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

        // Build audio pool: reuse the assigned AudioSource as slot 0,
        // then add extra components copying its spatial / mixer settings.
        audioTailCount = Mathf.Max(1, audioTailCount);
        audioPool      = new AudioSource[audioTailCount];
        audioPool[0]   = audioSource;

        for (int i = 1; i < audioTailCount; i++)
        {
            AudioSource extra = gameObject.AddComponent<AudioSource>();

            if (audioSource != null)
            {
                extra.outputAudioMixerGroup = audioSource.outputAudioMixerGroup;
                extra.spatialBlend          = audioSource.spatialBlend;
                extra.minDistance           = audioSource.minDistance;
                extra.maxDistance           = audioSource.maxDistance;
                extra.rolloffMode           = audioSource.rolloffMode;
                extra.dopplerLevel          = audioSource.dopplerLevel;
                extra.spread                = audioSource.spread;
            }

            extra.playOnAwake = false;
            audioPool[i]      = extra;
        }
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
        // Grab the next pool slot. If it is still playing (from audioTailCount
        // shots ago) it gets cut here - all more-recent shots keep their tail.
        if (audioPool != null && fireSound != null)
        {
            AudioSource src = audioPool[audioPoolIndex];
            audioPoolIndex  = (audioPoolIndex + 1) % audioPool.Length;

            src.Stop();
            src.clip   = fireSound;
            src.volume = fireSoundVolume;
            src.Play();
        }

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

        currentBarrel = (currentBarrel + 1) % shootPoints.Length;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Impact handling
    // ─────────────────────────────────────────────────────────────────────────

    private void HandleImpact(RaycastHit hit)
    {
        Vector3 hitPoint = hit.point;

        hit.collider.SendMessageUpwards("TakeDamage", damage,
                                        SendMessageOptions.DontRequireReceiver);

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

        if (impactVFXPrefab != null)
        {
            VisualEffect fx = Instantiate(impactVFXPrefab, hitPoint,
                                          Quaternion.LookRotation(hit.normal));
            fx.Play();
            Destroy(fx.gameObject, impactVFXLifetime);
        }

        if (useExplosionVFX && explosionVFXPrefab != null && impactRadius > 0f)
        {
            VisualEffect fx = Instantiate(explosionVFXPrefab, hitPoint,
                                          Quaternion.identity);
            fx.Play();
            Destroy(fx.gameObject, explosionVFXLifetime);
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
