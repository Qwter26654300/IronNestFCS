using System.IO;
using Il2Cpp;
using Il2CppTMPro;
using IronNestFCS.Logic.FCS;
using MelonLoader;
using MelonLoader.Utils;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEngine;

namespace IronNestFCS.Logic;

public class UnitEntry
{
    public string Name = "";
    public string DisplayName = "";
    public string AreaClusterKey = "";
    public List<IntPtr> AreaClusterMembers = new();
    public Vector3 WorldPos;
    public Vector3 VelocityWorldPerSecond;
    public bool HasVelocity;
    public float ObservedTime;
    public bool IsAlive;
    public EntityLocation? Location;
}

public class TacticalRadar
{
    private readonly FSC fcs;

    private bool showRadar = true;
    private Rect radarRect = new(0, 0, 0, 0);

    private bool autoPlaceMarkers = true;
    public bool AutoPlaceMarkers
    {
        get => autoPlaceMarkers;
        set
        {
            autoPlaceMarkers = value;
            if (!autoPlaceMarkers)
            {
                fcs.MapTable.ClearMarkerLocations();
            }
        }
    }

    private readonly List<UnitEntry> units = new();
    private readonly List<UnitEntry> protectedUnits = new();
    private readonly Dictionary<string, TrackSample> trackedUnits = new();
    private float lastScanTime;
    private int lastLoggedAliveCount = -1;
    private int lastLoggedProtectedCount = -1;
    private bool? lastLoggedAutoMarkers;
    private const float ScanInterval = 3f;

    private static readonly Color ClrTitle = Color.white;
    private static readonly Color ClrAlive = Color.red;
    private static readonly Color ClrDead = Color.yellow;
    private static readonly Color ClrDeadTitle = Color.white;
    private static readonly Color ClrLabel = new(0.72f, 0.65f, 0.55f);

    private static readonly bool DetailedRadarLog = false;

    public TacticalRadar(FSC fcs) => this.fcs = fcs;

    public List<UnitEntry> AliveUnits => units.Where(u => u.IsAlive && IsAutoTargetable(u)).ToList();
    public List<UnitEntry> ProtectedUnits => protectedUnits.Where(u => u.IsAlive).ToList();

    public void Update()
    {
        if (Time.time - lastScanTime > ScanInterval)
        {
            ScanForUnits();
            lastScanTime = Time.time;
        }
    }

    public void ForceScan()
    {
        ScanForUnits();
        lastScanTime = Time.time;
    }

    public void ForcePlaceMarkersOnce()
    {
        ScanForUnits(forcePlaceMarkers: true);
        lastScanTime = Time.time;
        MelonLogger.Msg("[Radar] 手动触发雷达刷新：已更新铁巢位置并按当前优先级更新 T1-T4。");
    }

