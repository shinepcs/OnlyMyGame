using UnityEngine;

public class MoveBoxOnTouch : MonoBehaviour
{
    [SerializeField]
    private Camera mainCamera;
    
    [SerializeField]
    private float moveSpeed = 5f; // 이동 속도
    
    private Vector3 targetPosition;
    private bool isMoving = false;

    void Start()
    {
        // 메인 카메라 자동 설정
        if (mainCamera == null)
        {
            mainCamera = Camera.main;
        }
        
        // 초기 위치를 타겟으로 설정
        targetPosition = transform.position;
    }

    void Update()
    {
        // 터치 또는 마우스 클릭 감지
        if (Input.GetMouseButtonDown(0))
        {
            HandleInput(Input.mousePosition);
        }

        // 타겟 위치로 이동
        if (isMoving)
        {
            MoveToTarget();
        }
    }

    void HandleInput(Vector3 screenPosition)
    {
        // 카메라가 없으면 입력 무시
        if (mainCamera == null)
        {
            Debug.LogWarning("Main camera not found. Cannot move box.");
            return;
        }
        
        // 스크린 좌표를 월드 좌표로 변환
        Ray ray = mainCamera.ScreenPointToRay(screenPosition);
        
        // Z축 거리를 카메라에서 큐브까지의 거리로 설정
        float distanceToCamera = Mathf.Abs(transform.position.z - mainCamera.transform.position.z);
        Vector3 worldPosition = ray.GetPoint(distanceToCamera);
        
        // Y축은 현재 큐브의 높이 유지
        targetPosition = new Vector3(worldPosition.x, transform.position.y, transform.position.z);
        isMoving = true;

        // 배경 색상을 랜덤하게 변경
        mainCamera.backgroundColor = new Color(Random.value, Random.value, Random.value);
    }

    void MoveToTarget()
    {
        // 부드럽게 타겟 위치로 이동
        transform.position = Vector3.Lerp(transform.position, targetPosition, moveSpeed * Time.deltaTime);
        
        // 목표 위치에 거의 도달하면 이동 완료
        if (Vector3.Distance(transform.position, targetPosition) < 0.01f)
        {
            transform.position = targetPosition;
            isMoving = false;
        }
    }
}
