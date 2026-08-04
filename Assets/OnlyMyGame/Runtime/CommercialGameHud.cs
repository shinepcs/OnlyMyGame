using System;
using System.Collections.Generic;
using System.Linq;
using OnlyMyGame.Core;
using UnityEngine;
using UnityEngine.UI;

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
        private readonly List<Text> dynamicActionLabels = new List<Text>();
        private readonly List<DynamicActionV1> displayedDynamicActions = new List<DynamicActionV1>();
        private GameController controller;
        private Font font;
        private GameObject interfaceRoot;
        private GameObject mainMenu;
        private GameObject pauseMenu;
        private GameObject ledgerPanel;
        private GameObject modalPanel;
        private GameObject outcomePanel;
        private Text headline;
        private Text resources;
        private Text objective;
        private Text relations;
        private Text rules;
        private Text journal;
        private Text selectionTitle;
        private Text selectionBody;
        private Text selectionHint;
        private Text dynamicActionSummary;
        private Text queue;
        private Text planningHint;
        private Text service;
        private Text endTurnLabel;
        private Text toast;
        private Text modalTitle;
        private Text modalBody;
        private Text outcomeTitle;
        private Text outcomeBody;
        private Text continueLabel;
        private Text mainMenuStatus;
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
        private Text ledgerBody;
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
            font = Resources.Load<Font>("Fonts/NanumGothic-Regular") ?? Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

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
            Anchor(headline.gameObject, new Vector2(0, 0), new Vector2(0, 1), new Vector2(24, 0), new Vector2(300, 0), new Vector2(0, 0.5f));
            resources = Label("Resources", top.transform, string.Empty, 19, Color.white, TextAnchor.MiddleCenter, FontStyle.Normal);
            Anchor(resources.gameObject, new Vector2(0.18f, 0), new Vector2(0.82f, 1), Vector2.zero, Vector2.zero, new Vector2(0.5f, 0.5f));
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
            endTurnLabel = endTurnButton.GetComponentInChildren<Text>();

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
                CommandType.Hire, CommandType.Build, CommandType.Upgrade
            };
            for (var i = 0; i < actions.Length; i++)
            {
                var command = actions[i];
                var button = ButtonObject("Action_" + command, parent, ActionLabel(command), ActionColor(command), () => controller.BeginCommand(command));
                var column = i % 3;
                var row = i / 3;
                Anchor(button.gameObject, new Vector2(0, 1), new Vector2(0, 1), new Vector2(18 + column * 114, -184 - row * 66), new Vector2(104, 56), new Vector2(0, 1));
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
                var label = button.GetComponentInChildren<Text>();
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
            continueLabel = continueButton.GetComponentInChildren<Text>();
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
            ledgerBody.verticalOverflow = VerticalWrapMode.Overflow;
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

        private void BuildModal()
        {
            modalPanel = PanelObject("RuleModal", interfaceRoot.transform, new Color(0.01f, 0.02f, 0.04f, 0.84f));
            Stretch(modalPanel);
            var card = PanelObject("RuleCard", modalPanel.transform, Ink);
            Anchor(card, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(760, 500), new Vector2(0.5f, 0.5f));
            modalTitle = Label("ModalTitle", card.transform, "", 30, Gold, TextAnchor.MiddleCenter, FontStyle.Bold);
            Anchor(modalTitle.gameObject, new Vector2(0, 1), new Vector2(1, 1), new Vector2(28, -34), new Vector2(-56, 50), new Vector2(0.5f, 1));
            modalBody = Label("ModalBody", card.transform, "", 17, Color.white, TextAnchor.UpperLeft, FontStyle.Normal);
            modalBody.resizeTextForBestFit = true;
            modalBody.resizeTextMinSize = 13;
            modalBody.resizeTextMaxSize = 17;
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
            outcomeBody.resizeTextForBestFit = true;
            outcomeBody.resizeTextMinSize = 15;
            outcomeBody.resizeTextMaxSize = 18;
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

            headline.text = "ONLY MY GAME   ·   TURN " + game.turn + "   ·   행운 " + game.luck;
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

            if (!mainMenu.activeSelf && controller.IsBlocked)
            {
                modalTitle.text = "세계 규칙 수신 중단";
                modalBody.text = controller.BlockReason + "\n\n안전한 규칙을 받기 전에는 다음 턴을 시작하지 않습니다. 연결을 확인한 뒤 다시 요청하거나 현재 상태를 저장하세요.";
                SetModalMode(ModalMode.Blocked);
            }
            else if (modalMode == ModalMode.Blocked) SetModalMode(ModalMode.None);

            if (!mainMenu.activeSelf && game.outcome != RunOutcome.Ongoing)
            {
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
            if (mainMenu.activeSelf || modalMode != ModalMode.None || outcomePanel.activeSelf || ledgerPanel.activeSelf) return;
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
            if (show && (mainMenu.activeSelf || modalMode != ModalMode.None || outcomePanel.activeSelf)) return;
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
                var label = modalRetryButton.GetComponentInChildren<Text>();
                if (label != null) label.text = delay > 0 ? "다시 요청  (" + delay + "초)" : "AI 규칙 다시 요청";
            }
            if (Input.GetKeyDown(KeyCode.Tab))
            {
                if (ledgerPanel.activeSelf) ToggleLedger(false);
                else ToggleLedger(true);
            }
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                if (ledgerPanel.activeSelf) ToggleLedger(false);
                else if (modalMode == ModalMode.RuleAnnouncement) DismissRuleAnnouncement();
                else if (modalMode == ModalMode.NewRunConfirmation) CancelModal();
                else if (modalMode == ModalMode.Blocked || mainMenu.activeSelf || outcomePanel.activeSelf) return;
                else if (controller.IsTargeting) controller.CancelTargeting();
                else TogglePause();
            }
            if (Input.GetKeyDown(KeyCode.Space) && endTurnButton.interactable && !mainMenu.activeSelf && !pauseMenu.activeSelf && !ledgerPanel.activeSelf && modalMode == ModalMode.None && !outcomePanel.activeSelf)
                controller.EndTurnFromHud();
        }

        private void RenderUnavailableState()
        {
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
                dynamicActionSummary.text = "규칙이 만든 행동 " + source.Count + "개" + (extra > 0 ? " · 장부에 " + extra + "개 더 표시" : " · 비용과 재사용 턴을 확인하세요");
            }

            for (var i = 0; i < dynamicActionButtons.Count; i++)
            {
                var hasAction = i < displayedDynamicActions.Count;
                dynamicActionButtons[i].gameObject.SetActive(hasAction);
                if (!hasAction) continue;

                var action = displayedDynamicActions[i];
                string reason;
                var ready = CanUseDynamicAction(action, game, player, out reason);
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
            if (player == null || !CanUseDynamicAction(displayedDynamicActions[slot], game, player, out reason))
            {
                Toast(string.IsNullOrWhiteSpace(reason) ? "이 특수 행동을 지금 사용할 수 없습니다." : reason, Gold);
                return;
            }

            dynamicActionHandler(displayedDynamicActions[slot]);
            Render();
        }

        private bool CanUseDynamicAction(DynamicActionV1 action, GameSnapshotV1 game, FactionState player, out string reason)
        {
            if (dynamicActionHandler == null)
            {
                reason = "실행 연결 대기";
                return false;
            }
            if (controller.CanRunDynamic(action))
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
            reason = "조건 또는 효과 확인 필요";
            return false;
        }

        private string BuildLedgerText()
        {
            var game = controller == null ? null : controller.Game;
            if (game == null) return "원정 규칙을 불러오는 중입니다.";

            var sections = new List<string>();
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
                           "발생/영향: " + SafeText(rule.worldCue, "세계 전체") + "\n" +
                           "조건: " + ConditionText(rule.condition) + "\n" +
                           "효과: " + SafeText(rule.description, "효과 설명 없음");
                }).ToList();
            sections.Add("<color=#FFD05A><b>세계 규칙</b></color>\n" + (ruleEntries.Count == 0 ? "현재 기록된 규칙이 없습니다." : string.Join("\n\n", ruleEntries)));

            var contracts = game.victoryContracts ?? new List<VictoryContractV1>();
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
            if (modalRetryButton != null) modalRetryButton.gameObject.SetActive(mode == ModalMode.Blocked);
            if (modalSaveButton != null) modalSaveButton.gameObject.SetActive(mode == ModalMode.Blocked);
            if (modalAcceptButton != null)
            {
                modalAcceptButton.gameObject.SetActive(mode == ModalMode.RuleAnnouncement || mode == ModalMode.NewRunConfirmation);
                var label = modalAcceptButton.GetComponentInChildren<Text>();
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
            if (condition == null || condition.op == CompareOp.Always) return "항상";
            if (condition.op == CompareOp.HasTag) return "아군 태그: " + SafeText(condition.text, "지정 태그");
            if (condition.op == CompareOp.OwnerIs) return "보이는 타일 소유 세력 = " + condition.value;
            var comparison = condition.op == CompareOp.Equal ? "=" : condition.op == CompareOp.GreaterOrEqual ? "이상" : "이하";
            return SafeText(condition.text, SafeText(condition.left, "상태") + " " + condition.value + " " + comparison);
        }
        private static string DynamicActionCost(DynamicActionV1 action) => "SP " + Math.Max(0, action.spCost) + (action.resourceAmount > 0 ? " · " + ResourceName(action.resourceCost) + " " + action.resourceAmount : string.Empty);
        private static string ResourceName(ResourceType type) => type == ResourceType.Food ? "식량" : type == ResourceType.Wood ? "목재" : type == ResourceType.Stone ? "석재" : type == ResourceType.Iron ? "철" : type == ResourceType.Coin ? "화폐" : "자원";
        private static string SafeText(string value, string fallback)
        {
            var text = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
            return text.Replace('<', '＜').Replace('>', '＞');
        }
        private static string ActionLabel(CommandType type) => type == CommandType.Move ? "이동\nSP 1" : type == CommandType.Gather ? "채집\nSP 2" : type == CommandType.Hunt ? "수렵\nSP 2" : type == CommandType.Attack ? "공격\nSP 2" : type == CommandType.Trade ? "거래\nSP 2" : type == CommandType.Persuade ? "설득\nSP 2" : type == CommandType.Hire ? "고용\nSP 2" : type == CommandType.Build ? "건설\nSP 3" : "강화\nSP 3";
        private static Color ActionColor(CommandType type) => type == CommandType.Attack ? new Color(0.58f, 0.2f, 0.22f, 1) : type == CommandType.Move ? new Color(0.12f, 0.42f, 0.58f, 1) : type == CommandType.Trade || type == CommandType.Persuade || type == CommandType.Hire ? new Color(0.45f, 0.34f, 0.16f, 1) : new Color(0.2f, 0.4f, 0.27f, 1);

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

        private Text Label(string name, Transform parent, string value, int size, Color color, TextAnchor alignment, FontStyle style)
        {
            var go = CreateObject(name, parent);
            var text = go.AddComponent<Text>();
            text.font = font;
            text.fontSize = size;
            text.fontStyle = style;
            text.color = color;
            text.alignment = alignment;
            text.text = value;
            text.supportRichText = true;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            text.raycastTarget = false;
            return text;
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
