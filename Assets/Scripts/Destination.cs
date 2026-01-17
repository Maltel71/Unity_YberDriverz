using UnityEngine;

namespace Ezereal
{
    public class Destination : MonoBehaviour
    {
        private MeshRenderer meshRenderer;

        private void Start()
        {
            // Get mesh renderer from child
            Transform meshChild = transform.Find("Mesh");
            if (meshChild != null)
            {
                meshRenderer = meshChild.GetComponent<MeshRenderer>();
            }

            // Start hidden
            SetMeshActive(false);
        }

        private void Update()
        {
            // Find all passengers (including inactive ones)
            Passenger[] allPassengers = FindObjectsOfType<Passenger>(true);

            bool isActiveDestination = false;
            foreach (Passenger passenger in allPassengers)
            {
                if (passenger.isPickedUp && passenger.currentDestination == transform)
                {
                    isActiveDestination = true;
                    break;
                }
            }

            SetMeshActive(isActiveDestination);
        }

        private void OnTriggerEnter(Collider other)
        {
            // Check if the collider or its root parent has "Player" tag
            Transform checkTransform = other.transform;
            while (checkTransform != null)
            {
                if (checkTransform.CompareTag("Player"))
                {
                    // Find all passengers (including inactive ones)
                    Passenger[] allPassengers = FindObjectsOfType<Passenger>(true);

                    foreach (Passenger passenger in allPassengers)
                    {
                        if (passenger.isPickedUp && passenger.currentDestination == transform)
                        {
                            passenger.CompleteDelivery();
                            return;
                        }
                    }
                    return;
                }
                checkTransform = checkTransform.parent;
            }
        }

        private void SetMeshActive(bool isActive)
        {
            if (meshRenderer != null)
            {
                meshRenderer.enabled = isActive;
            }
        }
    }
}