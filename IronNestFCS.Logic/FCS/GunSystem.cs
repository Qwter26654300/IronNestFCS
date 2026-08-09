using System.Collections;
using System;
using System.Linq;
using Il2Cpp;
using Il2CppTMPro;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic.FCS;


public enum BulletType {
    EMPT = 0,
    AP = 1,
    APHE = 2,
    ATMC = 3,
    CLMN = 4,
    CYAN = 5,
    DRIL = 6,
    EQKE = 7,
    FLCH = 8,
    HCHE = 9,
    HE = 10,
    INCN = 11,
    LE = 12,
    PLCM = 13,
    PHGN = 14,
    PRPG = 15,
    SMK = 16,
    STAR = 17,
    TEAR = 18,
    THRM = 19,
    WP = 20,
}

public class GunSystem {
    private string _surfix = "";

    private CylinderShellSelector? shellSelector;
    
    private List<string?> bullets = new();
    private LookAtTarget? nextBulletButton;
    private LookAtTarget? loadBulletButton;
    private List<LookAtTarget> powderButtons = new();
    private LookAtTarget? loadPowderButton;
    private GunController? gunController;
    private LinearSliderInteractable? elevationLever;
    private OdometerDisplay? remainingCharges;
    private OdometerDisplay? actualCharges;

    private TextMeshPro shellId;
    public bool LastActionFailed { get; private set; }
    public bool LastElevationReady { get; private set; }
    public bool LastElevationInterrupted { get; private set; }

    public bool TryBind(string surfix) {
        this._surfix = surfix;
        
        var gunSystem = GameObject.Find("Gun System " + surfix).transform;
        var reloadingConsole = gunSystem.Find("--Reloading Console");
        if (reloadingConsole == null) {
            MelonLogger.Error($"[FCS] GunSystem {surfix}: Can't find --Reloading Console");
            return false;
        }

        var chargeDisplays = reloadingConsole.GetComponentsInChildren<OdometerDisplay>(true);
        remainingCharges = chargeDisplays.Length > 0 ? chargeDisplays[0] : null;
        actualCharges = chargeDisplays.Length > 1 ? chargeDisplays[^1] : null;
        
        nextBulletButton = 
            reloadingConsole.Find("Universal Button Move Cylinder")
                .GetComponent<LookAtTarget>();    
        shellSelector = gunSystem.GetComponentInChildren<CylinderShellSelector>();
        
        shellId = GameObject.Find("Shell ID " + surfix)
            .GetComponent<TextMeshPro>();
        var loadShell = reloadingConsole.FindChild("Universal Button Load shell Rammer");
        if (loadShell == null) {
            MelonLogger.Error($"[FCS] GunSystem {surfix}: Can't find Universal Button Load shell Rammer");
            return false;
        }
        loadBulletButton = loadShell.GetComponent<LookAtTarget>();

        var powderController = reloadingConsole.Find("PowderChargeController");
        for (var i = 0; i < powderController.childCount; ++i) {
            var child = powderController.GetChild(i);
            if (!child.name.StartsWith("Button Dispencer")) continue;
            var button = child.GetComponent<LookAtTarget>();
            if (button == null) {
                MelonLogger.Error($"[FCS] GunSystem {surfix}: Found {child.name} but lack of LookAtTarget Component");
                return false;
            }
            powderButtons.Add(button);
        }

        loadPowderButton = reloadingConsole.FindChild("Universal Button Charge Rammer (1)").GetComponent<LookAtTarget>();
        gunController = GameObject.Find("Gun"+surfix).GetComponent<GunController>();
        elevationLever = GameObject.Find(".Elevation Lever Baseplate")?.transform.FindChild(".Elevation Lever " + surfix)
            .GetComponent<LinearSliderInteractable>();
        return true;
    }
    
    public bool CanFire() {
        return gunController.CanFire;
    }

    public bool HasFired() {
        return gunController.pendingReload;
    }

    public float CurrentElevation() {
        return gunController.CurrentElevation;
    }

    public IEnumerator SetElevation(float elevation, Action? maintain = null) {
        LastElevationReady = false;
        LastElevationInterrupted = false;
        if (Mathf.Abs(gunController.CurrentElevation - elevation) <= 0.15f) {
            LastElevationReady = true;
            yield break;
        }
        maintain?.Invoke();
        elevationLever.SetSliderValue(elevation);
        yield return new WaitForSeconds(0.1f);
        var waited = 0f;
        while (Mathf.Abs(gunController.CurrentElevation - elevation) > 0.15f && waited < 8f) {
            maintain?.Invoke();
            if (!Application.isFocused || Time.timeScale <= 0f) {
                LastElevationInterrupted = true;
                yield return null;
                continue;
            }
            elevationLever.SetSliderValue(elevation);
            yield return new WaitForSeconds(0.2f);
            waited += 0.2f;
        }
        LastElevationReady = Mathf.Abs(gunController.CurrentElevation - elevation) <= 0.15f;
        if (!LastElevationReady) {
            MelonLogger.Warning($"[FCS] GunSystem {_surfix}: SetElevation not exact. target={elevation:F2}, current={gunController.CurrentElevation:F2}");
        }
    }
    
