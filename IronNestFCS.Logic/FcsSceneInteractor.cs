using System.Collections;
using Il2Cpp;
using Il2CppTMPro;
using IronNestFCS.Logic.FCS;
using MelonLoader;
using UnityEngine;
using Object = UnityEngine.Object;

namespace IronNestFCS.Logic;

public class FcsSceneInteractor {
    private FSC fcs;

    private List<GameObject> destroyOnShutdown = new();
    private readonly ClickRaycaster clicks = new();

    // 当前选中的弹种（两管炮共享，由调度器决定任务派到哪管炮）。
    public BulletType selectedBulletType = BulletType.AP;

    private List<GameObject> bulletTypeBtns = new();
    private GameObject? otherSelectButton;
    private GameObject? otherPrevButton;
    private GameObject? otherNextButton;
    private TextMeshPro? otherBulletText;
    private readonly BulletType[] otherBulletTypes =
    {
        BulletType.CLMN,
        BulletType.CYAN,
        BulletType.DRIL,
        BulletType.EQKE,
        BulletType.FLCH,
        BulletType.INCN,
        BulletType.LE,
        BulletType.PLCM,
        BulletType.PHGN,
        BulletType.PRPG,
        BulletType.TEAR,
        BulletType.THRM,
        BulletType.WP,
    };
    private int otherBulletIndex;

    // 每个地图目标对应一个按钮：targetId -> 按钮。点击=用当前弹种为该目标入队一个任务。
    private readonly Dictionary<int, GameObject> targetButtons = new();

    public bool AutoFire = true;
    public bool maxCharge = false;

    public FcsSceneInteractor(FSC fcs) {
        this.fcs = fcs;
    }

    public void Initialize() {
        InitializeBulletTypeButtons();
        InitializeTargetButtons();
    }

    private void InitializeBulletTypeButtons() {
        const float z = -18.4181f;
        float x = 0.3488f;
        var fixedBullets = new[]
        {
            BulletType.AP,
            BulletType.HCHE,
            BulletType.HE,
            BulletType.STAR,
            BulletType.SMK,
            BulletType.APHE,
            BulletType.ATMC,
        };

        foreach (var type in fixedBullets) {
            AddBulletButton(type, ref x, z);
        }

        x -= 0.025f;
        InitializeOtherBulletButtons(ref x, z);

        GameObject autoFireButton = null;
        autoFireButton = AddButton(() => {
            AutoFire = !AutoFire;
            SetColor(autoFireButton, AutoFire ? Color.red : Color.white);
        }, AutoFire ? Color.red : Color.white);
        autoFireButton.transform.position = new Vector3(x, -0.6916f, z);
        autoFireButton.transform.localScale = Vector3.one * 0.02f;
        var autoFiretext = AddText("Auto Fire", 14f);
        autoFiretext.transform.SetParent(autoFireButton.transform, false);
        autoFiretext.transform.localPosition = new Vector3(-1.9f, 0, -10.6f);
        autoFiretext.transform.localScale = Vector3.one * 1.0f;
        
        x -= 0.05f;
        
        GameObject maxChargeButton = null;
        maxChargeButton = AddButton(() => {
            maxCharge = !maxCharge;
            SetColor(maxChargeButton, maxCharge ? Color.red : Color.white);
        }, maxCharge ? Color.red : Color.white);
        maxChargeButton.transform.position = new Vector3(x, -0.6916f, z);
        maxChargeButton.transform.localScale = Vector3.one * 0.02f;
        var maxChargeText = AddText("Max Charge", 14f);
        maxChargeText.transform.SetParent(maxChargeButton.transform, false);
        maxChargeText.transform.localPosition = new Vector3(-1.9f, 0, -10.6f);
        maxChargeText.transform.localScale = Vector3.one * 1.0f;
    }

    private void AddBulletButton(BulletType type, ref float x, float z) {
        GameObject button = null;
        button = AddButton(() => SelectBulletType(type, button), type == selectedBulletType ? Color.green : Color.white);
        button.transform.position = new Vector3(x, -0.6916f, z);
        button.transform.localScale = Vector3.one * 0.02f;
        bulletTypeBtns.Add(button);
        var text = AddText(type.ToString(), 14f);
        text.transform.SetParent(button.transform, false);
        text.transform.localPosition = new Vector3(-1.9f, 0, -10.6f);
        text.transform.localScale = Vector3.one * 1.0f;
        x -= 0.05f;
    }

