using Il2Cpp;
using IronNestFCS.Abstractions;
using IronNestFCS.Logic.FCS;
using MelonLoader;
using UnityEngine;
using UnityEngine.InputSystem;

namespace IronNestFCS.Logic;

public class FcsModule : IFcsModule
{
    private const float ApEffectiveRadiusKm = 0.11f;
    private const float ApProtectedSafeRadiusKm = 0.15f;
    private const float ApProtectScanRadiusKm = ApEffectiveRadiusKm + ApProtectedSafeRadiusKm + 0.05f;

    private readonly FSC fcs = new();
    private FcsWindow? window;
    private TacticalRadar? radar;

    private bool autoSweep;
    private readonly HashSet<EntityLocation> swept = new(new EntityLocationComparer());
    private float lastIdleSweepTime;

    public bool Initialize()
    {
        window = new FcsWindow(fcs);
        radar = new TacticalRadar(fcs);
        return fcs.TryBind();
    }

    public void Update()
    {
        fcs.Update();
        radar?.Update();

        if (fcs.AutomaticFireHalted && autoSweep)
        {
            autoSweep = false;
            swept.Clear();
            MelonLogger.Warning($"[FCS] Auto sweep disabled: {fcs.AutomaticFireHaltReason}");
        }

        if (window != null)
        {
            window.AutoSweepEnabled = autoSweep;
            window.AutoMarkerEnabled = radar?.AutoPlaceMarkers ?? true;
        }

        if (autoSweep && radar != null && fcs.IsBound)
        {
            EnqueueNewSweepTargets();
            if (fcs.PendingCount == 0 && !fcs.HasActiveTasks && Time.time - lastIdleSweepTime > 3f)
            {
                lastIdleSweepTime = Time.time;
                SweepCurrentHostiles(forceRequeueAlive: true);
            }
        }

        var kb = Keyboard.current;
        if (kb == null || !fcs.IsBound) return;
        var ctrl = kb.ctrlKey.isPressed;

        if (kb.numpad0Key.wasPressedThisFrame || (ctrl && kb.digit0Key.wasPressedThisFrame))
        {
            if (fcs.AutomaticFireHalted)
            {
                fcs.ClearAutomaticFireHalt();
            }
            autoSweep = !autoSweep;
            if (autoSweep)
            {
                if (radar != null) {
                    radar.AutoPlaceMarkers = true;
                    fcs.ManualMarkerPriorityMode = false;
                    radar.ForceScan();
                }
                SweepCurrentHostiles(forceRequeueAlive: true);
            }
            return;
        }
        if (kb.numpad5Key.wasPressedThisFrame || (ctrl && kb.digit5Key.wasPressedThisFrame))
        {
            if (radar != null) {
                radar.AutoPlaceMarkers = !radar.AutoPlaceMarkers;
                fcs.ManualMarkerPriorityMode = !radar.AutoPlaceMarkers;
                MelonLogger.Msg($"[FCS] 标点模式: {(radar.AutoPlaceMarkers ? "自动标点" : "手动优先")}");
                if (radar.AutoPlaceMarkers) {
                    radar.ForceScan();
                }
            }
            return;
        }
        if (kb.numpadMinusKey.wasPressedThisFrame) { AdjustAllValves(0f); return; }
        if (kb.numpadPlusKey.wasPressedThisFrame) { AdjustAllValves(999f); return; }
        if (kb.numpadPeriodKey.wasPressedThisFrame || (ctrl && kb.periodKey.wasPressedThisFrame))
        {
            fcs.BallisticCalculator.DebugStepShellDial(ctrl ? -1 : 1);
            return;
        }
        if (kb.numpad7Key.wasPressedThisFrame || (ctrl && kb.digit7Key.wasPressedThisFrame)) { fcs.AbortGun(LeftRight.Left); return; }
        if (kb.numpad8Key.wasPressedThisFrame || (ctrl && kb.digit8Key.wasPressedThisFrame)) { fcs.AbortGun(LeftRight.Right); return; }
        if (kb.numpad9Key.wasPressedThisFrame || (ctrl && kb.digit9Key.wasPressedThisFrame)) { fcs.AbortAllGuns(); return; }
        if (kb.numpad1Key.wasPressedThisFrame || (ctrl && kb.digit1Key.wasPressedThisFrame)) fcs.FireTarget(1);
        else if (kb.numpad2Key.wasPressedThisFrame || (ctrl && kb.digit2Key.wasPressedThisFrame)) fcs.FireTarget(2);
        else if (kb.numpad3Key.wasPressedThisFrame || (ctrl && kb.digit3Key.wasPressedThisFrame)) fcs.FireTarget(3);
        else if (kb.numpad4Key.wasPressedThisFrame || (ctrl && kb.digit4Key.wasPressedThisFrame)) fcs.FireTarget(4);
    }

