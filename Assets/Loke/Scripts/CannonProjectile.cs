using UnityEngine;
using UnityEngine.VFX;

// =============================================================================
//  CannonProjectile.cs  -  Unity 6  (Penetration System)
//
//  Physical cannon projectile with full armor penetration simulation.
//  Spawned and fully configured by CannonController immediately after
//  Instantiate(). CannonController sets every [HideInInspector] field so
//  you can share one prefab across all ammo types and the behaviour
//  changes purely from the ammo data.
//
//  Collision flow
//  ──────────────
//  OnCollisionEnter
//    │
//    ├─ ArmorPlate found on hit collider?
//    │   ├─ Run PenetrationCalculator.Calculate()
//    │   ├─ Forward PenetrationResult to TankHealth.ApplyPenetrationResult()
//    │   ├─ Spawn appropriate VFX (penetration spark vs ricochet flash)
//    │   └─ If APHE and penetrated: ApplyAPHEBlast() inside the hull
//    │
//    └─ No ArmorPlate (terrain, prop, destructible)
//        └─ Legacy TakeDamage + area damage + VFX (unchanged from original)
//
//  Velocity falloff
//  ────────────────
//  The Rigidbody handles physics naturally. At the moment of collision the
//  script reads rb.linearVelocity.magnitude — giving you realistic range
//  penalties for free. Add a small Drag value in the prefab's Rigidbody
//  to tune how quickly velocity (and therefore penetration) drops with
//  distance.
//
//  Prefab setup
//  ────────────
//    1. Mesh + stretched capsule/cylinder.
//    2. Rigidbody: mass ~10 kg, Drag 0–0.05, Angular Drag 0, Use Gravity true.
//    3. CapsuleCollider or SphereCollider.
//    4. Attach this script.
//    5. Put this prefab on its own physics layer and exclude it from the
//       tank's own layer in Project Settings → Physics.
// =============================================================================

public class CannonProjectile : MonoBehaviour
{
    // ── Damage / explosion — set by CannonController ──────────────────────────
    [HideInInspector] public float     damage                  = 80f;
    [HideInInspector] public float     impactRadius            = 0f;
    [HideInInspector] public float     explosionForce          = 0f;
    [HideInInspector] public float     explosionUpwardModifier = 1f;
    [HideInInspector] public LayerMask hitLayers;
    [HideInInspector] public bool      useExplosionVFX         = false;

    [HideInInspector] public VisualEffect impactVFXPrefab;
    [HideInInspector] public float        impactVFXLifetime    = 2f;
    [HideInInspector] public VisualEffect explosionVFXPrefab;
    [HideInInspector] public float        explosionVFXLifetime = 3f;

    [HideInInspector] public float lifetime = 10f;

    // ── Penetration data — set by CannonController ────────────────────────────
    [HideInInspector] public ShellType shellType          = ShellType.AP;

    [Tooltip("Shell outer diameter in mm (set by CannonController).")]
    [HideInInspector] public float caliber            = 75f;

    [Tooltip("Original muzzle velocity in m/s. Used to compute velocity retention.")]
    [HideInInspector] public float muzzleVelocity     = 800f;

    [Tooltip("Nominal penetration in mm RHA at 0° impact angle at muzzle velocity.")]
    [HideInInspector] public float basePenetrationMM  = 100f;

    [Tooltip("Degrees of normalization on impact. Set by CannonController.")]
    [HideInInspector] public float normalizationDeg   = 5f;

    [Tooltip("Impact angle at which ricochet occurs. Set by CannonController.")]
    [HideInInspector] public float ricochetAngleDeg   = 70f;

    // ── APHE internal detonation — set by CannonController ───────────────────
    [HideInInspector] public float apheBlastDamage    = 150f;
    [HideInInspector] public float apheBlastRadius    = 3f;

    // ─────────────────────────────────────────────────────────────────────────
    //  Private
    // ─────────────────────────────────────────────────────────────────────────

    private Rigidbody rb;
    private bool      hasImpacted = false;