    private void InitializeOtherBulletButtons(ref float x, float z) {
        const float rowY = -0.6916f;
        var selectZ = z;
        var prevZ = selectZ - 0.12f;
        var nextZ = selectZ - 0.18f;

        otherSelectButton = AddButton(() => SelectBulletType(otherBulletTypes[otherBulletIndex], otherSelectButton), Color.white);
        otherSelectButton.transform.position = new Vector3(x, rowY, selectZ);
        otherSelectButton.transform.localScale = Vector3.one * 0.02f;
        bulletTypeBtns.Add(otherSelectButton);
        var otherTextObj = AddText(otherBulletTypes[otherBulletIndex].ToString(), 14f);
        otherTextObj.transform.SetParent(otherSelectButton.transform, false);
        otherTextObj.transform.localPosition = new Vector3(-1.9f, 0, -10.6f);
        otherTextObj.transform.localScale = Vector3.one * 1.0f;
        otherBulletText = otherTextObj.GetComponent<TextMeshPro>();

        otherPrevButton = AddButton(() => CycleOtherBullet(-1), Color.white);
        otherPrevButton.transform.position = new Vector3(x, rowY, prevZ);
        otherPrevButton.transform.localScale = Vector3.one * 0.02f;
        bulletTypeBtns.Add(otherPrevButton);
        var prevText = AddText("<", 14f);
        prevText.transform.SetParent(otherPrevButton.transform, false);
        prevText.transform.localPosition = new Vector3(-1.9f, 0, -10.6f);
        prevText.transform.localScale = Vector3.one * 1.0f;

        otherNextButton = AddButton(() => CycleOtherBullet(1), Color.white);
        otherNextButton.transform.position = new Vector3(x, rowY, nextZ);
        otherNextButton.transform.localScale = Vector3.one * 0.02f;
        bulletTypeBtns.Add(otherNextButton);
        var nextText = AddText(">", 14f);
        nextText.transform.SetParent(otherNextButton.transform, false);
        nextText.transform.localPosition = new Vector3(-1.9f, 0, -10.6f);
        nextText.transform.localScale = Vector3.one * 1.0f;

        x -= 0.05f;
    }

    private void SelectBulletType(BulletType type, GameObject? selectedButton = null) {
        selectedBulletType = type;
        foreach (var btn in bulletTypeBtns) {
            SetColor(btn, btn == selectedButton ? Color.green : Color.white);
        }
    }

    private void CycleOtherBullet(int delta) {
        otherBulletIndex += delta;
        if (otherBulletIndex < 0) otherBulletIndex = otherBulletTypes.Length - 1;
        if (otherBulletIndex >= otherBulletTypes.Length) otherBulletIndex = 0;
        var type = otherBulletTypes[otherBulletIndex];
        if (otherBulletText != null) otherBulletText.text = type.ToString();
        selectedBulletType = type;
        foreach (var btn in bulletTypeBtns) {
            SetColor(btn, btn == otherSelectButton ? Color.green : Color.white);
        }
        MelonLogger.Msg($"[FCS] 弹种选择: {type}");
    }

    /// <summary>
    /// 4 个目标按钮（对应地图上 1~4 号炮兵标记）。点击即用当前选中弹种为该目标入队一个任务，
    /// 调度器自动派给空闲炮管。用 activeTargets 防止同一目标重复入队。
    /// </summary>
    private void InitializeTargetButtons() {
        const float z = -18.6381f;
        var x = 0.3488f;
        for (var i = 1; i <= 4; i++) {
            var targetId = i;
            GameObject button = null;
            button = AddButton(() => {
                fcs.RefreshPlayerTurretMarkerBeforeTargeting(!fcs.ManualMarkerPriorityMode);
                var task = fcs.MapTable.GetMarkTarget(targetId);
                if (task == null) {
                    return; // 地图上没有这个编号的目标
                }
                task.targetId = targetId;
                task.bulletType = selectedBulletType;
                task.manualPriority = fcs.ManualMarkerPriorityMode;
                task.userRequested = true;
                fcs.EnqueueTask(task);
                SetColor(button, Color.gray);
                button.GetComponent<Collider>().enabled = false;
                MelonCoroutines.Start(InvokeDelay(() => {
                    SetColor(button, Color.red);
                    button.GetComponent<Collider>().enabled = true;
                }, 1f));
            }, Color.red);
            button.transform.position = new Vector3(x, -0.6916f, z);
            button.transform.localScale = Vector3.one * 0.02f;
            targetButtons[targetId] = button;
            var text = AddText("T" + targetId, 14f);
            text.transform.SetParent(button.transform, false);
            text.transform.localPosition = new Vector3(-1.9f, 0, -10.6f);
            text.transform.localScale = Vector3.one * 1.0f;
            x -= 0.05f;
        }
    }