    /// <summary>Numpad +/-：实验性批量调节所有蒸汽泄漏点附近的阀门。</summary>
    private static void AdjustAllValves(float value)
    {
        var all = GameObject.FindObjectsOfType<GameObject>();
        var dials = new List<DialInteractable>();
        foreach (var go in all)
        {
            if (go == null) continue;
            var dial = go.GetComponent<DialInteractable>();
            if (dial != null) dials.Add(dial);
        }

        MelonLogger.Msg($"[Valve] Setting all steam valves to {value}...");
        var done = 0;
        foreach (var leak in all)
        {
            if (leak == null) continue;
            var name = leak.name?.ToLowerInvariant();
            if (name == null || !name.Contains("steam leak")) continue;

            DialInteractable? nearest = null;
            var minDistance = float.MaxValue;
            foreach (var dial in dials)
            {
                if (dial == null || dial.gameObject == null) continue;
                var distance = (dial.transform.position - leak.transform.position).magnitude;
                if (distance >= minDistance) continue;
                minDistance = distance;
                nearest = dial;
            }

            if (nearest == null) continue;
            nearest.SetDialValue(value);
            done++;
        }
        MelonLogger.Msg($"[Valve] Set {done} steam valves to {value}.");
    }

    /// <summary>持续扫荡时，只把新发现且未扫过的存活敌对目标按优先级加入队列。</summary>
    private void EnqueueNewSweepTargets()
    {
        var alive = radar?.AliveUnits;
        if (alive == null || alive.Count == 0) return;
        PruneSweptTargets(alive);
        foreach (var unit in SortByTargetPriority(alive))
        {
            if (unit.Location != null && !swept.Contains(unit.Location) && EnqueueSweepUnit(unit, swept.Count + 1))
            {
                swept.Add(unit.Location);
            }
        }
    }

    /// <summary>只保留当前仍存活的扫描目标，避免无尽模式同位置刷新后被旧记录挡住。</summary>
    private void PruneSweptTargets(List<UnitEntry> alive)
    {
        var aliveLocations = alive
            .Where(unit => unit.Location != null)
            .Select(unit => unit.Location!)
            .ToHashSet(new EntityLocationComparer());
        swept.RemoveWhere(loc => !aliveLocations.Contains(loc));
    }

    /// <summary>重新扫描当前存活目标并按优先级入队，用于开启扫荡或队列空闲时补扫。</summary>
    private void SweepCurrentHostiles(bool forceRequeueAlive)
    {
        var alive = radar?.AliveUnits;
        if (alive == null || alive.Count == 0) return;
        if (forceRequeueAlive)
        {
            swept.Clear();
        }
        var sorted = SortByTargetPriority(alive);
        for (var i = 0; i < sorted.Count; i++)
        {
            var location = sorted[i].Location;
            if (EnqueueSweepUnit(sorted[i], i + 1) && location != null)
            {
                swept.Add(location);
            }
        }
    }

    /// <summary>任务统一普通入队；FSC 内部会按目标优先级排序，同级再用角度近远提速。</summary>
    private bool EnqueueSweepUnit(UnitEntry unit, int id)
    {
        var worldPos = unit.WorldPos;
        var preserveAimPoint = false;
        if (fcs.SelectedBulletType == BulletType.AP
            && !TryResolveSafeApAimPoint(unit, out worldPos))
        {
            return false;
        }
        if (Vector3.Distance(worldPos, unit.WorldPos) > 0.01f)
        {
            preserveAimPoint = true;
        }

        fcs.FireAtWorldPos(id, worldPos, unit.Location, preserveAimPoint);
        return true;
    }

