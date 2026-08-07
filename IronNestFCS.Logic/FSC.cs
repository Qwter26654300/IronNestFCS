using HarmonyInstance = HarmonyLib.Harmony;
using System.Collections;
using Il2Cpp;
using IronNestFCS.Logic.FCS;
using MelonLoader;
using UnityEngine;
using UnityEngine.UI;

namespace IronNestFCS.Logic;

public enum LeftRight {
    Left,
    Right,
}

internal enum TurretSweepDirection {
    None,
    Clockwise,
    CounterClockwise,
}

/// <summary>
/// 火控核心流程：绑定游戏对象、读取游戏状态，并驱动游戏内控制台。
/// UI、快捷键和模块生命周期放在 <see cref="FcsModule"/> 与 <see cref="FcsWindow"/>。
/// 这里需要保持热重载安全：不要注册新的 IL2CPP 类型，关闭时要清理 IL2CPP 引用。
/// </summary>
public class FSC
{
    private const string HarmonyId = "com.svr2kos2.ironnestfcs.logic";
    private const float DumpDirection = 180f;
    private const float DumpElevation = 85f;
    private const int MaxPowderWasteForLoadedTarget = 1;
    private const int PowderRecoveryAttempts = 1;
    private const int ManualMapMarkerCount = 4;
    private const float ReloadRecoveryDirectionTolerance = 3f;
    private const float TurretTakeoverAngleLimit = 90f;
    private const float TurretPriorityAngleTolerance = 0.25f;
    private const float TurretPreRotationHoldTolerance = 0.5f;
    private const float TurretSameDirectionPriorityTolerance = 2f;
    private const float TurretSweepDirectionTolerance = 1f;
    private const int QueuePreviewLimit = 20;

    private HarmonyInstance? _harmony;
    
    private FcsSceneInteractor _sceneInteractor;
    private readonly PurchaseDeck _purchaseDeck = new();
    public readonly MapTable MapTable = new MapTable();
    public readonly BallisticCalculator BallisticCalculator = new BallisticCalculator();
    public readonly GunSystem LeftGun = new GunSystem();
    public readonly GunSystem RightGun = new GunSystem();
    public readonly Turret Turret = new Turret();
    public readonly TriggerConsole TriggerConsole = new();
    
    // 候选目标池；真实派发和 UI 预览都会动态排序，不按入队顺序固定执行。
    private readonly Queue<ArtilleryTask> _taskQueue = new();

    /// <summary>左右炮当前任务；null 表示该炮空闲。</summary>
    public ArtilleryTask? LeftTask { get; private set; }
    public ArtilleryTask? RightTask { get; private set; }

    /// <summary>尚未分配给炮管的排队任务数量。</summary>
    public int PendingCount => _taskQueue.Count;
    public bool HasActiveTasks => LeftTask != null || RightTask != null;
    public Queue<ArtilleryTask> QueueCan => new Queue<ArtilleryTask>(BuildDispatchPreview(QueuePreviewLimit));
    public BulletType SelectedBulletType => _sceneInteractor.selectedBulletType;

    /// <summary>
    /// 控制台互斥锁：保护弹道计算器、确认台、采购台这些共享操作。
    /// 临界区保持短小，用完即释放。
    /// </summary>
    private readonly CoroutineLock _deskLock = new();

    /// <summary>
    /// 方向机互斥锁：方向机是左右炮共享的，一发炮占住方向后，必须保持到这发打出去。
    /// 它和 <see cref="_deskLock"/> 分开，让转向可以和装填、高低机调整重叠。
    /// </summary>
    private readonly CoroutineLock _turretLock = new();

    private readonly List<(object handle, LeftRight gun)> _runningCoroutines = new();
    private object? _progressMonitorHandle;
    private readonly HashSet<LeftRight> _loadedRoundBlocked = new();
    private readonly HashSet<LeftRight> _reloadProtectedGuns = new();
    private readonly EntityLocationComparer _entityLocationComparer = new();
    private readonly HashSet<EntityLocation> _activeTargets;
    private readonly HashSet<string> _activeTargetKeys = new();
    private readonly Dictionary<LeftRight, TurretReservation> _turretReservations = new();
    // 进度超时监控。
    private float _leftProgressTime;
    private float _rightProgressTime;
    private Progress _lastLeftProgress;
    private Progress _lastRightProgress;
    private const float ProgressTimeout = 60f;
    private TurretSweepDirection _turretSweepDirection = TurretSweepDirection.None;
    public bool AutomaticFireHalted { get; private set; }
    public string? AutomaticFireHaltReason { get; private set; }
    public bool ManualMarkerPriorityMode { get; set; }
    public FSC() {
        this._sceneInteractor = new FcsSceneInteractor(this);
        _activeTargets = new HashSet<EntityLocation>(_entityLocationComparer);
    }

    public bool IsBound { get; private set; } = false;

    /// <summary>绑定游戏对象；返回 false 表示当前场景还没就绪。</summary>
    public bool TryBind()
    {
        _sceneInteractor = new FcsSceneInteractor(this);
        _sceneInteractor.Initialize();
        _harmony = new HarmonyInstance(HarmonyId);
        _deskLock.Reset();
        _turretLock.Reset();
        IsBound = MapTable.TryBind()
                  && BallisticCalculator.TryBind()
                  && LeftGun.TryBind("Left")
                  && RightGun.TryBind("Right")
                  && _purchaseDeck.TryBind()
                  && Turret.TryBind()
                  && TriggerConsole.TryBind();
        MelonLogger.Msg("[FCS] Initialize: " + (IsBound ? "success" : "failed"));
        if (IsBound) {
            ProtectLoadedGunAfterReload(LeftRight.Left, LeftGun);
            ProtectLoadedGunAfterReload(LeftRight.Right, RightGun);
            StartProgressMonitor();
        }
        return IsBound;
    }

    public void Update() {
        _sceneInteractor.Update();
    }

    /// <summary>小键盘 1-4 快捷触发地图目标。</summary>
    public void FireTarget(int targetId) {
        _sceneInteractor.FireTarget(targetId);
    }

    /// <summary>把扫描到的目标加入打击队列。</summary>
    public void FireAtWorldPos(int id, Vector3 worldPos, EntityLocation? location = null, bool preserveAimPoint = false) {
        _sceneInteractor.FireAtWorldPos(id, worldPos, location, preserveAimPoint);
    }

    /// <summary>把扫描到的目标插到打击队列最前面。</summary>
    public void FireAtWorldPosFront(int id, Vector3 worldPos, EntityLocation? location = null) {
        _sceneInteractor.FireAtWorldPosFront(id, worldPos, location);
    }

    /// <summary>雷达重新扫描后，只刷新尚未分配给炮位的候选任务；正在执行的左右炮任务不动。</summary>
    public void RefreshQueuedTargetsFromRadar(IEnumerable<UnitEntry> aliveUnits) {
        if (!IsBound || _taskQueue.Count == 0) {
            return;
        }

        var alive = aliveUnits
            .Where(unit => unit.IsAlive && unit.Location != null)
            .ToList();
        if (alive.Count == 0) {
            return;
        }

        var refreshed = 0;
        var dropped = 0;
        var existing = _taskQueue.ToArray();
        _taskQueue.Clear();

        foreach (var task in existing) {
            if (!IsTaskAlive(task)) {
                MelonLogger.Msg($"[FCS] Drop destroyed queued target T{task.targetId} during radar refresh.");
                ReleaseTaskTarget(task);
                dropped++;
                continue;
            }

            var match = task.location == null
                ? null
                : alive.FirstOrDefault(unit => unit.Location != null && _entityLocationComparer.Equals(unit.Location, task.location));
            if (!task.preserveAimPoint && match != null && MapTable.TryUpdateTaskFromWorldPos(task, match.WorldPos, match.Location)) {
                refreshed++;
            }

            _taskQueue.Enqueue(task);
        }

        if (refreshed > 0 || dropped > 0) {
            MelonLogger.Msg($"[FCS] Radar refreshed queued targets: updated={refreshed}, dropped={dropped}.");
        }
    }
    
    /// <summary>中止单门炮：停止协程、释放方向机预占，并把未完成任务放回队列。</summary>
    public void AbortGun(LeftRight gun) {
        MelonLogger.Msg($"[FCS] AbortGun {gun}");
        _loadedRoundBlocked.Remove(gun);
        var abortedTask = gun == LeftRight.Left ? LeftTask : RightTask;
        var gunSys = gun == LeftRight.Left ? LeftGun : RightGun;
        var alreadyFired = gunSys.HasFired();
        if (_turretReservations.TryGetValue(gun, out var reservation)) {
            CancelTurretReservation(reservation);
        }
        StopGunCoroutines(gun, "Abort");
        // 清空该炮位。
        if (gun == LeftRight.Left) LeftTask = null;
        else RightTask = null;
        if (alreadyFired) {
            MelonLogger.Msg($"[FCS] AbortGun {gun}: gun already fired, do not requeue task.");
            if (abortedTask != null) {
                abortedTask.progress = Progress.Finished;
                FinishTask(abortedTask);
            }
            TryDispatch();
            return;
        }
        // 被中断的任务放回队首。
        if (abortedTask != null) {
            RequeueTaskFront(abortedTask);
            TryDispatch();
        } else {
            TryDispatch();
        }
    }

    public void AbortAllGuns() {
        MelonLogger.Msg("[FCS] AbortAllGuns");
        var abortedLeft = LeftTask;
        var abortedRight = RightTask;

        for (int i = _runningCoroutines.Count - 1; i >= 0; i--) {
            StopCoroutineHandle(_runningCoroutines[i].handle, "AbortAll");
            _runningCoroutines.RemoveAt(i);
        }

        _loadedRoundBlocked.Clear();
        _activeTargets.Clear();
        _activeTargetKeys.Clear();
        _deskLock.Reset();
        _turretLock.Reset();
        LeftTask = null;
        RightTask = null;
        _turretReservations.Clear();
        _leftProgressTime = 0;
        _rightProgressTime = 0;

        RequeueTasksFront(abortedLeft, abortedRight);
        StartProgressMonitor();
        TryDispatch();
    }

    public void ClearAutomaticFireHalt() {
        if (!AutomaticFireHalted) {
            return;
        }
        MelonLogger.Msg("[FCS] Automatic fire halt cleared.");
        AutomaticFireHalted = false;
        AutomaticFireHaltReason = null;
        ResumeResourceBlockedGun(LeftRight.Left);
        ResumeResourceBlockedGun(LeftRight.Right);
        TryDispatch();
    }

    private void HaltAutomaticFire(string reason) {
        AutomaticFireHalted = true;
        AutomaticFireHaltReason = reason;
        MelonLogger.Error($"[FCS] Stop automatic fire: {reason}");
    }

    private void MarkResourceBlocked(LeftRight leftRight, ArtilleryTask task, string reason) {
        task.progress = Progress.ResourceBlocked;
        MarkProgress(leftRight, Progress.ResourceBlocked);
        if (leftRight == LeftRight.Left) _leftProgressTime = 0;
        else _rightProgressTime = 0;
        _loadedRoundBlocked.Add(leftRight);
        MelonLogger.Warning($"[FCS] {leftRight}: resource blocked; keep current slot. {reason}");
    }

    private void ResumeResourceBlockedGun(LeftRight leftRight) {
        var task = leftRight == LeftRight.Left ? LeftTask : RightTask;
        if (task?.progress != Progress.ResourceBlocked) {
            return;
        }
        var gunSys = leftRight == LeftRight.Left ? LeftGun : RightGun;
        if (!IsTaskAlive(task)) {
            MelonLogger.Warning($"[FCS] {leftRight}: resource-blocked target T{task.targetId} is gone; release slot.");
            ReleaseTaskTarget(task);
            ReleaseSlot(leftRight);
            return;
        }
        if (!gunSys.CanFire()) {
            MelonLogger.Warning($"[FCS] {leftRight}: resource block still waiting. {GunState(gunSys)}");
            return;
        }
        MelonLogger.Warning($"[FCS] {leftRight}: resume resource-blocked task T{task.targetId}.");
        task.progress = Progress.Pending;
        MarkProgress(leftRight, Progress.Pending);
        StartTaskRoutine(leftRight, task);
    }

