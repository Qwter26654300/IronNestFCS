using System.Collections;
using System.Reflection;
using Il2Cpp;
using Il2CppTMPro;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic.FCS;

public class BallisticCalculator {
    private const int ShellDialMin = 0;
    private const int ShellDialMax = 21;
    private const int TargetDialMin = 0;
    private const int TargetDialMax = 21;
    private const float TargetDialSettleSeconds = 1.25f;

    private DialInteractable? distanceDial;
    private DialInteractable? chargeDial;
    private DialInteractable? directionDial;
    private DialInteractable? shellDial;
    private DialInteractable? targetDial;
    private LookAtTarget? calculateButton;
    private OdometerDisplay? elevationDisplay;
    private Transform? controlsRoot;
    private Transform? shellDialTransform;
    private Transform? cardPrinterRoot;
    private BulletType? lastRequestedShellType;
    private readonly Dictionary<BulletType, float> calibratedShellDialValues = new();
    private bool shellCalibrationDone;
    private bool shellDialDeepLogged;
    private bool targetDialMissingLogged;
    private int debugShellDialValue = -1;

    public bool TryBind() {
        var controls = GameObject.Find("Balistic Calculator Controls");
        if (controls == null) return Missing("Balistic Calculator Controls");
        controlsRoot = controls.transform;

        var rangeParent = controls.transform.FindChild(".Range Dial Parent");
        if (rangeParent == null) return Missing(".Range Dial Parent");
        distanceDial = rangeParent.GetComponentInChildren<DialInteractable>();

        var chargeParent = controls.transform.FindChild(".Charge Dial Parent");
        if (chargeParent == null) return Missing(".Charge Dial Parent");
        chargeDial = chargeParent.GetComponentInChildren<DialInteractable>();

        directionDial = GameObject.Find(".Gross Range Dial")?.GetComponentInChildren<DialInteractable>();
        calculateButton = GameObject.Find("Calculate Universal Button")?.GetComponent<LookAtTarget>();
        elevationDisplay = GameObject.Find("Odomiter Output Elivation")?.GetComponent<OdometerDisplay>();
        shellDial = GameObject.Find(".Shell Dial")?.GetComponent<DialInteractable>();
        targetDial = FindTargetDial();
        shellDialTransform = shellDial?.transform;
        cardPrinterRoot = GameObject.Find("Fire Mission Card Printer")?.transform;

        return distanceDial != null
               && chargeDial != null
               && directionDial != null
               && calculateButton != null
               && elevationDisplay != null
               && shellDial != null;
    }

    private DialInteractable? FindTargetDial()
    {
        var direct = GameObject.Find(".Target Dial")?.GetComponent<DialInteractable>()
                     ?? GameObject.Find(".Target Dial")?.GetComponentInChildren<DialInteractable>()
                     ?? GameObject.Find("Target Dial")?.GetComponent<DialInteractable>()
                     ?? GameObject.Find("Target Dial")?.GetComponentInChildren<DialInteractable>();
        if (direct != null) return direct;

        var roots = controlsRoot != null
            ? controlsRoot.GetComponentsInChildren<DialInteractable>(true)
            : UnityEngine.Object.FindObjectsOfType<DialInteractable>();
        foreach (var dial in roots)
        {
            if (dial == null) continue;
            var path = GetTransformPath(dial.transform).ToLowerInvariant();
            if ((path.Contains("target") || path.Contains("目标"))
                && !path.Contains("range")
                && !path.Contains("charge")
                && !path.Contains("shell"))
            {
                return dial;
            }
        }

        return null;
    }

    private static bool Missing(string name) {
        MelonLogger.Warning($"[FCS] Can't find {name}，scene may not be loaded yet.");
        return false;
    }
    
    public IEnumerator SetDistance(float distance) {
        yield return FcsSceneInteractor.WaitUntilInteractive();
        distanceDial?.SetDialValue(distance);
        yield return new WaitForSeconds(0.5f);
    }
    
    public IEnumerator SetCharge(float charge) {
        yield return FcsSceneInteractor.WaitUntilInteractive();
        chargeDial?.SetDialValue(charge);
        yield return new WaitForSeconds(0.5f);
    }

