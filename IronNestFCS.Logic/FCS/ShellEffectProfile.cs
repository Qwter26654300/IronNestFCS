namespace IronNestFCS.Logic.FCS;

/// <summary>
/// 炮弹效果配置集中表。
/// ImpactRadiusKm / CreditCost / Damage 优先按 Iron Nest DB 记录；
/// 后续如果 DB 或实测有变化，先改这里，避免把数字散落到调度逻辑里。
/// </summary>
public sealed record ShellEffectProfile(
    BulletType Type,
    float? ImpactRadiusKm,
    int? CreditCost,
    float? Damage,
    bool PreferCluster,
    int MinClusterTargets = 2);

public static class ShellEffectProfiles
{
    private static readonly Dictionary<BulletType, ShellEffectProfile> Profiles = new()
    {
        // 数据摘自 https://ironnestdb.com/ammo；Impact radius 按网站数值直接使用。
        [BulletType.AP] = new(BulletType.AP, ImpactRadiusKm: 0.15f, CreditCost: 10, Damage: 2, PreferCluster: true),
        [BulletType.APHE] = new(BulletType.APHE, ImpactRadiusKm: 0.25f, CreditCost: 15, Damage: 2, PreferCluster: true),
        [BulletType.ATMC] = new(BulletType.ATMC, ImpactRadiusKm: 3.00f, CreditCost: 666, Damage: 2, PreferCluster: false),
        [BulletType.CLMN] = new(BulletType.CLMN, ImpactRadiusKm: 0.50f, CreditCost: 17, Damage: 1, PreferCluster: true),
        [BulletType.CYAN] = new(BulletType.CYAN, ImpactRadiusKm: 0.75f, CreditCost: 28, Damage: 1, PreferCluster: true),
        [BulletType.DRIL] = new(BulletType.DRIL, ImpactRadiusKm: 0.07f, CreditCost: 3, Damage: 1, PreferCluster: false),
        [BulletType.EQKE] = new(BulletType.EQKE, ImpactRadiusKm: 0.55f, CreditCost: 26, Damage: 2, PreferCluster: true),
        [BulletType.FLCH] = new(BulletType.FLCH, ImpactRadiusKm: 0.62f, CreditCost: 20, Damage: 1, PreferCluster: true),
        [BulletType.HCHE] = new(BulletType.HCHE, ImpactRadiusKm: 0.55f, CreditCost: 18, Damage: 1, PreferCluster: true),
        [BulletType.HE] = new(BulletType.HE, ImpactRadiusKm: 0.25f, CreditCost: 10, Damage: 1, PreferCluster: true),
        [BulletType.INCN] = new(BulletType.INCN, ImpactRadiusKm: 0.25f, CreditCost: 12, Damage: 1, PreferCluster: true),
        [BulletType.LE] = new(BulletType.LE, ImpactRadiusKm: 0.15f, CreditCost: 8, Damage: 1, PreferCluster: true),
        [BulletType.PLCM] = new(BulletType.PLCM, ImpactRadiusKm: 0.15f, CreditCost: 15, Damage: 1, PreferCluster: false),
        [BulletType.PHGN] = new(BulletType.PHGN, ImpactRadiusKm: 0.62f, CreditCost: 10, Damage: 1, PreferCluster: true),
        [BulletType.PRPG] = new(BulletType.PRPG, ImpactRadiusKm: 0.50f, CreditCost: 7, Damage: 1, PreferCluster: true),
        [BulletType.SMK] = new(BulletType.SMK, ImpactRadiusKm: 1.00f, CreditCost: 2, Damage: 1, PreferCluster: false),
        [BulletType.STAR] = new(BulletType.STAR, ImpactRadiusKm: 0.50f, CreditCost: 2, Damage: 0, PreferCluster: false),
        [BulletType.TEAR] = new(BulletType.TEAR, ImpactRadiusKm: 0.75f, CreditCost: 8, Damage: 0, PreferCluster: false),
        [BulletType.THRM] = new(BulletType.THRM, ImpactRadiusKm: 0.35f, CreditCost: 22, Damage: 1, PreferCluster: true),
        [BulletType.WP] = new(BulletType.WP, ImpactRadiusKm: 0.75f, CreditCost: 10, Damage: 0, PreferCluster: false),
    };

    public static ShellEffectProfile Get(BulletType type)
    {
        return Profiles.TryGetValue(type, out var profile)
            ? profile
            : new ShellEffectProfile(type, ImpactRadiusKm: null, CreditCost: null, Damage: null, PreferCluster: false);
    }

    public static bool TryGetImpactRadius(BulletType type, out float radiusKm)
    {
        var radius = Get(type).ImpactRadiusKm;
        radiusKm = radius ?? 0f;
        return radius.HasValue && radius.Value > 0f;
    }

    public static float ImpactRadiusOrDefault(BulletType type, float defaultRadiusKm)
    {
        return TryGetImpactRadius(type, out var radiusKm) ? radiusKm : defaultRadiusKm;
    }

    public static int CostOrDefault(BulletType type, int defaultCost = 999)
    {
        return Get(type).CreditCost ?? defaultCost;
    }

    public static float CoverageScore(BulletType type, int coveredTargets, int totalPriority = 0)
    {
        var cost = MathF.Max(1f, CostOrDefault(type));
        return (coveredTargets * 100f + totalPriority * 10f) / cost;
    }
}
