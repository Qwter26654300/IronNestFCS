using IronNestFCS.Logic.FCS;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic;

public class FcsWindow
{
    private readonly FSC fcs;

    private bool showWindow = true;
    private Rect panelRect = new(20, 20, 310, 150);

    private static readonly Color ClrTitle = new(0.96f, 0.65f, 0.14f);
    private static readonly Color ClrLabel = new(0.72f, 0.65f, 0.55f);
    private static readonly Color ClrIdle = Color.green;
    private static readonly Color ClrActive = new(0.27f, 0.72f, 0.82f);
    private static readonly Color ClrWarning = new(0.96f, 0.65f, 0.14f);
    private static readonly Color ClrFailed = new(0.83f, 0.18f, 0.18f);
    private static readonly Color ClrGreen = new(0.18f, 0.62f, 0.35f);
    private static readonly Color ClrWhite = Color.white;
    private static readonly Color ClrDiv = new(0.33f, 0.22f, 0.14f);
    private static readonly Color ClrSweep = new(0.96f, 0.35f, 0.14f);

    public bool AutoSweepEnabled { get; set; }
    public bool AutoMarkerEnabled { get; set; } = true;

    public FcsWindow(FSC fcs) => this.fcs = fcs;

    public void OnGui()
    {
        if (!showWindow) return;

        float h = 24f;
        float lineH = h + 4f;

        float extra = 0f;
        if (fcs.LeftTask != null) extra += lineH * 3;
        else extra += lineH;
        if (fcs.RightTask != null) extra += lineH * 3;
        else extra += lineH;
        var queuePreview = fcs.QueueCan;
        extra += lineH;
        if (fcs.AutomaticFireHalted) extra += lineH * 2;
        extra += lineH * (queuePreview.Count + 1);
        extra += 12f;

        panelRect.height = 140f + extra;

        GUI.Box(panelRect, "");

        float x = panelRect.x + 8f;
        float w = panelRect.width - 16f;
        float y = panelRect.y + 4f;

        var oldColor = GUI.color;
        GUI.color = ClrTitle;
        DrawLabel(new Rect(x, y, w, h), "IronNest FCS", 15);
        GUI.color = oldColor;
        y += lineH;

        if (AutoSweepEnabled)
        {
            GUI.color = ClrSweep;
            DrawLabel(new Rect(x, y, w, h), "[扫荡 开]", 14);
            GUI.color = oldColor;
            y += lineH;
        }

        GUI.color = AutoMarkerEnabled ? ClrWhite : ClrWarning;
        DrawLabel(new Rect(x, y, w, h), $"标点: {(AutoMarkerEnabled ? "自动标点" : "手动优先")}", 14);
        GUI.color = oldColor;
        y += lineH;

        if (fcs.AutomaticFireHalted)
        {
            GUI.color = ClrFailed;
            DrawLabel(new Rect(x, y, w, h), "[停火] 自动流程已停止", 14);
            y += lineH;
            GUI.color = ClrWarning;
            DrawLabel(new Rect(x, y, w, h), DisplayHaltReason(fcs.AutomaticFireHaltReason), 13);
            GUI.color = oldColor;
            y += lineH;
        }

        DrawDivider(x, y, w);
        y += 4f;

        if (!fcs.IsBound)
        {
            DrawLabel(new Rect(x, y, w, h), "等待场景...", 14);
            y += lineH;
            DrawLabel(new Rect(x, y, w, h), "按 F9 重新加载", 14);
            return;
        }

        y = DrawGunRow("左炮", fcs.LeftTask, x, y, w, h, lineH);
        DrawDivider(x, y, w);
        y += 4f;
        y = DrawGunRow("右炮", fcs.RightTask, x, y, w, h, lineH);
        DrawDivider(x, y, w);
        y += 4f;

        GUI.color = ClrWhite;
        DrawLabel(new Rect(x, y, w, h), $"队列: {fcs.PendingCount}", 14);
        GUI.color = oldColor;
        y += lineH;

        foreach (var item in queuePreview)
        {
            var marker = item.manualPriority ? "M" : " ";
            DrawLabel(new Rect(x, y, w, h),
                $"{marker} T{item.targetId}  {ConvertPosition(item.position)}  {item.angel,5:F1}°/{item.distance,5:F2}km  {item.bulletType}",
                13);
            y += lineH;
        }
    }