    // ─────────────────────────────────────────────────────────────────────────
    //  Lifecycle
    // ─────────────────────────────────────────────────────────────────────────

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();
    }

    private void Start()
    {
        Destroy(gameObject, lifetime);
    }

    private void OnCollisionEnter(Collision col)
    {
        if (hasImpacted) return;
        hasImpacted = true;

        ContactPoint contact   = col.GetContact(0);
        Vector3      hitPoint  = contact.point;
        Vector3      hitNormal = contact.normal; // outward-facing surface normal

        // Read velocity at the moment of impact (handles range-based pen falloff)
        float   currentSpeed = rb != null ? rb.linearVelocity.magnitude : muzzleVelocity;
        Vector3 shellDir     = rb != null ? rb.linearVelocity.normalized : transform.forward;

        // ── Armor penetration path ────────────────────────────────────────────
        ArmorPlate armor = col.collider.GetComponent<ArmorPlate>();
        if (armor != null)
            HandleArmorHit(armor, hitPoint, hitNormal, shellDir, currentSpeed);
        else
            HandleLegacyImpact(col.collider, hitPoint, hitNormal);

        Destroy(gameObject);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Armor hit handling
    // ─────────────────────────────────────────────────────────────────────────

    private void HandleArmorHit(ArmorPlate armor, Vector3 hitPoint,
                                  Vector3 hitNormal, Vector3 shellDir, float currentSpeed)
    {
        // ── Run primary penetration calculation ───────────────────────────────
        PenetrationResult result = PenetrationCalculator.Calculate(
            shellVelocityMS:   currentSpeed,
            muzzleVelocityMS:  muzzleVelocity,
            caliber:           caliber,
            basePenetrationMM: basePenetrationMM,
            shellType:         shellType,
            normalizationDeg:  normalizationDeg,
            ricochetAngleDeg:  ricochetAngleDeg,
            shellDirection:    shellDir,
            armorNormal:       hitNormal,
            armorPlate:        armor);

        LogResult(result, armor, currentSpeed);

        // ── Spaced armor: re-run against backing plate with reduced energy ────
        // If the outer (spaced) plate is defeated by a kinetic shell, the shell
        // still has to defeat the main backing plate — but with less energy.
        if (result.penetrated && armor.isSpacedArmor && armor.spacedBackingPlate != null)
        {
            // Remaining speed scales with sqrt of remaining penetration energy.
            float remainingFraction = Mathf.Clamp01(
                result.excessPenetrationMM / Mathf.Max(1f, result.shellPenetrationMM));
            float remainingSpeed = currentSpeed * Mathf.Sqrt(remainingFraction);

            PenetrationResult backingResult = PenetrationCalculator.Calculate(
                shellVelocityMS:   remainingSpeed,
                muzzleVelocityMS:  muzzleVelocity,
                caliber:           caliber,
                basePenetrationMM: basePenetrationMM,
                shellType:         shellType,
                normalizationDeg:  normalizationDeg,
                ricochetAngleDeg:  ricochetAngleDeg,
                shellDirection:    shellDir,
                armorNormal:       hitNormal,
                armorPlate:        armor.spacedBackingPlate);

            Debug.Log($"[CannonProjectile] Spaced backing plate check — " +
                      $"{(backingResult.penetrated ? "PENETRATED" : "BLOCKED")}");
            result = backingResult;
        }

        // ── Forward result to TankHealth ──────────────────────────────────────
        TankHealth health = armor.tankHealth;
        if (health != null)
            health.ApplyPenetrationResult(result, shellType, hitPoint);

        // ── Impact VFX ────────────────────────────────────────────────────────
        SpawnImpactVFX(result, hitPoint, hitNormal);

        // ── APHE internal detonation ──────────────────────────────────────────
        // The fuse arms on penetration and detonates roughly half a shell-length
        // inside the hull, maximising fragmentation damage to crew and modules.
        if (result.penetrated && shellType == ShellType.APHE && apheBlastRadius > 0f)
        {
            // Move the blast origin slightly inside the hull along the shell's path
            Vector3 blastOrigin = hitPoint + shellDir * (apheBlastRadius * 0.5f);
            ApplyAPHEBlast(blastOrigin);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  APHE internal explosion
    // ─────────────────────────────────────────────────────────────────────────

    private void ApplyAPHEBlast(Vector3 blastOrigin)
    {
        Collider[] nearby = Physics.OverlapSphere(blastOrigin, apheBlastRadius, hitLayers);
        foreach (Collider col in nearby)
        {
            col.SendMessageUpwards("TakeDamage", apheBlastDamage,
                                   SendMessageOptions.DontRequireReceiver);

            if (explosionForce > 0f)
            {
                Rigidbody nrb = col.attachedRigidbody;
                if (nrb != null)
                    nrb.AddExplosionForce(explosionForce, blastOrigin,
                                          apheBlastRadius, explosionUpwardModifier,
                                          ForceMode.Impulse);
            }
        }

        if (useExplosionVFX && explosionVFXPrefab != null)
        {
            VisualEffect fx = Instantiate(explosionVFXPrefab, blastOrigin, Quaternion.identity);
            fx.Play();
            Destroy(fx.gameObject, explosionVFXLifetime);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Legacy impact (terrain, props, destructibles without ArmorPlate)
    // ─────────────────────────────────────────────────────────────────────────

    private void HandleLegacyImpact(Collider hitCollider, Vector3 hitPoint, Vector3 hitNormal)
    {
        hitCollider.SendMessageUpwards("TakeDamage", damage,
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
                    Rigidbody nrb = col.attachedRigidbody;
                    if (nrb != null)
                        nrb.AddExplosionForce(explosionForce, hitPoint,
                                              impactRadius, explosionUpwardModifier,
                                              ForceMode.Impulse);
                }
            }
        }

        if (impactVFXPrefab != null)
        {
            VisualEffect fx = Instantiate(impactVFXPrefab, hitPoint,
                                           Quaternion.LookRotation(hitNormal));
            fx.Play();
            Destroy(fx.gameObject, impactVFXLifetime);
        }

        if (useExplosionVFX && explosionVFXPrefab != null && impactRadius > 0f)
        {
            VisualEffect fx = Instantiate(explosionVFXPrefab, hitPoint, Quaternion.identity);
            fx.Play();
            Destroy(fx.gameObject, explosionVFXLifetime);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  VFX helpers
    // ─────────────────────────────────────────────────────────────────────────

    private void SpawnImpactVFX(PenetrationResult result, Vector3 hitPoint, Vector3 hitNormal)
    {
        if (impactVFXPrefab == null) return;

        // For a penetrating hit the VFX faces INWARD (into the tank); for a
        // ricochet or block it faces OUTWARD along the surface normal.
        Quaternion rot = result.penetrated
            ? Quaternion.LookRotation(-hitNormal)
            : Quaternion.LookRotation(hitNormal);

        VisualEffect fx = Instantiate(impactVFXPrefab, hitPoint, rot);
        fx.Play();
        Destroy(fx.gameObject, impactVFXLifetime);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Debug logging
    // ─────────────────────────────────────────────────────────────────────────

    private void LogResult(PenetrationResult result, ArmorPlate armor, float speed)
    {
        string outcome = result.ricocheted          ? "RICOCHET"
                       : result.heatDefeatedBySpaced ? "HEAT DEFEATED (spaced armor)"
                       : result.penetrated           ? "PENETRATED"
                       : "BLOCKED";

        Debug.Log(
            $"[CannonProjectile] {shellType} ({caliber:F0}mm) → {armor.zoneName}\n" +
            $"  Speed:        {speed:F0} m/s\n" +
            $"  Shell pen:    {result.shellPenetrationMM:F0} mm\n" +
            $"  Impact angle: {result.impactAngleDeg:F1}° → {result.normalizedAngleDeg:F1}° (after norm)\n" +
            $"  Eff. armor:   {result.effectiveArmorMM:F0} mm RHA\n" +
            $"  Pen ratio:    {result.penRatio * 100f:F0}%\n" +
            $"  Result:       {outcome}");
    }
}