    private bool RecoverResourceBlockedGunIfReady(LeftRight leftRight) {
        var task = leftRight == LeftRight.Left ? LeftTask : RightTask;
        if (task?.progress != Progress.ResourceBlocked) {
            return false;
        }

        var gunSys = leftRight == LeftRight.Left ? LeftGun : RightGun;
        if (gunSys.HasFired()) {
            return CompleteIfAlreadyFired(leftRight);
        }

        if (!IsTaskAlive(task)) {
            MelonLogger.Warning($"[FCS] {leftRight}: resource-blocked target T{task.targetId} is gone; release slot.");
            ReleaseTaskTarget(task);
            ReleaseSlot(leftRight);
            return true;
        }

        if (!gunSys.CanFire()) {
            return false;
        }

        MelonLogger.Warning($"[FCS] {leftRight}: resource-blocked round is now ready; resume task T{task.targetId}. {GunState(gunSys)}");
        task.progress = Progress.Pending;
        MarkProgress(leftRight, Progress.Pending);
        StartTaskRoutine(leftRight, task);
        return true;
    }

    private static string GunState(GunSystem gunSys) {
        return $"chamber={gunSys.BulletInChamber() ?? "empty"}, actualPowder={gunSys.SelectedPowderCount()}, canFire={gunSys.CanFire()}, hasFired={gunSys.HasFired()}, lastActionFailed={gunSys.LastActionFailed}";
    }

    private void StartProgressMonitor() {
        if (_progressMonitorHandle != null) {
            return;
        }
        _progressMonitorHandle = MelonCoroutines.Start(ProgressTimeoutMonitor());
    }
    
    /// <summary>记录最近进度，用于超时判断。</summary>
    private void MarkProgress(LeftRight gun, Progress p) {
        var now = Time.time;
        if (gun == LeftRight.Left) { _leftProgressTime = now; _lastLeftProgress = p; }
        else { _rightProgressTime = now; _lastRightProgress = p; }
    }

    private void StopGunCoroutines(LeftRight gun, string reason) {
        for (int i = _runningCoroutines.Count - 1; i >= 0; i--) {
            if (_runningCoroutines[i].gun != gun) continue;
            StopCoroutineHandle(_runningCoroutines[i].handle, reason);
            _runningCoroutines.RemoveAt(i);
        }
    }

    private static void StopCoroutineHandle(object? handle, string reason) {
        if (handle == null) {
            return;
        }

        try { MelonCoroutines.Stop(handle); }
        catch (Exception ex) { MelonLogger.Error($"[FCS] {reason} stop failed: {ex}"); }
    }

    private bool CompleteIfAlreadyFired(LeftRight gun) {
        var gunSys = gun == LeftRight.Left ? LeftGun : RightGun;
        if (!gunSys.HasFired()) {
            return false;
        }
        var task = gun == LeftRight.Left ? LeftTask : RightTask;
        MelonLogger.Msg($"[FCS] {gun}: already fired, complete current task.");
        StopGunCoroutines(gun, "Complete fired");
        if (_turretReservations.TryGetValue(gun, out var reservation)) {
            ReleaseTurretOnce(reservation);
        }
        if (task != null) {
            task.progress = Progress.Finished;
            FinishTask(task);
        }
        ReleaseSlot(gun);
        return true;
    }

    private bool CompleteManualFireIfDetected(LeftRight gun, GunSystem gunSys, ArtilleryTask task, TurretReservation turret) {
        if (!gunSys.HasFired()) {
            return false;
        }
        MelonLogger.Msg($"[FCS] {gun}: manual fire detected; complete current task.");
        ReleaseTurretOnce(turret);
        task.progress = Progress.Finished;
        MarkProgress(gun, Progress.Finished);
        FinishTask(task);
        ReleaseSlot(gun);
        return true;
    }

    private void ProtectLoadedGunAfterReload(LeftRight gun, GunSystem gunSys) {
        if (gunSys.BulletInChamber() == null && !gunSys.CanFire()) {
            return;
        }
        _reloadProtectedGuns.Add(gun);
        MelonLogger.Warning($"[FCS] {gun}: loaded round detected after reload; recover only with same-direction targets.");
    }

    private void ReleaseReloadProtectionIfFired(LeftRight gun, GunSystem gunSys) {
        if (!_reloadProtectedGuns.Contains(gun) || !gunSys.HasFired()) {
            return;
        }
        MelonLogger.Msg($"[FCS] {gun}: reload-protected round fired; resume automatic dispatch.");
        _reloadProtectedGuns.Remove(gun);
        TryDispatch();
    }

    /// <summary>某门炮长时间停在同一进度时自动中止。</summary>
    private IEnumerator ProgressTimeoutMonitor() {
        while (true) {
            yield return new WaitForSeconds(2f);
            if (!Application.isFocused || Time.timeScale <= 0f) {
                continue;
            }
            var now = Time.time;
            ReleaseReloadProtectionIfFired(LeftRight.Left, LeftGun);
            ReleaseReloadProtectionIfFired(LeftRight.Right, RightGun);
            RecoverResourceBlockedGunIfReady(LeftRight.Left);
            RecoverResourceBlockedGunIfReady(LeftRight.Right);
            if (LeftTask != null && _leftProgressTime > 0 && now - _leftProgressTime > ProgressTimeout) {
                if (CompleteIfAlreadyFired(LeftRight.Left)) {
                    _leftProgressTime = 0;
                    continue;
                }
                if (RecoverWaitingForFireTimeout(LeftRight.Left)) {
                    continue;
                }
                MelonLogger.Msg($"[FCS] Timeout {_lastLeftProgress}, auto-abort Left");
                AbortGun(LeftRight.Left);
                _leftProgressTime = 0;
            }
            if (RightTask != null && _rightProgressTime > 0 && now - _rightProgressTime > ProgressTimeout) {
                if (CompleteIfAlreadyFired(LeftRight.Right)) {
                    _rightProgressTime = 0;
                    continue;
                }
                if (RecoverWaitingForFireTimeout(LeftRight.Right)) {
                    continue;
                }
                MelonLogger.Msg($"[FCS] Timeout {_lastRightProgress}, auto-abort Right");
                AbortGun(LeftRight.Right);
                _rightProgressTime = 0;
            }
        }
    }

    /// <summary>释放补丁、停止协程，并清理 IL2CPP 引用。</summary>
    public void Dispose()
    {
        if (_progressMonitorHandle != null) {
            try { MelonCoroutines.Stop(_progressMonitorHandle); }
            catch (Exception ex) { MelonLogger.Error($"[FCS] Stop progress monitor failed: {ex}"); }
            _progressMonitorHandle = null;
        }

        foreach (var (handle, _) in _runningCoroutines) {
            StopCoroutineHandle(handle, "Stop coroutines");
        }
        _runningCoroutines.Clear();

        _taskQueue.Clear();
        _activeTargets.Clear();
        _activeTargetKeys.Clear();
        LeftTask = null;
        RightTask = null;
        _turretReservations.Clear();

        _sceneInteractor.ShutDown();
        try { _harmony?.UnpatchSelf(); }
        catch (Exception ex) { MelonLogger.Error($"[FCS] UnpatchSelf failed: {ex}"); }
        _harmony = null;
    }

    public IEnumerator ExposeAllEntities() {
        while (true) {
            foreach (var m in MapTable.GetAllFireMissionEntities()) {
                m.GetComponent<Image>().enabled = true;
            }

            yield return new WaitForSeconds(1f);
        }
    }

    /// <summary>加入调度队列，并在有空闲炮位时立即派发。</summary>
    public void EnqueueTask(ArtilleryTask task) {
        if (!CanQueueTask(task)) {
            if (task.manualPriority && PromoteQueuedDuplicateToManualPriority(task)) {
                MelonLogger.Msg($"[FCS] Promote queued target T{task.targetId} to manual priority.");
                TryDispatch();
            }
            return;
        }
        task.progress = Progress.Pending;
        _taskQueue.Enqueue(task);
        TryDispatch();
    }

    /// <summary>把任务插到队首。</summary>
    public void EnqueueTaskFront(ArtilleryTask task) {
        if (!CanQueueTask(task)) {
            if (task.manualPriority && PromoteQueuedDuplicateToManualPriority(task)) {
                MelonLogger.Msg($"[FCS] Promote queued target T{task.targetId} to manual priority.");
                TryDispatch();
            }
            return;
        }
        task.progress = Progress.Pending;
        var existing = _taskQueue.ToArray();
        _taskQueue.Clear();
        _taskQueue.Enqueue(task);
        foreach (var t in existing) _taskQueue.Enqueue(t);
        TryDispatch();
    }

    private static int TaskPriority(ArtilleryTask task) {
        return TargetPriority.GetPriority(task.location);
    }

    private static int TaskStars(ArtilleryTask task) {
        return TargetPriority.GetStars(task.location);
    }

    private static int ManualPriority(ArtilleryTask task) {
        return task.manualPriority ? 1 : 0;
    }

    private float TurretDeltaForSort(ArtilleryTask task) {
        return Turret.DeltaFromCurrent(task.angel) ?? 360f;
    }

    /// <summary>持续从候选池挑选真实下一发，直到两门炮都忙或池为空。</summary>
    private void TryDispatch() {
        if (AutomaticFireHalted) {
            return;
        }

        while (_taskQueue.Count > 0) {
            LeftRight slot;
            if (LeftTask == null) slot = LeftRight.Left;
            else if (RightTask == null) slot = LeftRight.Right;
            else break; // 两门炮都忙。

            var task = TakeBestTaskForDispatch(slot);
            if (task == null) {
                break;
            }

            ReserveTaskTarget(task);
            if (slot == LeftRight.Left) LeftTask = task;
            else RightTask = task;
            UpdateTurretSweepDirection(task);
            StartTaskRoutine(slot, task);
        }
    }

    private ArtilleryTask? TakeBestTaskForDispatch(LeftRight slot) {
        var candidates = CleanCandidatePool();
        if (candidates.Count == 0) {
            return null;
        }

        if (_reloadProtectedGuns.Contains(slot)) {
            var sameDirection = candidates.Where(IsSameDirectionAsCurrentTurret).ToList();
            if (sameDirection.Count > 0) {
                candidates = sameDirection;
            }
            else if (TryGetLoadedRound(slot, out var shell, out var actualPowder)) {
                var compatibleLoadedTargets = candidates
                    .Where(task => IsCompatibleLoadedTask(task, shell, actualPowder))
                    .ToList();
                if (compatibleLoadedTargets.Count == 0) {
                    MelonLogger.Msg(
                        $"[FCS] {slot}: wait for compatible reload recovery target. " +
                        $"loaded={shell}/{actualPowder}, current={Turret.CurrentMapAngle?.ToString("F1") ?? "unknown"}.");
                    return null;
                }

                candidates = compatibleLoadedTargets;
                MelonLogger.Warning(
                    $"[FCS] {slot}: no same-direction reload recovery target; " +
                    $"use nearest compatible loaded round target. loaded={shell}/{actualPowder}, " +
                    $"current={Turret.CurrentMapAngle?.ToString("F1") ?? "unknown"}.");
            }
            else {
                MelonLogger.Msg($"[FCS] {slot}: wait for same-direction reload recovery target. current={Turret.CurrentMapAngle?.ToString("F1") ?? "unknown"}.");
                return null;
            }
        }

        var selected = BuildDispatchOrder(candidates, QueuePreviewLimit).FirstOrDefault();
        if (selected == null) {
            return null;
        }

        RemoveQueuedTask(selected);
        MelonLogger.Msg(
            $"[FCS] {slot}: dispatch T{selected.targetId}; priority={TaskPriority(selected)}, stars={TaskStars(selected)}, " +
            $"angle={selected.angel:F1}, sweep={_turretSweepDirection}.");
        return selected;
    }

