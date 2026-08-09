using Il2Cpp;
using IronNestFCS.Abstractions;
using IronNestFCS.Logic.FCS;
using MelonLoader;
using UnityEngine;
using UnityEngine.InputSystem;

namespace IronNestFCS.Logic;

public class FcsModule : IFcsModule
{
    private static readonly float ApEffectiveRadiusKm = ShellEffectProfiles.ImpactRadiusOrDefault(BulletType.AP, 0.15f);
    private static readonly float ApProtectedSafeRadiusKm = ShellEffectProfiles.ImpactRadiusOrDefault(BulletType.AP, 0.15f);
    private static readonly float ApProtectScanRadiusKm = ApEffectiveRadiusKm + ApProtectedSafeRadiusKm + 0.05f;
    private const float SmokeAllyPullRadiusKm = 0.50f;
    private const float SmokeDesiredEnemyClearanceKm = 0.45f;
    private const float SmokeMinEnemyClearanceKm = 0.32f;
    private const float SmokeDeconflictionCooldownSeconds = 8f;
    private const float SmokeDeconflictionSettleSeconds = 12f;
    private const float TrainAreaClusterRadiusKm = 1.35f;
    private const float TightTargetClusterKeyGridKm = 0.10f;
    private const float TrainAreaMaxLeadKm = 0.65f;
    private const float TrainAreaMinLeadKm = 0.04f;

    private readonly FSC fcs = new();
    private FcsWindow? window;
    private TacticalRadar? radar;

    private bool autoSweep;
    private readonly HashSet<EntityLocation> swept = new(new EntityLocationComparer());
    private readonly EntityLocationComparer entityLocationComparer = new();
    private readonly Dictionary<string, float> smokeDeconflictionCooldowns = new();
    private readonly HashSet<IntPtr> knownDepletedClusterMembers = new();
    private float lastIdleSweepTime;
    private float lastSmokeDeconflictionTime;

    public bool Initialize()
    {
        window = new FcsWindow(fcs);
        radar = new TacticalRadar(fcs);
        fcs.AutoFireSafetyBlockReason = GetAutoFireSafetyBlockReason;
        fcs.AutoFireTryMakeSafe = TryMakeAutoFireSafe;
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
            smokeDeconflictionCooldowns.Clear();
            knownDepletedClusterMembers.Clear();
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
                smokeDeconflictionCooldowns.Clear();
                knownDepletedClusterMembers.Clear();
                lastSmokeDeconflictionTime = 0f;
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
        if (kb.numpad6Key.wasPressedThisFrame || (ctrl && kb.digit6Key.wasPressedThisFrame)) { radar?.ForcePlaceMarkersOnce(); return; }
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
        PruneSmokeDeconflictionCooldowns(alive);
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

        var aliveDepletedMembers = alive
            .Where(unit => unit.Location != null && fcs.IsDepletedAreaClusterMember(unit))
            .Select(unit => unit.Location!)
            .ToList();
        knownDepletedClusterMembers.RemoveWhere(pointer => !aliveDepletedMembers.Any(loc => loc.Pointer == pointer));
        foreach (var location in aliveDepletedMembers)
        {
            if (knownDepletedClusterMembers.Add(location.Pointer))
            {
                swept.Remove(location);
            }
        }
    }