    private float DrawGunRow(string label, ArtilleryTask? task, float x, float y, float w, float h, float lineH)
    {
        var oldColor = GUI.color;

        if (task == null)
        {
            GUI.color = ClrIdle;
            DrawLabel(new Rect(x, y, w, h), $"{label} 空闲", 14);
            GUI.color = oldColor;
            return y + lineH;
        }

        Color stateColor = task.progress switch
        {
            Progress.Failed => ClrFailed,
            Progress.Finished => ClrGreen,
            Progress.Pending => ClrLabel,
            _ => ClrActive
        };

        GUI.color = stateColor;
        var marker = task.manualPriority ? "M " : "";
        DrawLabel(new Rect(x, y, w, h), $"{label} {marker}T{task.targetId}  {task.bulletType}  {ProgressText(task.progress)}", 14);
        GUI.color = oldColor;
        y += lineH;

        GUI.color = Color.red;
        DrawLabel(new Rect(x + 12f, y, w - 12f, h),
            $"目标: {task.angel:F1}° / {task.distance:F2}km",
            13);
        GUI.color = oldColor;
        y += lineH;

        float el = FcsCalc.Elevation(task.distance);
        int chg = FcsCalc.Charge(task.distance);
        GUI.color = Color.yellow;
        DrawLabel(new Rect(x + 12f, y, w - 12f, h),
            $"射击: {el:F2}°  |  {chg}号药",
            13);
        GUI.color = oldColor;
        y += lineH;

        return y;
    }

    private static string ProgressText(Progress progress)
    {
        return progress switch
        {
            Progress.Pending => "待分配",
            Progress.Calculating => "诸元计算",
            Progress.SelectingBullet => "选弹",
            Progress.LoadingBullet => "装弹",
            Progress.LoadingPowder => "装药",
            Progress.WaitLoading => "待成弹",
            Progress.Aiming => "瞄准",
            Progress.WaitingForFire => "待击发",
            Progress.ResourceBlocked => "资源阻塞",
            Progress.BackToIdle => "复位",
            Progress.Finished => "完成",
            Progress.Failed => "失败",
            _ => progress.ToString()
        };
    }

    private static string DisplayHaltReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return "资源不足";
        var text = reason.Replace("Left", "左炮").Replace("Right", "右炮");
        if (text.Contains("could not buy") && text.Contains("purchase did not enter cylinder"))
            return text.Replace("could not buy", "无法购买").Replace("purchase did not enter cylinder", "购买后未进弹仓");
        if (text.Contains("cannot buy powder and no target matches"))
            return text.Replace("cannot buy powder and no target matches", "无法购买药包，且没有目标匹配");
        if (text.Contains("cannot buy powder"))
            return text.Replace("cannot buy powder", "无法购买药包");
        if (text.Contains("was destroyed while a round is loaded"))
            return text.Replace("target", "目标").Replace("was destroyed while a round is loaded", "已被摧毁，当前炮仍有已装弹");
        return text;
    }

    private static void DrawDivider(float x, float y, float w)
    {
        var oldColor = GUI.color;
        GUI.color = ClrDiv;
        GUI.Label(new Rect(x, y, w, 1f), "");
        GUI.color = oldColor;
    }

    private static void DrawLabel(Rect rect, string text, int fontSize)
    {
        var oldFontSize = GUI.skin.label.fontSize;
        GUI.skin.label.fontSize = fontSize;
        GUI.Label(rect, text);
        GUI.skin.label.fontSize = oldFontSize;
    }

    public static string ConvertPosition(Vector3 position)
    {
        int leterIndex = (int)position.x;
        string zoneCol = leterIndex >= 0 && leterIndex < 26 ? ((char)('A' + leterIndex)).ToString() : "#";
        int zoneRow = (int)position.y + 1;
        int subCol = (int)(position.x * 10) % 10;
        int subRow = (int)(position.y * 10) % 10;
        return $"{zoneCol}{zoneRow} {subCol}:{subRow}";
    }
}
