using UnityEngine;

namespace OnlyMyGame.Runtime
{
    [CreateAssetMenu(menuName = "OnlyMyGame/Presentation Catalog")]
    public sealed class GamePresentationCatalog : ScriptableObject
    {
        // ==================== 지형 타일 ====================
        // Imported FBX model roots are serialized by Unity as Prefab assets. Keeping
        // these as Object avoids a GameObject/Prefab type mismatch while loading.
        public Object grassTile;
        public Object waterTile;
        public Object forestTile;
        public Object hillTile;

        // ---- 지형 변형 (강/해안/도로) ----
        public Object riverTileA;
        public Object riverTileB;
        public Object riverTileC;
        public Object coastTileA;
        public Object coastTileB;
        public Object roadTileA;
        public Object roadTileB;

        // ==================== 유닛 ====================
        public Object playerUnit;        // 기사 (플레이어)
        public Object skeletonUnit;      // 스켈레톤 (적)
        public Object neutralUnit;       // 마법사 (중립)
        public Object rangerUnit;        // 레인저
        public Object rogueUnit;         // 도적
        public Object barbarianUnit;     // 야만인
        public Object skeletonMageUnit;  // 스켈레톤 메이지
        public Object skeletonRogueUnit; // 스켈레톤 도적

        // ==================== 건물 ====================
        public Object playerHeadquarters;   // 플레이어 성
        public Object enemyHeadquarters;    // 적 성
        public Object settlement;           // 중립 정착지
        public Object lumbermill;           // 제재소
        public Object market;               // 시장
        public Object blacksmith;           // 대장간
        public Object barracks;             // 병영
        public Object tower;                // 감시탑
        public Object church;               // 교회
        public Object home;                 // 주택
        public Object well;                 // 우물
        public Object windmill;             // 풍차

        // ==================== 장식 ====================
        public Object treeDecoration;
        public Object rockDecoration;
        public Object mountainDecoration;
        public Object hillDecoration;
        public Object waterlilyDecoration;
        public Object waterplantDecoration;
        public Object cloudDecoration;

        // ==================== 자원 ====================
        public Object resourceWood;
        public Object resourceStone;
        public Object resourceIron;
        public Object resourceFood;
        public Object resourceGold;
        public Object resourceCopper;
        public Object resourceTextile;

        // ==================== 깃발 ====================
        public Object flagPlayer;
        public Object flagEnemy;
        public Object flagNeutral;

        // ==================== 소품 ====================
        public Object propBarrel;
        public Object propCrate;
        public Object propSack;
        public Object propTent;
        public Object propLadder;
        public Object propWheelbarrow;
        public Object propWeaponrack;
        public Object propTarget;
    }
}