    private void PruneSmokeDeconflictionCooldowns(List<UnitEntry> alive)
    {
        if (smokeDeconflictionCooldowns.Count == 0)
        {
            return;
        }

        var now = Time.time;
        var aliveKeys = alive.Select(DeconflictionKey).ToHashSet();
        foreach (var pair in smokeDeconflictionCooldowns.ToArray())
        {
            if (pair.Value <= now || !aliveKeys.Contains(pair.Key))
            {
                smokeDeconflictionCooldowns.Remove(pair.Key);
            }
        }
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
        var movingAreaAim = false;
        var areaClusterKey = "";
        IReadOnlyCollection<IntPtr>? areaClusterMembers = null;
        var bulletType = AutoSweepBulletFor(unit);
        var allowCluster = !fcs.IsDepletedAreaClusterMember(unit);
        if (IsMovingTrainCarLike(unit)
            && unit.HasVelocity
            && HasAnyLiveTrainStation())
        {
            return false;
        }
        if (bulletType == BulletType.AP
            && allowCluster
            && TryResolveTightTargetClusterAimPoint(unit, out worldPos, out var tightGrouped, out areaClusterKey, out areaClusterMembers))
        {
            preserveAimPoint = true;
            foreach (var groupedUnit in tightGrouped)
            {
                if (groupedUnit.Location != null)
                {
                    swept.Add(groupedUnit.Location);
                }
            }
            MelonLogger.Msg(
                $"[FCS] Tight AP cluster aim: grouped={tightGrouped.Count}, " +
                $"anchor={unit.DisplayName}.");
        }
        else if (bulletType == BulletType.AP
            && !TryResolveSafeApAimPoint(unit, out worldPos, logFailure: false))
        {
            TryEnqueueSmokeDeconfliction(unit, id);
            return false;
        }
        else if (IsAreaClusterShell(bulletType)
                 && allowCluster
                 && TryResolveAreaClusterAimPoint(unit, bulletType, out worldPos, out var grouped, out var leadKm))
        {
            movingAreaAim = true;
            foreach (var groupedUnit in grouped)
            {
                if (groupedUnit.Location != null)
                {
                    swept.Add(groupedUnit.Location);
                }
            }
            MelonLogger.Msg(
                $"[FCS] Area aim: {bulletType} grouped={grouped.Count}, " +
                $"lead={leadKm:F2}km, anchor={unit.DisplayName}.");
        }
        if (!movingAreaAim && Vector3.Distance(worldPos, unit.WorldPos) > 0.01f)
        {
            preserveAimPoint = true;
        }

        fcs.FireAtWorldPos(
            id,
            worldPos,
            unit.Location,
            preserveAimPoint,
            movingAreaAim,
            bulletType,
            areaClusterKey,
            areaClusterMembers);
        return true;
    }

    private BulletType AutoSweepBulletFor(UnitEntry unit)
    {
        if (IsMovingTrainCarLike(unit))
        {
            return unit.HasVelocity ? BulletType.HE : BulletType.HCHE;
        }

        return fcs.SelectedBulletType;
    }