    public string? BulletInChamber() {
        return gunController?.ChamberedShellBlueprint?.shellDefinition?.ShellId;
    }

    public string? DisplayedShellId() {
        return string.IsNullOrWhiteSpace(shellId?.text) ? null : shellId.text;
    }

    public bool TryGetLoadedBulletType(out BulletType bulletType, out string detail) {
        bulletType = default;
        var controllerId = BulletInChamber();
        var displayId = DisplayedShellId();
        var controllerOk = TryParseShellId(controllerId, out var controllerType);
        var displayOk = TryParseShellId(displayId, out var displayType);
        var loadedLike = controllerId != null || CanFire() || SelectedPowderCount() > 0;

        if (loadedLike && controllerOk && displayOk && controllerType != displayType && displayType != BulletType.EMPT) {
            bulletType = displayType;
            detail = $"{displayType} (display={NormalizeShellId(displayId)}, controller={NormalizeShellId(controllerId)})";
            return true;
        }

        if (controllerOk && controllerType != BulletType.EMPT) {
            bulletType = controllerType;
            detail = controllerType.ToString();
            return true;
        }

        if (loadedLike && displayOk && displayType != BulletType.EMPT) {
            bulletType = displayType;
            detail = $"{displayType} (display={NormalizeShellId(displayId)})";
            return true;
        }

        detail = controllerId ?? displayId ?? "empty";
        return false;
    }

    public bool IsChambered(BulletType type) {
        return TryGetLoadedBulletType(out var loadedType, out _) && loadedType == type;
    }
    
    public bool IsChamberEmpty() {
        return BulletInChamber() == null;
    }

    private void RefreshBullets() {
        bullets.Clear();
        if (shellSelector == null) return;
        foreach (var shell in shellSelector.bullets) {
            bullets.Add(shell?.GetComponent<ShellBlueprint>()?.shellDefinition?.ShellId);
        }
    }

    public bool TryGetFirstBulletInCylinder(out BulletType bulletType) {
        RefreshBullets();
        foreach (var bullet in bullets) {
            if (TryParseShellId(bullet, out bulletType)) {
                return true;
            }
        }

        bulletType = default;
        return false;
    }

    public IEnumerator NextBullet() {
        if (nextBulletButton == null) {
            MelonLogger.Error($"[FCS] GunSystem {_surfix}: NextBulletButton unbound");
            yield break;
        }
        yield return FcsSceneInteractor.WaitAndClick(nextBulletButton, label: $"{_surfix}.NextBullet");
    }
    
    /// <summary>
    /// 装填指定弹种：先把弹仓转到目标弹，再按装填。转弹仓每步之间要等 1 秒
    /// （游戏有转动动画/物理）。返回 IEnumerator，调用方用 yield return 等待它跑完。
    /// 必须走协程而非 async：continuation 要留在主线程才能安全访问 IL2CPP 对象。
    /// </summary>
    public IEnumerator LoadBullet(BulletType type) {
        RefreshBullets();
        var index = bullets.FindIndex(item => TryParseShellId(item, out var parsed) && parsed == type);
        if (index == -1) {
            MelonLogger.Error($"[FCS] GunSystem {_surfix}: " +
                              $"No {type} available in cylinder, current bullets: {string.Join(", ", bullets)}");
            yield break;
        }
        
        for (var i = 0; i < bullets.Count; ++i) {
            if (TryParseShellId(bullets[0], out var selected) && selected == type) {
                break;
            };
            yield return NextBullet();
            yield return new WaitForSeconds(1.5f);
            RefreshBullets();
        }
        if (!TryParseShellId(bullets[0], out var front) || front != type) {
            MelonLogger.Error($"[FCS] GunSystem {_surfix}: Can't find {type} after rotation, " +
                              $"current: {string.Join(", ", bullets)}");
            yield break;
        }
        yield return FcsSceneInteractor.WaitAndClick(loadBulletButton!, label: $"{_surfix}.LoadBullet", timeoutSeconds: 45f);
    }

