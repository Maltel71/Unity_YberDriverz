using UnityEngine;
using UnityEngine.VFX;

// =============================================================================
//  CannonProjectile.cs  -  Unity 6  (Module System Integration)
//
//  Physical cannon projectile with armor penetration and module damage routing.
//  Spawned and fully configured by CannonController immediately after Instantiate.
//
//  Collision priority
//  ──────────────────
//  OnCollisionEnter inspects the hit collider for components in this order:
//
//    1. TankModule  (direct module hit — may also have ArmorPlate on same object)
//       a. If ArmorPlate present: run penetration calc against module armor.
//          If penetrated → ModuleManager.ApplyPenetratingHit(hitModule=this module).
//       b. If no ArmorPlate: exposed module — apply ricochet check using the
//          module's ricochetAngleModifier, then deal damage directly.
//
//    2. ArmorPlate only (general hull armor zone, no specific module)
//       Run penetration calc. If penetrated → TankHealth.ApplyPenetrationResult()
//       (which delegates to ModuleManager for spall distribution).
//
//    3. Neither (terrain, props, destructibles)
//       Legacy: SendMessage TakeDamage + area force + VFX.
//
//  APHE detonation
//  ───────────────
//  When an APHE shell penetrates, CannonProjectile triggers the internal blast
//  via ModuleManager.ApplyPenetratingHit (which handles APHE internally) OR
//  via TankHealth.ApplyPenetrationResult. The projectile does NOT duplicate
//  the APHE logic — it's all inside ModuleManager/TankHealth.
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

    // ── Penetration / ballistic data — set by CannonController ───────────────
    [HideInInspector] public ShellType shellType          = ShellType.AP;
    [HideInInspector] public float     caliber            = 75f;
    [HideInInspector] public float     muzzleVelocity     = 800f;
    [HideInInspector] public float     basePenetrationMM  = 100f;
    [HideInInspector] public float     normalizationDeg   = 5f;
    [HideInInspector] public float     ricochetAngleDeg   = 70f;

    // ── APHE internal detonation — set by CannonController ───────────────────
    [HideInInspector] public float apheBlastDamage = 150f;
    [HideInInspector] public float apheBlastRadius = 3f;

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
        // Disable collision detection during instantiation to prevent Unity 6's
        // "Instantiate failed because the clone was destroyed during creation" error.
        // This occurs when the projectile spawns inside / overlapping another collider
        // (e.g. the barrel tip is inside the hull mesh), causing OnCollisionEnter to
        // fire synchronously during Instantiate — which then calls Destroy(gameObject)
        // before Instantiate has returned, crashing Unity.
        if (rb != null) rb.detectCollisions = false;
    }

    private void Start()
    {
        // Re-enable collision detection now that Instantiate has fully completed.
        if (rb != null) rb.detectCollisions = true;
        Destroy(gameObject, lifetime);
    }

    private void OnCollisionEnter(Collision col)
    {
        if (hasImpacted) return;
        hasImpacted = true;

        ContactPoint contact   = col.GetContact(0);
        Vector3      hitPoint  = contact.point;
        Vector3      hitNormal = contact.normal;   // outward-facing surface normal
        float        speed     = rb != null ? rb.linearVelocity.magnitude : muzzleVelocity;
        Vector3      shellDir  = rb != null ? rb.linearVelocity.normalized : transform.forward;

        TankModule module = col.collider.GetComponent<TankModule>();
        ArmorPlate armor  = col.collider.GetComponent<ArmorPlate>();

        if (module != null)
            HandleModuleHit(module, armor, hitPoint, hitNormal, shellDir, speed);
        else if (armor != null)
            HandleArmorHit(armor, hitPoint, hitNormal, shellDir, speed);
        else
            HandleLegacyImpact(col.collider, hitPoint, hitNormal);

        Destroy(gameObject);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Path 1 — Direct module hit
    // ─────────────────────────────────────────────────────────────────────────

    private void HandleModuleHit(TankModule module, ArmorPlate armor,
                                   Vector3 hitPoint, Vector3 hitNormal,
                                   Vector3 shellDir, float speed)
    {
        ModuleManager mgr = module.GetComponentInParent<ModuleManager>();

        if (armor != null)
        {
            // Module has its own armor screen (e.g. armoured engine firewall).
            // Run full penetration calc against that armor first.
            PenetrationResult result = RunPenetration(speed, shellDir, hitNormal, armor);
            LogResult(result, armor.zoneName, speed);
            SpawnImpactVFX(result, hitPoint, hitNormal);

            // Spaced armor re-check
            if (result.penetrated && armor.isSpacedArmor && armor.spacedBackingPlate != null)
                result = RerunAgainstBacking(result, speed, shellDir, hitNormal,
                                             armor.spacedBackingPlate);

            if (result.penetrated)
            {
                // Penetrated module armor — route through ModuleManager
                if (mgr != null)
                    mgr.ApplyPenetratingHit(result, shellType, module, hitPoint);
                else
                    module.TakeDamage(CalcBaseDamage(result), shellType);
            }
            else
            {
                // Blocked by module armor — relay to TankHealth for feedback
                armor.tankHealth?.ApplyPenetrationResult(result, shellType, hitPoint);
            }
        }
        else
        {
            // Exposed module with no armor — perform only a ricochet check.
            // The ricochetAngleModifier on the module adjusts the threshold.
            float ricochetThreshold = ricochetAngleDeg + module.ricochetAngleModifier;
            float cosImpact = Mathf.Clamp(
                Vector3.Dot(-shellDir.normalized, hitNormal.normalized), -1f, 1f);
            float impactAngle = Mathf.Acos(cosImpact) * Mathf.Rad2Deg;

            bool ricocheted = (shellType != ShellType.HEAT && shellType != ShellType.HE)
                           && impactAngle >= ricochetThreshold;

            if (!ricocheted)
            {
                Debug.Log($"[CannonProjectile] {shellType} hit exposed module " +
                          $"'{module.displayName}' at {impactAngle:F1}°");

                // Build a full-penetration result for damage routing
                PenetrationResult result = BuildFullPenResult(speed, impactAngle);

                if (mgr != null)
                    mgr.ApplyPenetratingHit(result, shellType, module, hitPoint);
                else
                    module.TakeDamage(CalcBaseDamage(result), shellType);

                SpawnVFX(impactVFXPrefab, hitPoint,
                         Quaternion.LookRotation(-hitNormal), impactVFXLifetime);
            }
            else
            {
                Debug.Log($"[CannonProjectile] RICOCHET off exposed module " +
                          $"'{module.displayName}' at {impactAngle:F1}°");
                SpawnVFX(impactVFXPrefab, hitPoint,
                         Quaternion.LookRotation(hitNormal), impactVFXLifetime);
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Path 2 — General hull armor hit (no TankModule on collider)
    // ─────────────────────────────────────────────────────────────────────────

    private void HandleArmorHit(ArmorPlate armor, Vector3 hitPoint,
                                  Vector3 hitNormal, Vector3 shellDir, float speed)
    {
        PenetrationResult result = RunPenetration(speed, shellDir, hitNormal, armor);
        LogResult(result, armor.zoneName, speed);
        SpawnImpactVFX(result, hitPoint, hitNormal);

        // Spaced armor re-check
        if (result.penetrated && armor.isSpacedArmor && armor.spacedBackingPlate != null)
            result = RerunAgainstBacking(result, speed, shellDir, hitNormal,
                                         armor.spacedBackingPlate);

        // Route to TankHealth, which delegates module damage to ModuleManager
        TankHealth health = armor.tankHealth;
        if (health != null)
        {
            // If ModuleManager is available, route directly for richer module routing
            ModuleManager mgr = health.GetComponent<ModuleManager>();
            if (mgr != null && result.penetrated)
                mgr.ApplyPenetratingHit(result, shellType, null, hitPoint);

            // Always call ApplyPenetrationResult for VFX/audio feedback and hull HP
            health.ApplyPenetrationResult(result, shellType, hitPoint);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Path 3 — Legacy (no ArmorPlate, no TankModule)
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
                Rigidbody nrb = col.attachedRigidbody;
                if (nrb != null && explosionForce > 0f)
                    nrb.AddExplosionForce(explosionForce, hitPoint,
                                          impactRadius, explosionUpwardModifier,
                                          ForceMode.Impulse);
            }
        }

        SpawnVFX(impactVFXPrefab, hitPoint,
                 Quaternion.LookRotation(hitNormal), impactVFXLifetime);

        if (useExplosionVFX && explosionVFXPrefab != null && impactRadius > 0f)
            SpawnVFX(explosionVFXPrefab, hitPoint, Quaternion.identity, explosionVFXLifetime);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Penetration helpers
    // ─────────────────────────────────────────────────────────────────────────

    private PenetrationResult RunPenetration(float speed, Vector3 dir,
                                              Vector3 normal, ArmorPlate plate)
    {
        return PenetrationCalculator.Calculate(
            shellVelocityMS:   speed,
            muzzleVelocityMS:  muzzleVelocity,
            caliber:           caliber,
            basePenetrationMM: basePenetrationMM,
            shellType:         shellType,
            normalizationDeg:  normalizationDeg,
            ricochetAngleDeg:  ricochetAngleDeg,
            shellDirection:    dir,
            armorNormal:       normal,
            armorPlate:        plate);
    }

    private PenetrationResult RerunAgainstBacking(PenetrationResult prev, float originalSpeed,
                                                    Vector3 dir, Vector3 normal,
                                                    ArmorPlate backing)
    {
        // Remaining speed proportional to sqrt of excess-pen fraction
        float remainFrac  = Mathf.Clamp01(prev.excessPenetrationMM /
                                           Mathf.Max(1f, prev.shellPenetrationMM));
        float remainSpeed = originalSpeed * Mathf.Sqrt(remainFrac);

        PenetrationResult r = RunPenetration(remainSpeed, dir, normal, backing);
        Debug.Log($"[CannonProjectile] Spaced backing check — " +
                  $"{(r.penetrated ? "PENETRATED" : "BLOCKED")} " +
                  $"({r.shellPenetrationMM:F0}mm vs {r.effectiveArmorMM:F0}mm)");
        return r;
    }

    /// <summary>
    /// Build a PenetrationResult that represents a full, guaranteed penetration
    /// against an unarmored/exposed component (no ArmorPlate present).
    /// </summary>
    private PenetrationResult BuildFullPenResult(float speed, float impactAngle)
    {
        float pen = PenetrationCalculator.DefaultNormalization(shellType);  // just to reference the class
        float shellPen = basePenetrationMM * Mathf.Pow(
            Mathf.Clamp01(speed / Mathf.Max(1f, muzzleVelocity)), 2f);

        return new PenetrationResult
        {
            penetrated             = true,
            ricocheted             = false,
            overmatched            = true,
            heatDefeatedBySpaced   = false,
            impactAngleDeg         = impactAngle,
            normalizedAngleDeg     = 0f,
            effectiveArmorMM       = 0f,
            shellPenetrationMM     = shellPen,
            penRatio               = 99f,
            excessPenetrationMM    = shellPen,
            postPenDamageMultiplier = 1.5f,   // full energy into the module
        };
    }

    private float CalcBaseDamage(PenetrationResult r)
        => 80f * r.postPenDamageMultiplier;

    // ─────────────────────────────────────────────────────────────────────────
    //  VFX helpers
    // ─────────────────────────────────────────────────────────────────────────

    private void SpawnImpactVFX(PenetrationResult result, Vector3 hitPoint, Vector3 hitNormal)
    {
        if (impactVFXPrefab == null) return;
        Quaternion rot = result.penetrated
            ? Quaternion.LookRotation(-hitNormal)
            : Quaternion.LookRotation(hitNormal);
        SpawnVFX(impactVFXPrefab, hitPoint, rot, impactVFXLifetime);
    }

    private void SpawnVFX(VisualEffect prefab, Vector3 pos, Quaternion rot, float life)
    {
        if (prefab == null) return;
        VisualEffect fx = Instantiate(prefab, pos, rot);
        fx.Play();
        Destroy(fx.gameObject, life);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Debug logging
    // ─────────────────────────────────────────────────────────────────────────

    private void LogResult(PenetrationResult r, string zoneName, float speed)
    {
        string outcome = r.ricocheted           ? "RICOCHET"
                       : r.heatDefeatedBySpaced ? "HEAT DEFEATED (spaced)"
                       : r.penetrated           ? "PENETRATED"
                       : "BLOCKED";

        Debug.Log(
            $"[CannonProjectile] {shellType} ({caliber:F0}mm) → {zoneName}\n" +
            $"  Speed:        {speed:F0} m/s\n" +
            $"  Shell pen:    {r.shellPenetrationMM:F0} mm\n" +
            $"  Impact angle: {r.impactAngleDeg:F1}° → {r.normalizedAngleDeg:F1}° (after norm)\n" +
            $"  Eff. armor:   {r.effectiveArmorMM:F0} mm RHA\n" +
            $"  Pen ratio:    {r.penRatio * 100f:F0}%\n" +
            $"  Result:       {outcome}");
    }
}
