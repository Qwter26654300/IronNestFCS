using Il2Cpp;
using MelonLoader;
using UnityEngine;
using System.Collections;

namespace IronNestFCS.Logic.FCS;

public class PurchaseDeck {
    private static readonly HashSet<BulletType> CommonShellCards = new() {
        BulletType.AP,
        BulletType.HCHE,
        BulletType.HE,
        BulletType.STAR,
        BulletType.SMK,
        BulletType.APHE,
        BulletType.ATMC,
    };

    private readonly Dictionary<BulletType, Transform> _shellCards = new();
    private Transform? _powderCard;
    private LookAtTarget? _buyButton;
    
    
    public bool TryBind() {
        _shellCards.Clear();
        _powderCard = null;
        var requisitionConsole = GameObject.Find("Requisition Console").transform;
        var cards = requisitionConsole.GetComponentsInChildren<PunchcardRuntime>();
        foreach (var card in cards) {
            var cardId = card.CurrentDefinition.ID;
            if (cardId == "PowderCharges") {
                _powderCard = card.transform;
                continue;
            }

            if (TryParseShellCard(cardId, out var shell)) {
                _shellCards[shell] = card.transform;
                if (!CommonShellCards.Contains(shell)) {
                    MelonLogger.Warning($"[FCS] PurchaseDeck: found uncommon shell purchase card {cardId} -> {shell}.");
                }
                continue;
            }

            if (cardId.EndsWith("Shell", StringComparison.OrdinalIgnoreCase)) {
                MelonLogger.Warning($"[FCS] PurchaseDeck: shell card {cardId} is not mapped to BulletType.");
            }
        }

        LogAvailableShellCards();
        
        _buyButton = requisitionConsole.FindChild("Universal Button").GetComponent<LookAtTarget>();
        
        return true;
    }

    public bool CanBuyShell(BulletType type) {
        return GetShellCard(type) != null;
    }

    private Transform? GetShellCard(BulletType type) {
        return _shellCards.TryGetValue(type, out var card) ? card : null;
    }

    private static bool TryParseShellCard(string cardId, out BulletType shell) {
        shell = default;
        if (!cardId.EndsWith("Shell", StringComparison.OrdinalIgnoreCase)) {
            return false;
        }

        var shellCode = cardId[..^"Shell".Length].ToUpperInvariant();
        if (shellCode == "SMOKE") {
            shellCode = "SMK";
        }
        if (shellCode == "PCLM") {
            shellCode = "PLCM";
        }

        return Enum.TryParse(shellCode, ignoreCase: true, out shell) && shell != BulletType.EMPT;
    }

    private void LogAvailableShellCards() {
        var common = _shellCards.Keys
            .Where(CommonShellCards.Contains)
            .OrderBy(shell => (int)shell)
            .ToList();
        var uncommon = _shellCards.Keys
            .Where(shell => !CommonShellCards.Contains(shell))
            .OrderBy(shell => (int)shell)
            .ToList();

        MelonLogger.Msg($"[FCS] PurchaseDeck: purchasable common shells: {FormatShellList(common)}");
        if (uncommon.Count > 0) {
            MelonLogger.Warning($"[FCS] PurchaseDeck: purchasable uncommon shells: {FormatShellList(uncommon)}");
        }
    }

    private static string FormatShellList(IReadOnlyCollection<BulletType> shells) {
        return shells.Count == 0 ? "none" : string.Join(", ", shells);
    }
    
    private DialInteractable GetLeftRightDial() {
        var consoleBox = GameObject.Find("Console Box").transform;
        return  consoleBox.GetComponentInChildren<DialInteractable>();
    }

    public IEnumerator BuyShell(BulletType type, LeftRight leftRight) {
        var card = GetShellCard(type);
        if (card == null) {
            MelonLogger.Error($"[FCS] BuyShell: Can't find {type} card");
            yield break;
        }
        var target = new Vector3(6.4814f, -2.4675f, -22.0968f);
        yield return FcsSceneInteractor.WaitUntilInteractive();
        card.position = target;
        card.GetComponent<DraggableItem>().MoveToSlot();
        yield return new WaitForSeconds(0.5f);
        
        yield return FcsSceneInteractor.WaitUntilInteractive();
        switch (leftRight) {
            case LeftRight.Left:
                GetLeftRightDial().SetDialValue(0);
                break;
            case LeftRight.Right:
                GetLeftRightDial().SetDialValue(1);
                break;
        }
        yield return FcsSceneInteractor.WaitAndClick(_buyButton, label: $"BuyShell.{type}.{leftRight}");
        yield return new WaitForSeconds(2f);
    }

    public IEnumerator BuyPowders() {
        if (_powderCard == null) {
            MelonLogger.Error("[FCS] BuyPowders: Can't find PowderCharges card");
            yield break;
        }
        yield return FcsSceneInteractor.WaitUntilInteractive();
        _powderCard.position = new Vector3(6.4814f, -2.4675f, -22.0968f);
        _powderCard.GetComponent<DraggableItem>().MoveToSlot();
        // 与 BuyShell 一致：等卡牌入槽稳定后再点购买，避免点击早于入槽导致本次采购无效。
        yield return new WaitForSeconds(0.5f);
        yield return FcsSceneInteractor.WaitAndClick(_buyButton, label: "BuyPowders");
        yield return new WaitForSeconds(2f);
    }
    
}
