using Il2Cpp;
using Il2CppTMPro;
using MelonLoader;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEngine;

namespace IronNestFCS.Logic.FCS;

public class MapTable {
    private const float MapMinX = 0f;
    private const float MapMaxX = 19.99f;
    private const float MapMinY = 0f;
    private const float MapMaxY = 9.99f;
    private const float DumpOutsideDistance = 2f;
    private const float MarkerStandbyTolerance = 0.03f;
    private const float MarkerEntityBindingToleranceKm = 0.35f;
    
    public Transform? turret;
    public Dictionary<int, Transform> artilleries;
    public Transform? fireMissionRoot;
    public FireMission? FireMission;
    private Transform? mapSurface;
    private Transform? playerTurretMarker;
    private Transform? playerTurretWorldSource;
    private string? lastPlayerTurretGrid;
    private readonly Dictionary<int, EntityLocation?> markerLocations = new();
    private readonly Dictionary<int, Vector3> markerStandbyPositions = new();
    private readonly HashSet<int> disabledMarkers = new();
    
    public bool TryBind() {
        artilleries = new Dictionary<int, Transform>();
        var turretObject = GameObject.Find("Player Turret Piece");
        if (turretObject == null) {
            MelonLogger.Warning("[FCS] 未找到 Player Turret Piece，当前场景尚未就绪");
            return false;
        }

        var mapObject = GameObject.Find("Draggable Surface");
        if (mapObject == null) {
            MelonLogger.Warning("[FCS] 未找到 Draggable Surface，当前场景尚未就绪");
            return false;
        }

        var turretPiece = turretObject.transform;
        turret = turretPiece;
        mapSurface = mapObject.transform;
        playerTurretMarker = null;
        playerTurretWorldSource = null;
        var map = mapObject.transform;
        for (var i = 0; i < map.childCount; ++i) {
            var t = map.GetChild(i);
            if (t.name != "MapToken_Artillery") {
                TrySetPlayerTurretMarkerCandidate(t, "name", IsExactPlayerTurretMarkerName(t.name));
                continue;
            }
            if (IsPlayerTurretMarker(t, turretPiece)) {
                playerTurretMarker = t;
                MelonLogger.Msg($"[FCS] 跳过玩家铁巢地图标记: {GetTransformPath(t)}");
                continue;
            }
            var tmp = t.GetComponentInChildren<TextMeshPro>();
            if (tmp == null) {
                TrySetPlayerTurretMarkerCandidate(t, "artillery token without text", force: true);
                continue;
            }
            if (!int.TryParse(tmp.text, out var id)) {
                TrySetPlayerTurretMarkerCandidate(t, $"artillery token text='{tmp.text}'", IsPlayerTurretMarkerText(tmp.text));
                continue;
            }
            if (artilleries.ContainsKey(id)) {
                MelonLogger.Warning($"[FCS] 跳过重复地图炮兵标记 T{id}: {GetTransformPath(t)}");
                continue;
            }
            artilleries.Add(id, t);
            markerStandbyPositions[id] = t.localPosition;
        }
        if (playerTurretMarker == null) {
            FindPlayerTurretMarkerFallback(map);
        }
        if (playerTurretMarker != null) {
            turret = playerTurretMarker;
        }
        TryAutoPlacePlayerTurretMarker();
        MelonLogger.Msg($"[FCS] 找到 Player Turret Piece: {turret}, Artilleries: {artilleries.Count}");
        var fireMissionObject = GameObject.Find("Fire Mission Root");
        if (fireMissionObject == null) {
            FireMission = UnityEngine.Object.FindObjectOfType<FireMission>();
            fireMissionRoot = FireMission?.transform;
            if (fireMissionRoot == null) {
                MelonLogger.Warning("[FCS] 未找到 Fire Mission Root，雷达将使用全场景 EntityLocation 兜底扫描");
                return true;
            }
            MelonLogger.Msg($"[FCS] 通过 FireMission 组件找到任务根节点: {GetTransformPath(fireMissionRoot)}");
            return true;
        }

        fireMissionRoot = fireMissionObject.transform;
        FireMission = fireMissionRoot.GetComponent<FireMission>();
        if (FireMission == null) {
            MelonLogger.Warning("[FCS] Fire Mission Root 没有 FireMission 组件，雷达将使用全场景 EntityLocation 兜底扫描");
        }
        return true;
    }