    private bool TryGetLoadedRound(LeftRight slot, out BulletType shell, out int actualPowder) {
        shell = BulletType.AP;
        actualPowder = 0;
        var gunSys = slot == LeftRight.Left ? LeftGun : RightGun;
        var chambered = gunSys.BulletInChamber();
        if (!TryParseBulletType(chambered, out shell)) {
            return false;
        }

        actualPowder = gunSys.SelectedPowderCount();
        return true;
    }

    private List<ArtilleryTask> BuildDispatchPreview(int limit) {
        var candidates = _taskQueue
            .Where(item => IsTaskAlive(item) && !IsTargetActive(item) && !IsTargetKeyActive(item))
            .ToList();
        return BuildDispatchOrder(candidates, limit);
    }

    private List<ArtilleryTask> CleanCandidatePool() {
        var cleaned = new List<ArtilleryTask>();
        foreach (var item in _taskQueue.ToArray()) {
            if (!IsTaskAlive(item)) {
                MelonLogger.Msg($"[FCS] Drop destroyed queued target T{item.targetId}.");
                ReleaseTaskTarget(item);
                continue;
            }
            if (IsTargetActive(item)) {
                MelonLogger.Msg($"[FCS] Drop duplicate active target T{item.targetId}.");
                continue;
            }
            if (IsTargetKeyActive(item)) {
                MelonLogger.Msg($"[FCS] Drop duplicate active target signature T{item.targetId}.");
                continue;
            }
            cleaned.Add(item);
        }
        _taskQueue.Clear();
        foreach (var item in cleaned) {
            _taskQueue.Enqueue(item);
        }
        return cleaned;
    }

    private void RemoveQueuedTask(ArtilleryTask task) {
        var existing = _taskQueue.ToArray();
        _taskQueue.Clear();
        foreach (var item in existing) {
            if (ReferenceEquals(item, task)) {
                continue;
            }
            _taskQueue.Enqueue(item);
        }
    }

    private List<ArtilleryTask> BuildDispatchOrder(List<ArtilleryTask> candidates, int limit) {
        var result = new List<ArtilleryTask>();
        var remaining = candidates.ToList();
        var referenceAngle = Turret.CurrentMapAngle;
        var direction = _turretSweepDirection;

        while (remaining.Count > 0 && result.Count < limit) {
            var maxManualPriority = remaining.Max(ManualPriority);
            var manualBand = remaining.Where(task => ManualPriority(task) == maxManualPriority).ToList();
            var maxPriority = manualBand.Max(TaskPriority);
            var priorityBand = manualBand.Where(task => TaskPriority(task) == maxPriority).ToList();
            var maxStars = priorityBand.Max(TaskStars);
            var starBand = priorityBand.Where(task => TaskStars(task) == maxStars).ToList();
            var orderedBand = OrderByTurretSweep(starBand, referenceAngle, direction);
            if (orderedBand.Count == 0) {
                break;
            }

            foreach (var task in orderedBand) {
                if (result.Count >= limit) {
                    break;
                }
                result.Add(task);
                remaining.Remove(task);
                if (referenceAngle.HasValue) {
                    direction = DirectionFromAngles(referenceAngle.Value, task.angel, direction);
                }
                referenceAngle = task.angel;
            }
        }

        return result;
    }

    private List<ArtilleryTask> OrderByTurretSweep(List<ArtilleryTask> tasks, float? referenceAngle, TurretSweepDirection direction) {
        if (!referenceAngle.HasValue || direction == TurretSweepDirection.None) {
            return tasks
                .OrderBy(TurretDeltaForSort)
                .ThenBy(task => task.distance)
                .ToList();
        }

        var forward = tasks
            .Where(task => IsInSweepDirection(referenceAngle.Value, task.angel, direction))
            .OrderBy(task => SweepDistance(referenceAngle.Value, task.angel, direction))
            .ThenBy(task => task.distance)
            .ToList();
        if (forward.Count > 0) {
            return forward;
        }

        var reverse = ReverseDirection(direction);
        return tasks
            .OrderBy(task => SweepDistance(referenceAngle.Value, task.angel, reverse))
            .ThenBy(task => task.distance)
            .ToList();
    }

    private void UpdateTurretSweepDirection(ArtilleryTask task) {
        var current = Turret.CurrentMapAngle;
        if (!current.HasValue) {
            return;
        }
        _turretSweepDirection = DirectionFromAngles(current.Value, task.angel, _turretSweepDirection);
    }

    private static TurretSweepDirection DirectionFromAngles(float fromAngle, float toAngle, TurretSweepDirection fallback) {
        var delta = Mathf.DeltaAngle(NormalizeAngle(fromAngle), NormalizeAngle(toAngle));
        if (Mathf.Abs(delta) <= TurretSweepDirectionTolerance) {
            return fallback;
        }
        return delta > 0f ? TurretSweepDirection.Clockwise : TurretSweepDirection.CounterClockwise;
    }

    private static bool IsInSweepDirection(float fromAngle, float toAngle, TurretSweepDirection direction) {
        var distance = SweepDistance(fromAngle, toAngle, direction);
        return distance <= 180f || distance <= TurretSweepDirectionTolerance;
    }

    private static float SweepDistance(float fromAngle, float toAngle, TurretSweepDirection direction) {
        return direction switch {
            TurretSweepDirection.Clockwise => ClockwiseDistance(fromAngle, toAngle),
            TurretSweepDirection.CounterClockwise => CounterClockwiseDistance(fromAngle, toAngle),
            _ => Mathf.Abs(Mathf.DeltaAngle(NormalizeAngle(fromAngle), NormalizeAngle(toAngle))),
        };
    }

    private static TurretSweepDirection ReverseDirection(TurretSweepDirection direction) {
        return direction switch {
            TurretSweepDirection.Clockwise => TurretSweepDirection.CounterClockwise,
            TurretSweepDirection.CounterClockwise => TurretSweepDirection.Clockwise,
            _ => TurretSweepDirection.None,
        };
    }

    private static float ClockwiseDistance(float fromAngle, float toAngle) {
        var distance = NormalizeAngle(toAngle) - NormalizeAngle(fromAngle);
        if (distance < 0f) distance += 360f;
        return distance;
    }

    private static float CounterClockwiseDistance(float fromAngle, float toAngle) {
        var distance = NormalizeAngle(fromAngle) - NormalizeAngle(toAngle);
        if (distance < 0f) distance += 360f;
        return distance;
    }

    /// <summary>
    /// 启动一个火控协程。协程由 Unity 主线程分帧驱动，可以安全访问 IL2CPP 对象。
    /// </summary>
    private void StartTaskRoutine(LeftRight leftRight, ArtilleryTask task) {
        var handle = MelonCoroutines.Start(RunTaskRoutine(leftRight, task));
        if (handle != null) {
            _runningCoroutines.Add((handle, leftRight));
        }
    }

    /// <summary>释放炮位，并尝试从队列拉取下一发任务。</summary>
    private void ReleaseSlot(LeftRight leftRight) {
        _loadedRoundBlocked.Remove(leftRight);
        _reloadProtectedGuns.Remove(leftRight);
        if (leftRight == LeftRight.Left) LeftTask = null;
        else RightTask = null;
        TryDispatch();
    }

    private void RequeueTaskFront(ArtilleryTask task) {
        ReleaseTaskTarget(task);
        if (!CanQueueTask(task)) {
            return;
        }
        task.progress = Progress.Pending;
        var existing = _taskQueue.ToArray();
        _taskQueue.Clear();
        _taskQueue.Enqueue(task);
        foreach (var item in existing) {
            _taskQueue.Enqueue(item);
        }
    }

    private void RequeueTasksFront(ArtilleryTask? leftTask, ArtilleryTask? rightTask) {
        var existing = _taskQueue.ToArray();
        _taskQueue.Clear();
        if (leftTask != null) {
            ReleaseTaskTarget(leftTask);
            if (CanQueueTask(leftTask)) {
                leftTask.progress = Progress.Pending;
                _taskQueue.Enqueue(leftTask);
            }
        }
        if (rightTask != null) {
            ReleaseTaskTarget(rightTask);
            if (CanQueueTask(rightTask)) {
                rightTask.progress = Progress.Pending;
                _taskQueue.Enqueue(rightTask);
            }
        }
        foreach (var item in existing) {
            if (CanQueueTask(item)) {
                _taskQueue.Enqueue(item);
            }
        }
    }

    private int RequiredPowder(ArtilleryTask task) {
        return _sceneInteractor.maxCharge ? 6 : BallisticCalculator.MinimumCharge(task.distance);
    }

    private bool CanQueueTask(ArtilleryTask task) {
        if (!IsTaskAlive(task)) {
            MelonLogger.Msg($"[FCS] Skip destroyed target T{task.targetId}.");
            return false;
        }
        if (IsTargetActive(task)) {
            MelonLogger.Msg($"[FCS] Skip target T{task.targetId}; it is already assigned to a barrel.");
            return false;
        }
        if (IsTargetKeyActive(task)) {
            MelonLogger.Msg($"[FCS] Skip target T{task.targetId}; matching signature is already assigned.");
            return false;
        }
        if (IsTargetQueued(task)) {
            MelonLogger.Msg($"[FCS] Skip target T{task.targetId}; it is already queued.");
            return false;
        }
        return true;
    }

    private bool IsTargetActive(ArtilleryTask task) {
        return task.location != null && _activeTargets.Contains(task.location);
    }

    private bool IsTargetQueued(ArtilleryTask task) {
        return _taskQueue.Any(item => IsSameTarget(item, task));
    }

    private bool PromoteQueuedDuplicateToManualPriority(ArtilleryTask task) {
        foreach (var item in _taskQueue) {
            if (!IsSameTarget(item, task)) {
                continue;
            }
            item.manualPriority = true;
            item.targetId = task.targetId;
            item.bulletType = task.bulletType;
            return true;
        }
        return false;
    }

    private void ReserveTaskTarget(ArtilleryTask task) {
        if (task.location != null) {
            _activeTargets.Add(task.location);
        }
        foreach (var key in TargetKeys(task)) {
            _activeTargetKeys.Add(key);
        }
    }

    private void ReleaseTaskTarget(ArtilleryTask task) {
        if (task.location != null) {
            _activeTargets.Remove(task.location);
        }
        foreach (var key in TargetKeys(task)) {
            _activeTargetKeys.Remove(key);
        }
    }

    private void FinishTask(ArtilleryTask task) {
        ReleaseTaskTarget(task);
        _sceneInteractor.TaskFinished(task);
    }

    private bool IsTargetKeyActive(ArtilleryTask task) {
        return TargetKeys(task).Any(key => _activeTargetKeys.Contains(key));
    }

    private bool IsSameTarget(ArtilleryTask left, ArtilleryTask right) {
        if (left.location != null && right.location != null) {
            return _entityLocationComparer.Equals(left.location, right.location);
        }
        var rightKeys = TargetKeys(right).ToHashSet();
        return TargetKeys(left).Any(rightKeys.Contains);
    }

    private IEnumerable<string> TargetKeys(ArtilleryTask task) {
        if (task.location != null) {
            yield return $"loc:{task.location.Pointer}";
            yield break;
        }
        var angle = MathF.Round(task.angel, 1);
        var distance = MathF.Round(task.distance, 2);
        yield return $"ballistic:{task.bulletType}:{angle:F1}:{distance:F2}";
    }

    private bool IsCompatibleLoadedTask(ArtilleryTask task, BulletType shell, int actualPowder) {
        if (task.bulletType != shell) return false;
        if (actualPowder <= 0) return true;
        var required = RequiredPowder(task);
        return required <= actualPowder && actualPowder - required <= MaxPowderWasteForLoadedTarget;
    }