    /// <summary>按杀伤优先级排序；角度近远不在这里抢高星目标，只在 FSC 队列中作为同级优化。</summary>
    private bool TryResolveSafeApAimPoint(UnitEntry target, out Vector3 aimWorldPos)
    {
        aimWorldPos = target.WorldPos;
        if (radar == null) return true;
        var targetKm = TacticalRadar.GetEntityKmPos(target);

        var protectedKms = radar.ProtectedUnits
            .Where(unit => unit.IsAlive)
            .Select(unit => (km: TacticalRadar.GetEntityKmPos(unit), unit.Name))
            .Where(item => Vector2.Distance(item.km, targetKm) <= ApProtectScanRadiusKm)
            .ToList();
        if (protectedKms.Count == 0) return true;

        if (IsApAimSafe(targetKm, targetKm, protectedKms.Select(item => item.km)))
        {
            return true;
        }

        var bestKm = targetKm;
        var bestOffset = float.MaxValue;
        var bestMinProtectedDistance = 0f;
        const int samples = 32;
        for (var ring = 1; ring <= 11; ring++)
        {
            var radius = ApEffectiveRadiusKm * ring / 11f;
            for (var i = 0; i < samples; i++)
            {
                var angle = Mathf.PI * 2f * i / samples;
                var candidate = targetKm + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
                if (!MapTable.IsKmInsideTacticalMap(candidate)) continue;
                if (Vector2.Distance(candidate, targetKm) > ApEffectiveRadiusKm) continue;

                var minProtectedDistance = protectedKms.Min(item => Vector2.Distance(candidate, item.km));
                if (minProtectedDistance < ApProtectedSafeRadiusKm) continue;

                var offset = Vector2.Distance(candidate, targetKm);
                if (offset < bestOffset
                    || Mathf.Approximately(offset, bestOffset) && minProtectedDistance > bestMinProtectedDistance)
                {
                    bestKm = candidate;
                    bestOffset = offset;
                    bestMinProtectedDistance = minProtectedDistance;
                }
            }
            if (bestOffset < float.MaxValue) break;
        }

        if (bestOffset >= float.MaxValue)
        {
            MelonLogger.Warning(
                $"[FCS] AP friendly-fire guard: skip {target.Name}; " +
                $"protected units near impact={protectedKms.Count}, targetKm=({targetKm.x:F2},{targetKm.y:F2}).");
            return false;
        }

        if (!fcs.MapTable.TryMapKmToWorldPos(target.WorldPos, bestKm, out aimWorldPos))
        {
            return false;
        }

        MelonLogger.Warning(
            $"[FCS] AP friendly-fire guard: offset {target.Name} by {bestOffset:F2}km; " +
            $"nearest protected={bestMinProtectedDistance:F2}km.");
        return true;
    }

    private static bool IsApAimSafe(Vector2 targetKm, Vector2 aimKm, IEnumerable<Vector2> protectedKms)
    {
        if (Vector2.Distance(aimKm, targetKm) > ApEffectiveRadiusKm)
        {
            return false;
        }

        return protectedKms.All(km => Vector2.Distance(aimKm, km) >= ApProtectedSafeRadiusKm);
    }

    private static List<UnitEntry> SortByTargetPriority(IEnumerable<UnitEntry> units)
    {
        return units
            .OrderByDescending(u => TargetPriority.GetPriority(u.Location))
            .ThenByDescending(u => TargetPriority.GetStars(u.Location))
            .ToList();
    }

    public void OnGui()
    {
        window?.OnGui();
        radar?.OnGui();
    }

    public void Shutdown()
    {
        fcs.Dispose();
        window = null;
        radar = null;
    }
}

internal sealed class EntityLocationComparer : IEqualityComparer<EntityLocation>
{
    public bool Equals(EntityLocation? x, EntityLocation? y)
    {
        if (ReferenceEquals(x, y)) return true;
        if (x is null || y is null) return false;
        return x.Pointer == y.Pointer;
    }

    public int GetHashCode(EntityLocation obj)
    {
        return obj.Pointer.GetHashCode();
    }
}
