using UnityEngine;
using UnityEngine.VFX;

// =============================================================================
//  CannonProjectile.cs  -  Unity 6  (Module System Integration)
//
//  Physical cannon projectile with armor penetration and module damage routing.
//  Spawned and fully configured by CannonController immediately after Instantiate.
//
//  ── Collision detection strategy ─────────────────────────────────────────────
//
//  HIGH-SPEED PROBLEM
//  ------------------
//  At muzzle velocities of 800–2000 m/s and a typical Unity fixed timestep of
//  0.02 s, a projectile travels 16–40 metres per physics step.  Unity's default
//  discrete collision detection only tests for overlap at the START and END of
//  each step, so:
//    • A thin armor plate (0.2–2 m) is traversed entirely within one step.
//    • Unity may detect an overlap, but the reported ContactPoint is at the
//      END position — already deep inside (or past) the target geometry.
//    • Alternatively, if the projectile starts before and ends past the
//      collider, the broadphase may miss it entirely (tunneling).
//  Both failures cause VFX/hit markers to appear inside objects.
//
//  PRIMARY FIX — FixedUpdate sweep raycast
//  ----------------------------------------
//  Every physics step, cast a ray from the position at the PREVIOUS step to the
//  position at the CURRENT step.  Physics.Raycast performs a mathematical
//  ray-triangle intersection and returns the exact surface entry point
//  (RaycastHit.point) regardless of projectile speed.  This is surface-accurate
//  for any velocity and is the industry-standard approach used by AAA military
//  shooters for kinetic penetrators.
//
//  SECONDARY FALLBACK — OnCollisionEnter (surface-corrected)
//  ----------------------------------------------------------
//  Kept for very slow projectiles or cases where the sweep ray misses (e.g.
//  the shell spawns overlapping the collider on frame 0).  The contact point is
//  corrected by casting a short ray backward along the impact normal to find the
//  actual surface before using it for VFX.
//
//  detectCollisions gap fix
//  ------------------------
//  The original code set detectCollisions = false in Awake and restored it in
//  Start to avoid a Unity 6 crash when instantiating inside a collider.  Start
//  does not run until the next frame, so during the very first FixedUpdate the
//  shell was already at full speed with collision detection off.  The fix:
//    1. Keep detectCollisions = false in Awake (crash prevention is still valid).
//    2. Restore it in Awake immediately after a Physics.SyncTransforms call is
//       NOT needed here — instead we restore it in Start but also add an early-
//       exit guard (_sweepReady) that prevents the sweep from firing until the
//       shell has moved at least one step away from its spawn point, avoiding
//       false positives at the barrel tip.
//
//  CollisionDetectionMode
//  ----------------------
//  Set to ContinuousDynamic at runtime so Unity's own broad-phase also sweeps
//  the shape between steps.  This is a belt-and-suspenders measure on top of the
//  manual raycast sweep.
//
//  ── Collision priority (unchanged from original) ─────────────────────────────
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

    // ── Sweep raycast — extra margin added to the ray length ─────────────────
    //
    // The ray extends slightly BEYOND the current position to catch the case
    // where the projectile's centre has just crossed a surface and the next
    // FixedUpdate hasn't run yet.  A value of 0.1 m is enough for any collider
    // that is physically thicker than that (all tank armor is).  Raise this
    // value if you add very thin decal or trigger colliders to the scene.
    [HideInInspector] public float sweepOvershoot = 0.1f;

    // ─────────────────────────────────────────────────────────────────────────
    //  Private
    // ─────────────────────────────────────────────────────────────────────────

    private Rigidbody rb;
    private bool      hasImpacted  = false;

    // Position at the END of the previous FixedUpdate — used as the sweep origin.
    private Vector3 _prevPosition;

    // Guard that prevents the sweep from firing during the very first physics
    // step (when the shell is still at/near its spawn point and detectCollisions
    // is being re-enabled).  Set to true at the end of the first FixedUpdate.
    private bool _sweepReady = false;

    // ─────────────────────────────────────────────────────────────────────────
    //  Lifecycle
    // ─────────────────────────────────────────────────────────────────────────

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();

        // Disable collision detection during instantiation to prevent Unity 6's
        // "Instantiate failed because the clone was destroyed during creation"
        // error.  This occurs when the projectile spawns inside / overlapping
        // another collider (e.g. the barrel tip inside the hull mesh), causing
        // OnCollisionEnter to fire synchronously during Instantiate — which
        // then calls Destroy(gameObject) before Instantiate has returned.
        if (rb != null)
        {
            rb.detectCollisions = false;

            // Enable swept collision mode so Unity's own broadphase also sweeps
            // the shape between physics steps.  This works alongside the manual
            // sweep raycast — not as a replacement for it.
            //
            // NOTE: ContinuousDynamic is more expensive than Discrete.  If you
            // have many simultaneous projectiles, profile first.  For a single
            // player cannon this is negligible.
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        }
    }

    private void Start()
    {
        // Re-enable collision detection now that Instantiate has fully
        // completed and the projectile has moved at least one Unity frame away
        // from the barrel tip.
        if (rb != null) rb.detectCollisions = true;

        // Seed the sweep origin with the current position so the first sweep
        // step in FixedUpdate has a valid start.
        _prevPosition = transform.position;

        Destroy(gameObject, lifetime);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  PRIMARY DETECTION — FixedUpdate sweep raycast
    // ─────────────────────────────────────────────────────────────────────────

    private void FixedUpdate()
    {
        if (hasImpacted) return;

        Vector3 currentPos = transform.position;

        // Skip the very first step — Start may not have run yet and
        // _prevPosition has not been seeded.  After this guard, _sweepReady is
        // true for all subsequent steps.
        if (!_sweepReady)
        {
            _prevPosition = currentPos;
            _sweepReady   = true;
            return;
        }

        Vector3 delta    = currentPos - _prevPosition;
        float   distance = delta.magnitude;

        if (distance > 0.001f)   // ignore numerical jitter when nearly stationary
        {
            Vector3 sweepDir    = delta / distance;             // normalised
            float   sweepLength = distance + sweepOvershoot;    // extend past current pos

            // Cast from where we WERE to where we ARE (plus overshoot).
            // RaycastHit.point is the exact surface intersection — always on the
            // face of the collider, never inside it.
            if (Physics.Raycast(_prevPosition, sweepDir, out RaycastHit hit,
                                sweepLength, hitLayers, QueryTriggerInteraction.Ignore))
            {
                ProcessHit(hit.point, hit.normal, hit.collider);
            }
        }

        _prevPosition = currentPos;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  SECONDARY FALLBACK — OnCollisionEnter (surface-corrected)
    // ─────────────────────────────────────────────────────────────────────────
    //
    //  Handles cases the sweep ray does not catch:
    //    • Very slow projectiles that barely moved between steps.
    //    • Spawn-frame overlaps where the shell starts inside a collider.
    //
    //  The raw ContactPoint is inside the mesh.  We correct it by casting a
    //  short confirmation ray backward along the impact normal to land exactly
    //  on the surface.

    private void OnCollisionEnter(Collision col)
    {
        if (hasImpacted) return;

        ContactPoint contact   = col.GetContact(0);
        Vector3      rawPoint  = contact.point;
        Vector3      hitNormal = contact.normal;

        // Surface-snap: cast backward along the normal from a point slightly
        // in front of (outside) the surface.  This guarantees we land on the
        // face of the collider, not inside its mesh.
        //
        //   rawPoint is at or slightly inside the surface.
        //   We step back 0.3 m along the normal (outward), then cast 0.5 m
        //   inward.  This works even if the contact is 0.2 m deep.
        const float snapOffset = 0.3f;
        const float snapLength = 0.5f;
        Vector3     snapOrigin = rawPoint + hitNormal * snapOffset;
        Vector3     snapDir    = -hitNormal;

        Vector3 surfacePoint = rawPoint;    // fallback if snap fails
        if (Physics.Raycast(snapOrigin, snapDir, out RaycastHit snapHit,
                            snapLength, hitLayers, QueryTriggerInteraction.Ignore))
        {
            // Confirm the snap hit the same collider we collided with.
            if (snapHit.collider == col.collider)
                surfacePoint = snapHit.point;
        }

        ProcessHit(surfacePoint, hitNormal, col.collider);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Unified hit processor
    // ─────────────────────────────────────────────────────────────────────────

    private void ProcessHit(Vector3 hitPoint, Vector3 hitNormal, Collider hitCollider)
    {
        if (hasImpacted) return;
        hasImpacted = true;

        float   speed    = rb != null ? rb.linearVelocity.magnitude : muzzleVelocity;
        Vector3 shellDir = rb != null ? rb.linearVelocity.normalized : transform.forward;

        TankModule module = hitCollider.GetComponent<TankModule>();
        ArmorPlate armor  = hitCollider.GetComponent<ArmorPlate>();

        if (module != null)
            HandleModuleHit(module, armor, hitPoint, hitNormal, shellDir, speed);
        else if (armor != null)
            HandleArmorHit(armor, hitPoint, hitNormal, shellDir, speed);
        else
            HandleLegacyImpact(hitCollider, hitPoint, hitNormal);

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