    /// <summary>任务完成回调</summary>
    public void TaskFinished(ArtilleryTask task) {
    }

    /// <summary>键盘快捷键触发射击目标（对应小键盘 1-4），等价于点击 T1-T4 按钮。</summary>
    public void FireTarget(int targetId) {
        if (!targetButtons.TryGetValue(targetId, out var button)) return;
        if (!button.GetComponent<Collider>().enabled) return;
        fcs.RefreshPlayerTurretMarkerBeforeTargeting(!fcs.ManualMarkerPriorityMode);
        var task = fcs.MapTable.GetMarkTarget(targetId);
        if (task == null) return;
        task.targetId = targetId;
        task.bulletType = selectedBulletType;
        task.manualPriority = fcs.ManualMarkerPriorityMode;
        task.userRequested = true;
        fcs.EnqueueTask(task);
        SetColor(button, Color.gray);
        button.GetComponent<Collider>().enabled = false;
        MelonCoroutines.Start(InvokeDelay(() => {
            SetColor(button, Color.red);
            button.GetComponent<Collider>().enabled = true;
        }, 1f));
    }

    public void FireAtWorldPos(
        int id,
        Vector3 worldPos,
        EntityLocation? location = null,
        bool preserveAimPoint = false,
        bool movingAreaAim = false,
        BulletType? bulletTypeOverride = null,
        string? areaClusterKey = null,
        IReadOnlyCollection<IntPtr>? areaClusterMembers = null)
    {
        fcs.RefreshPlayerTurretMarkerBeforeTargeting();
        var turret = fcs.MapTable.turret;
        if (turret == null) return;
        var mapSurface = GameObject.Find("Draggable Surface")?.transform;
        if (mapSurface == null) return;
        var localPos = mapSurface.InverseTransformPoint(worldPos);
        var target = localPos - turret.localPosition;
        var dist = target.magnitude * 3.8164f;
        var angle = Vector3.SignedAngle(target, Vector3.up, Vector3.forward);
        if (angle < 0) angle += 360;
        var task = new ArtilleryTask
        {
            targetId = id,
            angel = angle,
            distance = dist,
            position = localPos * 3.8164f + new Vector3(10.016f, 5.235f, 0f),
            location = location,
            targetTypeDialValue = TargetTypeMapper.FromLocation(location),
            preserveAimPoint = preserveAimPoint,
            movingAreaAim = movingAreaAim,
            areaClusterKey = areaClusterKey ?? "",
            areaClusterMembers = areaClusterMembers?.Distinct().ToList() ?? new List<IntPtr>(),
            bulletType = bulletTypeOverride ?? selectedBulletType
        };
        fcs.EnqueueTask(task);
    }

    public void FireAtWorldPosFront(int id, Vector3 worldPos, EntityLocation? location = null)
    {
        fcs.RefreshPlayerTurretMarkerBeforeTargeting();
        var turret = fcs.MapTable.turret;
        if (turret == null) return;
        var mapSurface = GameObject.Find("Draggable Surface")?.transform;
        if (mapSurface == null) return;
        var localPos = mapSurface.InverseTransformPoint(worldPos);
        var target = localPos - turret.localPosition;
        var dist = target.magnitude * 3.8164f;
        var angle = Vector3.SignedAngle(target, Vector3.up, Vector3.forward);
        if (angle < 0) angle += 360;
        var task = new ArtilleryTask
        {
            targetId = id,
            angel = angle,
            distance = dist,
            position = localPos * 3.8164f + new Vector3(10.016f, 5.235f, 0f),
            location = location,
            targetTypeDialValue = TargetTypeMapper.FromLocation(location),
            bulletType = selectedBulletType
        };
        fcs.EnqueueTaskFront(task);
    }
    
    public void Update() {
        clicks.Update();
    }

    public void ShutDown() {
        clicks.Clear();
        foreach (var obj in destroyOnShutdown) {
            Object.Destroy(obj);
        }
    }
    
    public GameObject AddButton(Action onClick) {
        return AddButton(onClick, Color.white);
    }

