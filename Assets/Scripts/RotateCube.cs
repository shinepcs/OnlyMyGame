using UnityEngine;

public class RotateCube : MonoBehaviour
{
    [SerializeField]
    private Vector3 rotationSpeed = new Vector3(0, 50, 0); // Y축을 중심으로 초당 50도 회전

    void Update()
    {
        // Time.deltaTime을 곱하여 프레임 독립적인 회전 구현
        transform.Rotate(rotationSpeed * Time.deltaTime);
    }
}