    public bool TryAutoPlacePlayerTurretMarker()
    {
        if (mapSurface == null || playerTurretMarker == null) {
            return false;
        }

        if (TryFindPlayerTurretGridKm(out var grid, out var kmPos)) {
            SetPlayerTurretMarkerKmPos(kmPos);
            if (lastPlayerTurretGrid != grid) {
                lastPlayerTurretGrid = grid;
                MelonLogger.Msg($"[FCS] 铁巢坐标已从情报面板更新: {grid} -> km=({kmPos.x:F2},{kmPos.y:F2})");
            }
            return true;
        }

        if (playerTurretWorldSource == null) {
            return true;
        }

        var local = mapSurface.InverseTransformPoint(playerTurretWorldSource.position);
        local.z = playerTurretMarker.localPosition.z;
        playerTurretMarker.localPosition = local;

        return true;
    }

    private void SetPlayerTurretMarkerKmPos(Vector2 kmPos)
    {
        if (playerTurretMarker == null) return;
        var local = KmToMapLocal(kmPos, playerTurretMarker.localPosition.z);
        playerTurretMarker.localPosition = local;
        turret = playerTurretMarker;
    }

    public void DebugScanMapTextsAndTurretSources()
    {
        MelonLogger.Msg("[FCS] Debug grid scan start. 只输出情报/任务面板 GRID。");

        var textCount = 0;
        var gridCount = 0;
        var texts = UnityEngine.Object.FindObjectsOfType<TMP_Text>();
        foreach (var tmp in texts) {
            if (tmp == null || tmp.transform == null) continue;
            var text = tmp.text ?? "";
            var path = GetTransformPath(tmp.transform);
            textCount++;
            gridCount += LogGridReferencesFromText(text, path);
        }

        MelonLogger.Msg($"[FCS] Debug grid scan finished. texts={textCount}, grids={gridCount}.");
    }

