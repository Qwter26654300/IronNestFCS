using UnityEngine;

using Il2Cpp;

namespace IronNestFCS.Logic.FCS;

public enum Progress {
    Pending,
    Calculating,
    SelectingBullet,
    LoadingBullet,
    LoadingPowder,
    WaitLoading,
    Aiming,
    WaitingForFire,
    ResourceBlocked,
    BackToIdle,
    Finished,
    Failed,
}

public class ArtilleryTask {
    public int targetId;
    public float angel;
    public float distance;
    public Vector3 position;
    public EntityLocation? location;
    public int targetTypeDialValue;
    public bool preserveAimPoint;
    public BulletType bulletType;
    public Progress progress;
    public bool manualPriority;
}