    public IEnumerator SetDirection(float angle) {
        yield return FcsSceneInteractor.WaitUntilInteractive();
        directionDial?.SetDialValue(angle);
        yield return new WaitForSeconds(0.5f);
    }

    public IEnumerator SetShellType(BulletType type) {
        yield return FcsSceneInteractor.WaitUntilInteractive();
        lastRequestedShellType = type;
        shellDial?.SetDialValue(GetShellDialValue(type));
        yield return new WaitForSeconds(0.5f);
        var displayed = ReadCurrentShellCode();
        var matches = ShellTextMatches(type, displayed);
        if (!matches)
        {
            MelonLogger.Warning($"[FCS] Ballistic shell mismatch: expected={type}, mappedValue={GetShellDialValue(type):F0}, displayed={displayed ?? "unknown"}.");
        }
    }

    public IEnumerator SetTargetType(int value) {
        yield return FcsSceneInteractor.WaitUntilInteractive();
        if (targetDial == null)
        {
            if (!targetDialMissingLogged)
            {
                targetDialMissingLogged = true;
                MelonLogger.Warning("[FCS] Ballistic target dial not found; target type remains unchanged.");
            }
            yield break;
        }
        var dialValue = Mathf.Clamp(value, TargetDialMin, TargetDialMax);
        targetDial.SetDialValue(dialValue);
        yield return new WaitForSeconds(TargetDialSettleSeconds);
        targetDial.SetDialValue(dialValue);
        yield return new WaitForSeconds(0.25f);
    }

    public void DebugStepShellDial(int delta)
    {
        if (shellDial == null)
        {
            MelonLogger.Warning("[FCS] 手动弹种遍历失败：弹种旋钮未绑定");
            return;
        }

        if (debugShellDialValue < ShellDialMin || debugShellDialValue > ShellDialMax)
        {
            debugShellDialValue = ShellDialMin;
        }
        else
        {
            debugShellDialValue += delta;
            if (debugShellDialValue > ShellDialMax) debugShellDialValue = ShellDialMin;
            if (debugShellDialValue < ShellDialMin) debugShellDialValue = ShellDialMax;
        }

        shellDial.SetDialValue(debugShellDialValue);
        MelonLogger.Warning($"[FCS] 手动弹种遍历：诸元弹种旋钮 = {debugShellDialValue}，请观察卡片方框显示");
    }

    public IEnumerator Calculate() {
        yield return FcsSceneInteractor.WaitUntilInteractive();
        calculateButton?.OnClickDown();
        yield return new WaitForSeconds(0.5f);
    }
    
    public float GetElevation() {
        return elevationDisplay?.currentNumber ?? 0;
    }

    public static int MinimumCharge(float distance) {
        return distance switch {
            < 5.0f => 1,
            < 10.0f => 2,
            < 15.0f => 3,
            < 20.0f => 4,
            < 25.0f => 5,
            _ => 6
        };
    }

    // 弹种旋钮在不同版本里可能变动；这些深度日志/校准函数平时不主动调用，保留给后续排查映射用。
    private void LogShellState(string stage, BulletType? expected)
    {
        var dial = DescribeDial(shellDial);
        var nearbyTexts = DescribeTexts(shellDialTransform, includeParent: true);
        var calculatorTexts = DescribeTexts(controlsRoot, includeParent: false);
        var cardTexts = DescribeShellLikeTexts(cardPrinterRoot);
        var visibleShellTexts = DescribeVisibleShellTexts();
        if (!shellDialDeepLogged)
        {
            shellDialDeepLogged = true;
            MelonLogger.Msg($"[FCS] Ballistic shell dial deep: {DescribeObjectDeep(shellDial)}");
        }
        MelonLogger.Msg(
            $"[FCS] Ballistic shell {stage}: expected={expected?.ToString() ?? "unknown"}, " +
            $"mappedValue={(expected.HasValue ? GetShellDialValue(expected.Value).ToString("F0") : "unknown")}, " +
            $"dial={dial}, visibleShellTexts=[{visibleShellTexts}], cardTexts=[{cardTexts}], " +
            $"shellTexts=[{nearbyTexts}], calculatorTexts=[{calculatorTexts}]");
    }