    private bool TryResolveAreaClusterAimPoint(UnitEntry anchor, BulletType bulletType, out Vector3 aimWorldPos, out List<UnitEntry> grouped, out float leadKm)
    {
        aimWorldPos = anchor.WorldPos;
        grouped = new List<UnitEntry>();
        leadKm = 0f;
        if (radar == null || !IsAreaClusterShell(bulletType))
        {
            return false;
        }
        if (!ShellEffectProfiles.TryGetImpactRadius(bulletType, out var shellRadiusKm))
        {
            return false;
        }

        var anchorKm = TacticalRadar.GetEntityKmPos(anchor);
        if (IsMovingTrainCarLike(anchor))
        {
            grouped = anchor.HasVelocity
                ? radar.AliveUnits
                    .Where(unit => unit.IsAlive)
                    .Where(IsMovingTrainCarLike)
                    .Where(unit => Vector2.Distance(TacticalRadar.GetEntityKmPos(unit), anchorKm) <= shellRadiusKm * 2f)
                    .ToList()
                : BuildStoppedTrainAreaCandidates(anchor, shellRadiusKm);
            grouped = SelectBestCoveredClusterSegment(grouped, shellRadiusKm, anchorKm);
        }
        else
        {
            grouped = radar.AliveUnits
                .Where(unit => unit.IsAlive)
                .Where(unit => !fcs.IsDepletedAreaClusterMember(unit))
                .Where(unit => Vector2.Distance(TacticalRadar.GetEntityKmPos(unit), anchorKm) <= shellRadiusKm * 2f)
                .ToList();
        }
        grouped = grouped
            .Where(unit => !fcs.IsDepletedAreaClusterMember(unit))
            .ToList();

        if (grouped.Count < 2)
        {
            return false;
        }

        var centerKm = new Vector2(
            grouped.Average(unit => TacticalRadar.GetEntityKmPos(unit).x),
            grouped.Average(unit => TacticalRadar.GetEntityKmPos(unit).y));
        if (grouped.Any(unit => Vector2.Distance(TacticalRadar.GetEntityKmPos(unit), centerKm) > shellRadiusKm))
        {
            return false;
        }

        var moving = grouped.Where(unit => unit.HasVelocity).ToList();
        if (moving.Count > 0)
        {
            var avgVelocity = new Vector3(
                moving.Average(unit => unit.VelocityWorldPerSecond.x),
                moving.Average(unit => unit.VelocityWorldPerSecond.y),
                moving.Average(unit => unit.VelocityWorldPerSecond.z));
            var leadWorld = anchor.WorldPos + avgVelocity * EstimateTrainAreaLeadSeconds();
            if (fcs.MapTable.TryWorldToKmPos(anchor.WorldPos, out var currentKm)
                && fcs.MapTable.TryWorldToKmPos(leadWorld, out var projectedKm))
            {
                var leadDelta = projectedKm - currentKm;
                if (leadDelta.magnitude > TrainAreaMaxLeadKm)
                {
                    leadDelta = leadDelta.normalized * TrainAreaMaxLeadKm;
                }
                if (leadDelta.magnitude >= TrainAreaMinLeadKm)
                {
                    centerKm += leadDelta;
                    leadKm = leadDelta.magnitude;
                }
            }
        }

        if (!MapTable.IsKmInsideTacticalMap(centerKm))
        {
            return false;
        }

        return fcs.MapTable.TryMapKmToWorldPos(anchor.WorldPos, centerKm, out aimWorldPos);
    }

    private bool TryResolveTightTargetClusterAimPoint(
        UnitEntry anchor,
        out Vector3 aimWorldPos,
        out List<UnitEntry> grouped,
        out string areaClusterKey,
        out IReadOnlyCollection<IntPtr> areaClusterMembers)
    {
        aimWorldPos = anchor.WorldPos;
        grouped = new List<UnitEntry>();
        areaClusterKey = "";
        areaClusterMembers = Array.Empty<IntPtr>();
        if (radar == null || IsMovingTrainCarLike(anchor)
            || !ShellEffectProfiles.TryGetImpactRadius(BulletType.AP, out var apClusterRadius))
        {
            return false;
        }

        var apEffectiveRadius = apClusterRadius;
        var tightClusterRadius = apEffectiveRadius * 2f;

        var anchorKm = TacticalRadar.GetEntityKmPos(anchor);
        grouped = radar.AliveUnits
            .Where(unit => unit.IsAlive)
            .Where(unit => !IsMovingTrainCarLike(unit))
            .Where(unit => !fcs.IsDepletedAreaClusterMember(unit))
            .Where(unit => Vector2.Distance(TacticalRadar.GetEntityKmPos(unit), anchorKm) <= tightClusterRadius)
            .ToList();

        if (grouped.Count < ShellEffectProfiles.Get(BulletType.AP).MinClusterTargets)
        {
            return false;
        }

        var centerKm = new Vector2(
            grouped.Average(unit => TacticalRadar.GetEntityKmPos(unit).x),
            grouped.Average(unit => TacticalRadar.GetEntityKmPos(unit).y));
        if (grouped.Any(unit => Vector2.Distance(TacticalRadar.GetEntityKmPos(unit), centerKm) > apEffectiveRadius))
        {
            return false;
        }

        var protectedKms = radar.ProtectedUnits
            .Where(unit => unit.IsAlive)
            .Select(TacticalRadar.GetEntityKmPos)
            .Where(km => Vector2.Distance(km, centerKm) <= ApProtectedSafeRadiusKm + 0.05f)
            .ToList();
        if (protectedKms.Any(km => Vector2.Distance(km, centerKm) < ApProtectedSafeRadiusKm))
        {
            return false;
        }

        if (!MapTable.IsKmInsideTacticalMap(centerKm))
        {
            return false;
        }
        if (!fcs.MapTable.TryMapKmToWorldPos(anchor.WorldPos, centerKm, out aimWorldPos))
        {
            return false;
        }

        areaClusterKey = BuildTightTargetClusterKey(centerKm);
        areaClusterMembers = grouped
            .Where(unit => unit.Location != null)
            .Select(unit => unit.Location!.Pointer)
            .Distinct()
            .ToList();
        return true;
    }

