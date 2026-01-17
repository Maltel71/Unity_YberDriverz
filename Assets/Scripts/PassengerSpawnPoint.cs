using UnityEngine;

namespace Ezereal
{
    public class PassengerSpawnPoint : MonoBehaviour
    {
        [Header("Settings")]
        [SerializeField] private GameObject passengerPrefab;
        [SerializeField] private float spawnDelay = 5f;

        private GameObject currentPassenger;
        private float nextSpawnTime;

        private void Start()
        {
            SpawnPassenger();
        }

        private void Update()
        {
            // Check if spawn point is empty and cooldown is done
            if (currentPassenger == null && Time.time >= nextSpawnTime)
            {
                SpawnPassenger();
            }
        }

        private void SpawnPassenger()
        {
            if (passengerPrefab != null)
            {
                currentPassenger = Instantiate(passengerPrefab, transform.position, transform.rotation);
                nextSpawnTime = Time.time + spawnDelay;
            }
        }
    }
}