    private bool IsCompatibleWithAvailablePowder(ArtilleryTask task, BulletType shell, int availablePowder) {
        return task.bulletType == shell && availablePowder > 0 && RequiredPowder(task) <= availablePowder;
    }

    private bool TryTakeQueuedTaskForAvailablePowder(BulletType shell, int availablePowder, out ArtilleryTask? matched) {
        matched = null;
        var existing = _taskQueue.ToArray();
        var compatible = existing
            .Where(item => IsTaskAlive(item) && IsCompatibleWithAvailablePowder(item, shell, availablePowder))
            .OrderByDescending(ManualPriority)
            .ThenBy(TurretDeltaForSort)
            .ThenByDescending(TaskPriority)
            .ThenByDescending(TaskStars)
            .ThenByDescending(RequiredPowder)
            .FirstOrDefault();
        _taskQueue.Clear();
        foreach (var item in existing) {
            if (!IsTaskAlive(item)) {
                MelonLogger.Msg($"[FCS] Drop destroyed queued target T{item.targetId}.");
                continue;
            }
            if (compatible != null && ReferenceEquals(item, compatible)) {
                matched = item;
                continue;
            }
            _taskQueue.Enqueue(item);
        }
        return matched != null;
    }

    private bool TryGetMarkerTaskForAvailablePowder(BulletType shell, int availablePowder, out ArtilleryTask? matched) {
        matched = null;
        if (MapTable.artilleries == null || MapTable.artilleries.Count == 0) {
            return false;
        }

        var candidates = new List<ArtilleryTask>();
        foreach (var targetId in MapTable.artilleries.Keys.Where(id => id >= 1 && id <= ManualMapMarkerCount).OrderBy(id => id)) {
            var markerTask = MapTable.GetMarkTarget(targetId);
            if (markerTask == null) {
                continue;
            }
            markerTask.targetId = targetId;
            markerTask.bulletType = shell;
            markerTask.manualPriority = ManualMarkerPriorityMode;
            if (!MapTable.IsMarkerInsideTacticalMap(targetId)) {
                continue;
            }
            if (!IsCompatibleWithAvailablePowder(markerTask, shell, availablePowder)) {
                continue;
            }
            if (IsTargetReservedOrQueued(markerTask)) {
                MelonLogger.Msg($"[FCS] Skip marker T{targetId}; matching target is already assigned or queued.");
                continue;
            }
            candidates.Add(markerTask);
        }

        matched = candidates
            .OrderByDescending(ManualPriority)
            .ThenBy(TurretDeltaForSort)
            .ThenByDescending(TaskPriority)
            .ThenByDescending(TaskStars)
            .ThenByDescending(RequiredPowder)
            .ThenBy(task => task.distance)
            .FirstOrDefault();

        return matched != null;
    }

    private bool TryFindTaskForAvailablePowder(BulletType shell, int availablePowder, out ArtilleryTask? matched) {
        return TryTakeQueuedTaskForAvailablePowder(shell, availablePowder, out matched)
               || TryGetMarkerTaskForAvailablePowder(shell, availablePowder, out matched);
    }

    private bool IsTaskAlive(ArtilleryTask task) {
        if (task.location == null) return true;
        return TacticalRadar.IsUnitAlive(task.location, task.location.gameObject);
    }

    private bool IsValidElevation(float elevation) {
        return elevation > 0.01f && elevation <= 90f;
    }

    private bool StopIfActiveTargetDestroyed(LeftRight leftRight, GunSystem gunSys, ArtilleryTask task, TurretReservation turret, string stage) {
        if (IsTaskAlive(task)) {
            return false;
        }

        MelonLogger.Warning($"[FCS] {leftRight}: target T{task.targetId} destroyed during {stage}; stop current shot.");
        task.progress = Progress.Failed;
        MarkProgress(leftRight, Progress.Failed);
        CancelTurretReservation(turret);
        FinishTask(task);

        if (gunSys.BulletInChamber() != null || gunSys.CanFire()) {
            HaltAutomaticFire($"{leftRight} target T{task.targetId} was destroyed while a round is loaded.");
            MarkResourceBlocked(leftRight, task, "target destroyed while loaded");
        }
        else {
            ReleaseSlot(leftRight);
        }
        return true;
    }

    private bool DropDestroyedTaskBeforeLoading(LeftRight leftRight, ArtilleryTask task, string stage) {
        if (IsTaskAlive(task)) {
            return false;
        }

        MelonLogger.Warning($"[FCS] {leftRight}: target T{task.targetId} destroyed before {stage}; skip purchase/calculation.");
        task.progress = Progress.Failed;
        MarkProgress(leftRight, Progress.Failed);
        FinishTask(task);
        ReleaseSlot(leftRight);
        return true;
    }

    private bool IsSameDirectionAsCurrentTurret(ArtilleryTask task) {
        var current = Turret.CurrentMapAngle;
        if (current == null) {
            return false;
        }
        return Mathf.Abs(Mathf.DeltaAngle(current.Value, task.angel)) <= ReloadRecoveryDirectionTolerance;
    }

    private TurretReservation MaintainTurretPriorityForLoadedRound(LeftRight leftRight, ArtilleryTask task, TurretReservation turret, string stage) {
        RequestTurretPriorityForActiveGun(leftRight);
        if (ShouldYieldTurretToWaitingShot(leftRight, task)) {
            return turret;
        }
        return EnsureTurretReservationForTask(leftRight, task, turret, stage);
    }

    private IEnumerator KeepAimingLoadedRound(LeftRight leftRight, GunSystem gunSys, ArtilleryTask task, TurretReservation turret, float elevation, string stage, Action<TurretReservation> updateTurret) {
        while (!gunSys.LastElevationReady && (gunSys.BulletInChamber() != null || gunSys.CanFire())) {
            if (gunSys.HasFired()) {
                yield break;
            }
            turret = MaintainTurretPriorityForLoadedRound(leftRight, task, turret, stage);
            updateTurret(turret);
            MelonLogger.Warning($"[FCS] {leftRight}: loaded round elevation not ready {stage}; keep aiming current task.");
            yield return gunSys.SetElevation(elevation, () => {
                turret = MaintainTurretPriorityForLoadedRound(leftRight, task, turret, stage);
                updateTurret(turret);
            });
        }
    }

    /// <summary>已装弹/药恢复时，优先选择兼容且方向机最近的目标，尽快把已成形弹药打出去。</summary>
    private bool TryTakeQueuedTaskForLoadedRound(BulletType shell, int actualPowder, out ArtilleryTask? matched) {
        matched = null;
        var existing = _taskQueue.ToArray();
        var compatible = existing
            .Where(item => IsTaskAlive(item) && IsCompatibleLoadedTask(item, shell, actualPowder))
            .OrderByDescending(ManualPriority)
            .ThenBy(TurretDeltaForSort)
            .ThenByDescending(TaskPriority)
            .ThenByDescending(TaskStars)
            .ThenBy(task => actualPowder > 0 ? actualPowder - RequiredPowder(task) : 0)
            .FirstOrDefault();
        _taskQueue.Clear();
        foreach (var item in existing) {
            if (!IsTaskAlive(item)) {
                MelonLogger.Msg($"[FCS] Drop destroyed queued target T{item.targetId}.");
                continue;
            }
            if (compatible != null && ReferenceEquals(item, compatible)) {
                matched = item;
                continue;
            }
            _taskQueue.Enqueue(item);
        }
        return matched != null;
    }

    private bool TryGetMarkerTaskForLoadedRound(BulletType shell, int actualPowder, out ArtilleryTask? matched) {
        matched = null;
        if (MapTable.artilleries == null || MapTable.artilleries.Count == 0) {
            return false;
        }

        var candidates = new List<ArtilleryTask>();
        foreach (var targetId in MapTable.artilleries.Keys.Where(id => id >= 1 && id <= ManualMapMarkerCount).OrderBy(id => id)) {
            var markerTask = MapTable.GetMarkTarget(targetId);
            if (markerTask == null) {
                continue;
            }
            markerTask.targetId = targetId;
            markerTask.bulletType = shell;
            markerTask.manualPriority = ManualMarkerPriorityMode;
            if (!MapTable.IsMarkerInsideTacticalMap(targetId)) {
                continue;
            }
            if (!IsCompatibleLoadedTask(markerTask, shell, actualPowder)) {
                continue;
            }
            if (IsTargetReservedOrQueued(markerTask)) {
                MelonLogger.Msg($"[FCS] Skip marker T{targetId}; matching target is already assigned or queued.");
                continue;
            }
            candidates.Add(markerTask);
        }

        matched = candidates
            .OrderByDescending(ManualPriority)
            .ThenBy(TurretDeltaForSort)
            .ThenByDescending(TaskPriority)
            .ThenByDescending(TaskStars)
            .ThenBy(task => actualPowder > 0 ? actualPowder - RequiredPowder(task) : 0)
            .ThenBy(task => task.distance)
            .FirstOrDefault();

        if (matched != null) {
            MelonLogger.Warning(
                $"[FCS] matched loaded round from map marker T{matched.targetId}. " +
                $"shell={shell}, actualPowder={actualPowder}, requiredPowder={RequiredPowder(matched)}");
        }
        return matched != null;
    }

    private bool TryFindTaskForLoadedRound(BulletType shell, int actualPowder, out ArtilleryTask? matched) {
        return TryTakeQueuedTaskForLoadedRound(shell, actualPowder, out matched)
               || TryGetMarkerTaskForLoadedRound(shell, actualPowder, out matched);
    }

    private bool IsTargetReservedOrQueued(ArtilleryTask task) {
        return IsTargetActive(task) || IsTargetKeyActive(task) || IsTargetQueued(task);
    }

    private IEnumerator WaitForQueuedTaskForLoadedRound(BulletType shell, int actualPowder, float seconds, Action<ArtilleryTask?> done) {
        var waited = 0f;
        while (waited < seconds) {
            if (TryFindTaskForLoadedRound(shell, actualPowder, out var matched)) {
                done(matched);
                yield break;
            }
            yield return new WaitForSeconds(0.5f);
            if (FcsSceneInteractor.IsInteractive) {
                waited += 0.5f;
            }
        }
        done(null);
    }

    private IEnumerator WaitForQueuedTaskForLoadedRoundAfterOtherGun(LeftRight leftRight, BulletType shell, int actualPowder, Action<ArtilleryTask?> done) {
        var other = leftRight == LeftRight.Left ? RightTask : LeftTask;
        var waited = 0f;
        while (other != null && waited < 6f) {
            if (TryFindTaskForLoadedRound(shell, actualPowder, out var matched)) {
                MelonLogger.Warning($"[FCS] {leftRight}: found new compatible target while waiting.");
                done(matched);
                yield break;
            }
            yield return new WaitForSeconds(0.5f);
            if (FcsSceneInteractor.IsInteractive) {
                waited += 0.5f;
            }
            other = leftRight == LeftRight.Left ? RightTask : LeftTask;
        }
        if (waited >= 6f) {
            MelonLogger.Warning($"[FCS] {leftRight}: timeout waiting for other gun; retrying target search before dump.");
        }
        yield return WaitForQueuedTaskForLoadedRound(shell, actualPowder, 3f, done);
    }

    private bool BothGunsBlocked() {
        return _loadedRoundBlocked.Contains(LeftRight.Left) && _loadedRoundBlocked.Contains(LeftRight.Right);
    }

    private bool TryParseBulletType(string? shell, out BulletType type) {
        return Enum.TryParse(shell, out type);
    }

