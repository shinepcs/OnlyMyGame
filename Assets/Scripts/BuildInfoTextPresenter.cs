using UnityEngine;
using UnityEngine.UI;

public class BuildInfoTextPresenter : MonoBehaviour
{
    [SerializeField] private Text targetText;

    private void Awake()
    {
        if (targetText == null)
        {
            targetText = GetComponent<Text>();
        }

        ShowBuildInfo();
    }

    private void ShowBuildInfo()
    {
        TextAsset buildInfoAsset = Resources.Load<TextAsset>("BuildInfo");

        if (targetText == null)
        {
            Debug.LogWarning("BuildInfoTextPresenter: targetText is not assigned.");
            return;
        }

        if (buildInfoAsset == null)
        {
            targetText.text = "Build info not found";
            Debug.LogWarning("BuildInfoTextPresenter: Resources/BuildInfo.txt not found.");
            return;
        }

        targetText.text = "info : " + buildInfoAsset.text;
    }
}