    private float GetShellDialValue(BulletType type)
    {
        return calibratedShellDialValues.TryGetValue(type, out var value) ? value : ShellDialValue(type);
    }

    private IEnumerator CalibrateShellDial()
    {
        if (shellCalibrationDone || shellDial == null)
        {
            yield break;
        }

        shellCalibrationDone = true;
        var found = new Dictionary<BulletType, float>();
        MelonLogger.Warning("[FCS] Ballistic shell calibration started; scanning dial 0..21.");
        for (var value = ShellDialMin; value <= ShellDialMax; value++)
        {
            yield return FcsSceneInteractor.WaitUntilInteractive();
            shellDial.SetDialValue(value);
            yield return new WaitForSeconds(0.18f);

            var text = ReadCurrentShellCode();
            MelonLogger.Msg($"[FCS] Ballistic shell map: value={value}, display={text ?? "unknown"}");
            if (text != null && TryParseShellCode(text, out var type) && !found.ContainsKey(type))
            {
                found[type] = value;
            }
        }

        foreach (var pair in found)
        {
            calibratedShellDialValues[pair.Key] = pair.Value;
        }

        MelonLogger.Warning(
            "[FCS] Ballistic shell calibration finished: " +
            string.Join(", ", calibratedShellDialValues.Select(pair => $"{pair.Key}={pair.Value:F0}")));
    }

    private string? ReadCurrentShellCode()
    {
        var candidate = GetVisibleShellTextCandidates().FirstOrDefault();
        return candidate?.Text;
    }

    private static bool ShellTextMatches(BulletType expected, string? text)
    {
        return text != null && TryParseShellCode(text, out var actual) && actual == expected;
    }

    private static bool TryParseShellCode(string text, out BulletType type)
    {
        var upper = NormalizeShellText(text);
        type = default;
        if (upper == "EMPT") { type = BulletType.EMPT; return true; }
        if (upper == "AP") { type = BulletType.AP; return true; }
        if (upper == "APHE") { type = BulletType.APHE; return true; }
        if (upper == "ATMC") { type = BulletType.ATMC; return true; }
        if (upper == "CLMN") { type = BulletType.CLMN; return true; }
        if (upper == "CYAN") { type = BulletType.CYAN; return true; }
        if (upper == "DRIL") { type = BulletType.DRIL; return true; }
        if (upper == "EQKE") { type = BulletType.EQKE; return true; }
        if (upper == "FLCH") { type = BulletType.FLCH; return true; }
        if (upper == "HE") { type = BulletType.HE; return true; }
        if (upper == "HCHE") { type = BulletType.HCHE; return true; }
        if (upper == "INCN") { type = BulletType.INCN; return true; }
        if (upper == "LE") { type = BulletType.LE; return true; }
        if (upper == "PLCM") { type = BulletType.PLCM; return true; }
        if (upper == "PHGN") { type = BulletType.PHGN; return true; }
        if (upper == "PRPG") { type = BulletType.PRPG; return true; }
        if (upper == "STAR") { type = BulletType.STAR; return true; }
        if (upper == "SMK" || upper == "SMOKE") { type = BulletType.SMK; return true; }
        if (upper == "TEAR") { type = BulletType.TEAR; return true; }
        if (upper == "THRM") { type = BulletType.THRM; return true; }
        if (upper == "WP") { type = BulletType.WP; return true; }
        return false;
    }

    private static string NormalizeShellText(string text)
    {
        return text.Trim()
            .Replace("\r", "")
            .Replace("\n", "")
            .Replace(" ", "")
            .ToUpperInvariant();
    }

    private static float ShellDialValue(BulletType type)
    {
        // Demo 版的弹种旋钮是 1~5；正式版扩展到了 0~21。
        // AP=1 已确认仍正确；其余弹种先保留旧值，通过日志里的 cardTexts 校准正式版映射。
        return type switch
        {
            BulletType.AP => 1f,
            BulletType.HCHE => 9f,
            BulletType.HE => 10f,
            BulletType.SMK => 16f,
            BulletType.STAR => 17f,
            _ => (float)type
        };
    }

