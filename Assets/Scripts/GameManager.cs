using UnityEngine;
using TMPro;

namespace Ezereal
{
    public class GameManager : MonoBehaviour
    {
        public static GameManager Instance;

        [Header("UI")]
        [SerializeField] private TMP_Text moneyText;

        [Header("Game State")]
        [SerializeField] private float currentMoney = 0f;

        private void Awake()
        {
            if (Instance == null)
            {
                Instance = this;
            }
            else
            {
                Destroy(gameObject);
            }

            UpdateMoneyUI();
        }

        public void AddMoney(float amount)
        {
            currentMoney += amount;
            UpdateMoneyUI();
        }

        private void UpdateMoneyUI()
        {
            if (moneyText != null)
            {
                moneyText.text = "$" + currentMoney.ToString("F0");
            }
        }
    }
}