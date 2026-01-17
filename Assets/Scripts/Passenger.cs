using UnityEngine;

namespace Ezereal
{
    public class Passenger : MonoBehaviour
    {
        [Header("Settings")]
        [SerializeField] private float pickupReward = 50f;
        [SerializeField] private float deliveryReward = 100f;

        [Header("State")]
        public bool isPickedUp = false;
        public Transform currentDestination;

        private void OnTriggerEnter(Collider other)
        {
            if (!isPickedUp)
            {
                // Check if the collider or its root parent has "Player" tag
                Transform checkTransform = other.transform;
                while (checkTransform != null)
                {
                    if (checkTransform.CompareTag("Player"))
                    {
                        PickupPassenger();
                        return;
                    }
                    checkTransform = checkTransform.parent;
                }
            }
        }

        private void PickupPassenger()
        {
            isPickedUp = true;

            // Choose random destination
            Destination[] destinations = FindObjectsOfType<Destination>();
            if (destinations.Length > 0)
            {
                currentDestination = destinations[Random.Range(0, destinations.Length)].transform;
                Debug.Log("Destination set to: " + currentDestination.name);
            }
            else
            {
                Debug.LogWarning("No destinations found in scene!");
            }

            // Add pickup money
            if (GameManager.Instance != null)
            {
                GameManager.Instance.AddMoney(pickupReward);
            }

            // Hide entire GameObject (passenger is now "in the car")
            gameObject.SetActive(false);

            Debug.Log("Passenger picked up!");
        }

        public void CompleteDelivery()
        {
            if (GameManager.Instance != null)
            {
                GameManager.Instance.AddMoney(deliveryReward);
            }

            currentDestination = null;
            Destroy(gameObject);

            Debug.Log("Delivery complete!");
        }
    }
}