    private IEnumerator ConfirmArmAndFire(LeftRight leftRight, GunSystem gunSys, bool allowManualFire, Action<bool> done) {
        var fired = false;
        var autoFire = _sceneInteractor.AutoFire || !allowManualFire;
        var maxAttempts = autoFire ? 2 : 1;
        var waitSeconds = autoFire ? 2f : 8f;
        for (var attempt = 1; attempt <= maxAttempts && !fired; attempt++) {
            if (attempt > 1) {
                MelonLogger.Warning($"[FCS] {leftRight}: fire not detected, retry trigger.");
            }
            yield return TriggerConsole.ConfirmTask();
            yield return TriggerConsole.ConfirmBullet();
            yield return TriggerConsole.ConfirmRotation();
            yield return TriggerConsole.ConfirmElevation();
            yield return TriggerConsole.ReadyToFire();
            yield return TriggerConsole.Arm(leftRight);
            if (autoFire) {
                yield return FcsSceneInteractor.WaitUntilInteractive();
                MelonLogger.Msg($"[FCS] {leftRight}: auto fire.");
                TriggerConsole.Fire();
            }
            yield return gunSys.WaitFire(waitSeconds, value => fired = value);
        }
        done(fired);
    }

    private IEnumerator DumpLoadedRound(LeftRight leftRight, GunSystem gunSys, TurretReservation turret, Action<bool>? done = null) {
        var fired = false;
        if (gunSys.BulletInChamber() == null) {
            done?.Invoke(true);
            yield break;
        }

        MelonLogger.Warning($"[FCS] {leftRight}: dumping loaded round out of map.");
        turret.Canceled = false;
        if (!turret.Acquired || turret.Released) {
            yield return _turretLock.Acquire();
            turret.Acquired = true;
            turret.Released = false;
        }

        yield return Turret.SetRotation(DumpDirection, () => turret.Canceled);
        if (turret.Canceled) {
            MelonLogger.Warning($"[FCS] {leftRight}: dump rotation canceled.");
            ReleaseTurretOnce(turret);
            done?.Invoke(false);
            yield break;
        }
        if (!Turret.LastRotationReady) {
            MelonLogger.Warning($"[FCS] {leftRight}: dump turret rotation not ready.");
            ReleaseTurretOnce(turret);
            done?.Invoke(false);
            yield break;
        }
        yield return gunSys.SetElevation(DumpElevation);
        if (turret.Canceled) {
            MelonLogger.Warning($"[FCS] {leftRight}: dump canceled before confirm.");
            ReleaseTurretOnce(turret);
            done?.Invoke(false);
            yield break;
        }
        yield return _deskLock.Acquire();
        try {
            yield return ConfirmArmAndFire(leftRight, gunSys, allowManualFire: false, value => fired = value);
            if (!fired && gunSys.HasFired()) {
                fired = true;
            }
            if (!fired) {
                MelonLogger.Warning($"[FCS] {leftRight}: dump fire not detected.");
            }
        }
        finally {
            _deskLock.Release();
            ReleaseTurretOnce(turret);
        }
        done?.Invoke(fired);
    }

    private IEnumerator FireLoadedRoundAtTask(LeftRight leftRight, GunSystem gunSys, TurretReservation turret, ArtilleryTask task, int powderCount, Action<bool>? done = null) {
        var fired = false;
        if (gunSys.BulletInChamber() == null) {
            done?.Invoke(true);
            yield break;
        }

        MelonLogger.Warning($"[FCS] {leftRight}: dumping loaded round at nearest map edge. angle={task.angel:F1}, distance={task.distance:F2}, powder={powderCount}");
        turret.Canceled = false;
        if (!turret.Acquired || turret.Released) {
            yield return _turretLock.Acquire();
            turret.Acquired = true;
            turret.Released = false;
        }

        var elevation = 0f;
        yield return _deskLock.Acquire();
        try {
            yield return CalculateBallisticSolution(task, powderCount);
            elevation = BallisticCalculator.GetElevation();
            MelonLogger.Warning($"[FCS] {leftRight}: dump solution elevation={elevation:F2}");
        }
        finally {
            _deskLock.Release();
        }

        if (!IsValidElevation(elevation)) {
            MelonLogger.Warning($"[FCS] {leftRight}: dump solution invalid, fallback to fixed dump shot.");
            yield return DumpLoadedRound(leftRight, gunSys, turret, done);
            yield break;
        }

        MelonLogger.Warning($"[FCS] {leftRight}: dump rotate.");
        yield return Turret.SetRotation(task.angel, () => turret.Canceled);
        if (turret.Canceled) {
            MelonLogger.Warning($"[FCS] {leftRight}: dump task rotation canceled.");
            ReleaseTurretOnce(turret);
            done?.Invoke(false);
            yield break;
        }
        if (!Turret.LastRotationReady) {
            MelonLogger.Warning($"[FCS] {leftRight}: dump task turret rotation not ready.");
            ReleaseTurretOnce(turret);
            done?.Invoke(false);
            yield break;
        }
        MelonLogger.Warning($"[FCS] {leftRight}: dump elevate.");
        yield return gunSys.SetElevation(elevation);
        if (turret.Canceled) {
            MelonLogger.Warning($"[FCS] {leftRight}: dump task canceled before confirm.");
            ReleaseTurretOnce(turret);
            done?.Invoke(false);
            yield break;
        }
        MelonLogger.Warning($"[FCS] {leftRight}: dump confirm and fire.");
        yield return _deskLock.Acquire();
        try {
            yield return ConfirmArmAndFire(leftRight, gunSys, allowManualFire: false, value => fired = value);
            if (!fired && gunSys.HasFired()) {
                fired = true;
            }
            if (fired) MelonLogger.Warning($"[FCS] {leftRight}: dump fire detected.");
            else MelonLogger.Warning($"[FCS] {leftRight}: dump fire not detected.");
        }
        finally {
            _deskLock.Release();
            ReleaseTurretOnce(turret);
        }
        done?.Invoke(fired);
    }

    private IEnumerator CalculateBallisticSolution(ArtilleryTask task, int powderCount) {
        yield return BallisticCalculator.SetDistance(task.distance);
        yield return BallisticCalculator.SetDirection(task.angel);
        yield return BallisticCalculator.SetCharge(powderCount);
        yield return BallisticCalculator.SetTargetType(task.targetTypeDialValue);
        yield return BallisticCalculator.SetShellType(task.bulletType);
        yield return BallisticCalculator.Calculate();
    }