    private static string BuildTightTargetClusterKey(Vector2 centerKm)
    {
        var x = Mathf.Round(centerKm.x / TightTargetClusterKeyGridKm) * TightTargetClusterKeyGridKm;
        var y = Mathf.Round(centerKm.y / TightTargetClusterKeyGridKm) * TightTargetClusterKeyGridKm;
        return $"tight:{x:F1}:{y:F1}";
    }

    private static bool IsAreaClusterShell(BulletType type)
    {
        return type == BulletType.HE || type == BulletType.HCHE || type == BulletType.CLMN;
    }

    private static List<UnitEntry> SelectBestCoveredClusterSegment(List<UnitEntry> units, float radiusKm, Vector2 anchorKm)
    {
        if (units.Count < 2)
        {
            return units;
        }

        var candidateCenters = units
            .Select(TacticalRadar.GetEntityKmPos)
            .ToList();
        for (var i = 0; i < units.Count; i++)
        {
            for (var j = i + 1; j < units.Count; j++)
            {
                candidateCenters.Add((TacticalRadar.GetEntityKmPos(units[i]) + TacticalRadar.GetEntityKmPos(units[j])) * 0.5f);
            }
        }

        return candidateCenters
            .Select(center => new
            {
                Center = center,
                Units = units
                    .Where(unit => Vector2.Distance(TacticalRadar.GetEntityKmPos(unit), center) <= radiusKm)
                    .ToList()
            })
            .Where(item => item.Units.Count >= 2)
            .OrderByDescending(item => item.Units.Count)
            .ThenByDescending(item => item.Units.Sum(unit => TargetPriority.GetPriority(unit.Location)))
            .ThenBy(item => Vector2.Distance(item.Center, anchorKm))
            .Select(item => item.Units)
            .FirstOrDefault() ?? new List<UnitEntry>();
    }

    private static float EstimateTrainAreaLeadSeconds()
    {
        return 3.5f;
    }

    private bool HasAnyLiveTrainStation()
    {
        if (radar == null)
        {
            return false;
        }

        return radar.AliveUnits
            .Where(unit => unit.IsAlive)
            .Any(IsTrainStationLike);
    }

    private List<UnitEntry> BuildStoppedTrainAreaCandidates(UnitEntry anchor, float shellRadiusKm)
    {
        if (radar == null)
        {
            return new List<UnitEntry>();
        }

        var anchorKm = TacticalRadar.GetEntityKmPos(anchor);
        var trainCluster = BuildConnectedTrainCluster(anchor, radar.AliveUnits);
        var pointers = trainCluster
            .Where(unit => unit.Location != null)
            .Select(unit => unit.Location!.Pointer)
            .ToHashSet();

        var nearby = radar.AliveUnits
            .Where(unit => unit.IsAlive)
            .Where(unit => !fcs.IsDepletedAreaClusterMember(unit))
            .Where(unit => !IsTrainStationLike(unit))
            .Where(unit => unit.Location == null || !pointers.Contains(unit.Location.Pointer))
            .Where(unit => Vector2.Distance(TacticalRadar.GetEntityKmPos(unit), anchorKm) <= shellRadiusKm * 2f)
            .ToList();

        trainCluster.AddRange(nearby);
        return trainCluster;
    }

