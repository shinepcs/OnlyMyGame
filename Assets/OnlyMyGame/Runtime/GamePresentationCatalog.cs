using UnityEngine;

namespace OnlyMyGame.Runtime
{
    [CreateAssetMenu(menuName = "OnlyMyGame/Presentation Catalog")]
    public sealed class GamePresentationCatalog : ScriptableObject
    {
        // Imported FBX model roots are serialized by Unity as Prefab assets. Keeping
        // these as Object avoids a GameObject/Prefab type mismatch while loading.
        public Object grassTile;
        public Object waterTile;
        public Object playerUnit;
        public Object skeletonUnit;
        public Object neutralUnit;
        public Object playerHeadquarters;
        public Object enemyHeadquarters;
        public Object settlement;
    }
}