    private IEnumerator SelectPowder(int count) {
        LastActionFailed = false;
        if (count > 0 && powderButtons.Count > 0) {
            yield return WaitPowderButtonReady(0, 45f);
            if (LastActionFailed) yield break;
        }
        for (var i = 0; i < count; i++) {
            if (i >= powderButtons.Count) {
                MelonLogger.Error($"[GunSystem] SelectPowder: out of range, i={i} count={count}");
                yield break;
            }
            if (powderButtons[i] == null) {
                MelonLogger.Error($"[GunSystem] SelectPowder: button {i} is null");
                LastActionFailed = true;
                yield break;
            }
            if (!powderButtons[i].isActive) {
                MelonLogger.Error($"[GunSystem] SelectPowder: button {i} is not active");
                LastActionFailed = true;
                yield break;
            }
            yield return FcsSceneInteractor.WaitAndClick(powderButtons[i], label: $"{_surfix}.SelectPowder[{i}]");
        }
    }

    private IEnumerator WaitPowderButtonReady(int index, float timeoutSeconds) {
        if (index >= powderButtons.Count || powderButtons[index] == null) yield break;
        var waited = 0f;
        while (!powderButtons[index].isActive && waited < timeoutSeconds) {
            yield return new WaitForSeconds(0.5f);
            if (Application.isFocused && Time.timeScale > 0f) {
                waited += 0.5f;
            }
        }
        if (!powderButtons[index].isActive) {
            MelonLogger.Error($"[GunSystem] WaitPowderButtonReady timeout, index={index}");
            LastActionFailed = true;
        }
    }

    private IEnumerator WaitLoadPowderButtonReady(float timeoutSeconds) {
        RefreshLoadPowderButtonIfInvalid();
        if (loadPowderButton == null) {
            MelonLogger.Error($"[GunSystem] WaitLoadPowderButtonReady: load powder button is null");
            LastActionFailed = true;
            yield break;
        }
        var waited = 0f;
        while (!loadPowderButton.isActive && !CanFire() && waited < timeoutSeconds) {
            RefreshLoadPowderButtonIfInvalid();
            if (loadPowderButton == null) {
                MelonLogger.Error($"[GunSystem] WaitLoadPowderButtonReady: load powder button lost during wait");
                LastActionFailed = true;
                yield break;
            }
            yield return new WaitForSeconds(0.5f);
            if (Application.isFocused && Time.timeScale > 0f) {
                waited += 0.5f;
            }
        }
        if (CanFire()) {
            yield break;
        }
        if (!loadPowderButton.isActive) {
            MelonLogger.Error($"[GunSystem] WaitLoadPowderButtonReady timeout");
            LastActionFailed = true;
        }
    }

    private void RefreshLoadPowderButtonIfInvalid() {
        if (loadPowderButton != null && loadPowderButton.gameObject != null) {
            return;
        }
        var gunSystem = GameObject.Find("Gun System " + _surfix)?.transform;
        var reloadingConsole = gunSystem?.Find("--Reloading Console");
        loadPowderButton = reloadingConsole?.FindChild("Universal Button Charge Rammer (1)")
            ?.GetComponent<LookAtTarget>();
        if (loadPowderButton == null) {
            MelonLogger.Warning($"[GunSystem] RefreshLoadPowderButton: rammer button missing for {_surfix}");
        }
        else {
            MelonLogger.Msg($"[GunSystem] RefreshLoadPowderButton: rebound rammer button for {_surfix}");
        }
    }

    public int SelectedPowderCount() {
        var count = actualCharges != null ? (int)actualCharges.CurrentNumber : 0;
        return count;
    }

    public IEnumerator LoadPowder(int count) {
        if (CanFire()) {
            yield break;
        }
        yield return new WaitForSeconds(0.5f);
        if (CanFire()) {
            yield break;
        }
        yield return SelectPowder(count);
        if (LastActionFailed) yield break;
        yield return new WaitForSeconds(0.5f);
        if (CanFire()) {
            yield break;
        }
        yield return PushPowder();
    }

    public IEnumerator CompletePowderSelection(int targetCount) {
        yield return CompletePowderSelectionFrom(SelectedPowderCount(), targetCount);
    }