    private static List<UnitEntry> BuildConnectedTrainCluster(UnitEntry anchor, IEnumerable<UnitEntry> units)
    {
        var candidates = units
            .Where(unit => unit.IsAlive)
            .Where(unit => IsMovingTrainCarLike(unit) && !unit.HasVelocity)
            .ToList();
        var seed = candidates.FirstOrDefault(unit => IsSameRadarUnit(unit, anchor));
        if (seed == null)
        {
            return new List<UnitEntry>();
        }

        var grouped = new List<UnitEntry>();
        var pending = new Queue<UnitEntry>();
        pending.Enqueue(seed);
        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            if (grouped.Any(unit => IsSameRadarUnit(unit, current)))
            {
                continue;
            }

            grouped.Add(current);
            var currentKm = TacticalRadar.GetEntityKmPos(current);
            foreach (var candidate in candidates)
            {
                if (grouped.Any(unit => IsSameRadarUnit(unit, candidate))
                    || pending.Any(unit => IsSameRadarUnit(unit, candidate)))
                {
                    continue;
                }

                if (Vector2.Distance(TacticalRadar.GetEntityKmPos(candidate), currentKm) <= TrainAreaClusterRadiusKm)
                {
                    pending.Enqueue(candidate);
                }
            }
        }

        return grouped;
    }

    private static bool IsStationaryTrainCarLike(UnitEntry unit)
    {
        return IsMovingTrainCarLike(unit) && !unit.HasVelocity;
    }

    private static bool IsSameRadarUnit(UnitEntry left, UnitEntry right)
    {
        if (left.Location != null && right.Location != null)
        {
            return left.Location.Pointer == right.Location.Pointer;
        }

        return ReferenceEquals(left, right)
               || string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase)
               && Vector3.Distance(left.WorldPos, right.WorldPos) < 0.01f;
    }

    private static bool IsMovingTrainCarLike(UnitEntry unit)
    {
        var text = $"{unit.Name} {unit.DisplayName}".ToLowerInvariant();
        return !text.Contains("station")
               && !text.Contains("terminal")
               && !text.Contains("总站")
               && (IsLetteredTrainCarName(unit.Name)
                   || text.Contains("train_transport")
                   || text.Contains("train_locomotive")
                   || text.Contains("flatcar")
                   || text.Contains("flatbed")
                   || text.Contains("railcar")
                   || text.Contains("freight")
                   || text.Contains("wagon")
                   || text.Contains("locomotive")
                   || text.Contains("列车")
                   || text.Contains("平板车")
                   || text.Contains("火车头"));
    }

    private static bool IsTrainStationLike(UnitEntry unit)
    {
        var text = $"{unit.Name} {unit.DisplayName}".ToLowerInvariant();
        return text.Contains("train_station")
               || text.Contains("train station")
               || text.Contains("target station")
               || text.Contains("terminal")
               || text.Contains("总站")
               || text.Contains("鎬荤珯");
    }

    private static bool IsLetteredTrainCarName(string name)
    {
        var key = name.Split('#')[0].Trim().ToLowerInvariant();
        return key.Length == 4
               && key.StartsWith("car")
               && key[3] >= 'a'
               && key[3] <= 'z';
    }

    /// <summary>按杀伤优先级排序；角度近远不在这里抢高星目标，只在 FSC 队列中作为同级优化。</summary>
    private bool TryResolveSafeApAimPoint(UnitEntry target, out Vector3 aimWorldPos, bool logFailure = true)
    {
        aimWorldPos = target.WorldPos;
        if (radar == null) return true;
        var targetKm = TacticalRadar.GetEntityKmPos(target);

        var protectedKms = GetProtectedUnitsNearApTarget(targetKm);
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
            if (logFailure)
            {
                MelonLogger.Warning(
                    $"[FCS] AP friendly-fire guard: skip {target.Name}; " +
                    $"protected units near impact={protectedKms.Count}, targetKm=({targetKm.x:F2},{targetKm.y:F2}).");
            }
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

    private List<(Vector2 km, string Name)> GetProtectedUnitsNearApTarget(Vector2 targetKm)
    {
        return radar == null
            ? new List<(Vector2 km, string Name)>()
            : radar.ProtectedUnits
                .Where(unit => unit.IsAlive)
                .Select(unit => (km: TacticalRadar.GetEntityKmPos(unit), unit.Name))
                .Where(item => Vector2.Distance(item.km, targetKm) <= ApProtectScanRadiusKm)
                .ToList();
    }

    private bool TryEnqueueSmokeDeconfliction(UnitEntry target, int id)
    {
        if (radar == null || !autoSweep)
        {
            return false;
        }

        var now = Time.time;
        if (fcs.HasActiveOrQueuedSmokeDeconfliction
            || now - lastSmokeDeconflictionTime < SmokeDeconflictionSettleSeconds)
        {
            return false;
        }

        var key = DeconflictionKey(target);
        if (smokeDeconflictionCooldowns.TryGetValue(key, out var nextAllowed) && now < nextAllowed)
        {
            return false;
        }

        var targetKm = TacticalRadar.GetEntityKmPos(target);
        var protectedKms = GetProtectedUnitsNearApTarget(targetKm);
        if (protectedKms.Count == 0)
        {
            return false;
        }

        var otherHostileKms = radar.AliveUnits
            .Where(unit => unit.IsAlive)
            .Where(unit => target.Location == null
                           || unit.Location == null
                           || !entityLocationComparer.Equals(unit.Location, target.Location))
            .Select(TacticalRadar.GetEntityKmPos)
            .ToList();

        if (!TryResolveSmokePullPoint(targetKm, protectedKms, otherHostileKms, out var smokeKm, out var enemyClearance, out var protectedReach))
        {
            smokeDeconflictionCooldowns[key] = now + SmokeDeconflictionCooldownSeconds;
            MelonLogger.Warning(
                $"[FCS] SMK deconfliction unavailable for {target.DisplayName}; " +
                $"protected={protectedKms.Count}, targetKm=({targetKm.x:F2},{targetKm.y:F2}).");
            return false;
        }

        if (!fcs.MapTable.TryMapKmToWorldPos(target.WorldPos, smokeKm, out var smokeWorldPos))
        {
            return false;
        }

        var smokeTask = new ArtilleryTask
        {
            targetId = id,
            bulletType = BulletType.SMK,
            preserveAimPoint = true,
            requireExactShell = true,
            targetTypeDialValue = 0,
            manualPriority = true,
            smokeDeconfliction = true
        };
        if (!fcs.MapTable.TryUpdateTaskFromWorldPos(smokeTask, smokeWorldPos, null))
        {
            return false;
        }

        fcs.EnqueueTaskFront(smokeTask);
        smokeDeconflictionCooldowns[key] = now + SmokeDeconflictionCooldownSeconds;
        lastSmokeDeconflictionTime = now;
        MelonLogger.Warning(
            $"[FCS] SMK deconfliction queued before AP target {target.DisplayName}; " +
            $"smoke=({smokeKm.x:F2},{smokeKm.y:F2}), enemyClearance={enemyClearance:F2}km, " +
            $"protectedReach={protectedReach:F2}km.");
        return true;
    }

    private static bool TryResolveSmokePullPoint(
        Vector2 targetKm,
        List<(Vector2 km, string Name)> protectedKms,
        List<Vector2> otherHostileKms,
        out Vector2 smokeKm,
        out float enemyClearance,
        out float protectedReach)
    {
        smokeKm = targetKm;
        enemyClearance = 0f;
        protectedReach = 0f;

        var affected = protectedKms
            .Where(item => Vector2.Distance(item.km, targetKm) <= SmokeAllyPullRadiusKm)
            .ToList();
        if (affected.Count == 0)
        {
            affected = protectedKms;
        }

        var center = new Vector2(
            affected.Average(item => item.km.x),
            affected.Average(item => item.km.y));
        var away = center - targetKm;
        if (away.sqrMagnitude < 0.0001f)
        {
            away = new Vector2(1f, 0f);
        }
        away.Normalize();

        var candidates = new List<Vector2>
        {
            center + away * SmokeDesiredEnemyClearanceKm,
            center + away * SmokeAllyPullRadiusKm
        };

        const int samples = 48;
        const int rings = 10;
        for (var ring = 0; ring <= rings; ring++)
        {
            var radius = SmokeAllyPullRadiusKm * ring / rings;
            for (var i = 0; i < samples; i++)
            {
                var angle = Mathf.PI * 2f * i / samples;
                candidates.Add(center + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius);
            }
        }

        var bestScore = float.MinValue;
        foreach (var candidate in candidates)
        {
            if (!MapTable.IsKmInsideTacticalMap(candidate)) continue;

            var maxProtectedDistance = affected.Max(item => Vector2.Distance(candidate, item.km));
            if (maxProtectedDistance > SmokeAllyPullRadiusKm) continue;

            var distanceToEnemy = Vector2.Distance(candidate, targetKm);
            if (distanceToEnemy < SmokeMinEnemyClearanceKm) continue;
            if (otherHostileKms.Any(km => Vector2.Distance(candidate, km) < SmokeMinEnemyClearanceKm)) continue;

            var clearanceBonus = distanceToEnemy >= SmokeDesiredEnemyClearanceKm ? 1f : 0f;
            var score = distanceToEnemy * 10f + clearanceBonus - maxProtectedDistance;
            if (score <= bestScore) continue;

            bestScore = score;
            smokeKm = candidate;
            enemyClearance = distanceToEnemy;
            protectedReach = maxProtectedDistance;
        }

        return bestScore > float.MinValue;
    }

    private static string DeconflictionKey(UnitEntry target)
    {
        return target.Location != null
            ? $"loc:{target.Location.Pointer}"
            : $"name:{target.Name}";
    }

    private string? GetAutoFireSafetyBlockReason(ArtilleryTask task, int actualPowder)
    {
        if (task.bulletType != BulletType.AP || radar == null)
        {
            return null;
        }

        var aimKm = new Vector2(task.position.x, task.position.y);
        var targetKm = aimKm;
        var targetName = $"T{task.targetId}";
        if (task.location != null)
        {
            var match = radar.AliveUnits.FirstOrDefault(unit =>
                unit.Location != null && entityLocationComparer.Equals(unit.Location, task.location));
            if (match == null || match.Location == null)
            {
                return null;
            }
            targetKm = TacticalRadar.GetEntityKmPos(match);
            targetName = match.Name;
        }

        if (Vector2.Distance(aimKm, targetKm) > ApEffectiveRadiusKm)
        {
            return $"AP aim no longer covers {targetName}; aim=({aimKm.x:F2},{aimKm.y:F2}), target=({targetKm.x:F2},{targetKm.y:F2})";
        }

        var protectedKms = radar.ProtectedUnits
            .Where(unit => unit.IsAlive)
            .Select(unit => (km: TacticalRadar.GetEntityKmPos(unit), unit.Name))
            .Where(item => Vector2.Distance(item.km, aimKm) <= ApProtectedSafeRadiusKm + 0.05f
                           || Vector2.Distance(item.km, targetKm) <= ApProtectScanRadiusKm)
            .ToList();
        if (protectedKms.Count == 0)
        {
            return null;
        }

        var nearest = protectedKms
            .Select(item => (item.Name, distance: Vector2.Distance(item.km, aimKm)))
            .OrderBy(item => item.distance)
            .First();
        if (nearest.distance >= ApProtectedSafeRadiusKm)
        {
            return null;
        }

        return $"AP protected target too close before fire: {nearest.Name} {nearest.distance:F2}km from impact";
    }

    private bool TryMakeAutoFireSafe(ArtilleryTask task, int actualPowder)
    {
        if (task.bulletType != BulletType.AP || radar == null)
        {
            return false;
        }

        var currentAimKm = new Vector2(task.position.x, task.position.y);

        if (task.location != null)
        {
            var sameTarget = radar.AliveUnits.FirstOrDefault(unit =>
                unit.Location != null && entityLocationComparer.Equals(unit.Location, task.location));
            if (sameTarget != null
                && TryBuildSafeApTask(sameTarget, task.targetId, actualPowder, out var sameTargetTask))
            {
                CopySafeTask(task, sameTargetTask);
                MelonLogger.Warning($"[FCS] AP friendly-fire guard: re-aim current target {sameTarget.Name} before fire.");
                return true;
            }
        }

        if (task.userRequested)
        {
            MelonLogger.Warning(
                $"[FCS] AP friendly-fire guard: manual T{task.targetId} is unsafe; " +
                "will not retarget user-requested shot.");
            return false;
        }

        var alternatives = radar.AliveUnits
            .Where(unit => unit.IsAlive && unit.Location != null)
            .Where(unit => !IsAssignedToOtherBarrel(unit, task))
            .OrderByDescending(unit => TargetPriority.GetPriority(unit.Location))
            .ThenByDescending(unit => TargetPriority.GetStars(unit.Location))
            .ThenBy(unit => Vector2.Distance(TacticalRadar.GetEntityKmPos(unit), currentAimKm))
            .ToList();

        for (var i = 0; i < alternatives.Count; i++)
        {
            var unit = alternatives[i];
            if (task.location != null
                && unit.Location != null
                && entityLocationComparer.Equals(unit.Location, task.location))
            {
                continue;
            }

            if (!TryBuildSafeApTask(unit, i + 1, actualPowder, out var safeTask))
            {
                continue;
            }

            CopySafeTask(task, safeTask);
            MelonLogger.Warning($"[FCS] AP friendly-fire guard: retarget loaded round to safe target {unit.Name}.");
            return true;
        }

        return false;
    }

    private bool TryBuildSafeApTask(UnitEntry unit, int id, int actualPowder, out ArtilleryTask task)
    {
        task = new ArtilleryTask
        {
            targetId = id,
            bulletType = BulletType.AP,
            location = unit.Location,
            targetTypeDialValue = TargetTypeMapper.FromLocation(unit.Location),
        };

        if (!TryResolveSafeApAimPoint(unit, out var aimWorldPos))
        {
            return false;
        }

        if (!fcs.MapTable.TryUpdateTaskFromWorldPos(task, aimWorldPos, unit.Location))
        {
            return false;
        }

        if (actualPowder > 0 && BallisticCalculator.MinimumCharge(task.distance) > actualPowder)
        {
            MelonLogger.Warning(
                $"[FCS] AP friendly-fire guard: skip {unit.Name}; " +
                $"safe target needs powder={BallisticCalculator.MinimumCharge(task.distance)}, loaded={actualPowder}.");
            return false;
        }

        task.preserveAimPoint = Vector3.Distance(aimWorldPos, unit.WorldPos) > 0.01f;
        return true;
    }

    private bool IsAssignedToOtherBarrel(UnitEntry unit, ArtilleryTask currentTask)
    {
        if (unit.Location == null)
        {
            return false;
        }

        return IsOtherTaskLocation(fcs.LeftTask, currentTask, unit.Location)
               || IsOtherTaskLocation(fcs.RightTask, currentTask, unit.Location);
    }

    private bool IsOtherTaskLocation(ArtilleryTask? task, ArtilleryTask currentTask, EntityLocation location)
    {
        return task != null
               && !ReferenceEquals(task, currentTask)
               && task.location != null
               && entityLocationComparer.Equals(task.location, location);
    }

    private static void CopySafeTask(ArtilleryTask target, ArtilleryTask source)
    {
        target.targetId = source.targetId;
        target.angel = source.angel;
        target.distance = source.distance;
        target.position = source.position;
        target.location = source.location;
        target.targetTypeDialValue = source.targetTypeDialValue;
        target.preserveAimPoint = source.preserveAimPoint;
        target.movingAreaAim = source.movingAreaAim;
        target.requireExactShell = source.requireExactShell;
        target.bulletType = source.bulletType;
        target.areaClusterKey = source.areaClusterKey;
        target.areaClusterMembers = source.areaClusterMembers.ToList();
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