    private static string DescribeTexts(Transform? root, bool includeParent)
    {
        if (root == null) return "";
        var scanRoot = includeParent ? root.parent ?? root : root;
        var values = new List<string>();
        try
        {
            foreach (var tmp in scanRoot.GetComponentsInChildren<TextMeshPro>(true))
            {
                if (tmp == null || string.IsNullOrWhiteSpace(tmp.text)) continue;
                var path = GetTransformPath(tmp.transform);
                values.Add($"{path}='{tmp.text.Trim()}'");
                if (values.Count >= 12) break;
            }
        }
        catch (Exception ex)
        {
            return $"read text failed: {ex.Message}";
        }
        return string.Join("; ", values);
    }

    private static string DescribeShellLikeTexts(Transform? root)
    {
        if (root == null) return "";
        var values = new List<string>();
        try
        {
            foreach (var tmp in root.GetComponentsInChildren<TextMeshPro>(true))
            {
                if (tmp == null || string.IsNullOrWhiteSpace(tmp.text)) continue;
                var text = NormalizeShellText(tmp.text);
                if (!LooksLikeShellText(text)) continue;
                values.Add($"{GetTransformPath(tmp.transform)}='{text}'");
                if (values.Count >= 20) break;
            }
        }
        catch (Exception ex)
        {
            return $"read card text failed: {ex.Message}";
        }
        return string.Join("; ", values);
    }

    private static string DescribeVisibleShellTexts()
    {
        var values = new List<string>();
        foreach (var candidate in GetVisibleShellTextCandidates().Take(16))
        {
            values.Add(
                $"{candidate.Text}@({candidate.World.x:F2},{candidate.World.y:F2},{candidate.World.z:F2}) " +
                $"visible={candidate.Visible} active={candidate.Active} dist={candidate.CameraDistance:F2} path={candidate.Path}");
        }
        return string.Join("; ", values);
    }