    private IEnumerator RunTaskRoutine(LeftRight leftRight, ArtilleryTask task) {
        var gunSys = leftRight == LeftRight.Left ? LeftGun : RightGun;
        MarkProgress(leftRight, task.progress);

        var turret = new TurretReservation();
        _turretReservations[leftRight] = turret;
        bool chamberAlreadyLoaded = false;
        var chambered = gunSys.BulletInChamber();
        var loadedPowder = chambered != null ? gunSys.SelectedPowderCount() : 0;
        MelonLogger.Msg($"[FCS] {leftRight}: task T{task.targetId} wants {task.bulletType}, chamber={chambered ?? "empty"}, actualPowder={loadedPowder}, canFire={gunSys.CanFire()}");
        if (TryParseBulletType(chambered, out var chamberedType)) {
            chamberAlreadyLoaded = true;
            if (!IsCompatibleLoadedTask(task, chamberedType, loadedPowder)) {
                MelonLogger.Warning($"[FCS] {leftRight}: loaded {chamberedType}/{loadedPowder} powder is not efficient for task {task.bulletType}/{RequiredPowder(task)}; searching queued targets.");
                _loadedRoundBlocked.Add(leftRight);
                if (!TryFindTaskForLoadedRound(chamberedType, loadedPowder, out var matched)) {
                    yield return WaitForQueuedTaskForLoadedRound(chamberedType, loadedPowder, 3f, value => matched = value);
                }
                if (matched == null) {
                    if (!BothGunsBlocked()) {
                        MelonLogger.Warning($"[FCS] {leftRight}: no target for loaded {chamberedType}/{loadedPowder} powder, waiting for a new compatible target or the other gun.");
                        yield return WaitForQueuedTaskForLoadedRoundAfterOtherGun(leftRight, chamberedType, loadedPowder, value => matched = value);
                    }
                    if (matched == null) {
                        turret.Canceled = true;
                        RequeueTaskFront(task);
                        var dumpTask = MapTable.GetNearestEdgeDumpTarget(-1, chamberedType);
                        var dumpFired = false;
                        if (dumpTask != null) {
                            yield return FireLoadedRoundAtTask(leftRight, gunSys, turret, dumpTask, loadedPowder, value => dumpFired = value);
                        }
                        else {
                            yield return DumpLoadedRound(leftRight, gunSys, turret, value => dumpFired = value);
                        }
                        if (!dumpFired && gunSys.BulletInChamber() != null) {
                            MelonLogger.Warning($"[FCS] {leftRight}: dump did not fire; keep current slot blocked for loaded round recovery.");
                            MarkProgress(leftRight, Progress.WaitingForFire);
                            yield break;
                        }
                        _loadedRoundBlocked.Remove(leftRight);
                        ReleaseSlot(leftRight);
                        yield break;
                    }
                }
                MelonLogger.Warning($"[FCS] {leftRight}: switched task to match loaded {chamberedType}/{loadedPowder} powder.");
                RequeueTaskFront(task);
                task = matched!;
                task.progress = Progress.Pending;
                ReserveTaskTarget(task);
                if (leftRight == LeftRight.Left) LeftTask = task;
                else RightTask = task;
            }
        }
        else {
            _loadedRoundBlocked.Remove(leftRight);
        }

        if (DropDestroyedTaskBeforeLoading(leftRight, task, "shell purchase")) {
            yield break;
        }

        if (!chamberAlreadyLoaded) {
            task.progress = Progress.SelectingBullet;
            MarkProgress(leftRight, Progress.SelectingBullet);
            if (!gunSys.HaveBulletInCylinder(task.bulletType)) {
                if (!gunSys.HaveEmptyShellInCylinder()) {
                    if (gunSys.TryGetFirstBulletInCylinder(out var fallbackShell)) {
                        MelonLogger.Warning(
                            $"[FCS] {leftRight}: no {task.bulletType} in cylinder and no empty shell slot; " +
                            $"use available {fallbackShell} for T{task.targetId}.");
                        task.bulletType = fallbackShell;
                    }
                    else {
                        MelonLogger.Warning($"[FCS] {leftRight}: no {task.bulletType} in cylinder and no empty shell slot; skip ballistic calculation for T{task.targetId}.");
                        task.progress = Progress.Failed;
                        MarkProgress(leftRight, Progress.Failed);
                        ReleaseTaskTarget(task);
                        ReleaseSlot(leftRight);
                        yield break;
                    }
                }

                yield return _deskLock.Acquire();
                try {
                    if (!gunSys.HaveBulletInCylinder(task.bulletType)) {
                        if (!_purchaseDeck.CanBuyShell(task.bulletType)) {
                            MelonLogger.Error($"[FCS] {leftRight}: no purchase card for {task.bulletType}; use existing shells only.");
                            HaltAutomaticFire($"{leftRight} has no purchase card for {task.bulletType}; existing shells only.");
                            task.progress = Progress.Failed;
                            MarkProgress(leftRight, Progress.Failed);
                            ReleaseTaskTarget(task);
                            ReleaseSlot(leftRight);
                            yield break;
                        }
                        yield return _purchaseDeck.BuyShell(task.bulletType, leftRight);
                        yield return gunSys.WaitUntilBulletInCylinder(task.bulletType, 5f);
                    }
                    if (!gunSys.HaveBulletInCylinder(task.bulletType)) {
                        MelonLogger.Error($"[FCS] {leftRight}: purchased {task.bulletType}, but it did not enter cylinder.");
                        HaltAutomaticFire($"{leftRight} could not buy {task.bulletType}; purchase did not enter cylinder.");
                        task.progress = Progress.Failed;
                        MarkProgress(leftRight, Progress.Failed);
                        ReleaseTaskTarget(task);
                        ReleaseSlot(leftRight);
                        yield break;
                    }
                }
                finally {
                    _deskLock.Release();
                }
            }
        }

        var powderCount = RequiredPowder(task);
        var selectedPowderCount = chamberAlreadyLoaded ? gunSys.SelectedPowderCount() : 0;
        var recalculatedWithLoadedPowder = false;
        MelonLogger.Msg($"[FCS] {leftRight}: target T{task.targetId}, shell={task.bulletType}, requiredPowder={powderCount}, selectedPowder={selectedPowderCount}");
        if (chamberAlreadyLoaded && selectedPowderCount > powderCount) {
            MelonLogger.Warning($"[FCS] {leftRight}: loaded powder {selectedPowderCount} exceeds required {powderCount}; recalculating same target with loaded powder.");
            powderCount = selectedPowderCount;
            recalculatedWithLoadedPowder = true;
        }
        bool alreadyLoaded = gunSys.CanFire();
        if (alreadyLoaded) {
            MelonLogger.Msg($"[FCS] {leftRight}: gun is already ready to fire, skip loading.");
        }

        // 阶段 1：先算出最终射击诸元，再预占共享方向机。
        float elevation = 0f;
        bool viable = true;
        bool resourceBlocked = false;
        bool destroyedBeforeLoad = false;
        ArtilleryTask? dumpAfterDesk = null;
        yield return _deskLock.Acquire();
        try {
            if (!IsTaskAlive(task)) {
                destroyedBeforeLoad = true;
                viable = false;
            }

            if (viable) {
                task.progress = Progress.Calculating;
                MarkProgress(leftRight, Progress.Calculating);
                yield return CalculateBallisticSolution(task, powderCount);
                elevation = BallisticCalculator.GetElevation();
                if (!IsTaskAlive(task)) {
                    destroyedBeforeLoad = true;
                    viable = false;
                }
            }

            if (viable && recalculatedWithLoadedPowder && !IsValidElevation(elevation)) {
                MelonLogger.Warning($"[FCS] {leftRight}: loaded powder cannot solve T{task.targetId}; searching another target.");
                RequeueTaskFront(task);
                if (TryParseBulletType(chambered, out var loadedShell)
                    && TryFindTaskForLoadedRound(loadedShell, selectedPowderCount, out var alternate)
                    && alternate != null) {
                    task = alternate;
                    ReserveTaskTarget(task);
                    if (leftRight == LeftRight.Left) LeftTask = task;
                    else RightTask = task;
                    powderCount = selectedPowderCount;
                    yield return CalculateBallisticSolution(task, powderCount);
                    elevation = BallisticCalculator.GetElevation();
                }
                else if (TryParseBulletType(chambered, out var dumpShell)) {
                    viable = false;
                    dumpAfterDesk = MapTable.GetNearestEdgeDumpTarget(-1, dumpShell);
                }
            }

            // 只有采购后库存确实增加，才继续补买药包。
            while (viable && gunSys.RemainingCharges() < powderCount) {
                if (!IsTaskAlive(task)) {
                    destroyedBeforeLoad = true;
                    viable = false;
                    break;
                }
                var beforePurchase = gunSys.RemainingCharges();
                yield return _purchaseDeck.BuyPowders();
                var afterPurchase = gunSys.RemainingCharges();
                if (!IsTaskAlive(task)) {
                    destroyedBeforeLoad = true;
                    viable = false;
                    break;
                }
                if (afterPurchase <= beforePurchase) {
                    MelonLogger.Error(
                        $"[FCS] {leftRight}: powder purchase did not increase charges. " +
                        $"required={powderCount}, before={beforePurchase}, after={afterPurchase}");

                    var availablePowder = afterPurchase;
                    var loadedOrReservedShell = chamberAlreadyLoaded && TryParseBulletType(chambered, out var loadedShell)
                        ? loadedShell
                        : task.bulletType;

                    if (availablePowder > 0 && (chamberAlreadyLoaded || gunSys.HaveBulletInCylinder(loadedOrReservedShell))) {
                        MelonLogger.Warning(
                            $"[FCS] {leftRight}: no money for full charge; try retarget loaded/reserved {loadedOrReservedShell} " +
                            $"with availablePowder={availablePowder}.");
                        RequeueTaskFront(task);
                        if (TryFindTaskForAvailablePowder(loadedOrReservedShell, availablePowder, out var alternate)
                            && alternate != null) {
                            task = alternate;
                            ReserveTaskTarget(task);
                            if (leftRight == LeftRight.Left) LeftTask = task;
                            else RightTask = task;
                            powderCount = RequiredPowder(task);
                            HaltAutomaticFire($"{leftRight} cannot buy powder; continue current round with {powderCount}/{availablePowder} available charges.");
                            yield return CalculateBallisticSolution(task, powderCount);
                            elevation = BallisticCalculator.GetElevation();
                            MelonLogger.Warning(
                                $"[FCS] {leftRight}: switched to T{task.targetId} for limited powder. " +
                                $"shell={task.bulletType}, requiredPowder={powderCount}, availablePowder={availablePowder}");
                            continue;
                        }

                        HaltAutomaticFire($"{leftRight} cannot buy powder and no target matches {loadedOrReservedShell}/{availablePowder}.");
                    }
                    else {
                        HaltAutomaticFire($"{leftRight} could not buy powder charges.");
                    }

                    task.progress = Progress.Failed;
                    MarkProgress(leftRight, Progress.Failed);
                    if (chamberAlreadyLoaded) {
                        ReserveTaskTarget(task);
                        MarkResourceBlocked(leftRight, task, $"chambered={chambered}, availablePowder={availablePowder}");
                        resourceBlocked = true;
                    }
                    viable = false;
                    break;
                }
            }

            if (viable) {
                task.progress = Progress.SelectingBullet;
                MarkProgress(leftRight, Progress.SelectingBullet);
            }
            if (viable && !chamberAlreadyLoaded && !gunSys.HaveBulletInCylinder(task.bulletType)) {
                if (!IsTaskAlive(task)) {
                    destroyedBeforeLoad = true;
                    viable = false;
                }
                else if (!gunSys.HaveEmptyShellInCylinder()) {
                    task.progress = Progress.Failed;
                    MarkProgress(leftRight, Progress.Failed);
                    viable = false;
                }
                else {
                    if (!_purchaseDeck.CanBuyShell(task.bulletType)) {
                        MelonLogger.Error($"[FCS] {leftRight}: no purchase card for {task.bulletType}; use existing shells only.");
                        HaltAutomaticFire($"{leftRight} has no purchase card for {task.bulletType}; existing shells only.");
                        task.progress = Progress.Failed;
                        MarkProgress(leftRight, Progress.Failed);
                        viable = false;
                    }
                    else {
                        yield return _purchaseDeck.BuyShell(task.bulletType, leftRight);
                        yield return gunSys.WaitUntilBulletInCylinder(task.bulletType, 5f);
                    }
                    if (viable && !IsTaskAlive(task)) {
                        destroyedBeforeLoad = true;
                        viable = false;
                    }
                    if (viable && !gunSys.HaveBulletInCylinder(task.bulletType)) {
                        MelonLogger.Error($"[FCS] {leftRight}: purchased {task.bulletType}, but it did not enter cylinder.");
                        HaltAutomaticFire($"{leftRight} could not buy {task.bulletType}; purchase did not enter cylinder.");
                        task.progress = Progress.Failed;
                        MarkProgress(leftRight, Progress.Failed);
                        viable = false;
                    }
                }
            }
        }
        finally {
            _deskLock.Release();
        }

        if (destroyedBeforeLoad) {
            MelonLogger.Warning($"[FCS] {leftRight}: target T{task.targetId} destroyed during preparation; stop before loading/firing.");
            task.progress = Progress.Failed;
            MarkProgress(leftRight, Progress.Failed);
            FinishTask(task);
            ReleaseTurretOnce(turret);
            ReleaseSlot(leftRight);
            yield break;
        }

        if (dumpAfterDesk != null) {
            var dumpFired = false;
            yield return FireLoadedRoundAtTask(leftRight, gunSys, turret, dumpAfterDesk, selectedPowderCount, value => dumpFired = value);
            if (!dumpFired && gunSys.BulletInChamber() != null) {
                MelonLogger.Warning($"[FCS] {leftRight}: dump did not fire; keep current slot blocked for loaded round recovery.");
                MarkProgress(leftRight, Progress.WaitingForFire);
                yield break;
            }
            _loadedRoundBlocked.Remove(leftRight);
            ReleaseSlot(leftRight);
            yield break;
        }

        if (resourceBlocked) {
            turret.Canceled = true;
            ReleaseTurretOnce(turret);
            yield break;
        }

        if (!viable) {
            turret.Canceled = true;
            ReleaseTurretOnce(turret);
            ReleaseTaskTarget(task);
            ReleaseSlot(leftRight);
            yield break;
        }

        if (StopIfActiveTargetDestroyed(leftRight, gunSys, task, turret, "ballistic calculation")) {
            yield break;
        }

        turret.TargetAngle = task.angel;
        StartTurretReservation(leftRight, task, turret);

        alreadyLoaded = gunSys.CanFire();
        if (alreadyLoaded) {
            MelonLogger.Msg($"[FCS] {leftRight}: gun became ready before loading step, skip loading.");
        }

        // 阶段 2：如果炮位尚未就绪，则装填炮弹和药包。
        if (!alreadyLoaded) {
            MelonLogger.Msg($"[FCS] {leftRight}: loading required. chamberAlreadyLoaded={chamberAlreadyLoaded}, canFire={gunSys.CanFire()}");
            if (!chamberAlreadyLoaded) {
                task.progress = Progress.LoadingBullet;
                MarkProgress(leftRight, Progress.LoadingBullet);
                yield return gunSys.LoadBullet(task.bulletType);
                yield return gunSys.WaitUntilChambered(task.bulletType, 8f);
                if (gunSys.BulletInChamber() != task.bulletType.ToString()) {
                    MelonLogger.Error($"[FCS] {leftRight}: failed to chamber {task.bulletType}, current chamber: {gunSys.BulletInChamber() ?? "empty"}");
                    task.progress = Progress.Failed;
                    MarkProgress(leftRight, Progress.Failed);
                    RequeueTaskFront(task);
                    var dumpFired = false;
                    yield return DumpLoadedRound(leftRight, gunSys, turret, value => dumpFired = value);
                    if (!dumpFired && gunSys.BulletInChamber() != null) {
                        MelonLogger.Warning($"[FCS] {leftRight}: dump did not fire; keep current slot blocked for loaded round recovery.");
                        MarkProgress(leftRight, Progress.WaitingForFire);
                        yield break;
                    }
                    ReleaseSlot(leftRight);
                    yield break;
                }
            }
            
            task.progress = Progress.LoadingPowder;
            MarkProgress(leftRight, Progress.LoadingPowder);
            var currentPowder = chamberAlreadyLoaded ? gunSys.SelectedPowderCount() : 0;
            if (!gunSys.CanFire()) {
                yield return currentPowder > 0
                    ? gunSys.CompletePowderSelectionFrom(currentPowder, powderCount)
                    : gunSys.LoadPowder(powderCount);
            }
            if (gunSys.LastActionFailed) {
                MelonLogger.Warning($"[FCS] {leftRight}: powder load action failed; reread powder display and retry.");
                var recovered = false;
                for (var retry = 1; retry <= PowderRecoveryAttempts && !gunSys.CanFire(); retry++) {
                    var actualPowder = gunSys.SelectedPowderCount();
                    MelonLogger.Warning($"[FCS] {leftRight}: powder recovery {retry}/{PowderRecoveryAttempts}, actualPowder={actualPowder}, targetPowder={powderCount}");
                    if (actualPowder > powderCount) {
                        powderCount = actualPowder;
                        yield return _deskLock.Acquire();
                        try {
                            yield return CalculateBallisticSolution(task, powderCount);
                            elevation = BallisticCalculator.GetElevation();
                        }
                        finally {
                            _deskLock.Release();
                        }
                        if (!IsValidElevation(elevation)) {
                            MelonLogger.Error($"[FCS] {leftRight}: overcharged powder cannot solve target. powder={powderCount}, elevation={elevation:F2}");
                            break;
                        }
                    }

                    if (actualPowder >= powderCount) {
                        yield return gunSys.PushPowder();
                    }
                    else if (actualPowder > 0) {
                        yield return gunSys.CompletePowderSelectionFrom(actualPowder, powderCount);
                    }
                    else {
                        yield return gunSys.LoadPowder(powderCount);
                    }

                    if (!gunSys.LastActionFailed) {
                        recovered = true;
                        break;
                    }
                    yield return new WaitForSeconds(1f);
                }

                if (!recovered && !gunSys.CanFire()) {
                    if (gunSys.BulletInChamber() != null) {
                        MelonLogger.Error($"[FCS] {leftRight}: powder recovery failed with loaded shell; keep slot resource-blocked. {GunState(gunSys)}");
                        CancelTurretReservation(turret);
                        MarkResourceBlocked(leftRight, task, "powder recovery failed with loaded shell");
                        TryDispatch();
                        yield break;
                    }

                    MelonLogger.Error($"[FCS] {leftRight}: powder recovery failed; requeue task and release slot. {GunState(gunSys)}");
                    task.progress = Progress.Failed;
                    MarkProgress(leftRight, Progress.Failed);
                    RequeueTaskFront(task);
                    CancelTurretReservation(turret);
                    ReleaseSlot(leftRight);
                    yield break;
                }
            }
            task.progress = Progress.WaitLoading;
            MarkProgress(leftRight, Progress.WaitLoading);
            turret = MaintainTurretPriorityForLoadedRound(leftRight, task, turret, "during wait loading");
            MelonLogger.Msg($"[FCS] {leftRight}: waiting CanFire after powder load. {GunState(gunSys)}");
            var waitCanFire = 0f;
            var canFireRecoveryTried = false;
            while (!gunSys.CanFire()) {
                if (StopIfActiveTargetDestroyed(leftRight, gunSys, task, turret, "CanFire wait")) {
                    yield break;
                }
                turret = MaintainTurretPriorityForLoadedRound(leftRight, task, turret, "during wait loading");
                if (!Application.isFocused || Time.timeScale <= 0f) {
                    yield return null;
                    continue;
                }
                yield return new WaitForSeconds(0.25f);
                waitCanFire += 0.25f;
                if (waitCanFire >= 8f) {
                    var timeoutChamber = gunSys.BulletInChamber();
                    var timeoutPowder = gunSys.SelectedPowderCount();
                    MelonLogger.Warning($"[FCS] {leftRight}: CanFire timeout. {GunState(gunSys)}");
                    if (timeoutChamber == task.bulletType.ToString() && timeoutPowder >= powderCount) {
                        if (!canFireRecoveryTried) {
                            canFireRecoveryTried = true;
                            MelonLogger.Warning($"[FCS] {leftRight}: loaded state matches but CanFire is false; retry powder push once.");
                            yield return gunSys.CompletePowderSelectionFrom(timeoutPowder, powderCount);
                        }
                        else {
                            MelonLogger.Warning($"[FCS] {leftRight}: loaded state still matches but CanFire is false; park this barrel and free turret. {GunState(gunSys)}");
                            CancelTurretReservation(turret);
                            MarkResourceBlocked(leftRight, task, "loaded state matches but CanFire stayed false");
                            TryDispatch();
                            yield break;
                        }
                        task.progress = Progress.WaitLoading;
                        MarkProgress(leftRight, Progress.WaitLoading);
                        turret = MaintainTurretPriorityForLoadedRound(leftRight, task, turret, "during wait loading recovery");
                        waitCanFire = 0f;
                        continue;
                    }
                    if (!canFireRecoveryTried && timeoutChamber == task.bulletType.ToString()) {
                        canFireRecoveryTried = true;
                        MelonLogger.Warning($"[FCS] {leftRight}: retry powder push before giving up. actualPowder={timeoutPowder}, targetPowder={powderCount}");
                        yield return timeoutPowder > 0
                            ? gunSys.CompletePowderSelectionFrom(timeoutPowder, powderCount)
                            : gunSys.LoadPowder(powderCount);
                        waitCanFire = 0f;
                        continue;
                    }
                    MelonLogger.Error($"[FCS] {leftRight}: CanFire recovery failed; dump loaded round.");
                    task.progress = Progress.Failed;
                    MarkProgress(leftRight, Progress.Failed);
                    RequeueTaskFront(task);
                    CancelTurretReservation(turret);
                    var dumpFired = false;
                    yield return DumpLoadedRound(leftRight, gunSys, turret, value => dumpFired = value);
                    if (!dumpFired && gunSys.BulletInChamber() != null) {
                        MelonLogger.Warning($"[FCS] {leftRight}: dump did not fire; keep current slot blocked for loaded round recovery.");
                        MarkProgress(leftRight, Progress.WaitingForFire);
                        yield break;
                    }
                    ReleaseSlot(leftRight);
                    yield break;
                }
            }
            MelonLogger.Msg($"[FCS] {leftRight}: leaving CanFire wait. canFire={gunSys.CanFire()}");
        }
        _loadedRoundBlocked.Remove(leftRight);

        if (StopIfActiveTargetDestroyed(leftRight, gunSys, task, turret, "before aiming")) {
            yield break;
        }

        // 阶段 3：调整高低机；失焦或暂停打断后继续瞄准同一目标。
        task.progress = Progress.Aiming;
        MarkProgress(leftRight, Progress.Aiming);
        RequestTurretPriorityForActiveGun(leftRight);
        turret = EnsureTurretReservationForTask(leftRight, task, turret, "before aiming");
        MelonLogger.Msg($"[FCS] {leftRight}: aiming elevation={elevation}");
        yield return gunSys.SetElevation(elevation, () => {
            turret = MaintainTurretPriorityForLoadedRound(leftRight, task, turret, "during aiming");
        });
        if (!gunSys.LastElevationReady && gunSys.LastElevationInterrupted) {
            MelonLogger.Msg($"[FCS] {leftRight}: elevation interrupted by focus/pause; resume same target.");
            yield return gunSys.SetElevation(elevation, () => {
                turret = MaintainTurretPriorityForLoadedRound(leftRight, task, turret, "during aiming resume");
            });
        }
        if (!gunSys.LastElevationReady) {
            if (gunSys.BulletInChamber() != null || gunSys.CanFire()) {
                yield return KeepAimingLoadedRound(leftRight, gunSys, task, turret, elevation, "before fire", value => turret = value);
            }
        }
        if (!gunSys.LastElevationReady) {
            if (gunSys.HasFired()) {
                task.progress = Progress.Finished;
                MarkProgress(leftRight, Progress.Finished);
                FinishTask(task);
                ReleaseSlot(leftRight);
                yield break;
            }
            MelonLogger.Warning($"[FCS] {leftRight}: elevation not ready; requeue task before fire.");
            CancelTurretReservation(turret);
            if (gunSys.BulletInChamber() == null && !gunSys.CanFire()) {
                RequeueTaskFront(task);
                ReleaseSlot(leftRight);
            }
            else {
                MelonLogger.Warning($"[FCS] {leftRight}: keep slot blocked because a loaded round is still present.");
                task.progress = Progress.WaitingForFire;
                MarkProgress(leftRight, Progress.WaitingForFire);
            }
            yield break;
        }

        // 阶段 4：保持共享方向机，完成确认台检查并击发。
        if (StopIfActiveTargetDestroyed(leftRight, gunSys, task, turret, "before fire")) {
            yield break;
        }
        task.progress = Progress.WaitingForFire;
        MarkProgress(leftRight, Progress.WaitingForFire);
        RequestTurretPriorityForActiveGun(leftRight);
        MelonLogger.Msg($"[FCS] {leftRight}: waiting turret ready.");
        while (!turret.Ready || !Turret.IsReadyFor(task.angel)) {
            if (CompleteManualFireIfDetected(leftRight, gunSys, task, turret)) {
                yield break;
            }
            RequestTurretPriorityForActiveGun(leftRight);
            if (!Application.isFocused || Time.timeScale <= 0f) {
                yield return null;
                continue;
            }
            if (turret.Canceled || turret.Released || (turret.Ready && !Turret.IsReadyFor(task.angel))) {
                if (ShouldYieldTurretToWaitingShot(leftRight, task)) {
                    yield return null;
                    continue;
                }
                turret = EnsureTurretReservationForTask(leftRight, task, turret, "before fire");
            }
            yield return null;
        }
        if (CompleteManualFireIfDetected(leftRight, gunSys, task, turret)) {
            yield break;
        }
        if (StopIfActiveTargetDestroyed(leftRight, gunSys, task, turret, "fire confirm")) {
            yield break;
        }
        MelonLogger.Msg($"[FCS] {leftRight}: turret ready, confirming fire.");
        yield return gunSys.SetElevation(elevation, () => {
            turret = MaintainTurretPriorityForLoadedRound(leftRight, task, turret, "during fire check");
        });
        if (CompleteManualFireIfDetected(leftRight, gunSys, task, turret)) {
            yield break;
        }
        if (!gunSys.LastElevationReady && gunSys.LastElevationInterrupted) {
            MelonLogger.Msg($"[FCS] {leftRight}: elevation fire-check interrupted by focus/pause; resume same target.");
            yield return gunSys.SetElevation(elevation, () => {
                turret = MaintainTurretPriorityForLoadedRound(leftRight, task, turret, "during fire-check resume");
            });
        }
        if (CompleteManualFireIfDetected(leftRight, gunSys, task, turret)) {
            yield break;
        }
        if (!gunSys.LastElevationReady) {
            if (gunSys.BulletInChamber() != null || gunSys.CanFire()) {
                yield return KeepAimingLoadedRound(leftRight, gunSys, task, turret, elevation, "at fire check", value => turret = value);
            }
        }
        if (CompleteManualFireIfDetected(leftRight, gunSys, task, turret)) {
            yield break;
        }
        if (!gunSys.LastElevationReady) {
            if (gunSys.HasFired()) {
                task.progress = Progress.Finished;
                MarkProgress(leftRight, Progress.Finished);
                FinishTask(task);
                ReleaseSlot(leftRight);
                yield break;
            }
            MelonLogger.Warning($"[FCS] {leftRight}: elevation not ready at fire check; requeue task.");
            CancelTurretReservation(turret);
            if (gunSys.BulletInChamber() == null && !gunSys.CanFire()) {
                RequeueTaskFront(task);
                ReleaseSlot(leftRight);
            }
            else {
                MelonLogger.Warning($"[FCS] {leftRight}: keep slot blocked because a loaded round is still present.");
                task.progress = Progress.WaitingForFire;
                MarkProgress(leftRight, Progress.WaitingForFire);
            }
            yield break;
        }
        var fireDetected = false;
        yield return _deskLock.Acquire();
        try {
            yield return ConfirmArmAndFire(leftRight, gunSys, allowManualFire: true, value => fireDetected = value);
        }
        finally {
            _deskLock.Release();
            ReleaseTurretOnce(turret);
        }

        if (!fireDetected) {
            MelonLogger.Warning($"[FCS] {leftRight}: fire not detected; requeue task.");
            task.progress = Progress.Failed;
            MarkProgress(leftRight, Progress.Failed);
            RequeueTaskFront(task);
            ReleaseSlot(leftRight);
            yield break;
        }
        MelonLogger.Msg($"[FCS] {leftRight}: fire detected.");

        // 阶段 5：后坐/复位开始后释放炮位。
        task.progress = Progress.BackToIdle;
        MarkProgress(leftRight, Progress.BackToIdle);
        // 复位开始后即可准备下一发。
        ReleaseSlot(leftRight);
        yield return gunSys.WaitBackToIdle();
        task.progress = Progress.Finished;
        MarkProgress(leftRight, Progress.Finished);
        FinishTask(task);
    }