    private static bool TryFindPlayerTurretGridKm(out string grid, out Vector2 kmPos)
    {
        grid = "";
        kmPos = default;

        var texts = UnityEngine.Object.FindObjectsOfType<TMP_Text>();
        foreach (var tmp in texts) {
            if (tmp == null) continue;
            var text = NormalizeGridText(StripRichTextTags(tmp.text ?? ""));
            if (string.IsNullOrWhiteSpace(text)) continue;

            if (TryFindPlayerTurretByObserverFix(text, out grid, out kmPos)) {
                return true;
            }

            foreach (var line in Regex.Split(text, @"\r?\n")) {
                if (TryFindPlayerTurretGridInLine(line, out grid, out kmPos)) {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryFindPlayerTurretGridInLine(string line, out string grid, out Vector2 kmPos)
    {
        grid = "";
        kmPos = default;
        if (string.IsNullOrWhiteSpace(line)) return false;

        // “铁巢从 B3 9:2 转移至未知位置”里的坐标是旧坐标，不能当当前铁巢位置。
        if (Regex.IsMatch(line, @"铁巢\s*从\s*[A-Z]\d{1,2}\s+\d:\d\s*转移", RegexOptions.IgnoreCase)) {
            return false;
        }

        var exactLine = Regex.Match(
            line,
            @"^\s*(?:铁巢|Iron\s+Nest|IRON\s+NEST)\s*(?:location)?\s*[-－—–:：]\s*(?<grid>[A-Z]\d{1,2}\s+\d:\d)",
            RegexOptions.IgnoreCase);
        if (exactLine.Success) {
            grid = exactLine.Groups["grid"].Value;
            return TryParseGridKm(grid, out kmPos);
        }

        var currentExact = Regex.Match(
            line,
            @"(?:铁巢|Iron\s+Nest|IRON\s+NEST)\s*(?:新位置|当前位置|位置)?\s*(?:位于|在|located\s+at)\s*(?<grid>[A-Z]\d{1,2}\s+\d:\d)",
            RegexOptions.IgnoreCase);
        if (currentExact.Success) {
            grid = currentExact.Groups["grid"].Value;
            return TryParseGridKm(grid, out kmPos);
        }

        var currentCell = Regex.Match(
            line,
            @"(?:铁巢|Iron\s+Nest|IRON\s+NEST)\s*(?:新位置|当前位置|位置)?\s*(?:位于|在|located\s+at)\s*(?<cell>[A-Z]\d{1,2})\s*(?:某处|附近|area)?",
            RegexOptions.IgnoreCase);
        if (currentCell.Success) {
            grid = currentCell.Groups["cell"].Value + " cell-center";
            return TryParseGridCellCenterKm(currentCell.Groups["cell"].Value, out kmPos);
        }

        return false;
    }

    private static bool TryFindPlayerTurretByObserverFix(string text, out string grid, out Vector2 kmPos)
    {
        grid = "";
        kmPos = default;

        var bearings = ExtractBearingObservations(text);
        var ranges = ExtractRangeObservations(text);
        if (bearings.Count + ranges.Count < 2) {
            return false;
        }

        if (bearings.Count == 1
            && ranges.Count == 1
            && TryIntersectBearingAndDistance(bearings[0].Origin, bearings[0].Bearing, ranges[0].Origin, ranges[0].DistanceKm, out kmPos)) {
            grid = $"observer-fix {bearings[0].Source} + {ranges[0].Source}";
            return true;
        }

        if (!TrySolveObserverFix(bearings, ranges, out kmPos, out var error)) {
            return false;
        }

        grid = $"observer-fix bearings={bearings.Count}, ranges={ranges.Count}, err={error:F2}km";
        return true;
    }

    private sealed class BearingObservation
    {
        public Vector2 Origin;
        public float Bearing;
        public string Source = "";
    }

    private sealed class RangeObservation
    {
        public Vector2 Origin;
        public float DistanceKm;
        public string Source = "";
    }

    private static List<BearingObservation> ExtractBearingObservations(string text)
    {
        var result = new List<BearingObservation>();
        foreach (Match match in Regex.Matches(
                     text,
                     @"发现\s*(?:铁巢|Iron\s+Nest|IRON\s+NEST).*?(?<bearing>\d{1,3}(?:\.\d+)?)\s*自\s*(?<origin>[A-Z]\d{1,2}\s+\d:\d)",
                     RegexOptions.IgnoreCase | RegexOptions.Singleline)) {
            if (!float.TryParse(match.Groups["bearing"].Value, out var bearing)
                || !TryParseGridKm(match.Groups["origin"].Value, out var origin)) {
                continue;
            }

            result.Add(new BearingObservation {
                Origin = origin,
                Bearing = NormalizeAngle(bearing),
                Source = $"{match.Groups["origin"].Value} {bearing:F0}deg"
            });
        }
        return result;
    }

    private static List<RangeObservation> ExtractRangeObservations(string text)
    {
        var result = new List<RangeObservation>();
        foreach (Match match in Regex.Matches(
                     text,
                     @"(?:测距仪显示我与铁巢相距|与铁巢相距)\s*(?<distance>\d+(?:\.\d+)?)\s*km.*?(?:车队|观测员|观察员|Observer)#?\d*\s*(?:当前)?位置\s*[:：]?\s*(?<origin>[A-Z]\d{1,2}\s+\d:\d)",
                     RegexOptions.IgnoreCase | RegexOptions.Singleline)) {
            if (!float.TryParse(match.Groups["distance"].Value, out var distanceKm)
                || !TryParseGridKm(match.Groups["origin"].Value, out var origin)) {
                continue;
            }

            result.Add(new RangeObservation {
                Origin = origin,
                DistanceKm = distanceKm,
                Source = $"{match.Groups["origin"].Value} {distanceKm:F2}km"
            });
        }

        foreach (Match match in Regex.Matches(
                     text,
                     @"(?:车队|观测员|观察员|Observer)#?\d*\s*(?:当前)?位置\s*[:：]?\s*(?<origin>[A-Z]\d{1,2}\s+\d:\d).*?(?:测距仪显示我与铁巢相距|与铁巢相距)\s*(?<distance>\d+(?:\.\d+)?)\s*km",
                     RegexOptions.IgnoreCase | RegexOptions.Singleline)) {
            if (!float.TryParse(match.Groups["distance"].Value, out var distanceKm)
                || !TryParseGridKm(match.Groups["origin"].Value, out var origin)
                || result.Any(item => Vector2.Distance(item.Origin, origin) < 0.01f && Mathf.Abs(item.DistanceKm - distanceKm) < 0.01f)) {
                continue;
            }

            result.Add(new RangeObservation {
                Origin = origin,
                DistanceKm = distanceKm,
                Source = $"{match.Groups["origin"].Value} {distanceKm:F2}km"
            });
        }

        return result;
    }

    private static bool TryIntersectBearingAndDistance(
        Vector2 rayOrigin,
        float bearing,
        Vector2 circleCenter,
        float radius,
        out Vector2 result)
    {
        result = default;
        if (radius <= 0f) return false;

        var radians = bearing * Mathf.Deg2Rad;
        var direction = new Vector2(Mathf.Sin(radians), Mathf.Cos(radians));
        if (direction.sqrMagnitude <= 0.0001f) return false;
        direction.Normalize();

        var offset = rayOrigin - circleCenter;
        var b = 2f * Vector2.Dot(offset, direction);
        var c = Vector2.Dot(offset, offset) - radius * radius;
        var discriminant = b * b - 4f * c;
        if (discriminant < -0.0001f) return false;

        var sqrt = Mathf.Sqrt(Mathf.Max(0f, discriminant));
        var t1 = (-b - sqrt) / 2f;
        var t2 = (-b + sqrt) / 2f;
        var candidates = new List<Vector2>();
        if (t1 >= 0f) candidates.Add(rayOrigin + direction * t1);
        if (t2 >= 0f) candidates.Add(rayOrigin + direction * t2);
        if (candidates.Count == 0) return false;

        var validCandidates = candidates
            .Where(IsKmInsideTacticalMap)
            .OrderBy(candidate => Vector2.Distance(candidate, rayOrigin))
            .ToList();
        if (validCandidates.Count == 0) return false;

        result = validCandidates[0];
        return true;
    }

    private static bool TrySolveObserverFix(
        List<BearingObservation> bearings,
        List<RangeObservation> ranges,
        out Vector2 result,
        out float error)
    {
        result = default;
        error = float.MaxValue;
        if (bearings.Count + ranges.Count < 2) return false;

        var seeds = BuildObserverFixSeeds(bearings, ranges);
        if (seeds.Count == 0) {
            seeds.Add(new Vector2((MapMinX + MapMaxX) * 0.5f, (MapMinY + MapMaxY) * 0.5f));
        }

        var best = seeds
            .Where(IsKmInsideTacticalMap)
            .Select(seed => (pos: seed, score: ObserverFixError(seed, bearings, ranges)))
            .OrderBy(item => item.score)
            .FirstOrDefault();
        if (!IsKmInsideTacticalMap(best.pos)) return false;

        var step = 0.25f;
        var current = best.pos;
        var currentScore = best.score;
        while (step >= 0.01f) {
            var improved = false;
            for (var dx = -1; dx <= 1; dx++) {
                for (var dy = -1; dy <= 1; dy++) {
                    if (dx == 0 && dy == 0) continue;
                    var candidate = current + new Vector2(dx * step, dy * step);
                    if (!IsKmInsideTacticalMap(candidate)) continue;

                    var score = ObserverFixError(candidate, bearings, ranges);
                    if (score >= currentScore) continue;

                    current = candidate;
                    currentScore = score;
                    improved = true;
                }
            }
            if (!improved) step *= 0.5f;
        }

        result = current;
        error = Mathf.Sqrt(currentScore / Mathf.Max(1, bearings.Count + ranges.Count));
        return IsKmInsideTacticalMap(result);
    }

    private static List<Vector2> BuildObserverFixSeeds(List<BearingObservation> bearings, List<RangeObservation> ranges)
    {
        var seeds = new List<Vector2>();
        foreach (var bearing in bearings) {
            foreach (var range in ranges) {
                if (TryIntersectBearingAndDistance(bearing.Origin, bearing.Bearing, range.Origin, range.DistanceKm, out var intersection)) {
                    seeds.Add(intersection);
                }
            }
        }

        foreach (var pair in IntersectBearingPairs(bearings)) {
            seeds.Add(pair);
        }

        foreach (var range in ranges) {
            seeds.Add(range.Origin);
        }
        foreach (var bearing in bearings) {
            seeds.Add(bearing.Origin + BearingDirection(bearing.Bearing) * 0.5f);
        }

        return seeds;
    }

    private static IEnumerable<Vector2> IntersectBearingPairs(List<BearingObservation> bearings)
    {
        for (var i = 0; i < bearings.Count; i++) {
            for (var j = i + 1; j < bearings.Count; j++) {
                var p = bearings[i].Origin;
                var r = BearingDirection(bearings[i].Bearing);
                var q = bearings[j].Origin;
                var s = BearingDirection(bearings[j].Bearing);
                var denom = Cross(r, s);
                if (Mathf.Abs(denom) < 0.0001f) continue;

                var t = Cross(q - p, s) / denom;
                var u = Cross(q - p, r) / denom;
                if (t < 0f || u < 0f) continue;

                var candidate = p + r * t;
                if (IsKmInsideTacticalMap(candidate)) yield return candidate;
            }
        }
    }

    private static float ObserverFixError(Vector2 candidate, List<BearingObservation> bearings, List<RangeObservation> ranges)
    {
        var score = 0f;
        foreach (var bearing in bearings) {
            var direction = BearingDirection(bearing.Bearing);
            var delta = candidate - bearing.Origin;
            var behindPenalty = Mathf.Max(0f, -Vector2.Dot(delta, direction));
            var perpendicular = Mathf.Abs(Cross(delta, direction));
            score += perpendicular * perpendicular + behindPenalty * behindPenalty * 4f;
        }

        foreach (var range in ranges) {
            var miss = Vector2.Distance(candidate, range.Origin) - range.DistanceKm;
            score += miss * miss;
        }

        return score;
    }

    private static Vector2 BearingDirection(float bearing)
    {
        var radians = NormalizeAngle(bearing) * Mathf.Deg2Rad;
        return new Vector2(Mathf.Sin(radians), Mathf.Cos(radians)).normalized;
    }

    private static float Cross(Vector2 a, Vector2 b)
    {
        return a.x * b.y - a.y * b.x;
    }

    private static float NormalizeAngle(float angle)
    {
        angle %= 360f;
        return angle < 0f ? angle + 360f : angle;
    }

    private static int LogGridReferencesFromText(string text, string path)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;

        var count = 0;
        var plainText = NormalizeGridText(StripRichTextTags(text));
        foreach (Match match in Regex.Matches(
                     plainText,
                     @"(?<label>铁巢|Iron\s+Nest|IRON\s+NEST|观测员#?\d+|Observer#?\d+|观察员#?\d+|EnemyTarget|Target)\s*(?:location)?\s*[-－—–:：]?\s*(?<grid>[A-Z]\d{1,2}\s+\d:\d)",
                     RegexOptions.IgnoreCase)) {
            MelonLogger.Msg(
                $"[FCS] Debug grid: label='{match.Groups["label"].Value}', " +
                $"grid='{match.Groups["grid"].Value}', path={path}");
            count++;
        }

        foreach (Match match in Regex.Matches(
                     text,
                     @"\[(?<kind>GRID|POINT)\s+<(?<key>[^>]+)>\]",
                     RegexOptions.IgnoreCase)) {
            MelonLogger.Msg(
                $"[FCS] Debug grid token: kind={match.Groups["kind"].Value}, " +
                $"key={match.Groups["key"].Value}, path={path}");
            count++;
        }

        return count;
    }

    private static bool TryParseGridKm(string grid, out Vector2 kmPos)
    {
        kmPos = default;
        var match = Regex.Match(
            grid.Trim(),
            @"^(?<col>[A-Z])(?<row>\d{1,2})\s+(?<subx>\d):(?<suby>\d)$",
            RegexOptions.IgnoreCase);
        if (!match.Success) return false;

        var col = char.ToUpperInvariant(match.Groups["col"].Value[0]) - 'A';
        if (!int.TryParse(match.Groups["row"].Value, out var row)
            || !int.TryParse(match.Groups["subx"].Value, out var subX)
            || !int.TryParse(match.Groups["suby"].Value, out var subY)) {
            return false;
        }

        kmPos = new Vector2(col + (subX + 0.5f) / 10f, row - 1f + (subY + 0.5f) / 10f);
        return IsKmInsideTacticalMap(kmPos);
    }

    private static bool TryParseGridCellCenterKm(string cell, out Vector2 kmPos)
    {
        kmPos = default;
        var match = Regex.Match(cell.Trim(), @"^(?<col>[A-Z])(?<row>\d{1,2})$", RegexOptions.IgnoreCase);
        if (!match.Success) return false;

        var col = char.ToUpperInvariant(match.Groups["col"].Value[0]) - 'A';
        if (!int.TryParse(match.Groups["row"].Value, out var row)) return false;

        kmPos = new Vector2(col + 0.5f, row - 0.5f);
        return IsKmInsideTacticalMap(kmPos);
    }

    private static string StripRichTextTags(string text)
    {
        return Regex.Replace(text, "<.*?>", "");
    }

    private static string NormalizeGridText(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var chars = text.ToCharArray();
        for (var i = 0; i < chars.Length; i++) {
            if (chars[i] >= '\uFF01' && chars[i] <= '\uFF5E') {
                chars[i] = (char)(chars[i] - 0xFEE0);
            }
            else if (chars[i] == '\u3000' || chars[i] == '\u00A0') {
                chars[i] = ' ';
            }
        }
        return new string(chars);
    }

    private void TrySetPlayerTurretMarkerCandidate(Transform candidate, string reason, bool force = false)
    {
        if (playerTurretMarker != null || !force && !LooksLikePlayerTurretMarker(candidate)) {
            return;
        }

        playerTurretMarker = candidate;
    }

    private void FindPlayerTurretMarkerFallback(Transform map)
    {
        for (var i = 0; i < map.childCount; ++i) {
            var child = map.GetChild(i);
            if (!LooksLikePlayerTurretMarker(child)) {
                continue;
            }
            playerTurretMarker = child;
            return;
        }
    }

    private static bool LooksLikePlayerTurretMarker(Transform candidate)
    {
        var name = candidate.name.ToLowerInvariant();
        var path = GetTransformPath(candidate).ToLowerInvariant();
        if (name.Contains("grid")) return false;
        return name.Contains("player")
               || name.Contains("nest")
               || name.Contains("iron")
               || path.Contains("player")
               || path.Contains("nest")
               || path.Contains("iron");
    }

    private static bool IsExactPlayerTurretMarkerName(string name)
    {
        return name.Equals("Player Turret Piece", StringComparison.OrdinalIgnoreCase)
               || name.Equals("Iron Nest Turret Piece", StringComparison.OrdinalIgnoreCase)
               || name.Equals("Iron Next Turret Piece", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPlayerTurretMarkerText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var value = text.ToLowerInvariant();
        return value.Contains("铁巢")
               || value.Contains("玩家")
               || value.Contains("player")
               || value.Contains("turret")
               || value.Contains("nest")
               || value.Contains("iron");
    }

    private static bool IsPlayerTurretMarker(Transform marker, Transform turretTransform)
    {
        return marker == turretTransform
               || turretTransform.IsChildOf(marker)
               || marker.IsChildOf(turretTransform);
    }

    private static string GetTransformPath(Transform transform)
    {
        var path = transform.name;
        var parent = transform.parent;
        while (parent != null) {
            path = parent.name + "/" + path;
            parent = parent.parent;
        }
        return path;
    }

    public void SetMarkerWorldPos(int index, Vector3 worldPos, EntityLocation? location = null)
    {
        if (!artilleries.TryGetValue(index, out var marker)) return;
        if (mapSurface == null) return;
        var local = mapSurface.InverseTransformPoint(worldPos);
        local.z = marker.localPosition.z;
        marker.localPosition = local;
        markerLocations[index] = location;
        disabledMarkers.Remove(index);
    }

    public bool TryUpdateTaskFromWorldPos(ArtilleryTask task, Vector3 worldPos, EntityLocation? location = null)
    {
        if (turret == null || mapSurface == null) return false;

        var localPos = mapSurface.InverseTransformPoint(worldPos);
        var target = localPos - turret.localPosition;
        var dist = target.magnitude * 3.8164f;
        var angle = Vector3.SignedAngle(target, Vector3.up, Vector3.forward);
        if (angle < 0) angle += 360;

        task.angel = angle;
        task.distance = dist;
        task.position = localPos * 3.8164f + new Vector3(10.016f, 5.235f, 0f);
        if (location != null) {
            task.location = location;
            task.targetTypeDialValue = TargetTypeMapper.FromLocation(location);
        }
        return true;
    }

    public bool TryWorldToKmPos(Vector3 worldPos, out Vector2 kmPos)
    {
        kmPos = default;
        if (mapSurface == null) return false;
        var local = mapSurface.InverseTransformPoint(worldPos);
        var pos = local * 3.8164f + new Vector3(10.016f, 5.235f, 0f);
        kmPos = new Vector2(pos.x, pos.y);
        return true;
    }

    public bool TryMapKmToWorldPos(Vector3 referenceWorldPos, Vector2 kmPos, out Vector3 worldPos)
    {
        worldPos = default;
        if (mapSurface == null) return false;
        var local = mapSurface.InverseTransformPoint(referenceWorldPos);
        local = KmToMapLocal(kmPos, local.z);
        worldPos = mapSurface.TransformPoint(local);
        return true;
    }

    private static Vector3 KmToMapLocal(Vector2 kmPos, float z)
    {
        return new Vector3((kmPos.x - 10.016f) / 3.8164f, (kmPos.y - 5.235f) / 3.8164f, z);
    }

    public static bool IsKmInsideTacticalMap(Vector2 kmPos)
    {
        return kmPos.x >= MapMinX && kmPos.x <= MapMaxX
               && kmPos.y >= MapMinY && kmPos.y <= MapMaxY;
    }

    public void ResetMarker(int index)
    {
        if (!artilleries.TryGetValue(index, out var marker)) return;
        if (markerStandbyPositions.TryGetValue(index, out var standby)) {
            marker.localPosition = new Vector3(standby.x, standby.y, marker.localPosition.z);
        }
        markerLocations.Remove(index);
        disabledMarkers.Add(index);
    }

    public void ClearMarkerLocations()
    {
        markerLocations.Clear();
    }

    public void ClearMarkerLocation(int index)
    {
        markerLocations.Remove(index);
    }

    public Dictionary<int, EntityLocation> GetBoundMarkerLocations()
    {
        return markerLocations
            .Where(pair => pair.Key >= 1 && pair.Key <= 4 && pair.Value != null)
            .ToDictionary(pair => pair.Key, pair => pair.Value!);
    }

    public void RebindMarkerLocationsFromEntities(IEnumerable<UnitEntry> aliveUnits)
    {
        if (artilleries == null || artilleries.Count == 0) return;

        var candidates = aliveUnits
            .Where(unit => unit.IsAlive && unit.Location != null)
            .Select(unit => (Location: unit.Location!, km: TacticalRadar.GetEntityKmPos(unit), unit.DisplayName))
            .ToList();

        foreach (var index in artilleries.Keys.Where(id => id >= 1 && id <= 4).ToList()) {
            if (IsMarkerDisabledAtStandby(index) || !IsMarkerInsideTacticalMap(index)) {
                markerLocations.Remove(index);
                continue;
            }

            var markerKm = GetMarkerKmPos(index);
            if (markerKm == null) {
                markerLocations.Remove(index);
                continue;
            }

            var nearest = candidates
                .Select(item => (item.Location, item.DisplayName, distance: Vector2.Distance(markerKm.Value, item.km)))
                .Where(item => item.distance <= MarkerEntityBindingToleranceKm)
                .OrderBy(item => item.distance)
                .FirstOrDefault();

            if (nearest.Location == null) {
                markerLocations.Remove(index);
                continue;
            }

            var old = markerLocations.TryGetValue(index, out var previous) ? previous : null;
            markerLocations[index] = nearest.Location;
            if (old == null || old.Pointer != nearest.Location.Pointer) {
                MelonLogger.Msg(
                    $"[FCS] T{index} rebound to current entity {nearest.DisplayName}, " +
                    $"distance={nearest.distance:F2}km.");
            }
        }
    }

    public void SetMarkerByKmPos(int index, Vector2 kmPos)
    {
        if (!artilleries.TryGetValue(index, out var marker)) return;
        var local = KmToMapLocal(kmPos, marker.localPosition.z);
        marker.localPosition = local;
        markerLocations.Remove(index);
        disabledMarkers.Remove(index);
    }

    public void SetMarkerLocalPos(int index, Vector2 localPos)
    {
        if (!artilleries.TryGetValue(index, out var marker)) return;
        marker.localPosition = new Vector3(localPos.x, localPos.y, marker.localPosition.z);
        markerLocations.Remove(index);
        disabledMarkers.Remove(index);
    }

    public ArtilleryTask? GetMarkTarget(int index) {
        if (turret == null) {
            MelonLogger.Error("[FCS] GetMarkTarget: turret unbound");
            return null;
        }

        if (!artilleries.TryGetValue(index, out var marker)) {
            MelonLogger.Error($"[FCS] GetMarkTarget: index {index} not found, artillery count: {artilleries.Count}");
            return null;
        }

        if (IsMarkerDisabledAtStandby(index)) {
            MelonLogger.Warning($"[FCS] GetMarkTarget: T{index} is standby; ignored.");
            return null;
        }

        if (!IsMarkerInsideTacticalMap(index)) {
            MelonLogger.Warning($"[FCS] GetMarkTarget: T{index} is outside tactical map; ignored.");
            return null;
        }

        var target = marker.localPosition - turret.localPosition;
        var dist = target.magnitude * 3.8164f;
        var angle = Vector3.SignedAngle(target, Vector3.up, Vector3.forward);
        if (angle < 0) angle += 360;
        EntityLocation? boundLocation = null;
        var mismatchKm = 0f;
        if (markerLocations.TryGetValue(index, out var cachedLocation)
            && cachedLocation != null
            && IsMarkerStillOnBoundEntity(index, cachedLocation, out mismatchKm)) {
            boundLocation = cachedLocation;
        }
        else if (cachedLocation != null) {
            markerLocations.Remove(index);
            MelonLogger.Msg(
                $"[FCS] GetMarkTarget: T{index} moved away from cached entity by {mismatchKm:F2}km; " +
                "no current entity binding.");
        }

        var task = new ArtilleryTask {
            angel = angle,
            distance = dist,
            position = marker.localPosition * 3.8164f + new Vector3(10.016f, 5.235f, 0f),
            location = boundLocation,
            preserveAimPoint = boundLocation == null
        };
        task.targetTypeDialValue = TargetTypeMapper.FromLocation(task.location);
        return task;
    }

    private bool IsMarkerStillOnBoundEntity(int index, EntityLocation location, out float mismatchKm)
    {
        mismatchKm = float.MaxValue;
        var markerKm = GetMarkerKmPos(index);
        if (markerKm == null) return false;
        if (!TryGetLocationKmPos(location, out var entityKm)) return true;

        mismatchKm = Vector2.Distance(markerKm.Value, entityKm);
        return mismatchKm <= MarkerEntityBindingToleranceKm;
    }

    private Vector2? GetMarkerKmPos(int index)
    {
        if (!artilleries.TryGetValue(index, out var marker)) return null;
        var markerKm3 = marker.localPosition * 3.8164f + new Vector3(10.016f, 5.235f, 0f);
        return new Vector2(markerKm3.x, markerKm3.y);
    }

    private static bool TryGetLocationKmPos(EntityLocation location, out Vector2 kmPos)
    {
        kmPos = default;
        try {
            var locProp = location.GetType().GetProperty("LocalPosition", BindingFlags.Public | BindingFlags.Instance);
            if (locProp == null) return false;
            var val = locProp.GetValue(location);
            if (val is Vector2 v2) {
                kmPos = v2;
                return true;
            }
        }
        catch { }
        return false;
    }

    private bool IsMarkerDisabledAtStandby(int index)
    {
        if (!disabledMarkers.Contains(index)) {
            return false;
        }
        if (!artilleries.TryGetValue(index, out var marker)
            || !markerStandbyPositions.TryGetValue(index, out var standby)) {
            return true;
        }
        if (Vector2.Distance(
                new Vector2(marker.localPosition.x, marker.localPosition.y),
                new Vector2(standby.x, standby.y)) <= MarkerStandbyTolerance) {
            return true;
        }

        disabledMarkers.Remove(index);
        return false;
    }

    public bool IsMarkerInsideTacticalMap(int index) {
        if (!artilleries.TryGetValue(index, out var marker)) return false;
        var kmPos = marker.localPosition * 3.8164f + new Vector3(10.016f, 5.235f, 0f);
        return kmPos.x >= MapMinX && kmPos.x <= MapMaxX
               && kmPos.y >= MapMinY && kmPos.y <= MapMaxY;
    }

    public ArtilleryTask? GetNearestEdgeDumpTarget(int targetId, BulletType bulletType) {
        if (turret == null) {
            MelonLogger.Error("[FCS] GetNearestEdgeDumpTarget: turret unbound");
            return null;
        }

        var pos = turret.localPosition * 3.8164f + new Vector3(10.016f, 5.235f, 0f);
        var x = Mathf.Clamp(pos.x, MapMinX, MapMaxX);
        var y = Mathf.Clamp(pos.y, MapMinY, MapMaxY);
        var left = x;
        var right = MapMaxX - x;
        var bottom = y;
        var top = MapMaxY - y;
        var dump = new Vector2(x, 0f);
        var min = bottom;

        if (top < min) {
            min = top;
            dump = new Vector2(x, MapMaxY + DumpOutsideDistance);
        }
        if (left < min) {
            min = left;
            dump = new Vector2(MapMinX - DumpOutsideDistance, y);
        }
        if (right < min) {
            dump = new Vector2(MapMaxX + DumpOutsideDistance, y);
        }
        if (bottom <= min) {
            dump = new Vector2(x, MapMinY - DumpOutsideDistance);
        }

        var localTarget = new Vector3((dump.x - 10.016f) / 3.8164f, (dump.y - 5.235f) / 3.8164f, turret.localPosition.z);
        var target = localTarget - turret.localPosition;
        var dist = target.magnitude * 3.8164f;
        var angle = Vector3.SignedAngle(target, Vector3.up, Vector3.forward);
        if (angle < 0) angle += 360;
        return new ArtilleryTask {
            targetId = targetId,
            angel = angle,
            distance = dist,
            position = new Vector3(dump.x, dump.y, 0f),
            bulletType = bulletType
        };
    }

    public List<EntityLocation> GetAllFireMissionEntities() {
        List<EntityLocation> res = new();
        if (fireMissionRoot == null) {
            return res;
        }

        for (var i = 0; i < fireMissionRoot.childCount; ++i) {
            var m = fireMissionRoot.GetChild(i).GetComponent<EntityLocation>();
            if (m != null) res.Add(m);
        }
        return res;
    }
    
}
