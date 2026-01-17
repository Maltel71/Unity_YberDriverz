using UnityEngine;

namespace Ezereal
{
    public class DirectionArrow : MonoBehaviour
    {
        [Header("Settings")]
        [SerializeField] private Transform arrowMesh; // Assign the mesh child here
        [SerializeField] private bool hideWhenNoDestination = true;
        [SerializeField] private float rotationSpeed = 10f;
        [SerializeField] private float searchInterval = 0.5f;

        private Transform currentDestination;
        private float nextSearchTime;

        private void Start()
        {
            if (arrowMesh != null && hideWhenNoDestination)
            {
                arrowMesh.gameObject.SetActive(false);
            }
        }

        private void LateUpdate()
        {
            // Only search for passenger occasionally
            if (Time.time >= nextSearchTime)
            {
                currentDestination = FindActiveDestination();
                nextSearchTime = Time.time + searchInterval;
            }

            if (currentDestination != null && arrowMesh != null)
            {
                // Show arrow
                if (hideWhenNoDestination)
                {
                    arrowMesh.gameObject.SetActive(true);
                }

                // Calculate direction from parent position (not mesh position)
                Vector3 direction = currentDestination.position - transform.position;
                direction.y = 0;

                if (direction.sqrMagnitude > 0.01f)
                {
                    Quaternion lookRotation = Quaternion.LookRotation(direction);
                    arrowMesh.rotation = Quaternion.Slerp(arrowMesh.rotation, lookRotation, Time.deltaTime * rotationSpeed);
                }
            }
            else
            {
                // Hide arrow
                if (arrowMesh != null && hideWhenNoDestination)
                {
                    arrowMesh.gameObject.SetActive(false);
                }
            }
        }

        private Transform FindActiveDestination()
        {
            Passenger[] passengers = FindObjectsOfType<Passenger>(true);
            foreach (Passenger passenger in passengers)
            {
                if (passenger.isPickedUp && passenger.currentDestination != null)
                {
                    return passenger.currentDestination;
                }
            }
            return null;
        }
    }
}