    /// <summary>记录一发炮对共享方向机的占用状态。</summary>
    private sealed class TurretReservation {
        public float TargetAngle;
        public bool Acquired;  // 已拿到方向机锁。
        public bool Ready;     // 已转到该任务方向。
        public bool Canceled;  // 主流程已放弃本次预占。
        public bool Released;
    }

    /// <summary>
    /// 预占共享方向机并转到该任务方向；已装弹并进入高低机瞄准/待击发阶段的炮拥有优先级。
    /// </summary>
    private void StartTurretReservation(LeftRight leftRight, ArtilleryTask task, TurretReservation res) {
        res.TargetAngle = task.angel;
        var handle = MelonCoroutines.Start(ReserveTurretAndRotate(leftRight, task, res));
        if (handle != null) {
            _runningCoroutines.Add((handle, leftRight));
        }
    }

    private IEnumerator ReserveTurretAndRotate(LeftRight leftRight, ArtilleryTask task, TurretReservation res) {
        try {
            var yieldedToWaitingShot = false;
            while (ShouldYieldTurretToWaitingShot(leftRight, task)) {
                if (res.Canceled) {
                    ReleaseTurretOnce(res);
                    yield break;
                }
                if (!yieldedToWaitingShot) {
                    MelonLogger.Msg($"[FCS] {leftRight}: yield turret to active loaded gun.");
                    yieldedToWaitingShot = true;
                }
                yield return null;
            }
            var heldForAssignedShot = false;
            while (ShouldHoldPreRotationForAssignedShot(leftRight, task)) {
                if (res.Canceled) {
                    ReleaseTurretOnce(res);
                    yield break;
                }
                if (!heldForAssignedShot) {
                    var other = leftRight == LeftRight.Left ? LeftRight.Right : LeftRight.Left;
                    var otherTask = other == LeftRight.Left ? LeftTask : RightTask;
                    MelonLogger.Msg(
                        $"[FCS] {leftRight}: hold pre-rotation T{task.targetId} angle={task.angel:F1}; " +
                        $"{other} assigned T{otherTask?.targetId} angle={otherTask?.angel:F1}, current={Turret.CurrentMapAngle?.ToString("F1") ?? "unknown"}.");
                    heldForAssignedShot = true;
                }
                yield return null;
            }
            while (!_turretLock.TryAcquire()) {
                if (res.Canceled) {
                    ReleaseTurretOnce(res);
                    yield break;
                }
                yield return null;
            }
            res.Acquired = true;
            if (res.Canceled) {
                ReleaseTurretOnce(res);
                yield break;
            }
            yield return Turret.SetRotation(task.angel, () => res.Canceled);
            if (res.Canceled) {
                ReleaseTurretOnce(res);
                yield break;
            }
            if (!Turret.LastRotationReady) {
                MelonLogger.Warning($"[FCS] {leftRight}: turret rotation not ready for T{task.targetId}.");
                ReleaseTurretOnce(res);
                yield break;
            }
            res.Ready = true;
        }
        finally {
            if (res.Canceled) {
                ReleaseTurretOnce(res);
            }
        }
    }