    public GameObject AddButton(Action onClick, Color color) {
        // 用自带 BoxCollider 的 cube 当可点击目标，靠 ClickRaycaster 自己 raycast 检测点击，
        // 不依赖游戏的 LookAtTarget，也不注册新 IL2CPP 类型（保持可热重载）。
        var button = GameObject.CreatePrimitive(PrimitiveType.Cube);
        destroyOnShutdown.Add(button);
        var collider = button.GetComponent<Collider>();
        clicks.Register(collider, onClick);
        SetColor(button, color);
        return button;
    }

    /// <summary>
    /// 给对象的 Renderer 换上当前渲染管线（URP）的材质并设颜色。
    /// CreatePrimitive 默认用内置管线的 Standard 材质，在 URP 下 shader 无效会渲染成紫色；
    /// 这里用 URP 的 Unlit shader 重建材质（不受光照影响，纯色所见即所得）。
    /// </summary>
    public static void SetColor(GameObject go, Color color) {
        var renderer = go.GetComponent<Renderer>();
        if (renderer == null)
            return;

        var shader = Shader.Find("Universal Render Pipeline/Unlit")
                     ?? Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) {
            MelonLogger.Warning("[FCS] Can't find URP shader. Use default material color instead.");
            // 退而求其次：直接改现有材质颜色
            if (renderer.material != null)
                renderer.material.color = color;
            return;
        }

        var mat = new Material(shader);
        // URP Unlit 用 _BaseColor 控制颜色；同时设 color 兼容。
        mat.color = color;
        if (mat.HasProperty("_BaseColor"))
            mat.SetColor("_BaseColor", color);
        renderer.material = mat;
    }

    /// <summary>
    /// 在 3D 世界里创建一段文本（World Space 的 TextMeshPro，非 UGUI）。
    /// 返回 GameObject，调用方自行设 transform.position/scale。文本/字号后续可通过
    /// go.GetComponent&lt;TextMeshPro&gt;() 修改。英文数字用默认字体即可显示。
    /// </summary>
    public GameObject AddText(string text, float fontSize = 4f) {
        var go = new GameObject("FcsText");
        destroyOnShutdown.Add(go);
        go.transform.Rotate(new Vector3(90, 0, 0));
        go.transform.Rotate(new Vector3(0, 0, -90));
        var tmp = go.AddComponent<TextMeshPro>();
        // AddComponent 后 Awake 未必已执行，字体可能未自动赋值导致不渲染；
        // 显式赋默认字体（含 ASCII，英文数字足够）。
        if (tmp.font == null && TMP_Settings.defaultFontAsset != null)
            tmp.font = TMP_Settings.defaultFontAsset;
        tmp.text = text;
        tmp.fontSize = fontSize;
        tmp.color = Color.white;
        // 锚点设到左上角，方便从左上往下排版（Center 会以几何中心为原点）。
        // tmp.alignment = TextAlignmentOptions.MidlineLeft;
        return go;
    }
    
    public static bool IsInteractive => Application.isFocused && Time.timeScale > 0f;

    public static IEnumerator WaitUntilInteractive() {
        while (!IsInteractive) {
            yield return null;
        }
    }

    public static IEnumerator WaitAndClick(LookAtTarget? button, bool waitActive = true, string label = "", float timeoutSeconds = 10f) {
        if (button == null) {
            MelonLogger.Error($"[FCS] WaitAndClick: button is null. label={label}");
            yield break;
        }
        yield return WaitUntilInteractive();
        var waited = 0f;
        while ((waitActive && button.isActive == false || button.nextAllowedClickTime > Time.realtimeSinceStartup)
               && waited < timeoutSeconds) {
            if (!IsInteractive) {
                yield return null;
                continue;
            }
            yield return null;
            waited += Time.deltaTime;
        }
        if (waitActive && button.isActive == false) {
            var name = button.gameObject?.name ?? "unknown";
            MelonLogger.Error($"[FCS] WaitAndClick: button is not active after timeout. label={label}, object={name}");
            yield break;
        }
        yield return WaitUntilInteractive();
        yield return new WaitForSeconds(0.1f);
        button.OnClickDown();
        yield return WaitUntilInteractive();
        yield return new WaitForSeconds(0.1f);
        button.OnClickUp();
    }
    
    public static IEnumerator InvokeDelay(Action action, float delay) {
        yield return new WaitForSeconds(delay);
        action();
    }
    
}
