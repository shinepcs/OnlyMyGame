using System;
using System.Collections.Generic;
using System.Linq;
using OnlyMyGame.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using RuleEventType = OnlyMyGame.Core.EventType;

namespace OnlyMyGame.Runtime
{
    /// <summary>
    /// Runtime-built, resolution-independent game HUD. Keeping the layout in code makes
    /// the WebGL presentation deterministic and prevents broken scene references from
    /// turning the game into an invisible debug prototype.
    /// </summary>
    public sealed class CommercialGameHud : MonoBehaviour
    {
        private enum ModalMode
        {
            None,
            Blocked,
            RuleAnnouncement,
            NewRunConfirmation
        }

        private const int DynamicActionSlotCount = 3;
        private static readonly Color Ink = new Color(0.035f, 0.055f, 0.09f, 0.97f);
        private static readonly Color Panel = new Color(0.075f, 0.105f, 0.16f, 0.94f);
        private static readonly Color PanelLight = new Color(0.11f, 0.15f, 0.22f, 0.96f);
        private static readonly Color Gold = new Color(1f, 0.78f, 0.28f, 1f);
        private static readonly Color Cyan = new Color(0.2f, 0.78f, 0.92f, 1f);
        private static readonly Color Green = new Color(0.35f, 0.82f, 0.5f, 1f);
        private static readonly Color Red = new Color(0.95f, 0.3f, 0.32f, 1f);
        private static readonly Color Muted = new Color(0.68f, 0.73f, 0.8f, 1f);

        private readonly Dictionary<CommandType, Button> actionButtons = new Dictionary<CommandType, Button>();
        private readonly List<Button> dynamicActionButtons = new List<Button>();
        private readonly List<TextMeshProUGUI> dynamicActionLabels = new List<TextMeshProUGUI>();
        private readonly List<DynamicActionV1> displayedDynamicActions = new List<DynamicActionV1>();
        private readonly Dictionary<BuildingType, Button> buildTypeButtons = new Dictionary<BuildingType, Button>();
        private readonly Dictionary<BuildingType, TextMeshProUGUI> buildTypeLabels = new Dictionary<BuildingType, TextMeshProUGUI>();
        private GameController controller;
        private TMP_FontAsset font;
        private GameObject interfaceRoot;
        private GameObject mainMenu;
        private GameObject pauseMenu;
        private GameObject ledgerPanel;
        private GameObject buildPickerPanel;
        private GameObject modalPanel;
        private GameObject outcomePanel;
        private TextMeshProUGUI headline;
        private TextMeshProUGUI luckBadge;
        private TextMeshProUGUI resources;
        private TextMeshProUGUI objective;
        private TextMeshProUGUI relations;
        private TextMeshProUGUI rules;
        private TextMeshProUGUI journal;
        private TextMeshProUGUI selectionTitle;
        private TextMeshProUGUI selectionBody;
        private TextMeshProUGUI selectionHint;
        private TextMeshProUGUI dynamicActionSummary;
        private TextMeshProUGUI queue;
        private TextMeshProUGUI planningHint;
        private TextMeshProUGUI service;
        private TextMeshProUGUI endTurnLabel;
        private TextMeshProUGUI toast;
        private TextMeshProUGUI modalTitle;
        private TextMeshProUGUI modalBody;
        private TextMeshProUGUI outcomeTitle;
        private TextMeshProUGUI outcomeBody;
        private TextMeshProUGUI continueLabel;
        private TextMeshProUGUI mainMenuStatus;
        private Image spFill;
        private Button continueButton;
        private Button previousRunButton;
        private Button endTurnButton;
        private Button undoButton;
        private Button clearButton;
        private Button modalRetryButton;
        private Button modalSaveButton;
        private Button modalAcceptButton;
        private Button modalCancelButton;
        private TextMeshProUGUI ledgerBody;
        private GameObject toastPanel;
        private Action<DynamicActionV1> dynamicActionHandler;
        private ModalMode modalMode;
        private float toastUntil;
        private bool initialized;

        public void Initialize(GameController gameController)
        {
            if (initialized) return;
            initialized = true;
            controller = gameController;
            font = Resources.Load<TMP_FontAsset>("Fonts/NanumGothic-Regular SDF");
            if (font == null)
            {
                throw new InvalidOperationException("NanumGothic TMP font asset is required for crisp WebGL UI text.");
            }

            var canvas = GetComponent<Canvas>();
            if (canvas == null) canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 40;
            var scaler = GetComponent<CanvasScaler>() ?? gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
            if (GetComponent<GraphicRaycaster>() == null) gameObject.AddComponent<GraphicRaycaster>();
            transform.localScale = Vector3.one;

            foreach (Transform child in transform) child.gameObject.SetActive(false);
            interfaceRoot = CreateObject("CommercialHud", transform);
            Stretch(interfaceRoot.GetComponent<RectTransform>());
            BuildPersistentHud();
            BuildMainMenu();
            BuildPauseMenu();
            BuildLedger();
            BuildBuildPicker();
            BuildModal();
            BuildOutcome();
            dynamicActionHandler = controller.RunDynamicFromHud;
            Render();
        }

        private void BuildPersistentHud()
        {
            var top = PanelObject("TopBar", interfaceRoot.transform, new Color(0.025f, 0.04f, 0.075f, 0.96f));
            Anchor(top, new Vector2(0, 1), new Vector2(1, 1), Vector2.zero, new Vector2(0, 82), new Vector2(0.5f, 1));
            headline = Label("Headline", top.transform, "ONLY MY GAME", 24, Gold, TextAnchor.MiddleLeft, FontStyle.Bold);
            Anchor(headline.gameObject, new Vector2(0, 0), new Vector2(0, 1), new Vector2(24, 0), new Vector2(275, 0), new Vector2(0, 0.5f));
            luckBadge = Label("LuckBadge", top.transform, "☀  행운 --", 17, Gold, TextAnchor.MiddleCenter, FontStyle.Bold);
            Anchor(luckBadge.gameObject, new Vector2(0, 0), new Vector2(0, 1), new Vector2(300, 0), new Vector2(142, 0), new Vector2(0, 0.5f));
            resources = Label("Resources", top.transform, string.Empty, 19, Color.white, TextAnchor.MiddleCenter, FontStyle.Normal);
            Anchor(resources.gameObject, new Vector2(0.23f, 0), new Vector2(0.79f, 1), Vector2.zero, Vector2.zero, new Vector2(0.5f, 0.5f));
            service = Label("Service", top.transform, "AI 연결 확인 중", 14, Muted, TextAnchor.MiddleRight, FontStyle.Normal);
            Anchor(service.gameObject, new Vector2(1, 0), new Vector2(1, 1), new Vector2(-248, 0), new Vector2(150, 0), new Vector2(1, 0.5f));
            var help = ButtonObject("Help", top.transform, "도움말", new Color(0.16f, 0.22f, 0.31f, 1), () => ToggleLedger(true));
            Anchor(help.gameObject, new Vector2(1, 0.5f), new Vector2(1, 0.5f), new Vector2(-158, 0), new Vector2(92, 42), new Vector2(0.5f, 0.5f));
            var menu = ButtonObject("Menu", top.transform, "메뉴", new Color(0.16f, 0.22f, 0.31f, 1), TogglePause);
            Anchor(menu.gameObject, new Vector2(1, 0.5f), new Vector2(1, 0.5f), new Vector2(-58, 0), new Vector2(80, 42), new Vector2(0.5f, 0.5f));

            var left = PanelObject("MissionPanel", interfaceRoot.transform, Panel);
            Anchor(left, new Vector2(0, 1), new Vector2(0, 1), new Vector2(18, -100), new Vector2(340, 506), new Vector2(0, 1));
            SectionHeader(left.transform, "원정 브리핑", Gold, 16);
            objective = Label("Objective", left.transform, string.Empty, 17, Color.white, TextAnchor.UpperLeft, FontStyle.Bold);
            Anchor(objective.gameObject, new Vector2(0, 1), new Vector2(1, 1), new Vector2(18, -62), new Vector2(-36, 120), new Vector2(0, 1));
            relations = Label("Relations", left.transform, string.Empty, 15, Muted, TextAnchor.UpperLeft, FontStyle.Normal);
            Anchor(relations.gameObject, new Vector2(0, 1), new Vector2(1, 1), new Vector2(18, -188), new Vector2(-36, 105), new Vector2(0, 1));
            rules = Label("Rules", left.transform, string.Empty, 14, new Color(0.9f, 0.85f, 0.66f), TextAnchor.UpperLeft, FontStyle.Normal);
            Anchor(rules.gameObject, new Vector2(0, 1), new Vector2(1, 1), new Vector2(18, -302), new Vector2(-36, 145), new Vector2(0, 1));
            var ledger = ButtonObject("LedgerButton", left.transform, "규칙 장부 열기  [Tab]", new Color(0.22f, 0.28f, 0.37f, 1), () => ToggleLedger(true));
            Anchor(ledger.gameObject, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0, 24), new Vector2(294, 42), new Vector2(0.5f, 0));

            var right = PanelObject("SelectionPanel", interfaceRoot.transform, Panel);
            Anchor(right, new Vector2(1, 1), new Vector2(1, 1), new Vector2(-18, -100), new Vector2(372, 636), new Vector2(1, 1));
            selectionTitle = Label("SelectionTitle", right.transform, "원정대 선택", 21, Cyan, TextAnchor.MiddleLeft, FontStyle.Bold);
            Anchor(selectionTitle.gameObject, new Vector2(0, 1), new Vector2(1, 1), new Vector2(18, -18), new Vector2(-36, 40), new Vector2(0, 1));
            selectionBody = Label("SelectionBody", right.transform, string.Empty, 15, Color.white, TextAnchor.UpperLeft, FontStyle.Normal);
            Anchor(selectionBody.gameObject, new Vector2(0, 1), new Vector2(1, 1), new Vector2(18, -64), new Vector2(-36, 100), new Vector2(0, 1));
            selectionHint = Label("SelectionHint", right.transform, string.Empty, 13, Gold, TextAnchor.MiddleLeft, FontStyle.Bold);
            Anchor(selectionHint.gameObject, new Vector2(0, 1), new Vector2(1, 1), new Vector2(18, -150), new Vector2(-36, 26), new Vector2(0, 1));
            BuildActionGrid(right.transform);
            BuildDynamicActions(right.transform);

