using UnityEngine;

namespace OnlyMyGame.Runtime
{
    [CreateAssetMenu(menuName = "OnlyMyGame/Presentation Catalog")]
    public sealed class GamePresentationCatalog : ScriptableObject
    {
        public GameObject grassTile;
        public GameObject waterTile;
        public GameObject playerUnit;
        public GameObject skeletonUnit;
        public GameObject neutralUnit;
        public GameObject playerHeadquarters;
        public GameObject enemyHeadquarters;
        public GameObject settlement;
    }
}
