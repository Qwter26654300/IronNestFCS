using System;
using System.Reflection;
using Il2Cpp;

namespace IronNestFCS.Logic.FCS;

internal static class TargetTypeMapper
{
    public const int DefaultTarget = 0;

    public static int FromLocation(EntityLocation? location)
    {
        if (location == null)
        {
            return DefaultTarget;
        }

        var name = location.gameObject != null ? location.gameObject.name : "";
        var icon = "";
        var role = "";
        var roleNum = -1;

        try
        {
            var entityProp = location.GetType().GetProperty("Entity", BindingFlags.Public | BindingFlags.Instance);
            var entity = entityProp?.GetValue(location);
            if (entity != null)
            {
                var entType = entity.GetType();
                icon = ReadString(entity, entType, "Icon") ?? "";
                role = ReadValue(entity, entType, "Role") ?? "";
                roleNum = ReadInt(entity, entType, "Role");
            }
        }
        catch
        {
            // Fallback to object name.
        }

        return FromEntityText(name, icon, role, roleNum);
    }

    public static int FromEntityText(string name, string icon = "", string role = "", int roleNum = -1)
    {
        var text = $"{name} {icon} {role}".ToLowerInvariant();

        if (text.Contains("hospital") || text.Contains("medical")) return 7;
        if (text.Contains("civ_riot") || text.Contains("riot")) return 9;
        if (text.Contains("target station") || text.Contains("terminal") || text.Contains("train_station") || text.Contains("train station"))
            return DefaultTarget;
        if (IsLetteredTrainCarName(name)) return 10;
        if (text.Contains("train") || text.Contains("flatcar") || text.Contains("flatbed") || text.Contains("railcar")
            || text.Contains("locomotive") || text.Contains("freight") || text.Contains("wagon")
            || text.Contains("列车") || text.Contains("平板车") || text.Contains("火车头")) return 10;
        if (text.Contains("ammunition") || text.Contains("ammo") || text.Contains("supply") || text.Contains("cache")) return 6;
        if (text.Contains("fire direction") || text.Contains("fdc") || text.Contains("artillery commander")) return 3;
        if (text.Contains("enemycommander")
            || text.Contains("enemy commander")
            || text.Contains("command post")
            || text.Contains("command center")
            || text.Contains("headquarter")
            || text.Contains("hq")
            || text.Contains("commander")) return 12;
        if (text.Contains("artillery")) return 2;
        if (text.Contains("bunker") || text.Contains("fort") || text.Contains("fortification") || text.Contains("pillbox")) return 4;
        if (text.Contains("recon") || text.Contains("spotter") || text.Contains("scout")) return 11;
        if (text.Contains("infantry")) return 1;

        if (roleNum >= 0)
        {
            const int roleArtillery = 128;
            const int roleInfantry = 131072;
            const int roleFortification = 65536;
            const int roleTank = 262144;

            if ((roleNum & roleArtillery) != 0) return 2;
            if ((roleNum & roleInfantry) != 0) return 1;
            if ((roleNum & roleTank) != 0) return 8;
            if ((roleNum & roleFortification) != 0) return 4;
        }

        return DefaultTarget;
    }

    private static string? ReadString(object entity, Type entType, string name)
    {
        return ReadValue(entity, entType, name) as string;
    }

    private static object? ReadRawValue(object entity, Type entType, string name)
    {
        var prop = entType.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (prop == null) return null;
        return prop.GetValue(entity);
    }

    private static string? ReadValue(object entity, Type entType, string name)
    {
        return ReadRawValue(entity, entType, name)?.ToString();
    }

    private static int ReadInt(object entity, Type entType, string name)
    {
        var value = ReadRawValue(entity, entType, name);
        if (value is int i) return i;
        if (value is Enum e) return Convert.ToInt32(e);
        return -1;
    }

    private static bool IsLetteredTrainCarName(string name)
    {
        var key = name.Split('#')[0].Trim().ToLowerInvariant();
        return key.Length == 4
               && key.StartsWith("car")
               && key[3] >= 'a'
               && key[3] <= 'z';
    }
}