    public IEnumerator CompletePowderSelectionFrom(int currentCount, int targetCount) {
        LastActionFailed = false;
        if (CanFire()) {
            yield break;
        }
        if (currentCount >= targetCount) {
            yield return PushPowder();
            yield break;
        }

        for (var i = currentCount; i < targetCount; i++) {
            if (CanFire()) {
                yield break;
            }
            yield return WaitPowderButtonReady(i, 45f);
            if (LastActionFailed) yield break;
            if (i >= powderButtons.Count) {
                MelonLogger.Error($"[GunSystem] CompletePowderSelection: out of range, i={i} target={targetCount}");
                LastActionFailed = true;
                yield break;
            }
            if (!powderButtons[i].isActive) {
                MelonLogger.Error($"[GunSystem] CompletePowderSelection: button {i} is not active");
                LastActionFailed = true;
                yield break;
            }
            yield return FcsSceneInteractor.WaitAndClick(powderButtons[i], label: $"{_surfix}.AddPowder[{i}]");
        }
        yield return new WaitForSeconds(0.5f);
        if (CanFire()) {
            yield break;
        }
        yield return PushPowder();
    }

    public IEnumerator PushPowder() {
        LastActionFailed = false;
        yield return WaitLoadPowderButtonReady(45f);
        if (LastActionFailed) yield break;
        if (CanFire()) {
            yield break;
        }
        yield return FcsSceneInteractor.WaitAndClick(loadPowderButton!, label: $"{_surfix}.LoadPowder", timeoutSeconds: 45f);
    }

    public bool HaveBulletInCylinder(BulletType type) {
        RefreshBullets();
        return bullets.Any(item => TryParseShellId(item, out var parsed) && parsed == type);
    }

    public IEnumerator WaitUntilBulletInCylinder(BulletType type, float timeoutSeconds) {
        var waited = 0f;
        while (waited < timeoutSeconds) {
            if (HaveBulletInCylinder(type)) {
                yield break;
            }
            yield return new WaitForSeconds(0.25f);
            if (Application.isFocused && Time.timeScale > 0f) {
                waited += 0.25f;
            }
        }
    }

    public IEnumerator WaitUntilChambered(BulletType type, float timeoutSeconds) {
        var waited = 0f;
        while (waited < timeoutSeconds) {
            if (IsChambered(type)) {
                yield break;
            }
            yield return new WaitForSeconds(0.25f);
            if (Application.isFocused && Time.timeScale > 0f) {
                waited += 0.25f;
            }
        }
    }
    
    public bool HaveEmptyShellInCylinder() {
        RefreshBullets();
        return bullets.Contains(null);
    }

    private static bool TryParseShellId(string? shellId, out BulletType bulletType) {
        bulletType = default;
        var normalized = NormalizeShellId(shellId);
        if (string.IsNullOrWhiteSpace(normalized)) {
            return false;
        }

        if (normalized == "SMOKE") normalized = "SMK";
        if (Enum.TryParse(normalized, ignoreCase: true, out bulletType)) {
            return true;
        }

        foreach (var code in KnownShellCodes) {
            if (normalized.Contains(code) && Enum.TryParse(code, ignoreCase: true, out bulletType)) {
                return true;
            }
        }
        return false;
    }

    private static string NormalizeShellId(string? text) {
        if (string.IsNullOrWhiteSpace(text)) return "";
        return text.Trim()
            .Replace("\r", "")
            .Replace("\n", "")
            .Replace(" ", "")
            .Replace("Shell", "", StringComparison.OrdinalIgnoreCase)
            .ToUpperInvariant();
    }

    private static readonly string[] KnownShellCodes = {
        "APHE", "ATMC", "CLMN", "CYAN", "DRIL", "EQKE", "FLCH", "HCHE", "INCN", "PLCM",
        "PHGN", "PRPG", "STAR", "SMK", "TEAR", "THRM", "EMPT", "HE", "AP", "LE", "WP"
    };

    public IEnumerator WaitBackToIdle() {
        while (gunController.elevationChangeVelocity != 0) {
            if (!Application.isFocused || Time.timeScale <= 0f) {
                yield return null;
                continue;
            }
            yield return new WaitForSeconds(0.1f);
        }
        var waited = 0f;
        while (waited < 1.5f) {
            if (!Application.isFocused || Time.timeScale <= 0f) {
                yield return null;
                continue;
            }
            yield return new WaitForSeconds(0.1f);
            waited += 0.1f;
        }
    }

    public IEnumerator WaitFire(float timeoutSeconds, Action<bool> done) {
        var waited = 0f;
        while (!gunController.pendingReload && waited < timeoutSeconds) {
            if (!Application.isFocused || Time.timeScale <= 0f) {
                yield return null;
                continue;
            }
            yield return new WaitForSeconds(0.1f);
            waited += 0.1f;
        }
        done(gunController.pendingReload);
    }
    
    public int RemainingCharges() {
        return (int)remainingCharges.CurrentNumber;
    }

}
