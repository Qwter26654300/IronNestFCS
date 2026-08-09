using System;
using System.Reflection;
using Il2Cpp;

namespace IronNestFCS.Logic;

internal static class TargetPriority {
    private const int RoleArtillery = 128;
    private const int RoleFortification = 65536;
    private const int RoleTank = 262144;
    private const int RoleAlly = 2;
    private const int RoleEnemy = 1;
    private const int RoleTarget = 32;

    /// <summary>返回优先值：4=★≥3/FDC/火炮，3=★≥1/装甲，2=Hostile/Target，1=其余，0=友方。</summary>
    public static int GetPriority(EntityLocation? loc) {
        if (loc == null) return 1;
        try {
            if (!TryGetEntity(loc, out var entity, out var entType)) return 1;

            var roleVal = ReadRole(entity, entType);
            var stars = ReadStars(entity, entType);
            var name = loc.gameObject != null ? loc.gameObject.name.ToLowerInvariant() : "";
            var icon = ReadStringProperty(entity, entType, "Icon")?.ToLowerInvariant();
            var text = $"{name} {icon}";
            var isFdc = icon?.Contains("fire direction") == true;

            if (roleVal >= 0) {
                var hasAlly = (roleVal & RoleAlly) != 0;
                var hasEnemy = (roleVal & RoleEnemy) != 0;
                if (hasAlly && !hasEnemy) return 0;

                if (IsTrainStation(text)) return 4;
                if (stars >= 3) return 4;
                if (isFdc) return 4;
                if ((roleVal & RoleArtillery) != 0) return 4;

                if (stars >= 1) return 3;
                if ((roleVal & RoleEnemy) != 0 || (roleVal & RoleTarget) != 0) {
                    var armored = (roleVal & RoleFortification) != 0 || (roleVal & RoleTank) != 0;
                    return armored ? 3 : 2;
                }
            }

            if (icon?.Contains("enemy") == true) return 2;
        }
        catch { }
        return 1;
    }

    private static bool IsTrainStation(string text) {
        return text.Contains("train_station")
               || text.Contains("train station")
               || text.Contains("target station")
               || text.Contains("terminal");
    }

    public static int GetStars(EntityLocation? loc) {
        if (loc == null) return 0;
        try {
            return TryGetEntity(loc, out var entity, out var entType)
                ? ReadStars(entity, entType)
                : 0;
        }
        catch {
            return 0;
        }
    }

    private static bool TryGetEntity(EntityLocation loc, out object entity, out Type entType) {
        entity = null!;
        entType = null!;
        var entityProp = loc.GetType().GetProperty("Entity", BindingFlags.Public | BindingFlags.Instance);
        if (entityProp == null) return false;
        var value = entityProp.GetValue(loc);
        if (value == null) return false;
        entity = value;
        entType = value.GetType();
        return true;
    }

    private static int ReadRole(object entity, Type entType) {
        var roleProp = entType.GetProperty("Role", BindingFlags.Public | BindingFlags.Instance);
        if (roleProp == null) return -1;
        var value = roleProp.GetValue(entity);
        if (value is int i) return i;
        if (value is Enum e) return Convert.ToInt32(e);
        return -1;
    }

    private static int ReadStars(object entity, Type entType) {
        var starsProp = entType.GetProperty("Stars", BindingFlags.Public | BindingFlags.Instance);
        if (starsProp == null) return 0;
        var value = starsProp.GetValue(entity);
        return value is int i ? i : 0;
    }

    private static string? ReadStringProperty(object entity, Type entType, string name) {
        var prop = entType.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        return prop?.GetValue(entity) as string;
    }
}
