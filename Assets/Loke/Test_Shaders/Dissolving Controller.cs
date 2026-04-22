using UnityEngine;
using UnityEngine.InputSystem;
using System.Collections;

public class DissolvingController : MonoBehaviour
{
    public MeshRenderer MeshRender;
    public float dissolveRate = 0.0125f;
    public float refreshRate = 0.025f;

    private Material[] Materials;

    void Start()
    {
        if (MeshRender != null)
            Materials = MeshRender.materials;
    }

    void Update()
    {
        if (Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame)
        {
            StartCoroutine(DissolveCo());
        }
    }

    IEnumerator DissolveCo()
    {
        if (Materials.Length > 0)
        {
            float counter = 0;

            while (Materials[0].GetFloat("_Dissolve_Amount") < 1)
            {
                counter += dissolveRate;
                for (int i = 0; i < Materials.Length; i++)
                {
                    Materials[i].SetFloat("_Dissolve_Amount", counter);
                }
                yield return new WaitForSeconds(refreshRate);
            }
        }
    }
}