    private TurretReservation EnsureTurretReservationForTask(LeftRight leftRight, ArtilleryTask task, TurretReservation turret, string stage) {
        var hasCurrent = _turretReservations.TryGetValue(leftRight, out var currentReservation);
        var stale = !hasCurrent
                    || !ReferenceEquals(currentReservation, turret)
                    || turret.Canceled
                    || turret.Released
                    || (turret.Ready && !Turret.IsReadyFor(task.angel));
        if (!stale) {
            return turret;
        }

        MelonLogger.Warning($"[FCS] {leftRight}: turret reservation is stale {stage}; restart rotation. target={task.angel:F2}, current={Turret.CurrentMapAngle?.ToString("F2") ?? "unknown"}");
        CancelTurretReservation(turret);
        turret = new TurretReservation { TargetAngle = task.angel };
        _turretReservations[leftRight] = turret;
        StartTurretReservation(leftRight, task, turret);
        return turret;
    }

    private bool ShouldHoldPreRotationForAssignedShot(LeftRight candidate, ArtilleryTask candidateTask) {
        if (GetTurretPriorityStage(candidateTask) > 0) {
            return false;
        }

        var other = candidate == LeftRight.Left ? LeftRight.Right : LeftRight.Left;
        var otherTask = other == LeftRight.Left ? LeftTask : RightTask;
        if (!IsAssignedShotWaitingForTurret(otherTask)) {
            return false;
        }

        var current = Turret.CurrentMapAngle;
        if (!current.HasValue) {
            return false;
        }

        return IsAngleBetweenCurrentAndCandidate(current.Value, otherTask!.angel, candidateTask.angel);
    }

    private static bool IsAssignedShotWaitingForTurret(ArtilleryTask? task) {
        return task?.progress is Progress.Calculating
            or Progress.SelectingBullet
            or Progress.LoadingBullet
            or Progress.LoadingPowder
            or Progress.WaitLoading
            or Progress.Aiming
            or Progress.WaitingForFire;
    }

    private static bool IsAngleBetweenCurrentAndCandidate(float currentAngle, float blockingAngle, float candidateAngle) {
        var candidateDelta = Mathf.DeltaAngle(NormalizeAngle(currentAngle), NormalizeAngle(candidateAngle));
        var blockingDelta = Mathf.DeltaAngle(NormalizeAngle(currentAngle), NormalizeAngle(blockingAngle));
        if (Mathf.Abs(candidateDelta) <= TurretPreRotationHoldTolerance) {
            return false;
        }
        if (Mathf.Abs(blockingDelta) <= TurretPreRotationHoldTolerance) {
            return true;
        }
        if (Mathf.Sign(candidateDelta) != Mathf.Sign(blockingDelta)) {
            return false;
        }
        return Mathf.Abs(blockingDelta) + TurretPreRotationHoldTolerance < Mathf.Abs(candidateDelta);
    }

    private static float NormalizeAngle(float angle) {
        angle %= 360f;
        if (angle < 0f) angle += 360f;
        return angle;
    }

    /// <summary>方向机调度：已装弹待发炮只在近角可抢时打断当前占用；远角保持当前方向机链条。</summary>
    private bool ShouldYieldTurretToWaitingShot(LeftRight leftRight, ArtilleryTask task) {
        var other = leftRight == LeftRight.Left ? LeftRight.Right : LeftRight.Left;
        var otherTask = other == LeftRight.Left ? LeftTask : RightTask;
        if (GetTurretPriorityStage(otherTask) <= 0) {
            return false;
        }

        return ShouldActiveGunTakeTurretPriority(other, otherTask!, leftRight, task);
    }

    private void RequestTurretPriorityForActiveGun(LeftRight leftRight) {
        var selfTask = leftRight == LeftRight.Left ? LeftTask : RightTask;
        if (GetTurretPriorityStage(selfTask) <= 0) return;

        var other = leftRight == LeftRight.Left ? LeftRight.Right : LeftRight.Left;
        var otherTask = other == LeftRight.Left ? LeftTask : RightTask;
        if (!ShouldActiveGunTakeTurretPriority(leftRight, selfTask!, other, otherTask)) return;

        if (!_turretReservations.TryGetValue(other, out var reservation)) {
            return;
        }
        if (reservation.Canceled || reservation.Released) {
            return;
        }
        MelonLogger.Msg($"[FCS] {leftRight}: request turret priority; cancel {other} reservation.");
        CancelTurretReservation(reservation);
    }

    private bool ShouldActiveGunTakeTurretPriority(LeftRight candidate, ArtilleryTask candidateTask, LeftRight incumbent, ArtilleryTask? incumbentTask) {
        var candidateStage = GetTurretPriorityStage(candidateTask);
        if (candidateStage <= 0) {
            return false;
        }

        var incumbentStage = GetTurretPriorityStage(incumbentTask);
        var incumbentHoldingTurret = IsTurretReservationHolding(incumbent);
        var candidateDelta = TurretDeltaForPriority(candidateTask);
        var incumbentDelta = TurretDeltaForPriority(incumbentTask);

        if (incumbentStage <= 0) {
            return !incumbentHoldingTurret || IsNearTurretAngle(candidateDelta);
        }

        if (incumbentHoldingTurret && IsSameTurretDirection(candidateTask, incumbentTask)) {
            return false;
        }

        if (candidateDelta.HasValue && incumbentDelta.HasValue) {
            var candidateNear = IsNearTurretAngle(candidateDelta);
            var incumbentNear = IsNearTurretAngle(incumbentDelta);

            if (candidateNear && !incumbentNear) {
                return true;
            }
            if (!candidateNear && incumbentNear) {
                return false;
            }
            if (candidateNear && incumbentNear) {
                if (candidateDelta.Value + TurretPriorityAngleTolerance < incumbentDelta.Value) {
                    return true;
                }
                if (incumbentDelta.Value + TurretPriorityAngleTolerance < candidateDelta.Value) {
                    return false;
                }
            }
            else if (incumbentHoldingTurret) {
                return false;
            }
        }

        if (candidateStage != incumbentStage) {
            return candidateStage > incumbentStage;
        }
        return HasTurretPriorityTime(candidate, incumbent);
    }

    private float? TurretDeltaForPriority(ArtilleryTask? task) {
        return task == null ? null : Turret.DeltaFromCurrent(task.angel);
    }

    private static bool IsSameTurretDirection(ArtilleryTask candidateTask, ArtilleryTask? incumbentTask) {
        if (incumbentTask == null) {
            return false;
        }
        return Mathf.Abs(Mathf.DeltaAngle(candidateTask.angel, incumbentTask.angel)) <= TurretSameDirectionPriorityTolerance;
    }

    private static bool IsNearTurretAngle(float? delta) {
        return delta.HasValue && delta.Value < TurretTakeoverAngleLimit;
    }

    private bool IsTurretReservationHolding(LeftRight leftRight) {
        return _turretReservations.TryGetValue(leftRight, out var reservation)
               && reservation.Acquired
               && !reservation.Canceled
               && !reservation.Released;
    }

    private static int GetTurretPriorityStage(ArtilleryTask? task) {
        return task?.progress switch {
            Progress.WaitingForFire => 3,
            Progress.Aiming => 2,
            Progress.WaitLoading => 1,
            _ => 0,
        };
    }

    private bool RecoverWaitingForFireTimeout(LeftRight leftRight) {
        var task = leftRight == LeftRight.Left ? LeftTask : RightTask;
        if (task?.progress != Progress.WaitingForFire) {
            return false;
        }

        MelonLogger.Warning($"[FCS] {leftRight}: WaitingForFire timeout; keep loaded round and recover turret priority.");
        if (_turretReservations.TryGetValue(leftRight, out var reservation) && !reservation.Ready) {
            CancelTurretReservation(reservation);
        }
        RequestTurretPriorityForActiveGun(leftRight);
        MarkProgress(leftRight, Progress.WaitingForFire);
        return true;
    }

    private bool HasTurretPriorityTime(LeftRight leftRight, LeftRight other) {
        var selfTime = leftRight == LeftRight.Left ? _leftProgressTime : _rightProgressTime;
        var otherTime = other == LeftRight.Left ? _leftProgressTime : _rightProgressTime;
        if (selfTime <= 0 || otherTime <= 0) {
            return leftRight == LeftRight.Left;
        }
        return selfTime <= otherTime;
    }

    private void CancelTurretReservation(TurretReservation res) {
        res.Canceled = true;
        if (res.Acquired) {
            ReleaseTurretOnce(res);
        }
        else {
            foreach (var item in _turretReservations.Where(item => ReferenceEquals(item.Value, res)).ToList()) {
                _turretReservations.Remove(item.Key);
            }
        }
    }

    /// <summary>方向机锁只释放一次，并同步删除对应预占记录。</summary>
    private void ReleaseTurretOnce(TurretReservation res) {
        if (res.Acquired && !res.Released) {
            res.Released = true;
            _turretLock.Release();
        }
        foreach (var item in _turretReservations.Where(item => ReferenceEquals(item.Value, res)).ToList()) {
            _turretReservations.Remove(item.Key);
        }
    }
}