            var bottom = PanelObject("PlanningPanel", interfaceRoot.transform, PanelLight);
            Anchor(bottom, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0, 18), new Vector2(820, 184), new Vector2(0.5f, 0));
            planningHint = Label("PlanningHint", bottom.transform, string.Empty, 15, Gold, TextAnchor.MiddleLeft, FontStyle.Bold);
            Anchor(planningHint.gameObject, new Vector2(0, 1), new Vector2(1, 1), new Vector2(18, -12), new Vector2(-36, 30), new Vector2(0, 1));
            var spTrack = PanelObject("SpTrack", bottom.transform, new Color(0.02f, 0.035f, 0.055f, 1));
            Anchor(spTrack, new Vector2(0, 1), new Vector2(0, 1), new Vector2(18, -48), new Vector2(510, 12), new Vector2(0, 1));
            var fill = PanelObject("SpFill", spTrack.transform, Cyan);
            Stretch(fill);
            spFill = fill.GetComponent<Image>();
            spFill.type = Image.Type.Filled;
            spFill.fillMethod = Image.FillMethod.Horizontal;
            queue = Label("Queue", bottom.transform, string.Empty, 14, Color.white, TextAnchor.UpperLeft, FontStyle.Normal);
            Anchor(queue.gameObject, new Vector2(0, 1), new Vector2(0, 1), new Vector2(18, -70), new Vector2(510, 92), new Vector2(0, 1));
            undoButton = ButtonObject("Undo", bottom.transform, "마지막 취소", new Color(0.22f, 0.27f, 0.34f, 1), controller.UndoLastCommand);
            Anchor(undoButton.gameObject, new Vector2(1, 1), new Vector2(1, 1), new Vector2(-270, -24), new Vector2(124, 40), new Vector2(1, 1));
            clearButton = ButtonObject("Clear", bottom.transform, "전체 취소", new Color(0.27f, 0.22f, 0.25f, 1), controller.ClearCommands);
            Anchor(clearButton.gameObject, new Vector2(1, 1), new Vector2(1, 1), new Vector2(-136, -24), new Vector2(112, 40), new Vector2(1, 1));
            endTurnButton = ButtonObject("EndTurn", bottom.transform, "", new Color(0.11f, 0.55f, 0.68f, 1), controller.EndTurnFromHud);
            Anchor(endTurnButton.gameObject, new Vector2(1, 0), new Vector2(1, 0), new Vector2(-18, 18), new Vector2(246, 78), new Vector2(1, 0));
            endTurnLabel = endTurnButton.GetComponentInChildren<TextMeshProUGUI>();

            var journalPanel = PanelObject("JournalStrip", interfaceRoot.transform, new Color(0.04f, 0.065f, 0.1f, 0.88f));
            Anchor(journalPanel, new Vector2(0, 0), new Vector2(0, 0), new Vector2(18, 18), new Vector2(420, 190), new Vector2(0, 0));
            journal = Label("Journal", journalPanel.transform, string.Empty, 13, Muted, TextAnchor.LowerLeft, FontStyle.Normal);
            Anchor(journal.gameObject, Vector2.zero, Vector2.one, new Vector2(14, 12), new Vector2(-28, -24), new Vector2(0, 0));