    private void ScanForUnits(bool forcePlaceMarkers = false)
    {
        try
        {
            ScanForUnitsInternal(forcePlaceMarkers);
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"[Radar] Scan crashed: {ex}");
        }
        FlushLog();
    }

    private void ScanForUnitsInternal(bool forcePlaceMarkers)
    {
        units.Clear();
        protectedUnits.Clear();

        var fireMissionRoot = FindFireMissionRoot();
        var turretRef = GameObject.Find("Player Turret Piece")?.transform;

        Log($"[Radar] Scan start. FireMissionRoot={fireMissionRoot != null} Turret={turretRef != null}");

        if (fireMissionRoot != null)
        {
            Log($"[Radar] FireMissionRoot children: {fireMissionRoot.childCount}");
            for (int i = 0; i < fireMissionRoot.childCount; i++)
            {
                var child = fireMissionRoot.GetChild(i);
                if (IsNonCombatRadarObjectName(child.name))
                {
                    continue;
                }
                var loc = child.GetComponent<EntityLocation>();
                if (loc == null) continue;
                bool hostile = IsHostile(loc, child);
                bool isAlive = IsUnitAlive(loc, child.gameObject);
                var entityInfo = GetEntityInfo(loc);
                Log($"[Radar] Entity: {child.name}  hostile={hostile}  alive={isAlive}  icon={entityInfo.icon}  role={entityInfo.role}  roleNum={entityInfo.roleNum}");
                var entry = new UnitEntry
                {
                    Name = child.name,
                    DisplayName = BuildDisplayName(child.name, entityInfo.icon, entityInfo.role),
                    WorldPos = child.position,
                    IsAlive = isAlive,
                    Location = loc
                };
                UpdateMotionTrack(entry);
                if (hostile) units.Add(entry);
                else if (isAlive && IsProtectedUnitNameOrRole(child.name, entityInfo.icon, entityInfo.role, entityInfo.roleNum)) protectedUnits.Add(entry);
            }
        }

        // Fire Mission Root 有些关卡/实体拿不到完整目标；全场景兜底扫描只每 3 秒跑一次，先保留保证识别率。
        var allRoots = GameObject.FindObjectsOfType<GameObject>();
        int nameMatchCount = 0;
        foreach (var obj in allRoots)
        {
            if (obj == null) continue;
            var n = obj.name;
            if (IsNonCombatRadarObjectName(n) || IsRadarUiLabelObject(obj))
            {
                continue;
            }
            var loc = obj.GetComponent<EntityLocation>();
            var nameCandidate = IsNameFallbackTargetCandidate(n);
            if ((loc != null || nameCandidate) && obj.transform != null)
            {
                if (loc != null) {
                    var hostile = IsHostile(loc, obj.transform);
                    if (!hostile) {
                        var info = GetEntityInfo(loc);
                        if (IsUnitAlive(loc, obj)
                            && IsProtectedUnitNameOrRole(n, info.icon, info.role, info.roleNum)
                            && !protectedUnits.Any(unit => unit.Location == loc)) {
                            var entry = new UnitEntry
                            {
                                Name = n,
                                DisplayName = BuildDisplayName(n, info.icon, info.role),
                                WorldPos = obj.transform.position,
                                IsAlive = true,
                                Location = loc
                            };
                            UpdateMotionTrack(entry);
                            protectedUnits.Add(entry);
                        }
                        continue;
                    }
                }
                if (!IsKnownUnit(loc, obj))
                {
                    nameMatchCount++;
                    Log($"[Radar] NameMatch: {n}  hasEntityLocation={loc != null}  activeInHierarchy={obj.activeInHierarchy}");
                    var entry = new UnitEntry
                    {
                        Name = n,
                        DisplayName = BuildDisplayName(n, loc != null ? GetEntityInfo(loc).icon : "", loc != null ? GetEntityInfo(loc).role : ""),
                        WorldPos = obj.transform.position,
                        IsAlive = loc != null ? IsUnitAlive(loc, obj) : obj.activeInHierarchy,
                        Location = loc
                    };
                    UpdateMotionTrack(entry);
                    units.Add(entry);
                }
            }
        }

        Log($"[Radar] Total FireMission entities: {units.Count - nameMatchCount}  NameMatch entities: {nameMatchCount}  Total: {units.Count}");

        var alive = SortByTargetPriority(DeduplicateUnits(units.Where(u => u.IsAlive && IsAutoTargetable(u))));
        Log($"[Radar] Alive hostile count: {alive.Count}");
        if (alive.Count != lastLoggedAliveCount
            || ProtectedUnits.Count != lastLoggedProtectedCount
            || lastLoggedAutoMarkers != AutoPlaceMarkers) {
            MelonLogger.Msg($"[Radar] Alive hostile count={alive.Count}, protected={ProtectedUnits.Count}, autoMarkers={AutoPlaceMarkers}");
            lastLoggedAliveCount = alive.Count;
            lastLoggedProtectedCount = ProtectedUnits.Count;
            lastLoggedAutoMarkers = AutoPlaceMarkers;
        }
        for (int i = 0; i < Mathf.Min(alive.Count, 4); i++)
        {
            Vector2 km = GetEntityKmPos(alive[i]);
            Log($"[Radar] Marker {i + 1} -> {alive[i].DisplayName} km=({km.x:F2},{km.y:F2})");
        }
        if (AutoPlaceMarkers || forcePlaceMarkers)
        {
            fcs.RefreshPlayerTurretMarkerBeforeTargeting();
            fcs.MapTable.RebindMarkerLocationsFromEntities(alive);
            var markerTargets = BuildMarkerAssignments(alive);
            for (int i = 1; i <= 4; i++)
            {
                if (markerTargets.TryGetValue(i, out var target)) {
                    fcs.MapTable.SetMarkerWorldPos(
                        i,
                        target.WorldPos,
                        string.IsNullOrWhiteSpace(target.AreaClusterKey) ? target.Location : null);
                    Log($"[Radar] Auto marker T{i} -> {target.DisplayName} loc={LocationId(target.Location)}");
                }
                else {
                    fcs.MapTable.ClearMarkerLocation(i);
                    Log($"[Radar] Auto marker T{i} -> none, keep position");
                }
            }
        }
        else
        {
            fcs.MapTable.ClearMarkerLocations();
        }

        fcs.RefreshQueuedTargetsFromRadar(alive);
        FlushLog();
    }

    private Dictionary<int, UnitEntry> BuildMarkerAssignments(List<UnitEntry> alive)
    {
        var assignments = new Dictionary<int, UnitEntry>();
        var used = new HashSet<IntPtr>();
        var usedClusterKeys = new HashSet<string>();
        var markerPool = CollapseCloseMarkerTargets(CollapseTrainMarkerTargets(alive));
        var byLocation = alive
            .Where(unit => unit.Location != null)
            .GroupBy(unit => unit.Location!.Pointer)
            .ToDictionary(group => group.Key, group => group.First());
        var collapsedByLocation = markerPool
            .SelectMany(unit => ClusterMemberPointers(unit).Select(pointer => (pointer, unit)))
            .GroupBy(item => item.pointer)
            .ToDictionary(group => group.Key, group => group.First().unit);

        // 已经被 T1-T4 标到的实体先占坑，避免刷新时把另一个 T 标签拉到同一实体上。
        foreach (var pair in fcs.MapTable.GetBoundMarkerLocations().OrderBy(pair => pair.Key))
        {
            var key = pair.Value.Pointer;
            if (used.Contains(key))
            {
                continue;
            }

            if (!collapsedByLocation.TryGetValue(key, out var unit)
                && !byLocation.TryGetValue(key, out unit))
            {
                continue;
            }

            assignments[pair.Key] = unit;
            MarkMarkerTargetUsed(unit, used, usedClusterKeys);
        }

        foreach (var markerId in Enumerable.Range(1, 4).Where(id => !assignments.ContainsKey(id)))
        {
            var next = markerPool.FirstOrDefault(unit => {
                return !IsMarkerTargetUsed(unit, used, usedClusterKeys);
            });
            if (next == null) break;

            assignments[markerId] = next;
            MarkMarkerTargetUsed(next, used, usedClusterKeys);
        }

        return assignments;
    }

    private static IEnumerable<IntPtr> ClusterMemberPointers(UnitEntry unit)
    {
        if (unit.AreaClusterMembers.Count > 0)
        {
            return unit.AreaClusterMembers;
        }

        return unit.Location == null ? Enumerable.Empty<IntPtr>() : new[] { unit.Location.Pointer };
    }

    private static bool IsMarkerTargetUsed(UnitEntry unit, HashSet<IntPtr> used, HashSet<string> usedClusterKeys)
    {
        if (!string.IsNullOrWhiteSpace(unit.AreaClusterKey) && usedClusterKeys.Contains(unit.AreaClusterKey))
        {
            return true;
        }

        return ClusterMemberPointers(unit).Any(used.Contains);
    }

    private static void MarkMarkerTargetUsed(UnitEntry unit, HashSet<IntPtr> used, HashSet<string> usedClusterKeys)
    {
        if (!string.IsNullOrWhiteSpace(unit.AreaClusterKey))
        {
            usedClusterKeys.Add(unit.AreaClusterKey);
        }

        foreach (var pointer in ClusterMemberPointers(unit))
        {
            used.Add(pointer);
        }
    }

    private List<UnitEntry> CollapseTrainMarkerTargets(List<UnitEntry> alive)
    {
        const float trainClusterRadiusKm = 1.35f;
        var hasAnyTrainStation = alive.Any(unit => unit.IsAlive && IsTrainStationLike(unit));
        var result = new List<UnitEntry>();
        var consumed = new HashSet<IntPtr>();

        foreach (var unit in alive)
        {
            if (unit.Location != null && consumed.Contains(unit.Location.Pointer))
            {
                continue;
            }

            if (fcs.IsDepletedAreaClusterMember(unit)
                || !IsMovingTrainCarLike(unit)
                || unit.HasVelocity && hasAnyTrainStation)
            {
                result.Add(unit);
                if (unit.Location != null) consumed.Add(unit.Location.Pointer);
                continue;
            }

            var anchorKm = GetEntityKmPos(unit);
            var cluster = BuildConnectedTrainCluster(unit, alive, consumed, trainClusterRadiusKm)
                .Where(candidate => !fcs.IsDepletedAreaClusterMember(candidate))
                .ToList();
            var trainShellRadius = ShellEffectProfiles.ImpactRadiusOrDefault(BulletType.HCHE, 0.55f);
            if (!unit.HasVelocity)
            {
                var pointers = cluster
                    .Where(candidate => candidate.Location != null)
                    .Select(candidate => candidate.Location!.Pointer)
                    .ToHashSet();
                cluster.AddRange(alive
                    .Where(candidate => candidate.IsAlive)
                    .Where(candidate => !fcs.IsDepletedAreaClusterMember(candidate))
                    .Where(candidate => !IsTrainStationLike(candidate))
                    .Where(candidate => candidate.Location == null || !pointers.Contains(candidate.Location.Pointer))
                    .Where(candidate => Vector2.Distance(GetEntityKmPos(candidate), anchorKm) <= trainShellRadius * 2f));
            }
            cluster = SelectBestCoveredClusterSegment(cluster, trainShellRadius, anchorKm);

            if (cluster.Count < 2)
            {
                result.Add(unit);
                if (unit.Location != null) consumed.Add(unit.Location.Pointer);
                continue;
            }

            var representative = cluster
                .OrderByDescending(candidate => TargetPriority.GetPriority(candidate.Location))
                .ThenByDescending(candidate => TargetPriority.GetStars(candidate.Location))
                .ThenBy(candidate => Vector2.Distance(GetEntityKmPos(candidate), anchorKm))
                .First();
            var centerWorld = new Vector3(
                cluster.Average(item => item.WorldPos.x),
                cluster.Average(item => item.WorldPos.y),
                cluster.Average(item => item.WorldPos.z));
            var centerVelocity = new Vector3(
                cluster.Average(item => item.VelocityWorldPerSecond.x),
                cluster.Average(item => item.VelocityWorldPerSecond.y),
                cluster.Average(item => item.VelocityWorldPerSecond.z));
            var centerKm = new Vector2(
                cluster.Average(item => GetEntityKmPos(item).x),
                cluster.Average(item => GetEntityKmPos(item).y));

            result.Add(new UnitEntry
            {
                Name = representative.Name,
                DisplayName = representative.DisplayName,
                AreaClusterKey = BuildAreaClusterKey(centerKm),
                AreaClusterMembers = cluster
                    .Where(item => item.Location != null)
                    .Select(item => item.Location!.Pointer)
                    .Distinct()
                    .ToList(),
                WorldPos = centerWorld,
                VelocityWorldPerSecond = centerVelocity,
                HasVelocity = cluster.Any(item => item.HasVelocity),
                ObservedTime = representative.ObservedTime,
                IsAlive = representative.IsAlive,
                Location = representative.Location
            });
            foreach (var item in cluster)
            {
                if (item.Location != null) consumed.Add(item.Location.Pointer);
            }
        }

        return result;
    }

    private static List<UnitEntry> BuildConnectedTrainCluster(
        UnitEntry anchor,
        List<UnitEntry> alive,
        HashSet<IntPtr> consumed,
        float linkRadiusKm)
    {
        var candidates = alive
            .Where(IsMovingTrainCarLike)
            .Where(candidate => candidate.Location == null || !consumed.Contains(candidate.Location.Pointer))
            .ToList();
        var seed = candidates.FirstOrDefault(unit => IsSameRadarUnit(unit, anchor));
        if (seed == null)
        {
            return new List<UnitEntry>();
        }

        var cluster = new List<UnitEntry>();
        var pending = new Queue<UnitEntry>();
        pending.Enqueue(seed);
        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            if (cluster.Any(unit => IsSameRadarUnit(unit, current)))
            {
                continue;
            }

            cluster.Add(current);
            var currentKm = GetEntityKmPos(current);
            foreach (var candidate in candidates)
            {
                if (cluster.Any(unit => IsSameRadarUnit(unit, candidate))
                    || pending.Any(unit => IsSameRadarUnit(unit, candidate)))
                {
                    continue;
                }

                if (Vector2.Distance(GetEntityKmPos(candidate), currentKm) <= linkRadiusKm)
                {
                    pending.Enqueue(candidate);
                }
            }
        }

        return cluster;
    }

    private List<UnitEntry> CollapseCloseMarkerTargets(List<UnitEntry> alive)
    {
        if (!ShellEffectProfiles.TryGetImpactRadius(BulletType.AP, out var apEffectiveRadiusKm))
        {
            return alive;
        }

        var tightClusterRadiusKm = apEffectiveRadiusKm * 2f;
        var minClusterTargets = ShellEffectProfiles.Get(BulletType.AP).MinClusterTargets;
        var result = new List<UnitEntry>();
        var consumed = new HashSet<IntPtr>();

        foreach (var unit in alive)
        {
            if (unit.Location != null && consumed.Contains(unit.Location.Pointer))
            {
                continue;
            }
            if (fcs.IsDepletedAreaClusterMember(unit)
                || !string.IsNullOrWhiteSpace(unit.AreaClusterKey)
                || IsMovingTrainCarLike(unit))
            {
                result.Add(unit);
                MarkConsumed(unit, consumed);
                continue;
            }

            var anchorKm = GetEntityKmPos(unit);
            var cluster = alive
                .Where(candidate => string.IsNullOrWhiteSpace(candidate.AreaClusterKey))
                .Where(candidate => !IsMovingTrainCarLike(candidate))
                .Where(candidate => !fcs.IsDepletedAreaClusterMember(candidate))
                .Where(candidate => candidate.Location == null || !consumed.Contains(candidate.Location.Pointer))
                .Where(candidate => Vector2.Distance(GetEntityKmPos(candidate), anchorKm) <= tightClusterRadiusKm)
                .ToList();

            if (cluster.Count < minClusterTargets)
            {
                result.Add(unit);
                MarkConsumed(unit, consumed);
                continue;
            }

            var centerKm = new Vector2(
                cluster.Average(item => GetEntityKmPos(item).x),
                cluster.Average(item => GetEntityKmPos(item).y));
            if (cluster.Any(item => Vector2.Distance(GetEntityKmPos(item), centerKm) > apEffectiveRadiusKm))
            {
                result.Add(unit);
                MarkConsumed(unit, consumed);
                continue;
            }

            var representative = cluster
                .OrderByDescending(candidate => TargetPriority.GetPriority(candidate.Location))
                .ThenByDescending(candidate => TargetPriority.GetStars(candidate.Location))
                .ThenBy(candidate => Vector2.Distance(GetEntityKmPos(candidate), anchorKm))
                .First();
            var centerWorld = new Vector3(
                cluster.Average(item => item.WorldPos.x),
                cluster.Average(item => item.WorldPos.y),
                cluster.Average(item => item.WorldPos.z));
            var centerVelocity = new Vector3(
                cluster.Average(item => item.VelocityWorldPerSecond.x),
                cluster.Average(item => item.VelocityWorldPerSecond.y),
                cluster.Average(item => item.VelocityWorldPerSecond.z));

            result.Add(new UnitEntry
            {
                Name = representative.Name,
                DisplayName = representative.DisplayName,
                AreaClusterKey = BuildTightClusterKey(centerKm),
                AreaClusterMembers = cluster
                    .Where(item => item.Location != null)
                    .Select(item => item.Location!.Pointer)
                    .Distinct()
                    .ToList(),
                WorldPos = centerWorld,
                VelocityWorldPerSecond = centerVelocity,
                HasVelocity = cluster.Any(item => item.HasVelocity),
                ObservedTime = representative.ObservedTime,
                IsAlive = representative.IsAlive,
                Location = representative.Location
            });

            foreach (var item in cluster)
            {
                MarkConsumed(item, consumed);
            }
        }

        return result;
    }

    private static void MarkConsumed(UnitEntry unit, HashSet<IntPtr> consumed)
    {
        foreach (var pointer in ClusterMemberPointers(unit))
        {
            consumed.Add(pointer);
        }
    }

    private static string BuildAreaClusterKey(Vector2 centerKm)
    {
        const float gridKm = 0.5f;
        var x = Mathf.Round(centerKm.x / gridKm) * gridKm;
        var y = Mathf.Round(centerKm.y / gridKm) * gridKm;
        return $"train:{x:F1}:{y:F1}";
    }

    private static string BuildTightClusterKey(Vector2 centerKm)
    {
        const float gridKm = 0.1f;
        var x = Mathf.Round(centerKm.x / gridKm) * gridKm;
        var y = Mathf.Round(centerKm.y / gridKm) * gridKm;
        return $"tight:{x:F1}:{y:F1}";
    }

    private static bool IsStationaryTrainCarLike(UnitEntry unit)
    {
        return IsMovingTrainCarLike(unit) && !unit.HasVelocity;
    }

    private static List<UnitEntry> SelectBestCoveredClusterSegment(List<UnitEntry> units, float radiusKm, Vector2 anchorKm)
    {
        if (units.Count < 2)
        {
            return units;
        }

        var candidateCenters = units
            .Select(GetEntityKmPos)
            .ToList();
        for (var i = 0; i < units.Count; i++)
        {
            for (var j = i + 1; j < units.Count; j++)
            {
                candidateCenters.Add((GetEntityKmPos(units[i]) + GetEntityKmPos(units[j])) * 0.5f);
            }
        }

        return candidateCenters
            .Select(center => new
            {
                Center = center,
                Units = units
                    .Where(unit => Vector2.Distance(GetEntityKmPos(unit), center) <= radiusKm)
                    .ToList()
            })
            .Where(item => item.Units.Count >= 2)
            .OrderByDescending(item => item.Units.Count)
            .ThenByDescending(item => item.Units.Sum(unit => TargetPriority.GetPriority(unit.Location)))
            .ThenBy(item => Vector2.Distance(item.Center, anchorKm))
            .Select(item => item.Units)
            .FirstOrDefault() ?? new List<UnitEntry>();
    }

    private static IEnumerable<UnitEntry> DeduplicateUnits(IEnumerable<UnitEntry> entries)
    {
        var seenLocations = new HashSet<IntPtr>();
        var seenApprox = new List<UnitEntry>();

        foreach (var entry in entries)
        {
            if (entry.Location != null)
            {
                var pointer = entry.Location.Pointer;
                if (!seenLocations.Add(pointer))
                {
                    continue;
                }
            }

            var km = GetEntityKmPos(entry);
            var duplicateApprox = seenApprox.Any(existing =>
                string.Equals(existing.Name, entry.Name, StringComparison.OrdinalIgnoreCase)
                && Vector2.Distance(GetEntityKmPos(existing), km) <= 0.05f);
            if (duplicateApprox)
            {
                continue;
            }

            seenApprox.Add(entry);
            yield return entry;
        }
    }

    private static Transform? FindFireMissionRoot()
    {
        var root = GameObject.Find("Fire Mission Root")?.transform;
        if (root != null) return root;
        return UnityEngine.Object.FindObjectOfType<FireMission>()?.transform;
    }

    private bool IsKnownUnit(EntityLocation? loc, GameObject obj)
    {
        if (loc != null) {
            return units.Exists(u => u.Location == loc);
        }
        return units.Exists(u => u.Location == null && u.Name == obj.name && u.WorldPos == obj.transform.position);
    }

    private static string LocationId(EntityLocation? location)
    {
        return location == null ? "null" : location.Pointer.ToString();
    }

    private void UpdateMotionTrack(UnitEntry entry)
    {
        entry.ObservedTime = Time.time;
        var key = TrackKey(entry);
        if (key == null) return;

        if (trackedUnits.TryGetValue(key, out var previous))
        {
            var dt = Time.time - previous.Time;
            if (dt >= 0.5f && dt <= ScanInterval + 1.5f)
            {
                var delta = entry.WorldPos - previous.WorldPos;
                if (delta.magnitude > 0.002f)
                {
                    entry.VelocityWorldPerSecond = delta / dt;
                    entry.HasVelocity = true;
                }
            }
        }

        trackedUnits[key] = new TrackSample { WorldPos = entry.WorldPos, Time = Time.time };
    }

    private static string? TrackKey(UnitEntry entry)
    {
        if (entry.Location != null) return $"loc:{entry.Location.Pointer}";
        return string.IsNullOrWhiteSpace(entry.Name) ? null : $"name:{entry.Name}";
    }

    private static string DisplayName(UnitEntry unit)
    {
        return string.IsNullOrWhiteSpace(unit.DisplayName) ? unit.Name : unit.DisplayName;
    }

    private static string BuildDisplayName(string name, string icon, string role)
    {
        var source = $"{name} {icon} {role}".ToLowerInvariant();
        var suffix = ReadNumberSuffix(name);
        var label = "目标";

        if (source.Contains("train_station") || source.Contains("train station") || source.Contains("target station") || source.Contains("terminal")) label = "目标总站";
        else if (source.Contains("train_locomotive") || source.Contains("locomotive") || source.Contains("engine")) label = "火车头";
        else if (IsLetteredTrainCarName(name) || source.Contains("train_transport") || source.Contains("flatcar") || source.Contains("flatbed") || source.Contains("railcar") || source.Contains("freight") || source.Contains("wagon")) label = "列车/平板车";
        else if (source.Contains("commander")) label = "敌方指挥官";
        else if (source.Contains("fdc") || source.Contains("fire_direction") || source.Contains("fire direction")) label = "火控中心";
        else if (source.Contains("ammunition") || source.Contains("ammo") || source.Contains("supply") || source.Contains("cache")) label = "补给仓库";
        else if (source.Contains("hospital") || source.Contains("medical")) label = "医疗目标";
        else if (source.Contains("mechanized") || source.Contains("mech")) label = "机械化步兵";
        else if (source.Contains("armour") || source.Contains("armor") || source.Contains("tank")) label = "装甲目标";
        else if (source.Contains("bunker") || source.Contains("fort") || source.Contains("fortification") || source.Contains("pillbox")) label = "地堡";
        else if (source.Contains("artillery")) label = "炮兵";
        else if (source.Contains("recon") || source.Contains("spotter") || source.Contains("scout")) label = "侦察";
        else if (source.Contains("infantry")) label = "步兵";
        else if (source.Contains("target")) label = "目标";

        return suffix > 0 ? $"{label}#{suffix}" : label;
    }

    private static int ReadNumberSuffix(string name)
    {
        var match = Regex.Match(name, @"#(?<id>\d+)$");
        return match.Success && int.TryParse(match.Groups["id"].Value, out var id) ? id : 0;
    }

    public void OnGui()
    {
        if (!showRadar) return;

        if (radarRect.width < 10) radarRect = new Rect(Screen.width - 260, 10, 240, 160);

        var aliveUnits = units.Where(u => u.IsAlive).ToList();
        var deadUnits = units.Where(u => !u.IsAlive).ToList();

        float h = 24f;
        float lineH = h + 4f;
        const int maxAliveShow = 16;
        const int maxDeadShow = 5;
        int aliveShow = Mathf.Min(aliveUnits.Count, maxAliveShow);
        int deadShow = Mathf.Min(deadUnits.Count, maxDeadShow);
        int rowCount = aliveShow
                       + (aliveUnits.Count > aliveShow ? 1 : 0)
                       + (deadUnits.Count > 0 ? deadShow + 1 : 0)
                       + (deadUnits.Count > deadShow ? 1 : 0);
        radarRect.height = 58f + rowCount * lineH;

        GUI.Box(radarRect, "");

        float x = radarRect.x + 8f;
        float w = radarRect.width - 16f;
        float itemX = x + 10f;
        float itemW = w - 10f;
        float y = radarRect.y + 6f;

        void DrawLabel(Rect rect, string text, int fontSize)
        {
            var oldFontSize = GUI.skin.label.fontSize;
            GUI.skin.label.fontSize = fontSize;
            GUI.Label(rect, text);
            GUI.skin.label.fontSize = oldFontSize;
        }

        var oldColor = GUI.color;
        GUI.color = ClrTitle;
        DrawLabel(new Rect(x, y, w, h), $"目标 ({aliveUnits.Count} 存活) [{(AutoPlaceMarkers ? "自动" : "手动")}]", 15);
        GUI.color = oldColor;
        y += lineH + 2f;

        if (aliveUnits.Count == 0 && deadUnits.Count == 0)
        {
            GUI.color = ClrLabel;
            DrawLabel(new Rect(itemX, y, itemW, h), "未发现目标", 14);
            GUI.color = oldColor;
            return;
        }

        for (int i = 0; i < aliveShow; i++)
        {
            GUI.color = ClrAlive;
            DrawLabel(new Rect(itemX, y, itemW, h), $"● {DisplayName(aliveUnits[i])}", 14);
            GUI.color = oldColor;
            y += lineH;
        }
        if (aliveUnits.Count > aliveShow)
        {
            GUI.color = ClrLabel;
            DrawLabel(new Rect(itemX, y, itemW, h), $"... 另有 {aliveUnits.Count - aliveShow} 个存活", 13);
            GUI.color = oldColor;
            y += lineH;
        }

        if (deadUnits.Count > 0)
        {
            y += 2f;
            GUI.color = ClrDeadTitle;
            DrawLabel(new Rect(x, y, w, h), $"已摧毁 ({deadUnits.Count}):", 14);
            GUI.color = oldColor;
            y += lineH;

            for (int i = 0; i < deadShow; i++)
            {
                GUI.color = ClrDead;
                DrawLabel(new Rect(itemX, y, itemW, h), $"○ {DisplayName(deadUnits[i])}", 14);
                GUI.color = oldColor;
                y += lineH;
            }
            if (deadUnits.Count > deadShow)
            {
                GUI.color = ClrLabel;
                DrawLabel(new Rect(itemX, y, itemW, h), $"... 另有 {deadUnits.Count - deadShow} 个", 13);
                GUI.color = oldColor;
            }
        }
    }

    private static readonly List<string> _logLines = new();
    private static bool _logWritten;
    private static bool _onceLogged;
    public static Vector2 GetEntityKmPos(UnitEntry unit)
    {
        if (unit.Location != null)
        {
            try
            {
                var locProp = unit.Location.GetType().GetProperty("LocalPosition",
                    BindingFlags.Public | BindingFlags.Instance);
                if (locProp != null)
                {
                    var val = locProp.GetValue(unit.Location);
                    if (val is Vector2 v2) return v2;
                }
            }
            catch { }
        }
        return new Vector2(unit.WorldPos.x, unit.WorldPos.y);
    }

    private static void Log(string msg)
    {
        if (!DetailedRadarLog) return;
        _logLines.Add($"[{System.DateTime.Now:HH:mm:ss}] {msg}");
    }

    private static List<UnitEntry> SortByTargetPriority(IEnumerable<UnitEntry> entries)
    {
        return entries
            .OrderByDescending(u => TargetPriority.GetPriority(u.Location))
            .ThenByDescending(u => TargetPriority.GetStars(u.Location))
            .ToList();
    }

    private static bool IsAutoTargetable(UnitEntry unit)
    {
        if (IsNonCombatRadarObjectName(unit.Name))
        {
            return false;
        }

        // 自动开火优先依赖 EntityLocation；没有实体定位时只允许明确的敌方目标名兜底。
        return unit.Location != null || IsNameFallbackTargetCandidate(unit.Name);
    }

    private static bool IsNameFallbackTargetCandidate(string name)
    {
        var n = name.ToLowerInvariant();
        if (IsNonCombatRadarObjectName(name))
        {
            return false;
        }

        return n.Contains("enemytarget")
               || n.StartsWith("tgt_")
               || n.StartsWith("target_");
    }

    private static bool IsMovingTrainCarLike(UnitEntry unit)
    {
        var text = $"{unit.Name} {unit.DisplayName}".ToLowerInvariant();
        return !text.Contains("station")
               && !text.Contains("terminal")
               && !text.Contains("总站")
               && !text.Contains("鎬荤珯")
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
                   || text.Contains("火车")
                   || text.Contains("鍒楄溅"));
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

    private static bool IsRadarUiLabelObject(GameObject obj)
    {
        var text = "";
        try
        {
            var tmp = obj.GetComponent<TMP_Text>();
            if (tmp != null) text = tmp.text ?? "";
        }
        catch { }

        var combined = $"{obj.name} {text}".Trim();
        if (string.IsNullOrWhiteSpace(combined)) return false;

        var normalized = combined.Trim().ToLowerInvariant();
        if (Regex.IsMatch(normalized, @"^#?\d+\s*$")) return true;
        if (Regex.IsMatch(normalized, @"^#?\d+\s+(civ|ap|he|hche|smk|star|atmc|aphe)\s*$")) return true;
        if (normalized is "civ" or "ap" or "he" or "hche" or "smk" or "star" or "atmc" or "aphe") return true;
        return normalized.Contains("textmeshpro")
               || normalized.Contains("tmp text")
               || normalized.Contains("nametag")
               || normalized.Contains("name tag")
               || normalized.Contains("nameplate")
               || normalized.Contains("name plate");
    }

    private static bool IsNonCombatRadarObjectName(string name)
    {
        var n = name.ToLowerInvariant();
        return n.Contains("killtoken")
               || n.Contains("killtokens")
               || n.Contains("kill token")
               || n.Contains("kill_command")
               || n.Contains("kill command")
               || n.Contains("command token")
               || n.Contains("fmod")
               || n.Contains("sfx")
               || n.Contains("audio")
               || n.Contains("sound")
               || n.Contains("impact")
               || n.Contains("target_hit")
               || n.Contains("target_impact")
               || n.Contains("maptoken")
               || n.Contains("draggableitemgridarea")
               || n.Contains("gridarea")
               || n.Contains("marker")
               || n.Contains("reward")
               || n.Contains("score")
               || n.Contains("label")
               || n.Contains("icon");
    }

    private static bool IsProtectedUnitNameOrRole(string name, string icon, string role, int roleNum)
    {
        var text = $"{name} {icon} {role}".ToLowerInvariant();
        if (text.Contains("reference") || text.Contains("refrence")) return false;
        if (text.Contains("friendly") || text.Contains("frendly")) return true;
        if (text.Contains("civilian") || text.Contains("civ")) return true;
        if (text.Contains("hospital")) return true;
        if (text.Contains("police")) return true;
        if (text.Contains("ally")) return true;
        if (text.Contains("spotter") || text.Contains("recon")) return true;
        if (text.Contains("propaganda")) return true;

        const int roleAlly = 2;
        return roleNum >= 0 && (roleNum & roleAlly) != 0;
    }

    private static void FlushLog()
    {
        if (_logLines.Count == 0) return;
        try
        {
            var dir = Path.Combine(MelonEnvironment.UserDataDirectory, "IronNestFCS");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "radar_log.txt");
            File.AppendAllLines(path, _logLines);
            if (!_logWritten)
            {
                Log($"[Radar] Log written to: {path}");
                _logWritten = true;
            }
        }
        catch { }
        _logLines.Clear();
    }

    private struct TrackSample { public Vector3 WorldPos; public float Time; }
    private struct EntityBrief { public string icon; public string role; public int roleNum; }

    private static EntityBrief GetEntityInfo(EntityLocation loc)
    {
        var brief = new EntityBrief { icon = "?", role = "?", roleNum = -1 };
        try
        {
            var type = loc.GetType();
            var entityProp = type.GetProperty("Entity", BindingFlags.Public | BindingFlags.Instance);
            if (entityProp == null) return brief;
            var entity = entityProp.GetValue(loc);
            if (entity == null) return brief;
            var entType = entity.GetType();

            var iconProp = entType.GetProperty("Icon", BindingFlags.Public | BindingFlags.Instance);
            if (iconProp != null)
            {
                var val = iconProp.GetValue(entity);
                if (val is string s) brief.icon = s;
            }

            var roleProp = entType.GetProperty("Role", BindingFlags.Public | BindingFlags.Instance);
            if (roleProp != null)
            {
                var val = roleProp.GetValue(entity);
                if (val != null)
                {
                    brief.role = val.ToString();
                    if (val is int i) brief.roleNum = i;
                    else if (val is Enum e) brief.roleNum = Convert.ToInt32(e);
                }
            }
        }
        catch { }
        return brief;
    }

    public static bool IsUnitAlive(EntityLocation loc, GameObject go)
    {
        if (!go.activeInHierarchy) return false;

        try
        {
            var type = loc.GetType();

            var enabledProp = type.GetProperty("enabled", BindingFlags.Public | BindingFlags.Instance);
            if (enabledProp != null)
            {
                var enabledVal = enabledProp.GetValue(loc);
                if (enabledVal is bool b && !b) return false;
            }

            var entityProp = type.GetProperty("Entity", BindingFlags.Public | BindingFlags.Instance);
            if (entityProp != null)
            {
                var entity = entityProp.GetValue(loc);
                if (entity != null)
                {
                    if (!_onceLogged)
                    {
                        _onceLogged = true;
                        var entType = entity.GetType();
                        Log($"[Radar] MapEntity type: {entType.FullName}");
                        foreach (var f in entType.GetFields(BindingFlags.Public | BindingFlags.Instance))
                        {
                            try { Log($"[Radar] MapEntity field {f.Name} = {f.GetValue(entity)} ({f.FieldType.Name})"); }
                            catch { }
                        }
                        foreach (var p in entType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                        {
                            try { Log($"[Radar] MapEntity prop {p.Name} = {p.GetValue(entity)} ({p.PropertyType.Name})"); }
                            catch { }
                        }
                    }

                    var entType2 = entity.GetType();
                    foreach (var f in entType2.GetFields(BindingFlags.Public | BindingFlags.Instance))
                    {
                        var fn = f.Name.ToLower();
                        if (fn.Contains("alive") || fn.Contains("dead") || fn.Contains("destroyed") || fn.Contains("health") || fn.Contains("active"))
                        {
                            var val = f.GetValue(entity);
                            if (val is bool bVal) return fn.Contains("alive") || fn.Contains("active") ? bVal : !bVal;
                            if (val is float fVal) return fVal > 0;
                            if (val is int iVal) return iVal > 0;
                        }
                    }
                    foreach (var p in entType2.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    {
                        var pn = p.Name.ToLower();
                        if (pn.Contains("alive") || pn.Contains("dead") || pn.Contains("destroyed") || pn.Contains("active"))
                        {
                            var val = p.GetValue(entity);
                            if (val is bool bVal) return pn.Contains("alive") || pn.Contains("active") ? bVal : !bVal;
                        }
                    }
                }
            }
        }
        catch { }

        return go.activeSelf;
    }

    private static bool IsHostile(EntityLocation loc, Transform t)
    {
        var name = t.name;
        const int RoleAlly = 2;
        const int RoleEnemy = 1;
        const int RoleReference = 33554432;
        object? entity = null;

        try
        {
            var type = loc.GetType();

            var entityProp = type.GetProperty("Entity", BindingFlags.Public | BindingFlags.Instance);
            if (entityProp != null)
            {
                entity = entityProp.GetValue(loc);
                if (entity != null)
                {
                    var entType = entity.GetType();

                    // 第一优先：Icon + Role 判断（比字段遍历更可靠）
                    var iconProp = entType.GetProperty("Icon", BindingFlags.Public | BindingFlags.Instance);
                    var roleProp = entType.GetProperty("Role", BindingFlags.Public | BindingFlags.Instance);

                    string? icon = null;
                    int roleVal = -1;
                    if (iconProp != null)
                    {
                        var v = iconProp.GetValue(entity);
                        if (v is string s) icon = s;
                    }
                    if (roleProp != null)
                    {
                        var v = roleProp.GetValue(entity);
                        if (v is int i) roleVal = i;
                        else if (v is Enum e) roleVal = Convert.ToInt32(e);
                    }

                    if (roleVal >= 0)
                    {
                        bool hasAlly = (roleVal & RoleAlly) != 0;
                        bool hasEnemy = (roleVal & RoleEnemy) != 0;
                        bool isReference = (roleVal & RoleReference) != 0;

                        if (isReference) { Log($"[Radar] {name} -> neutral (Reference)"); return false; }
                        if (hasAlly && !hasEnemy) { Log($"[Radar] {name} -> friendly (Ally, roleNum={roleVal})"); return false; }
                        if (hasEnemy) { Log($"[Radar] {name} -> hostile (Enemy, roleNum={roleVal})"); return true; }
                    }

                    if (icon != null)
                    {
                        var iconLower = icon.ToLower();
                        if (iconLower.Contains("frendly") || iconLower.Contains("friendly")) { Log($"[Radar] {name} -> friendly ({icon})"); return false; }
                        if (iconLower.Contains("enemy")) { Log($"[Radar] {name} -> hostile ({icon})"); return true; }
                    }

                    // 第二优先：遍历字段找 team/side/faction/enemy/hostile
                    foreach (var f in entType.GetFields(BindingFlags.Public | BindingFlags.Instance))
                    {
                        var fn = f.Name.ToLower();
                        if (fn.Contains("team") || fn.Contains("side") || fn.Contains("faction"))
                        {
                            var val = f.GetValue(entity);
                            if (val is int iVal) { bool r = iVal != 0; Log($"[Radar] {name}.Entity.{f.Name}={iVal} -> hostile={r}"); return r; }
                        }
                        if (fn.Contains("enemy") || fn.Contains("hostile"))
                        {
                            var val = f.GetValue(entity);
                            if (val is bool bVal) { Log($"[Radar] {name}.Entity.{f.Name}={bVal} -> hostile={bVal}"); return bVal; }
                        }
                    }
                    foreach (var p in entType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    {
                        var pn = p.Name.ToLower();
                        if (pn.Contains("team") || pn.Contains("side") || pn.Contains("faction"))
                        {
                            var val = p.GetValue(entity);
                            if (val is int iVal) { bool r = iVal != 0; Log($"[Radar] {name}.Entity.{p.Name}={iVal} -> hostile={r}"); return r; }
                        }
                        if (pn.Contains("isenemy") || pn.Contains("hostile"))
                        {
                            var val = p.GetValue(entity);
                            if (val is bool bVal) { Log($"[Radar] {name}.Entity.{p.Name}={bVal} -> hostile={bVal}"); return bVal; }
                        }
                    }
                }
            }

            foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.Name.Contains("Team") || p.Name.Contains("team")
                    || p.Name.Contains("IsEnemy") || p.Name.Contains("isEnemy")
                    || p.Name.Contains("Hostile") || p.Name.Contains("hostile")
                    || p.Name.Contains("Side") || p.Name.Contains("side")
                    || p.Name.Contains("Faction") || p.Name.Contains("faction"))
                {
                    var val = p.GetValue(loc);
                    if (val is int iVal && iVal != 0) { Log($"[Radar] {name}.{p.Name}={iVal} -> hostile=true"); return true; }
                    if (val is bool bVal) { Log($"[Radar] {name}.{p.Name}={bVal} -> hostile={bVal}"); return bVal; }
                }
            }
        }
        catch (Exception ex) { MelonLogger.Warning($"[Radar] IsHostile err {name}: {ex.Message}"); }

        // Entity/Icon 都无结果时，用 DB 数据做名字二次判断
        var nameLower = name.ToLower();
        if (nameLower.Contains("police") || nameLower.Contains("prop")
            || nameLower.Contains("civ") || nameLower.Contains("smoke")
            || nameLower.Contains("reference") || nameLower.Contains("ref"))
        {
            Log($"[Radar] {name} -> neutral/civilian by name");
            return false;
        }
        if (nameLower.Contains("hospital") && !nameLower.Contains("ally"))
        {
            Log($"[Radar] {name} -> neutral hospital by name");
            return false;
        }
        if (nameLower.Contains("enemy") || nameLower.Contains("hostile")
            || nameLower.Contains("artillery") || nameLower.Contains("fdc")
            || nameLower.Contains("target"))
        {
            Log($"[Radar] {name} -> hostile by name");
            return true;
        }
        if (nameLower.Contains("friendly") || nameLower.Contains("ally"))
        {
            Log($"[Radar] {name} -> friendly by name");
            return false;
        }

        Log($"[Radar] {name} -> hostile (no match, assuming hostile)");
        return true;
    }
}

public static class FcsCalc
{
    private static readonly (float factor, float min, float max)[] Config =
    {
        (12.0f, 0, 5),
        (6.0f, 5, 10),
        (4.0f, 10, 15),
        (3.0f, 15, 20),
        (2.4f, 20, 25),
        (2.0f, 25, 30),
    };

    public static int Charge(float distance)
    {
        for (int i = 0; i < Config.Length; i++)
            if (distance > Config[i].min && distance <= Config[i].max)
                return i + 1;
        return distance > 30 ? 6 : 1;
    }

    public static float Elevation(float distance)
    {
        int chg = Charge(distance);
        float el = distance * Config[chg - 1].factor;
        return el > 60 || distance > 30 ? float.NaN : el;
    }
}
