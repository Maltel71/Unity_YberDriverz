using UnityEngine;
using UnityEngine.VFX;

// =============================================================================
//  CannonProjectile.cs  -  Unity 6
//
//  Physical cannon projectile. Spawned by CannonController.
//  CannonController sets all damage/VFX data via Initialize() right after
//  instantiation, then applies velocity.
//
//  Setup (Projectile Prefab):
//    1. Create a GameObject with a visible mesh (e.g. a stretched capsule/cylinder).
//    2. Add a Rigidbody: set Mass low (e.g. 5-20 kg), Drag = 0, use Gravity.
//    3. Add a Collider (CapsuleCollider or SphereCollider).
//    4. Attach this script.
//    5. Put the projectile on its own layer and exclude the tank layer from
//       collisions in Edit > Project Settings > Physics.
// =============================================================================

public class CannonProjectile : MonoBehaviour
{
    // Set by CannonController.Initialize() - do not set these in the Inspector
    [HideInInspector] public float   damage;
    [HideInInspector] public float   impactRadius;
    [HideInInspector] public float   explosionForce;
    [HideInInspector] public float   explosionUpwardModifier;
    [HideInInspector] public LayerMask hitLayers;
    [HideInInspector] public bool    useExplosionVFX;

    [HideInInspector] public VisualEffect impactVFXPrefab;
    [HideInInspector] public float        impactVFXLifetime;
    [HideInInspector] public VisualEffect explosionVFXPrefab;
    [HideInInspector] public float        explosionVFXLifetime;

    [HideInInspector] public float lifetime = 10f;

    // ─────────────────────────────────────────────────────────────────────────

    private bool hasImpacted = false;

    private void Start()
    {
        Destroy(gameObject, lifetime);
    }

    private void OnCollisionEnter(Collision col)
    {
        if (hasImpacted) return;
        hasImpacted = true;

        ContactPoint contact = col.GetContact(0);
        HandleImpact(contact.point, contact.normal, col.collider);

        Destroy(gameObject);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Impact
    // ─────────────────────────────────────────────────────────────────────────

    private void HandleImpact(Vector3 hitPoint, Vector3 hitNormal, Collider hitCollider)
    {
        // Direct hit damage
        hitCollider.SendMessageUpwards("TakeDamage", damage,
                                       SendMessageOptions.DontRequireReceiver);

        // Area damage + explosion force
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

        // Impact VFX
        if (impactVFXPrefab != null)
        {
            VisualEffect fx = Instantiate(impactVFXPrefab, hitPoint,
                                          Quaternion.LookRotation(hitNormal));
            fx.Play();
            Destroy(fx.gameObject, impactVFXLifetime);
        }

        // Explosion VFX
        if (useExplosionVFX && explosionVFXPrefab != null && impactRadius > 0f)
        {
            VisualEffect fx = Instantiate(explosionVFXPrefab, hitPoint,
                                          Quaternion.identity);
            fx.Play();
            Destroy(fx.gameObject, explosionVFXLifetime);
        }
    }
}