    private static List<ShellTextCandidate> GetVisibleShellTextCandidates()
    {
        var candidates = new List<ShellTextCandidate>();
        try
        {
            var camera = Camera.main;
            foreach (var tmp in UnityEngine.Object.FindObjectsOfType<TextMeshPro>())
            {
                if (tmp == null || string.IsNullOrWhiteSpace(tmp.text)) continue;
                var text = NormalizeShellText(tmp.text);
                if (!IsExactShellCode(text)) continue;

                var renderer = tmp.GetComponent<Renderer>();
                var visible = renderer != null && renderer.isVisible;
                var active = tmp.gameObject != null && tmp.gameObject.activeInHierarchy;
                var world = tmp.transform.position;
                var distance = camera != null ? Vector3.Distance(camera.transform.position, world) : 0f;
                var path = GetTransformPath(tmp.transform);
                var score = 0;
                if (visible) score += 100;
                if (active) score += 50;
                if (path.Contains("FireMissionCard3D")) score += 20;
                if (path.Contains("Fire Mission Card Printer")) score += 10;

                candidates.Add(new ShellTextCandidate
                {
                    Text = text,
                    Path = path,
                    Active = active,
                    Visible = visible,
                    World = world,
                    CameraDistance = distance,
                    Score = score
                });
            }
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[FCS] GetVisibleShellTextCandidates failed: {ex.Message}");
        }

        return candidates
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.CameraDistance)
            .ToList();
    }

    private static bool LooksLikeShellText(string text)
    {
        var upper = text.ToUpperInvariant();
        return upper.Contains("AP")
               || upper.Contains("APHE")
               || upper.Contains("HE")
               || upper.Contains("HCHE")
               || upper.Contains("ATMC")
               || upper.Contains("CLMN")
               || upper.Contains("CYAN")
               || upper.Contains("DRIL")
               || upper.Contains("EQKE")
               || upper.Contains("FLCH")
               || upper.Contains("INCN")
               || upper.Contains("PLCM")
               || upper.Contains("PHGN")
               || upper.Contains("PRPG")
               || upper.Contains("STAR")
               || upper.Contains("SMK")
               || upper.Contains("SMOKE")
               || upper.Contains("TEAR")
               || upper.Contains("THRM")
               || upper.Contains("WP")
               || upper.Contains("SHELL");
    }

    private static bool IsExactShellCode(string text)
    {
        var upper = NormalizeShellText(text);
        return upper is "EMPT" or "AP" or "APHE" or "ATMC" or "CLMN" or "CYAN" or "DRIL"
            or "EQKE" or "FLCH" or "HCHE" or "HE" or "INCN" or "LE" or "PLCM"
            or "PHGN" or "PRPG" or "SMK" or "SMOKE" or "STAR" or "TEAR" or "THRM" or "WP";
    }

    private sealed class ShellTextCandidate
    {
        public string Text = "";
        public string Path = "";
        public bool Active;
        public bool Visible;
        public Vector3 World;
        public float CameraDistance;
        public int Score;
    }

    private static string DescribeDial(DialInteractable? dial)
    {
        if (dial == null) return "null";
        var parts = new List<string>();
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
        try
        {
            var type = dial.GetType();
            foreach (var prop in type.GetProperties(flags))
            {
                if (prop.GetIndexParameters().Length > 0) continue;
                if (!IsSimpleReadable(prop.PropertyType)) continue;
                try
                {
                    parts.Add($"{prop.Name}={prop.GetValue(dial)}");
                }
                catch { }
                if (parts.Count >= 10) break;
            }
            foreach (var field in type.GetFields(flags))
            {
                if (!IsSimpleReadable(field.FieldType)) continue;
                try
                {
                    parts.Add($"{field.Name}={field.GetValue(dial)}");
                }
                catch { }
                if (parts.Count >= 16) break;
            }
        }
        catch (Exception ex)
        {
            parts.Add($"read dial failed: {ex.Message}");
        }
        return string.Join(", ", parts);
    }

    private static string DescribeObjectDeep(object? obj, int maxParts = 80)
    {
        if (obj == null) return "null";
        var parts = new List<string>();
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        try
        {
            var type = obj.GetType();
            parts.Add($"type={type.FullName}");
            foreach (var prop in type.GetProperties(flags))
            {
                if (prop.GetIndexParameters().Length > 0) continue;
                if (!IsInterestingMember(prop.Name) && !IsSimpleReadable(prop.PropertyType)) continue;
                try
                {
                    var value = prop.GetValue(obj);
                    parts.Add($"{prop.Name}={FormatDeepValue(value)}");
                }
                catch { }
                if (parts.Count >= maxParts / 2) break;
            }
            foreach (var field in type.GetFields(flags))
            {
                if (!IsInterestingMember(field.Name) && !IsSimpleReadable(field.FieldType)) continue;
                try
                {
                    var value = field.GetValue(obj);
                    parts.Add($"{field.Name}={FormatDeepValue(value)}");
                }
                catch { }
                if (parts.Count >= maxParts) break;
            }
        }
        catch (Exception ex)
        {
            parts.Add($"read deep failed: {ex.Message}");
        }
        return string.Join(", ", parts);
    }

    private static bool IsInterestingMember(string name)
    {
        var lower = name.ToLowerInvariant();
        return lower.Contains("value")
               || lower.Contains("output")
               || lower.Contains("index")
               || lower.Contains("option")
               || lower.Contains("texture")
               || lower.Contains("sprite")
               || lower.Contains("shell")
               || lower.Contains("dial")
               || lower.Contains("mode");
    }

    private static string FormatDeepValue(object? value)
    {
        if (value == null) return "null";
        if (value is string s) return s;
        if (value is bool or int or float or double or Enum) return value.ToString() ?? "";
        return value.GetType().FullName ?? value.ToString() ?? "";
    }

    private static bool IsSimpleReadable(Type type)
    {
        return type == typeof(string)
               || type == typeof(bool)
               || type == typeof(int)
               || type == typeof(float)
               || type == typeof(double)
               || type.IsEnum;
    }

    private static string GetTransformPath(Transform transform)
    {
        var path = transform.name;
        var parent = transform.parent;
        while (parent != null)
        {
            path = parent.name + "/" + path;
            parent = parent.parent;
        }
        return path;
    }
    
}