            toastPanel = PanelObject("Toast", interfaceRoot.transform, new Color(0.04f, 0.08f, 0.13f, 0.97f));
            toastPanel.GetComponent<Image>().raycastTarget = false;
            Anchor(toastPanel, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -98), new Vector2(660, 50), new Vector2(0.5f, 1));
            toast = Label("Message", toastPanel.transform, string.Empty, 18, Color.white, TextAnchor.MiddleCenter, FontStyle.Bold);
            Stretch(toast.rectTransform);
            var outline = toast.gameObject.AddComponent<Outline>();
            outline.effectColor = new Color(0, 0, 0, 0.72f);
            outline.effectDistance = new Vector2(1, -1);
            toastPanel.SetActive(false);
        }

        private void BuildActionGrid(Transform parent)
        {
            var actions = new[]
            {
                CommandType.Move, CommandType.Gather, CommandType.Hunt,
                CommandType.Attack, CommandType.Trade, CommandType.Persuade,
                CommandType.Hire, CommandType.Build, CommandType.Upgrade, CommandType.Capture
            };
            for (var i = 0; i < actions.Length; i++)
            {
                var command = actions[i];
                var button = ButtonObject("Action_" + command, parent, ActionLabel(command), ActionColor(command), () => controller.BeginCommand(command));
                var column = i % 4;
                var row = i / 4;
                Anchor(button.gameObject, new Vector2(0, 1), new Vector2(0, 1), new Vector2(18 + column * 82, -184 - row * 66), new Vector2(76, 56), new Vector2(0, 1));
                actionButtons[command] = button;
            }
        }

        private void BuildDynamicActions(Transform parent)
        {
            var header = Label("DynamicHeader", parent, "AI 특수 행동", 14, Gold, TextAnchor.MiddleLeft, FontStyle.Bold);
            Anchor(header.gameObject, new Vector2(0, 1), new Vector2(1, 1), new Vector2(18, -378), new Vector2(-36, 26), new Vector2(0, 1));
            dynamicActionSummary = Label("DynamicSummary", parent, "세계 규칙이 새 행동을 해금할 수 있습니다.", 12, Muted, TextAnchor.UpperLeft, FontStyle.Normal);
            Anchor(dynamicActionSummary.gameObject, new Vector2(0, 1), new Vector2(1, 1), new Vector2(18, -408), new Vector2(-36, 38), new Vector2(0, 1));
            for (var i = 0; i < DynamicActionSlotCount; i++)
            {
                var slot = i;
                var button = ButtonObject("DynamicAction_" + i, parent, string.Empty, new Color(0.34f, 0.24f, 0.47f, 1), () => RequestDynamicAction(slot));
                Anchor(button.gameObject, new Vector2(0, 1), new Vector2(1, 1), new Vector2(18, -454 - i * 52), new Vector2(-36, 44), new Vector2(0, 1));
                var label = button.GetComponentInChildren<TextMeshProUGUI>();
                label.fontSize = 13;
                dynamicActionButtons.Add(button);
                dynamicActionLabels.Add(label);
                button.gameObject.SetActive(false);
            }
        }

        private void BuildMainMenu()
        {
            mainMenu = PanelObject("MainMenu", interfaceRoot.transform, new Color(0.015f, 0.025f, 0.05f, 0.9f));
            Stretch(mainMenu);
            var card = PanelObject("Card", mainMenu.transform, Ink);
            Anchor(card, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(660, 620), new Vector2(0.5f, 0.5f));
            var title = Label("Title", card.transform, "ONLY MY GAME", 48, Gold, TextAnchor.MiddleCenter, FontStyle.Bold);
            Anchor(title.gameObject, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, -68), new Vector2(0, 72), new Vector2(0.5f, 1));
            var subtitle = Label("Subtitle", card.transform, "매 턴, 세계의 규칙이 다시 쓰인다", 21, Color.white, TextAnchor.MiddleCenter, FontStyle.Normal);
            Anchor(subtitle.gameObject, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, -148), new Vector2(0, 42), new Vector2(0.5f, 1));
            var pitch = Label("Pitch", card.transform, "절차 생성 핵사곤을 탐험하고 자원을 모아 살아남으세요.\n당신의 선택을 읽은 AI가 다음 턴의 규칙과 승리 조건을 만듭니다.", 16, Muted, TextAnchor.MiddleCenter, FontStyle.Normal);
            Anchor(pitch.gameObject, new Vector2(0, 1), new Vector2(1, 1), new Vector2(38, -205), new Vector2(-76, 80), new Vector2(0.5f, 1));
            continueButton = ButtonObject("Continue", card.transform, "원정 계속하기", new Color(0.1f, 0.55f, 0.68f, 1), controller.ContinueRun);
            continueLabel = continueButton.GetComponentInChildren<TextMeshProUGUI>();
            Anchor(continueButton.gameObject, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0, 206), new Vector2(320, 62), new Vector2(0.5f, 0));
            var fresh = ButtonObject("NewRun", card.transform, "새 원정 시작", new Color(0.35f, 0.42f, 0.2f, 1), controller.StartNewRun);
            Anchor(fresh.gameObject, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0, 136), new Vector2(320, 58), new Vector2(0.5f, 0));
            previousRunButton = ButtonObject("PreviousRun", card.transform, "이전 원정 복구", new Color(0.28f, 0.28f, 0.38f, 1), controller.RestorePreviousRun);
            Anchor(previousRunButton.gameObject, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0, 72), new Vector2(320, 52), new Vector2(0.5f, 0));
            mainMenuStatus = Label("SaveStatus", card.transform, string.Empty, 13, Muted, TextAnchor.MiddleCenter, FontStyle.Normal);
            Anchor(mainMenuStatus.gameObject, new Vector2(0, 0), new Vector2(1, 0), new Vector2(28, 262), new Vector2(-56, 24), new Vector2(0.5f, 0));
            var controls = Label("Controls", card.transform, "마우스 선택 · WASD 카메라 · 휠 확대 · Space 턴 종료 · Tab 규칙 장부", 13, Muted, TextAnchor.MiddleCenter, FontStyle.Normal);
            Anchor(controls.gameObject, new Vector2(0, 0), new Vector2(1, 0), new Vector2(20, 20), new Vector2(-40, 36), new Vector2(0.5f, 0));
        }

        private void BuildPauseMenu()
        {
            pauseMenu = PanelObject("PauseMenu", interfaceRoot.transform, new Color(0.01f, 0.02f, 0.04f, 0.78f));
            Stretch(pauseMenu);
            var card = PanelObject("PauseCard", pauseMenu.transform, Ink);
            Anchor(card, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(440, 420), new Vector2(0.5f, 0.5f));
            var title = Label("PauseTitle", card.transform, "원정 일시 정지", 30, Gold, TextAnchor.MiddleCenter, FontStyle.Bold);
            Anchor(title.gameObject, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, -38), new Vector2(0, 52), new Vector2(0.5f, 1));
            var resume = ButtonObject("Resume", card.transform, "계속하기", new Color(0.1f, 0.55f, 0.68f, 1), TogglePause);
            Anchor(resume.gameObject, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0, 72), new Vector2(280, 54), new Vector2(0.5f, 0.5f));
            var center = ButtonObject("Center", card.transform, "원정대에 카메라 맞추기", new Color(0.2f, 0.27f, 0.36f, 1), () => { controller.FocusPlayer(); TogglePause(); });
            Anchor(center.gameObject, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0, 8), new Vector2(280, 54), new Vector2(0.5f, 0.5f));
            var openLedger = ButtonObject("OpenLedger", card.transform, "세계 규칙 장부", new Color(0.24f, 0.27f, 0.39f, 1), () => { pauseMenu.SetActive(false); ToggleLedger(true); });
            Anchor(openLedger.gameObject, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0, -56), new Vector2(280, 54), new Vector2(0.5f, 0.5f));
            var menu = ButtonObject("ToMenu", card.transform, "저장 후 메인 메뉴", new Color(0.3f, 0.24f, 0.25f, 1), controller.SaveAndReturnToMenu);
            Anchor(menu.gameObject, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0, -120), new Vector2(280, 54), new Vector2(0.5f, 0.5f));
            var hint = Label("PauseHint", card.transform, "Esc 계속하기 · 진행은 매 턴 자동 저장됩니다", 12, Muted, TextAnchor.MiddleCenter, FontStyle.Normal);
            Anchor(hint.gameObject, new Vector2(0, 0), new Vector2(1, 0), new Vector2(20, 18), new Vector2(-40, 28), new Vector2(0.5f, 0));
            pauseMenu.SetActive(false);
        }

        private void BuildLedger()
        {
            ledgerPanel = PanelObject("LedgerPanel", interfaceRoot.transform, new Color(0.01f, 0.02f, 0.04f, 0.84f));
            Stretch(ledgerPanel);
            var card = PanelObject("LedgerCard", ledgerPanel.transform, Ink);
            Anchor(card, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(900, 680), new Vector2(0.5f, 0.5f));
            var title = Label("LedgerTitle", card.transform, "세계 규칙 장부", 30, Gold, TextAnchor.MiddleLeft, FontStyle.Bold);
            Anchor(title.gameObject, new Vector2(0, 1), new Vector2(1, 1), new Vector2(34, -24), new Vector2(-68, 52), new Vector2(0, 1));
            var close = ButtonObject("Close", card.transform, "닫기  [Tab]", new Color(0.22f, 0.28f, 0.37f, 1), () => ToggleLedger(false));
            Anchor(close.gameObject, new Vector2(1, 1), new Vector2(1, 1), new Vector2(-28, -24), new Vector2(130, 44), new Vector2(1, 1));
            var scroll = CreateObject("LedgerScroll", card.transform);
            Anchor(scroll, Vector2.zero, Vector2.one, new Vector2(38, 42), new Vector2(-76, -128), new Vector2(0, 0));
            var viewport = CreateObject("Viewport", scroll.transform);
            Stretch(viewport.GetComponent<RectTransform>());
            var viewportImage = viewport.AddComponent<Image>();
            viewportImage.color = Color.clear;
            viewportImage.raycastTarget = true;
            viewport.AddComponent<RectMask2D>();
            ledgerBody = Label("LedgerBody", viewport.transform, string.Empty, 16, Color.white, TextAnchor.UpperLeft, FontStyle.Normal);
            var bodyRect = ledgerBody.rectTransform;
            bodyRect.anchorMin = new Vector2(0, 1);
            bodyRect.anchorMax = new Vector2(1, 1);
            bodyRect.pivot = new Vector2(0.5f, 1);
            bodyRect.anchoredPosition = Vector2.zero;
            bodyRect.sizeDelta = Vector2.zero;
            bodyRect.localScale = Vector3.one;
            ledgerBody.overflowMode = TextOverflowModes.Overflow;
            var fitter = ledgerBody.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var scrollRect = scroll.AddComponent<ScrollRect>();
            scrollRect.content = bodyRect;
            scrollRect.viewport = viewport.GetComponent<RectTransform>();
            scrollRect.horizontal = false;
            scrollRect.vertical = true;
            scrollRect.movementType = ScrollRect.MovementType.Elastic;
            scrollRect.scrollSensitivity = 34;
            ledgerPanel.SetActive(false);
        }

        private void BuildBuildPicker()
        {
            buildPickerPanel = PanelObject("BuildPicker", interfaceRoot.transform, new Color(0.01f, 0.02f, 0.04f, 0.86f));
            Stretch(buildPickerPanel);
            var card = PanelObject("BuildPickerCard", buildPickerPanel.transform, Ink);
            Anchor(card, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(820, 650), new Vector2(0.5f, 0.5f));
            var title = Label("BuildPickerTitle", card.transform, "건설할 건물을 선택하세요", 30, Gold, TextAnchor.MiddleCenter, FontStyle.Bold);
            Anchor(title.gameObject, new Vector2(0, 1), new Vector2(1, 1), new Vector2(34, -30), new Vector2(-68, 50), new Vector2(0.5f, 1));
            var hint = Label("BuildPickerHint", card.transform, "현재 또는 이동 예약 타일에 건설 · SP 3 · 표시 비용은 다른 명령의 예약분을 반영합니다", 15, Muted, TextAnchor.MiddleCenter, FontStyle.Normal);
            Anchor(hint.gameObject, new Vector2(0, 1), new Vector2(1, 1), new Vector2(34, -86), new Vector2(-68, 34), new Vector2(0.5f, 1));

            var types = controller.BuildTypes;
            for (var index = 0; index < types.Count; index++)
            {
                var type = types[index];
                var selectedType = type;
                var column = index % 2;
                var row = index / 2;
                var button = ButtonObject("BuildType_" + type, card.transform, string.Empty, new Color(0.16f, 0.26f, 0.34f, 1), () => SelectBuildType(selectedType));
                Anchor(button.gameObject, new Vector2(0, 1), new Vector2(0, 1), new Vector2(42 + column * 374, -142 - row * 126), new Vector2(362, 108), new Vector2(0, 1));
                var label = button.GetComponentInChildren<TextMeshProUGUI>();
                label.fontSize = 16;
                label.enableAutoSizing = true;
                label.fontSizeMin = 12;
                label.fontSizeMax = 16;
                buildTypeButtons[type] = button;
                buildTypeLabels[type] = label;
            }

            var cancel = ButtonObject("BuildPickerCancel", card.transform, "취소  [Esc]", new Color(0.25f, 0.3f, 0.38f, 1), HideBuildPicker);
            Anchor(cancel.gameObject, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0, 24), new Vector2(260, 52), new Vector2(0.5f, 0));
            buildPickerPanel.SetActive(false);
        }

        private void BuildModal()
        {
            modalPanel = PanelObject("RuleModal", interfaceRoot.transform, new Color(0.01f, 0.02f, 0.04f, 0.84f));
            Stretch(modalPanel);
            var card = PanelObject("RuleCard", modalPanel.transform, Ink);
            Anchor(card, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(760, 500), new Vector2(0.5f, 0.5f));
            modalTitle = Label("ModalTitle", card.transform, "", 30, Gold, TextAnchor.MiddleCenter, FontStyle.Bold);
            Anchor(modalTitle.gameObject, new Vector2(0, 1), new Vector2(1, 1), new Vector2(28, -34), new Vector2(-56, 50), new Vector2(0.5f, 1));
            modalBody = Label("ModalBody", card.transform, "", 17, Color.white, TextAnchor.UpperLeft, FontStyle.Normal);
            modalBody.enableAutoSizing = true;
            modalBody.fontSizeMin = 13;
            modalBody.fontSizeMax = 17;
            Anchor(modalBody.gameObject, Vector2.zero, Vector2.one, new Vector2(42, 100), new Vector2(-84, -200), new Vector2(0, 0));
            modalRetryButton = ButtonObject("Retry", card.transform, "AI 규칙 다시 요청", new Color(0.55f, 0.27f, 0.2f, 1), controller.RetryRulesFromHud);
            Anchor(modalRetryButton.gameObject, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(-130, 28), new Vector2(240, 54), new Vector2(0.5f, 0));
            modalSaveButton = ButtonObject("Save", card.transform, "저장 후 메뉴", new Color(0.24f, 0.29f, 0.36f, 1), controller.SaveAndReturnToMenu);
            Anchor(modalSaveButton.gameObject, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(130, 28), new Vector2(220, 54), new Vector2(0.5f, 0));
            modalAcceptButton = ButtonObject("AcceptRules", card.transform, "규칙을 확인했습니다", new Color(0.1f, 0.55f, 0.68f, 1), AcceptModal);
            Anchor(modalAcceptButton.gameObject, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0, 28), new Vector2(270, 54), new Vector2(0.5f, 0));
            modalCancelButton = ButtonObject("CancelModal", card.transform, "취소", new Color(0.24f, 0.29f, 0.36f, 1), CancelModal);
            Anchor(modalCancelButton.gameObject, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(140, 28), new Vector2(220, 54), new Vector2(0.5f, 0));
            SetModalMode(ModalMode.None);
            modalPanel.SetActive(false);
        }

        private void BuildOutcome()
        {
            outcomePanel = PanelObject("Outcome", interfaceRoot.transform, new Color(0.01f, 0.02f, 0.04f, 0.88f));
            Stretch(outcomePanel);
            var card = PanelObject("OutcomeCard", outcomePanel.transform, Ink);
            Anchor(card, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(620, 440), new Vector2(0.5f, 0.5f));
            outcomeTitle = Label("OutcomeTitle", card.transform, string.Empty, 42, Gold, TextAnchor.MiddleCenter, FontStyle.Bold);
            Anchor(outcomeTitle.gameObject, new Vector2(0, 1), new Vector2(1, 1), new Vector2(30, -54), new Vector2(-60, 64), new Vector2(0.5f, 1));
            outcomeBody = Label("OutcomeBody", card.transform, string.Empty, 18, Color.white, TextAnchor.MiddleCenter, FontStyle.Normal);
            outcomeBody.enableAutoSizing = true;
            outcomeBody.fontSizeMin = 15;
            outcomeBody.fontSizeMax = 18;
            Anchor(outcomeBody.gameObject, new Vector2(0, 0), new Vector2(1, 1), new Vector2(46, 112), new Vector2(-92, -242), new Vector2(0.5f, 0.5f));
            var again = ButtonObject("Again", card.transform, "새 원정 시작", new Color(0.1f, 0.55f, 0.68f, 1), controller.StartNewRun);
            Anchor(again.gameObject, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(-140, 32), new Vector2(250, 58), new Vector2(0.5f, 0));
            var menu = ButtonObject("OutcomeMenu", card.transform, "메인 메뉴", new Color(0.25f, 0.3f, 0.38f, 1), ReturnFromOutcomeToMenu);
            Anchor(menu.gameObject, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(140, 32), new Vector2(250, 58), new Vector2(0.5f, 0));
            outcomePanel.SetActive(false);
        }

        public void Render()
        {
            if (!initialized || controller == null) return;
            var game = controller.Game;
            if (game == null)
            {
                RenderUnavailableState();
                return;
            }

            var factions = game.factions ?? new List<FactionState>();
            var player = factions.FirstOrDefault(f => f != null && (f.id == 1 || f.kind == FactionKind.Player));
            if (player == null)
            {
                RenderUnavailableState();
                return;
            }

            headline.text = "ONLY MY GAME   ·   TURN " + game.turn;
            luckBadge.text = "☀  행운 " + game.luck;
            luckBadge.color = game.luck >= 70 ? Cyan : game.luck >= 35 ? Gold : Red;
            resources.text = "[식] " + player.resources.food + "    [목] " + player.resources.wood + "    [석] " + player.resources.stone + "    [철] " + player.resources.iron + "    [금] " + player.resources.coin;
            service.text = controller.ServiceStatus;
            service.color = controller.ServiceOnline ? Green : controller.ServiceChecking ? Gold : Red;

            var contracts = game.victoryContracts ?? new List<VictoryContractV1>();
            var goal = contracts.LastOrDefault(c => c != null);
            var completedGoal = contracts.FirstOrDefault(c => c != null && c.id == game.completedContractId) ?? goal;
            objective.text = goal == null
                ? "목표 수신 대기\n첫 턴을 계획하면 AI가 승리 계약을 제안합니다."
                : SafeText(goal.title, "승리 계약") + "\n" + SafeText(goal.description, "계약 조건을 확인하세요.") + "\n진행 " + GameRules.Progress(game, goal.progressKey) + " / " + goal.target +
                  (goal.replaceWarningTurn > 0 ? "\n<color=#FFD05A>교체 예고 · 다음 규칙 수신 때 변경될 수 있음</color>" : string.Empty);
            var rivals = factions.Where(f => f != null && f != player).ToList();
            relations.text = "세력 관계\n" + (rivals.Count == 0 ? "· 접촉한 세력 없음" : string.Join("\n", rivals.Select(RelationLine)));
            var ruleSource = game.activeRules ?? new List<RuleNodeV1>();
            var active = ruleSource.Where(r => r != null && GameRules.IsRuleActive(r, game.turn)).OrderByDescending(r => r.priority).ThenByDescending(r => r.appliedTurn).Take(3).ToList();
            rules.text = "활성 세계 규칙\n" + (active.Count == 0 ? "· 현재 활성 규칙 없음" : string.Join("\n", active.Select(r => "· " + SafeText(r.name, "이름 없는 규칙") + "  [" + RemainingTurns(r, game.turn) + "턴]")));

            RenderSelection();
            RenderDynamicActions(game, player);
            var used = controller.PlannedSp;
            var remaining = Math.Max(0, player.sp - used);
            spFill.fillAmount = player.sp <= 0 ? 0 : remaining / (float)player.sp;
            planningHint.text = controller.IsTargeting ? controller.TargetingPrompt : "계획 SP  " + remaining + " / " + player.sp + "   ·   유닛을 선택하고 행동을 예약하세요";
            queue.text = BuildQueueText();
            var ledger = controller.Ledger ?? new List<string>();
            var recent = ledger.Skip(Math.Max(0, ledger.Count - 6));
            journal.text = ledger.Count == 0 ? "원정 기록\n선택 → 행동 예약 → 턴 종료 순서로 진행하세요." : string.Join("\n", recent);
            undoButton.interactable = controller.Commands.Count > 0 && !controller.IsBusy;
            clearButton.interactable = controller.Commands.Count > 0 && !controller.IsBusy;
            endTurnLabel.text = controller.IsBlocked ? "규칙 수신을 재시도하세요" : controller.IsBusy ? "AI가 세계를 다시 쓰는 중…" : controller.Commands.Count == 0 ? "대기하고 턴 종료  [Space]" : "명령 확정 · 턴 종료  [Space]";
            endTurnButton.interactable = !controller.IsBusy && !controller.IsBlocked && game.outcome == RunOutcome.Ongoing;
            if (buildPickerPanel.activeSelf) RefreshBuildPicker();

            if (!mainMenu.activeSelf && controller.IsBlocked)
            {
                modalTitle.text = "세계 규칙 수신 중단";
                modalBody.text = controller.BlockReason + "\n\n안전한 규칙을 받기 전에는 다음 턴을 시작하지 않습니다. 연결을 확인한 뒤 다시 요청하거나 현재 상태를 저장하세요.";
                SetModalMode(ModalMode.Blocked);
            }
            else if (modalMode == ModalMode.Blocked) SetModalMode(ModalMode.None);

            if (!mainMenu.activeSelf && game.outcome != RunOutcome.Ongoing)
            {
                HideBuildPicker();
                outcomePanel.SetActive(true);
                outcomePanel.transform.SetAsLastSibling();
                outcomeTitle.text = game.outcome == RunOutcome.Victory ? "원정 성공!" : "원정 종료";
                outcomeTitle.color = game.outcome == RunOutcome.Victory ? Gold : Red;
                outcomeBody.text = game.outcome == RunOutcome.Victory
                    ? "AI가 만든 승리 계약을 완수했습니다.\n\n" + game.turn + "턴 · 처치 " + game.playerKills + " · 건물 " + (game.buildings ?? new List<BuildingState>()).Count(b => b != null && b.factionId == 1) + "\n" + (completedGoal == null ? "나만의 원정을 완성했습니다." : SafeText(completedGoal.title, "승리 계약"))
                    : "지휘 본부와 복구 가능한 원정대를 모두 잃었습니다.\n다음 원정에서는 시야와 자원 생산을 먼저 확보해 보세요.";
            }
            else outcomePanel.SetActive(false);
        }

        private void RenderSelection()
        {
            var unit = controller.SelectedUnit;
            var building = controller.SelectedBuilding;
            if (unit != null)
            {
                var faction = (controller.Game.factions ?? new List<FactionState>()).FirstOrDefault(f => f != null && f.id == unit.factionId);
                var isPlayer = faction != null && faction.kind == FactionKind.Player || unit.factionId == 1;
                selectionTitle.text = faction == null ? "알 수 없는 원정대" : SafeText(faction.name, "이름 없는 세력");
                selectionTitle.color = isPlayer ? Cyan : faction != null && faction.kind == FactionKind.Skeleton ? Red : Gold;
                var role = unit.tags == null ? null : unit.tags.FirstOrDefault();
                selectionBody.text = SafeText(role, "유닛") + "   HP " + unit.hp + "/5\n좌표 " + unit.position + "   속도 " + unit.speed + "\n" + (isPlayer ? "이 유닛의 다음 행동을 예약합니다." : "관계 " + (faction == null ? 0 : faction.relationToPlayer));
                selectionHint.text = isPlayer ? controller.IsTargeting ? "강조된 타일 또는 대상을 선택하세요" : "서로 다른 행동을 공유 SP 한도 안에서 조합할 수 있습니다" : "아군을 선택하면 대응 행동을 예약할 수 있습니다";
            }
            else if (building != null)
            {
                var faction = (controller.Game.factions ?? new List<FactionState>()).FirstOrDefault(f => f != null && f.id == building.factionId);
                selectionTitle.text = controller.BuildingName(building.type) + "  Lv." + building.level;
                selectionTitle.color = building.factionId == 1 ? Cyan : Red;
                selectionBody.text = (faction == null ? "알 수 없는 세력" : SafeText(faction.name, "이름 없는 세력")) + "\nHP " + building.hp + "/" + (12 + (building.level - 1) * 3) + "   좌표 " + building.position + "\n" + controller.BuildingBenefit(building.type, building.level);
                selectionHint.text = building.factionId == 1 ? "강화는 가장 가까운 아군 원정대가 수행합니다" : "적 거점은 공격 대상으로 지정할 수 있습니다";
            }
            else
            {
                selectionTitle.text = "원정대 선택";
                selectionTitle.color = Cyan;
                selectionBody.text = "아군 유닛이나 거점을 클릭하세요.\n우클릭은 선택/타깃 지정을 취소합니다.";
                selectionHint.text = "먼저 아군을 선택해 사용 가능한 행동을 확인하세요";
            }

            foreach (var pair in actionButtons)
            {
                pair.Value.interactable = controller.CanBeginCommand(pair.Key);
            }
        }

        public void ShowMainMenu(bool hasSave)
        {
            mainMenu.SetActive(true);
            continueButton.interactable = hasSave;
            continueLabel.text = hasSave ? "원정 계속하기" : "계속할 원정 없음";
            previousRunButton.interactable = controller.HasPreviousRun;
            mainMenuStatus.text = hasSave ? "자동 저장된 원정이 있습니다." : controller.HasPreviousRun ? "이전 원정을 복구하거나 새로 시작할 수 있습니다." : "새 원정을 시작하면 턴마다 자동 저장됩니다.";
            pauseMenu.SetActive(false);
            ledgerPanel.SetActive(false);
            HideBuildPicker();
            outcomePanel.SetActive(false);
            SetModalMode(ModalMode.None);
            mainMenu.transform.SetAsLastSibling();
        }

        public void HideMainMenu() => mainMenu.SetActive(false);

        public void ShowNewRunConfirmation()
        {
            modalTitle.text = "새 원정을 시작할까요?";
            modalBody.text = "현재 자동 저장은 ‘이전 원정’ 슬롯에 보관됩니다. 새 세계를 만든 뒤에도 메인 메뉴에서 한 번 되돌릴 수 있습니다.";
            SetModalMode(ModalMode.NewRunConfirmation);
        }

        /// <summary>
        /// The controller owns validation and state mutation for AI-generated actions.
        /// Supplying this handler enables the action cards without coupling the HUD to
        /// the controller's private execution implementation.
        /// </summary>
        public void SetDynamicActionHandler(Action<DynamicActionV1> handler)
        {
            dynamicActionHandler = handler;
            Render();
        }

        public void ShowRuleAnnouncement(string summary, IEnumerable<RuleNodeV1> newRules, IEnumerable<VictoryContractV1> contracts)
        {
            var lines = new List<string>();
            if (!string.IsNullOrWhiteSpace(summary)) lines.Add(SafeText(summary, "세계 규칙이 변경되었습니다."));
            if (newRules != null)
            {
                lines.AddRange(newRules.Where(r => r != null).Select(r => "◆ " + SafeText(r.name, "새 세계 규칙") + "\n   " + SafeText(r.description, "효과 설명 없음") + "\n   지속 " + Math.Max(1, r.durationTurns) + "턴 · 영향 " + SafeText(r.worldCue, "월드 전체")));
            }
            if (contracts != null)
            {
                lines.AddRange(contracts.Where(c => c != null).Select(c =>
                    (c.replaceWarningTurn == controller.Game.turn ? "⚠ 승리 계약 교체 예고: " : "★ 새 승리 계약: ") +
                    SafeText(c.title, "이름 없는 계약") + "\n   " +
                    (c.replaceWarningTurn == controller.Game.turn ? "최소 유지 기간 뒤 다음 턴부터 교체될 수 있습니다." : SafeText(c.description, "계약 조건을 장부에서 확인하세요."))));
            }
            if (lines.Count == 0) lines.Add("세계의 변화가 적용되었습니다. 규칙 장부에서 현재 효과를 확인하세요.");
            modalTitle.text = "다음 턴, 세계가 변합니다";
            modalBody.text = string.Join("\n\n", lines);
            SetModalMode(ModalMode.RuleAnnouncement);
        }

        public void Toast(string message, Color color)
        {
            if (!initialized || toastPanel == null || toast == null) return;
            toast.text = SafeText(message, "상태가 변경되었습니다.");
            toast.color = color;
            toastPanel.SetActive(true);
            toastPanel.transform.SetAsLastSibling();
            toastUntil = Time.unscaledTime + 2.4f;
        }

        private void TogglePause()
        {
            if (mainMenu.activeSelf || modalMode != ModalMode.None || outcomePanel.activeSelf || ledgerPanel.activeSelf || buildPickerPanel.activeSelf) return;
            if (controller.IsBusy)
            {
                Toast("턴 처리가 끝난 뒤 일시 정지 메뉴를 열 수 있습니다.", Gold);
                return;
            }
            pauseMenu.SetActive(!pauseMenu.activeSelf);
            if (pauseMenu.activeSelf) pauseMenu.transform.SetAsLastSibling();
        }

        private void ToggleLedger(bool show)
        {
            if (show && (mainMenu.activeSelf || modalMode != ModalMode.None || outcomePanel.activeSelf || buildPickerPanel.activeSelf)) return;
            if (show)
            {
                ledgerBody.text = BuildLedgerText();
                ledgerBody.rectTransform.anchoredPosition = Vector2.zero;
                pauseMenu.SetActive(false);
                ledgerPanel.transform.SetAsLastSibling();
            }
            ledgerPanel.SetActive(show);
        }

        private void Update()
        {
            if (!initialized) return;
            if (toastPanel != null && toastPanel.activeSelf && Time.unscaledTime >= toastUntil) toastPanel.SetActive(false);
            if (modalMode == ModalMode.Blocked && modalRetryButton != null)
            {
                var delay = controller.RetryDelaySeconds;
                modalRetryButton.interactable = delay <= 0;
                var label = modalRetryButton.GetComponentInChildren<TextMeshProUGUI>();
                if (label != null) label.text = delay > 0 ? "다시 요청  (" + delay + "초)" : "AI 규칙 다시 요청";
            }
            if (Input.GetKeyDown(KeyCode.Tab))
            {
                if (buildPickerPanel.activeSelf) return;
                if (ledgerPanel.activeSelf) ToggleLedger(false);
                else ToggleLedger(true);
            }
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                if (buildPickerPanel.activeSelf) HideBuildPicker();
                else if (ledgerPanel.activeSelf) ToggleLedger(false);
                else if (modalMode == ModalMode.RuleAnnouncement) DismissRuleAnnouncement();
                else if (modalMode == ModalMode.NewRunConfirmation) CancelModal();
                else if (modalMode == ModalMode.Blocked || mainMenu.activeSelf || outcomePanel.activeSelf) return;
                else if (controller.IsTargeting) controller.CancelTargeting();
                else TogglePause();
            }
            if (Input.GetKeyDown(KeyCode.Space) && endTurnButton.interactable && !mainMenu.activeSelf && !pauseMenu.activeSelf && !ledgerPanel.activeSelf && !buildPickerPanel.activeSelf && modalMode == ModalMode.None && !outcomePanel.activeSelf)
                controller.EndTurnFromHud();
        }

        private void RenderUnavailableState()
        {
            HideBuildPicker();
            headline.text = "ONLY MY GAME   ·   원정 준비 중";
            resources.text = "원정 데이터를 불러오고 있습니다.";
            objective.text = "잠시만 기다려 주세요.";
            relations.text = "세력 관계\n· 데이터 준비 중";
            rules.text = "활성 세계 규칙\n· 데이터 준비 중";
            selectionTitle.text = "원정대 선택";
            selectionBody.text = "원정이 준비되면 유닛과 거점을 선택할 수 있습니다.";
            selectionHint.text = string.Empty;
            dynamicActionSummary.text = "세계 규칙을 불러오는 중입니다.";
            queue.text = "예약된 명령 없음";
            planningHint.text = "원정 준비 중";
            journal.text = "저장 데이터를 확인하고 있습니다.";
            spFill.fillAmount = 0;
            foreach (var button in actionButtons.Values) button.interactable = false;
            foreach (var button in dynamicActionButtons) button.gameObject.SetActive(false);
            undoButton.interactable = false;
            clearButton.interactable = false;
            endTurnButton.interactable = false;
            endTurnLabel.text = "원정 준비 중…";
        }

        private string BuildQueueText()
        {
            if (controller.Commands == null || controller.Commands.Count == 0) return "예약된 명령 없음\n유닛을 선택해 이번 턴의 계획을 세우세요.";
            var visibleCount = Math.Min(3, controller.Commands.Count);
            var lines = controller.Commands.Take(visibleCount).Select((command, index) => (index + 1) + ". " + controller.Describe(command)).ToList();
            if (controller.Commands.Count > visibleCount) lines.Add("+ " + (controller.Commands.Count - visibleCount) + "개 명령 더 있음");
            return string.Join("\n", lines);
        }

        private void RenderDynamicActions(GameSnapshotV1 game, FactionState player)
        {
            displayedDynamicActions.Clear();
            var source = game.dynamicActions ?? new List<DynamicActionV1>();
            displayedDynamicActions.AddRange(source.Where(action => action != null)
                .OrderBy(action => Math.Max(game.turn, action.availableTurn))
                .ThenBy(action => action.name)
                .Take(DynamicActionSlotCount));

            if (source.Count == 0)
            {
                dynamicActionSummary.text = "세계 규칙이 새 행동을 해금할 수 있습니다.";
            }
            else if (dynamicActionHandler == null)
            {
                dynamicActionSummary.text = "특수 행동 " + source.Count + "개 · 실행 연결 대기 중";
            }
            else
            {
                var extra = Math.Max(0, source.Count - DynamicActionSlotCount);
                dynamicActionSummary.text = "규칙이 만든 행동 " + source.Count + "개" + (extra > 0 ? " · 장부에 " + extra + "개 더 표시" : " · 대상 행동은 아군 선택 후 월드에서 지정하세요");
            }

            var renderAvailability = dynamicActionHandler != null && controller != null
                ? controller.CanRunDynamicsForHud(displayedDynamicActions)
                : new List<bool>();

            for (var i = 0; i < dynamicActionButtons.Count; i++)
            {
                var hasAction = i < displayedDynamicActions.Count;
                dynamicActionButtons[i].gameObject.SetActive(hasAction);
                if (!hasAction) continue;

                var action = displayedDynamicActions[i];
                string reason;
                var ready = CanUseDynamicAction(
                    action,
                    game,
                    player,
                    i < renderAvailability.Count ? (bool?)renderAvailability[i] : null,
                    out reason);
                dynamicActionLabels[i].text = SafeText(action.name, "이름 없는 특수 행동") + "\n" + DynamicActionCost(action) + (ready ? " · 사용 가능" : " · " + reason);
                dynamicActionLabels[i].color = ready ? Color.white : Muted;
                dynamicActionButtons[i].interactable = ready;
            }
        }

        private void RequestDynamicAction(int slot)
        {
            if (slot < 0 || slot >= displayedDynamicActions.Count || controller == null || controller.Game == null) return;
            var game = controller.Game;
            var player = (game.factions ?? new List<FactionState>()).FirstOrDefault(f => f != null && (f.id == 1 || f.kind == FactionKind.Player));
            var reason = "플레이어 원정대 정보를 찾을 수 없습니다.";
            if (player == null || !CanUseDynamicAction(displayedDynamicActions[slot], game, player, null, out reason))
            {
                Toast(string.IsNullOrWhiteSpace(reason) ? "이 특수 행동을 지금 사용할 수 없습니다." : reason, Gold);
                return;
            }

            dynamicActionHandler(displayedDynamicActions[slot]);
            Render();
        }

        private bool CanUseDynamicAction(
            DynamicActionV1 action,
            GameSnapshotV1 game,
            FactionState player,
            bool? precomputedCanRun,
            out string reason)
        {
            if (dynamicActionHandler == null)
            {
                reason = "실행 연결 대기";
                return false;
            }
            if (precomputedCanRun ?? controller.CanRunDynamic(action))
            {
                reason = string.Empty;
                return true;
            }
            if (game.outcome != RunOutcome.Ongoing || controller.IsBusy || controller.IsBlocked)
            {
                reason = "현재 사용 불가";
                return false;
            }
            if (game.turn < action.availableTurn)
            {
                reason = (action.availableTurn - game.turn) + "턴 후";
                return false;
            }
            var remainingSp = Math.Max(0, player.sp - controller.PlannedSp);
            if (remainingSp < Math.Max(0, action.spCost))
            {
                reason = "SP 부족";
                return false;
            }
            if (action.resourceAmount > 0 && player.resources.Get(action.resourceCost) < action.resourceAmount)
            {
                reason = ResourceName(action.resourceCost) + " 부족";
                return false;
            }
            if (DynamicActionTargeting.RequiresTarget(action))
            {
                var actor = controller.SelectedUnit;
                reason = actor == null || actor.factionId != 1 ? "아군 유닛 선택 필요" : "시야·거리 내 유효 대상 없음";
                return false;
            }
            reason = "조건 또는 효과 확인 필요";
            return false;
        }

        private string BuildLedgerText()
        {
            var game = controller == null ? null : controller.Game;
            if (game == null) return "원정 규칙을 불러오는 중입니다.";

            var sections = new List<string>();
            var contracts = game.victoryContracts ?? new List<VictoryContractV1>();
            var ruleSource = game.activeRules ?? new List<RuleNodeV1>();
            var ruleEntries = ruleSource.Where(rule => rule != null)
                .OrderByDescending(rule => rule.appliedTurn)
                .ThenByDescending(rule => rule.priority)
                .Take(12)
                .Select(rule =>
                {
                    var status = game.turn < rule.appliedTurn
                        ? "예고 · " + rule.appliedTurn + "턴부터"
                        : GameRules.IsRuleActive(rule, game.turn) ? "활성 · " + RemainingTurns(rule, game.turn) + "턴 남음" : "종료";
                    return "◆ " + SafeText(rule.name, "이름 없는 규칙") + "   [" + status + "]\n" +
                           "발생 원인: " + TriggerText(rule.trigger) + "\n" +
                           "새 조건: " + ConditionText(rule.condition) + "\n" +
                           "논리 상태: " + StateDefinitionsText(rule.stateDefinitions, game) + "\n" +
                           "효과 설명: " + SafeText(rule.description, "효과 설명 없음") + "\n" +
                           "실행식: " + EffectsText(rule.effects) + "\n" +
                           "지속 시간: " + Math.Max(1, rule.durationTurns) + "턴\n" +
                           "승리 조건 영향: " + VictoryImpactText(rule, contracts) + "\n" +
                           "월드 표식: " + SafeText(rule.worldCue, "영향 대상에 범용 표식");
                }).ToList();
            sections.Add("<color=#FFD05A><b>세계 규칙</b></color>\n" + (ruleEntries.Count == 0 ? "현재 기록된 규칙이 없습니다." : string.Join("\n\n", ruleEntries)));

            var start = Math.Max(0, contracts.Count - 3);
            var contractEntries = contracts.Skip(start).Where(contract => contract != null).Select(contract =>
                "★ " + SafeText(contract.title, "이름 없는 승리 계약") + "   " + GameRules.Progress(game, contract.progressKey) + "/" + contract.target + "\n" +
                SafeText(contract.description, "계약 조건 설명 없음") +
                (contract.replaceWarningTurn > 0 ? "\n<color=#FFD05A>⚠ " + contract.replaceWarningTurn + "턴에 교체 예고됨</color>" : string.Empty) +
                (string.IsNullOrWhiteSpace(contract.worldCue) ? string.Empty : "\n영향 표식: " + SafeText(contract.worldCue, "세계 전체"))).ToList();
            sections.Add("<color=#59D7F2><b>승리 계약</b></color>\n" + (contractEntries.Count == 0 ? "아직 제안된 승리 계약이 없습니다." : string.Join("\n\n", contractEntries)));

            var actions = game.dynamicActions ?? new List<DynamicActionV1>();
            var actionEntries = actions.Where(action => action != null).Take(8).Select(action =>
                "◇ " + SafeText(action.name, "이름 없는 특수 행동") + "   " + DynamicActionCost(action) + "\n" +
                SafeText(action.description, "규칙이 만든 특수 행동") + "\n" +
                (game.turn < action.availableTurn ? action.availableTurn + "턴부터 사용" : "현재 사용 가능") + " · 재사용 " + Math.Max(1, action.cooldown) + "턴").ToList();
            sections.Add("<color=#C69AF4><b>AI 특수 행동</b></color>\n" + (actionEntries.Count == 0 ? "해금된 특수 행동이 없습니다." : string.Join("\n\n", actionEntries)));
            return string.Join("\n\n", sections);
        }

        private void SetModalMode(ModalMode mode)
        {
            modalMode = mode;
            if (mode != ModalMode.None) HideBuildPicker();
            if (modalRetryButton != null) modalRetryButton.gameObject.SetActive(mode == ModalMode.Blocked);
            if (modalSaveButton != null) modalSaveButton.gameObject.SetActive(mode == ModalMode.Blocked);
            if (modalAcceptButton != null)
            {
                modalAcceptButton.gameObject.SetActive(mode == ModalMode.RuleAnnouncement || mode == ModalMode.NewRunConfirmation);
                var label = modalAcceptButton.GetComponentInChildren<TextMeshProUGUI>();
                if (label != null) label.text = mode == ModalMode.NewRunConfirmation ? "새 원정 시작" : "규칙을 확인했습니다";
                var rect = modalAcceptButton.GetComponent<RectTransform>();
                if (rect != null) rect.anchoredPosition = mode == ModalMode.NewRunConfirmation ? new Vector2(-140, 28) : new Vector2(0, 28);
            }
            if (modalCancelButton != null) modalCancelButton.gameObject.SetActive(mode == ModalMode.NewRunConfirmation);
            if (modalPanel != null)
            {
                modalPanel.SetActive(mode != ModalMode.None);
                if (mode != ModalMode.None) modalPanel.transform.SetAsLastSibling();
            }
        }

        private void DismissRuleAnnouncement()
        {
            if (modalMode == ModalMode.RuleAnnouncement) SetModalMode(ModalMode.None);
        }

        private void AcceptModal()
        {
            if (modalMode == ModalMode.RuleAnnouncement)
            {
                DismissRuleAnnouncement();
                return;
            }
            if (modalMode != ModalMode.NewRunConfirmation) return;
            SetModalMode(ModalMode.None);
            controller.ConfirmNewRun();
        }

        private void CancelModal()
        {
            if (modalMode == ModalMode.NewRunConfirmation) SetModalMode(ModalMode.None);
        }

        public void ShowBuildPicker()
        {
            if (!initialized || controller == null || buildPickerPanel == null) return;
            RefreshBuildPicker();
            buildPickerPanel.SetActive(true);
            buildPickerPanel.transform.SetAsLastSibling();
        }

        private void RefreshBuildPicker()
        {
            if (controller == null) return;
            foreach (var type in controller.BuildTypes)
            {
                if (!buildTypeButtons.TryGetValue(type, out var button) || !buildTypeLabels.TryGetValue(type, out var label)) continue;
                var ironCost = GameRules.BuildingIronCost(type);
                var affordable = controller.CanBuildType(type);
                label.text = controller.BuildingName(type) + "\n" + controller.BuildingBenefit(type, 1) + "\n목재 " + GameRules.BuildingCost(type) + (ironCost > 0 ? " · 철 " + ironCost : string.Empty) + (affordable ? " · 건설 가능" : " · 현재 선택 불가");
                label.color = affordable ? Color.white : Muted;
                button.interactable = affordable;
            }
        }

        private void SelectBuildType(BuildingType type)
        {
            if (controller != null && controller.QueueBuildFromHud(type)) HideBuildPicker();
            else RefreshBuildPicker();
        }

        private void HideBuildPicker()
        {
            if (buildPickerPanel != null) buildPickerPanel.SetActive(false);
        }

        private void ReturnFromOutcomeToMenu()
        {
            outcomePanel.SetActive(false);
            ShowMainMenu(false);
        }

        private static string RelationLine(FactionState faction)
        {
            var state = faction.relationToPlayer <= -40 ? "적대" : faction.relationToPlayer < 20 ? "경계" : faction.relationToPlayer < 60 ? "우호" : "동맹";
            return "· " + SafeText(faction.name, "이름 없는 세력") + "  " + state + "  " + faction.relationToPlayer;
        }

        private static int RemainingTurns(RuleNodeV1 rule, int turn) => Math.Max(0, rule.appliedTurn + Math.Max(1, rule.durationTurns) - turn);
        private static string ConditionText(ConditionNode condition)
        {
            var visited = 0;
            return ConditionText(condition, 1, ref visited);
        }

        private static string ConditionText(ConditionNode condition, int depth, ref int visited)
        {
            if (condition == null) return "항상";
            if (depth > RuleLimits.MaxConditionDepth || visited++ >= RuleLimits.MaxConditionNodes) return "검증 한도 내 복합 조건";

            string primary;
            if (condition.predicate != null) primary = PredicateText(condition.predicate, depth + 1, ref visited);
            else if (condition.op == CompareOp.Always) primary = "항상";
            else if (condition.op == CompareOp.HasTag)
            {
                primary = SelectorText(condition.left) + "에 ‘" + SafeText(condition.text, "지정 태그") + "’ 태그가 있음";
            }
            else if (condition.op == CompareOp.OwnerIs)
            {
                var selector = string.IsNullOrWhiteSpace(condition.left) ? condition.text : condition.left;
                primary = TileSelectorText(selector) + "의 소유 세력 = " + condition.value;
            }
            else
            {
                var comparison = condition.op == CompareOp.Equal ? "=" : condition.op == CompareOp.GreaterOrEqual ? "≥" : "≤";
                primary = SafeText(condition.text, SafeText(condition.left, "상태") + " " + comparison + " " + condition.value);
            }

            var children = new List<string>();
            foreach (var child in condition.all ?? new List<ConditionNode>())
            {
                if (child != null) children.Add(ConditionText(child, depth + 1, ref visited));
            }
            return children.Count == 0 ? primary : "(" + primary + " AND " + string.Join(" AND ", children) + ")";
        }

        private static string PredicateText(PredicateExpressionV1 predicate, int depth, ref int visited)
        {
            if (predicate == null) return "식 없음";
            if (depth > RuleLimits.MaxConditionDepth || visited++ >= RuleLimits.MaxConditionNodes) return "검증 한도 내 논리식";
            if (predicate.op == PredicateExpressionOp.All || predicate.op == PredicateExpressionOp.Any)
            {
                var joiner = predicate.op == PredicateExpressionOp.All ? " AND " : " OR ";
                var children = new List<string>();
                foreach (var child in predicate.children ?? new List<PredicateExpressionV1>())
                    children.Add(PredicateText(child, depth + 1, ref visited));
                return children.Count == 0 ? "조건 없음" : "(" + string.Join(joiner, children) + ")";
            }
            if (predicate.op == PredicateExpressionOp.Not) return "NOT (" + PredicateText(predicate.child, depth + 1, ref visited) + ")";
            if (predicate.op == PredicateExpressionOp.BoolState) return StateReferenceText(predicate.state) + " = 참";
            if (predicate.op == PredicateExpressionOp.SetContains) return StateReferenceText(predicate.state) + "에 ‘" + SafeText(predicate.element, "값") + "’ 포함";
            var comparison = predicate.op == PredicateExpressionOp.NumberEqual ? "=" :
                predicate.op == PredicateExpressionOp.NumberNotEqual ? "≠" :
                predicate.op == PredicateExpressionOp.NumberGreater ? ">" :
                predicate.op == PredicateExpressionOp.NumberGreaterOrEqual ? "≥" :
                predicate.op == PredicateExpressionOp.NumberLess ? "<" : "≤";
            return NumberExpressionText(predicate.left, depth + 1, ref visited) + " " + comparison + " " + NumberExpressionText(predicate.right, depth + 1, ref visited);
        }

        private static string NumberExpressionText(NumberExpressionV1 expression, int depth, ref int visited)
        {
            if (expression == null) return "값 없음";
            if (depth > RuleLimits.MaxConditionDepth || visited++ >= RuleLimits.MaxConditionNodes) return "제한된 계산식";
            if (expression.op == NumberExpressionOp.Constant) return expression.constant.ToString();
            if (expression.op == NumberExpressionOp.State) return StateReferenceText(expression.state);
            if (expression.op == NumberExpressionOp.Add || expression.op == NumberExpressionOp.Subtract || expression.op == NumberExpressionOp.Multiply || expression.op == NumberExpressionOp.Divide)
            {
                var symbol = expression.op == NumberExpressionOp.Add ? "+" : expression.op == NumberExpressionOp.Subtract ? "−" : expression.op == NumberExpressionOp.Multiply ? "×" : "÷";
                return "(" + NumberExpressionText(expression.left, depth + 1, ref visited) + " " + symbol + " " + NumberExpressionText(expression.right, depth + 1, ref visited) + ")";
            }
            if (expression.op == NumberExpressionOp.CountUnits) return "유닛 수[" + SafeText(expression.selector, "전체") + "]";
            if (expression.op == NumberExpressionOp.CountBuildings) return "건물 수[" + SafeText(expression.selector, "전체") + "]";
            if (expression.op == NumberExpressionOp.CountTiles) return "타일 수[" + SafeText(expression.selector, "전체") + "]";
            if (expression.op == NumberExpressionOp.Distance) return "거리(" + SafeText(expression.selector, "대상 A") + ", " + SafeText(expression.secondSelector, "대상 B") + ")";
            return "최근 " + Math.Max(1, expression.recentTurns) + "턴 " + GameController.CommandKorean(expression.action) + " 비율";
        }

        private static string StateDefinitionsText(IEnumerable<StateDefinitionV1> definitions, GameSnapshotV1 game)
        {
            var entries = (definitions ?? Enumerable.Empty<StateDefinitionV1>())
                .Where(definition => definition != null)
                .Take(8)
                .Select(definition =>
                {
                    var current = (game?.typedRuleState ?? new List<TypedRuleStateEntryV1>())
                        .FirstOrDefault(entry => entry != null && entry.scope == definition.scope &&
                                                 string.Equals(entry.key, definition.key, StringComparison.Ordinal) &&
                                                 string.Equals(NormalizeScopeId(entry.scope, entry.scopeId), NormalizeScopeId(definition.scope, definition.scopeId), StringComparison.Ordinal) &&
                                                 (entry.scope != RuleStateScope.Turn || entry.stateTurn == game.turn));
                    var value = StateValueText(definition, current);
                    var token = SafeText(definition.iconToken, "state");
                    var label = "◈ " + SafeText(definition.koreanName, definition.key) + " [" + token + "] = " + value + " · " + StateScopeText(definition.scope, definition.scopeId);
                    return IsSafeColor(definition.colorHex) ? "<color=" + definition.colorHex + ">" + label + "</color>" : label;
                })
                .ToList();
            return entries.Count == 0 ? "추가 상태 없음" : string.Join(" / ", entries);
        }

        private static string StateValueText(StateDefinitionV1 definition, TypedRuleStateEntryV1 current)
        {
            if (definition.valueType == RuleStateValueType.Number) return (current == null ? definition.initialNumber : current.numberValue).ToString();
            if (definition.valueType == RuleStateValueType.Boolean) return (current == null ? definition.initialBool : current.boolValue) ? "참" : "거짓";
            var values = current?.setValue ?? definition.initialSet ?? new List<string>();
            return values.Count == 0 ? "{}" : "{" + string.Join(", ", values.Take(6).Select(value => SafeText(value, "값"))) + (values.Count > 6 ? ", …" : string.Empty) + "}";
        }

        private static string StateScopeText(RuleStateScope scope, string scopeId)
        {
            var name = scope == RuleStateScope.Run ? "원정" : scope == RuleStateScope.Turn ? "턴" : scope == RuleStateScope.Faction ? "세력" : scope == RuleStateScope.Unit ? "유닛" : scope == RuleStateScope.Building ? "건물" : "타일";
            return string.IsNullOrWhiteSpace(scopeId) ? name : name + " " + SafeText(scopeId, "대상");
        }

        private static string StateReferenceText(StateReferenceV1 reference)
        {
            if (reference == null) return "상태 없음";
            return StateScopeText(reference.scope, reference.scopeId) + "의 " + SafeText(reference.key, "상태");
        }

        private static string EffectsText(IEnumerable<EffectNode> effects)
        {
            var entries = (effects ?? Enumerable.Empty<EffectNode>()).Where(effect => effect != null).Take(RuleLimits.MaxEffectsPerRule).Select(EffectText).ToList();
            return entries.Count == 0 ? "효과 없음" : string.Join(" → ", entries);
        }

        private static string EffectText(EffectNode effect)
        {
            if (effect.type == EffectType.TypedState) return MutationText(effect.stateMutation);
            if (effect.type == EffectType.Resource) return ResourceName(effect.resource) + " " + Signed(effect.amount);
            if (effect.type == EffectType.Sp) return "SP " + Signed(effect.amount);
            if (effect.type == EffectType.Relation) return "관계 " + Signed(effect.amount) + " [" + SafeText(effect.target, "대상 세력") + "]";
            if (effect.type == EffectType.Status) return "상태 " + SafeText(effect.key, "효과") + " = " + effect.amount;
            if (effect.type == EffectType.Spawn) return SafeText(effect.target, "세력") + " 유닛 " + Math.Max(0, effect.amount) + " 생성";
            if (effect.type == EffectType.UnlockAction) return "특수 행동 ‘" + SafeText(effect.key, "새 행동") + "’ 해금";
            if (effect.type == EffectType.Schedule) return Math.Max(0, effect.delay) + "턴 뒤 " + SafeText(effect.key, "예약 이벤트");
            return "유닛 " + SafeText(effect.target, "대상") + "의 세력을 " + SafeText(effect.key, "새 세력") + "로 변경";
        }

        private static string MutationText(StateMutationV1 mutation)
        {
            if (mutation == null) return "유효하지 않은 상태 효과";
            var target = StateReferenceText(mutation.state);
            if (mutation.op == StateMutationOp.Toggle) return target + " 반전";
            if (mutation.op == StateMutationOp.SetAdd) return target + "에 ‘" + SafeText(mutation.element, "값") + "’ 추가";
            if (mutation.op == StateMutationOp.SetRemove) return target + "에서 ‘" + SafeText(mutation.element, "값") + "’ 제거";
            if (mutation.setValues != null && mutation.setValues.Count > 0) return target + " = {" + string.Join(", ", mutation.setValues.Take(6).Select(value => SafeText(value, "값"))) + "}";
            if (mutation.numberValue != null)
            {
                var visited = 0;
                return target + (mutation.op == StateMutationOp.Add ? " += " : " = ") + NumberExpressionText(mutation.numberValue, 1, ref visited);
            }
            return target + " = " + (mutation.boolValue ? "참" : "거짓");
        }

        private static string NormalizeScopeId(RuleStateScope scope, string scopeId)
        {
            if (scope == RuleStateScope.Run || scope == RuleStateScope.Turn) return string.Empty;
            if (scope == RuleStateScope.Faction && string.Equals(scopeId, "player", StringComparison.OrdinalIgnoreCase)) return "faction:1";
            return (scopeId ?? string.Empty).ToLowerInvariant();
        }

        private static bool IsSafeColor(string color)
        {
            return color != null && color.Length == 7 && color[0] == '#' && color.Skip(1).All(Uri.IsHexDigit);
        }

        private static string Signed(int value) => value > 0 ? "+" + value : value.ToString();

        private static string TriggerText(RuleEventType trigger)
        {
            if (trigger == RuleEventType.TurnStart) return "턴 시작";
            if (trigger == RuleEventType.TurnEnd) return "턴 종료";
            if (trigger == RuleEventType.Move) return "유닛 이동";
            if (trigger == RuleEventType.Attack) return "공격";
            if (trigger == RuleEventType.Kill) return "처치";
            if (trigger == RuleEventType.Gather) return "채집 또는 수렵";
            if (trigger == RuleEventType.Build) return "건설 또는 강화";
            if (trigger == RuleEventType.Trade) return "거래";
            if (trigger == RuleEventType.RelationChanged) return "세력 관계 변화";
            if (trigger == RuleEventType.Capture) return "점령 성공";
            return trigger == RuleEventType.TileEntered ? "타일 진입" : SafeText(trigger.ToString(), "세계 상태 변화");
        }

        private static string VictoryImpactText(RuleNodeV1 rule, IEnumerable<VictoryContractV1> contracts)
        {
            var affectedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var effect in rule.effects ?? new List<EffectNode>())
            {
                if (effect == null) continue;
                if (effect.type == EffectType.Resource && effect.resource == ResourceType.Coin) affectedKeys.Add("coin");
                if (effect.type == EffectType.Relation) affectedKeys.Add("alliances");
                if (effect.type == EffectType.Spawn || effect.type == EffectType.FactionSwitch) affectedKeys.Add("territory");
            }
            if (rule.trigger == RuleEventType.Kill) affectedKeys.Add("kills");
            if (rule.trigger == RuleEventType.Build) { affectedKeys.Add("build"); affectedKeys.Add("buildings"); }
            if (rule.trigger == RuleEventType.Move) affectedKeys.Add("move");
            if (rule.trigger == RuleEventType.Trade) affectedKeys.Add("trade");

            var titles = (contracts ?? Enumerable.Empty<VictoryContractV1>())
                .Where(contract => contract != null && affectedKeys.Contains(contract.progressKey ?? string.Empty))
                .Select(contract => SafeText(contract.title, "승리 계약"))
                .Distinct()
                .Take(3)
                .ToList();
            return titles.Count == 0 ? "현재 계약에 직접 연결되지 않음" : string.Join(", ", titles) + " 진행에 영향 가능";
        }

        private static string SelectorText(string selector)
        {
            if (string.IsNullOrWhiteSpace(selector) || string.Equals(selector, "any", StringComparison.OrdinalIgnoreCase)) return "모든 살아있는 유닛";
            if (string.Equals(selector, "player", StringComparison.OrdinalIgnoreCase)) return "아군 유닛";
            if (selector.StartsWith("unit:", StringComparison.OrdinalIgnoreCase)) return "유닛 " + SafeText(selector.Substring(5), "지정 대상");
            if (selector.StartsWith("faction:", StringComparison.OrdinalIgnoreCase)) return "세력 " + SafeText(selector.Substring(8), "지정 세력") + " 유닛";
            return SafeText(selector, "지정 대상");
        }

        private static string TileSelectorText(string selector)
        {
            if (string.IsNullOrWhiteSpace(selector) || string.Equals(selector, "any", StringComparison.OrdinalIgnoreCase)) return "지도 타일 중 하나";
            if (string.Equals(selector, "player_tile", StringComparison.OrdinalIgnoreCase)) return "대표 아군이 있는 타일";
            return SafeText(selector, "지정 타일");
        }
        private static string DynamicActionCost(DynamicActionV1 action) =>
            "SP " + Math.Max(0, action.spCost) +
            (action.resourceAmount > 0 ? " · " + ResourceName(action.resourceCost) + " " + action.resourceAmount : string.Empty) +
            (DynamicActionTargeting.RequiresTarget(action) ? " · " + DynamicActionTargeting.DescribeSelector(action.targetSelector) : string.Empty);
        private static string ResourceName(ResourceType type) => type == ResourceType.Food ? "식량" : type == ResourceType.Wood ? "목재" : type == ResourceType.Stone ? "석재" : type == ResourceType.Iron ? "철" : type == ResourceType.Coin ? "화폐" : "자원";
        private static string SafeText(string value, string fallback)
        {
            var text = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
            return text.Replace('<', '＜').Replace('>', '＞');
        }
        private static string ActionLabel(CommandType type) => type == CommandType.Move ? "이동\nSP 1" : type == CommandType.Gather ? "채집\nSP 2" : type == CommandType.Hunt ? "수렵\nSP 2" : type == CommandType.Attack ? "공격\nSP 2" : type == CommandType.Trade ? "거래\nSP 2" : type == CommandType.Persuade ? "설득\nSP 2" : type == CommandType.Hire ? "고용\nSP 2" : type == CommandType.Build ? "건설\nSP 3" : type == CommandType.Capture ? "점령\nSP 2" : "강화\nSP 3";
        private static Color ActionColor(CommandType type) => type == CommandType.Attack ? new Color(0.58f, 0.2f, 0.22f, 1) : type == CommandType.Move || type == CommandType.Capture ? new Color(0.12f, 0.42f, 0.58f, 1) : type == CommandType.Trade || type == CommandType.Persuade || type == CommandType.Hire ? new Color(0.45f, 0.34f, 0.16f, 1) : new Color(0.2f, 0.4f, 0.27f, 1);

        private void SectionHeader(Transform parent, string text, Color color, int size)
        {
            var label = Label("Header", parent, text, size, color, TextAnchor.MiddleLeft, FontStyle.Bold);
            Anchor(label.gameObject, new Vector2(0, 1), new Vector2(1, 1), new Vector2(18, -14), new Vector2(-36, 34), new Vector2(0, 1));
        }

        private GameObject CreateObject(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go;
        }

        private GameObject PanelObject(string name, Transform parent, Color color)
        {
            var go = CreateObject(name, parent);
            var image = go.AddComponent<Image>();
            image.color = color;
            return go;
        }

        private TextMeshProUGUI Label(string name, Transform parent, string value, int size, Color color, TextAnchor alignment, FontStyle style)
        {
            var go = CreateObject(name, parent);
            var text = go.AddComponent<TextMeshProUGUI>();
            text.font = font;
            text.fontSize = size;
            text.fontStyle = style == FontStyle.Bold ? FontStyles.Bold : FontStyles.Normal;
            text.color = color;
            text.alignment = ToTextAlignment(alignment);
            text.text = value;
            text.richText = true;
            text.enableWordWrapping = true;
            text.overflowMode = TextOverflowModes.Truncate;
            text.raycastTarget = false;
            return text;
        }

        private static TextAlignmentOptions ToTextAlignment(TextAnchor alignment)
        {
            switch (alignment)
            {
                case TextAnchor.UpperLeft: return TextAlignmentOptions.TopLeft;
                case TextAnchor.UpperCenter: return TextAlignmentOptions.Top;
                case TextAnchor.UpperRight: return TextAlignmentOptions.TopRight;
                case TextAnchor.MiddleLeft: return TextAlignmentOptions.MidlineLeft;
                case TextAnchor.MiddleCenter: return TextAlignmentOptions.Center;
                case TextAnchor.MiddleRight: return TextAlignmentOptions.MidlineRight;
                case TextAnchor.LowerLeft: return TextAlignmentOptions.BottomLeft;
                case TextAnchor.LowerCenter: return TextAlignmentOptions.Bottom;
                case TextAnchor.LowerRight: return TextAlignmentOptions.BottomRight;
                default: return TextAlignmentOptions.TopLeft;
            }
        }

        private Button ButtonObject(string name, Transform parent, string label, Color color, Action callback)
        {
            var go = PanelObject(name, parent, color);
            var button = go.AddComponent<Button>();
            button.targetGraphic = go.GetComponent<Image>();
            var colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.12f, 1.12f, 1.12f, 1);
            colors.pressedColor = new Color(0.75f, 0.75f, 0.75f, 1);
            colors.disabledColor = new Color(0.45f, 0.45f, 0.45f, 0.55f);
            colors.colorMultiplier = 1;
            button.colors = colors;
            if (callback != null) button.onClick.AddListener(() => callback());
            var text = Label("Label", go.transform, label, 15, Color.white, TextAnchor.MiddleCenter, FontStyle.Bold);
            Stretch(text.rectTransform);
            return button;
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            rect.localScale = Vector3.one;
        }

        private static void Stretch(GameObject go)
        {
            if (go != null) Stretch(go.GetComponent<RectTransform>());
        }

        private static void Anchor(GameObject go, Vector2 min, Vector2 max, Vector2 position, Vector2 size, Vector2 pivot)
        {
            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = min;
            rect.anchorMax = max;
            rect.pivot = pivot;
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
            rect.localScale = Vector3.one;
        }
    }
}
