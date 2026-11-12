using System;
using System.Collections.Generic;
using System.Linq;
using DiceModders.ModCore;

namespace DiceModders.LobbyRules;

using Core = DiceModders.ModCore.ModCore;
using GameBuildingTypeName = DiceKingdoms.Game.BuildingTypeName;
using GameTechnologyName = DiceKingdoms.Game.TechnologyName;
using GameUnitType = DiceKingdoms.Game.Unit.Type;

/// <summary>
/// The unit, building and technology values the host changed in the Game values window. Only the changed values are kept, as
/// "u.civ.unit.Stat=v", "b.building.Field=v" and "t.technology.Resource=v" (civ -1 = every civilization), in ONE text lobby rule, so the host's
/// values reach every player with the lobby data, are frozen at match start, and ModCore drops them for everybody when a player lacks the mod.
/// Every change of the rule is applied through ModCore's unit stats and balance (their own switches are lobby rules too).
/// </summary>
internal static class GameValues
{
    internal static readonly string[] UnitStats = { "AttackPeriod", "AttackDelay", "AggroDistance", "Range", "Parry", "BlockArrow", "MissChance" };
    internal static readonly string[] Percent = { "Parry", "BlockArrow", "MissChance" };   // chances, edited as whole percents
    internal static readonly string[] Resources = { "Food", "Wood", "Stone", "Gold", "Hammer", "Culture", "Military", "Disaster" };

    /// <summary>Raised on every player when the host's values change (also after a match starts or ends).</summary>
    internal static event Action Changed;

    private static LobbyRule<string> _rule;
    private static UnitStatsMod _units;
    private static BalanceMod _balance;
    private static readonly SortedDictionary<string, int> Values = new(StringComparer.Ordinal);

    internal static bool Editable => _rule.Editable;
    internal static int Count => Values.Count;

    internal static void Install()
    {
        _units = Core.Units.Register("lobbyrules.units", "Edited unit values", 0, "The unit values the host set in the Game values window.");
        _balance = Core.Balance.Register("lobbyrules.balance", "Edited building values", 0, "The building and technology values the host set in the Game values window.");
        _rule = Core.Lobby.Rules.Text("lobbyrules.values", "Game values", "", null, () => false);
        _rule.Changed += _ => Load();
        Load();
    }

    internal static int? Get(string key) => Values.TryGetValue(key, out var v) ? v : null;

    /// <summary>Host: sets (or with null removes) one value. Every player receives it through the lobby.</summary>
    internal static bool Set(string key, int? value)
    {
        if (!Editable) return false;
        var next = new SortedDictionary<string, int>(Values, StringComparer.Ordinal);
        if (value == null) next.Remove(key); else next[key] = value.Value;
        return _rule.TrySet(Encode(next));
    }

    internal static bool ResetAll() => Editable && _rule.TrySet("");

    private static string Encode(SortedDictionary<string, int> values) => string.Join(";", values.Select(kv => kv.Key + "=" + kv.Value));

    private static void Load()
    {
        Values.Clear();
        foreach (var part in (_rule.Value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int i = part.IndexOf('=');
            if (i > 0 && int.TryParse(part.Substring(i + 1), out int v)) Values[part.Substring(0, i)] = v;
        }
        try { Apply(); }
        catch (Exception e) { Plugin.L.LogError("game values: " + e); }
        Changed?.Invoke();
    }

    /// <summary>The kept values as ModCore unit-stat and balance changes.</summary>
    private static void Apply()
    {
        _units.ClearAll();
        _balance.ClearAll();
        var units = new Dictionary<(int civ, int unit), UnitStatChange>();
        var buildings = new Dictionary<int, BuildingChange>();
        var techs = new Dictionary<int, ResourceChange>();
        foreach (var (key, v) in Values)
        {
            var p = key.Split('.');
            if (p[0] == "u" && p.Length == 4 && int.TryParse(p[1], out int civ) && int.TryParse(p[2], out int unit))
            {
                if (civ >= 0 && (civ = Core.Civilizations.FromLobbyNumber(civ)) < 0) continue;   // the host's number -> ours; a civilization we lack is skipped
                if (!units.TryGetValue((civ, unit), out var c)) units[(civ, unit)] = c = new UnitStatChange();
                SetUnit(c, p[3], v);
            }
            else if (p[0] == "b" && p.Length == 3 && int.TryParse(p[1], out int b))
            {
                if (!buildings.TryGetValue(b, out var c)) buildings[b] = c = new BuildingChange();
                switch (p[2])
                {
                    case "Health": c.Health = v; break;
                    case "TowerRange": c.TowerRange = v; break;
                    case "TowerPeriod": c.TowerPeriod = v; break;
                    default: SetResource(c.Cost ??= new ResourceChange(), p[2], v); break;
                }
            }
            else if (p[0] == "t" && p.Length == 3 && int.TryParse(p[1], out int t))
            {
                if (!techs.TryGetValue(t, out var c)) techs[t] = c = new ResourceChange();
                SetResource(c, p[2], v);
            }
        }
        foreach (var ((civ, unit), c) in units)
        {
            if (civ < 0) _units.Set((GameUnitType)unit, c);
            else _units.Set(civ, (GameUnitType)unit, c);
        }
        foreach (var (b, c) in buildings) _balance.Set((GameBuildingTypeName)b, c);
        foreach (var (t, c) in techs) _balance.SetCost((GameTechnologyName)t, c);
    }

    private static void SetUnit(UnitStatChange c, string stat, int v)
    {
        switch (stat)
        {
            case "AttackPeriod": c.AttackPeriod = v; break;
            case "AttackDelay": c.AttackDelay = v; break;
            case "AggroDistance": c.AggroDistance = v; break;
            case "Range": c.Range = v; break;
            case "Parry": c.Parry = new Chance(v, 100); break;
            case "BlockArrow": c.BlockArrow = new Chance(v, 100); break;
            case "MissChance": c.MissChance = new Chance(v, 100); break;
        }
    }

    private static void SetResource(ResourceChange c, string name, int v)
    {
        switch (name)
        {
            case "Food": c.Food = v; break;
            case "Wood": c.Wood = v; break;
            case "Stone": c.Stone = v; break;
            case "Gold": c.Gold = v; break;
            case "Hammer": c.Hammer = v; break;
            case "Culture": c.Culture = v; break;
            case "Military": c.Military = v; break;
            case "Disaster": c.Disaster = v; break;
        }
    }

    // ---- the game's own values, for the window ---------------------------------------------------------------------------------------

    internal static int Percentage(Chance c) => c.Denominator == 0 ? 0 : (int)Math.Round(100.0 * c.Numerator / c.Denominator);

    /// <summary>A unit stat before this mod: the game's value, with a mod civilization's own defaults on top.</summary>
    internal static int UnitBase(int civ, int unit, string stat)
    {
        var g = Core.Units.Default((GameUnitType)unit);
        var own = civ >= 0 ? Core.Units.CivilizationDefaults(civ, (GameUnitType)unit) : null;
        return stat switch
        {
            "AttackPeriod" => own?.AttackPeriod ?? g.AttackPeriod,
            "AttackDelay" => own?.AttackDelay ?? g.AttackDelay,
            "AggroDistance" => own?.AggroDistance ?? g.AggroDistance,
            "Range" => own?.Range ?? g.Range,
            "Parry" => Percentage(own?.Parry ?? g.Parry),
            "BlockArrow" => Percentage(own?.BlockArrow ?? g.BlockArrow),
            "MissChance" => Percentage(own?.MissChance ?? g.MissChance),
            _ => 0,
        };
    }

    internal static bool IsRanged(int unit) => Core.Units.Default((GameUnitType)unit).IsRanged;

    internal static int Resource(ResourceValues r, string name) => name switch
    {
        "Food" => r.Food, "Wood" => r.Wood, "Stone" => r.Stone, "Gold" => r.Gold, "Hammer" => r.Hammer,
        "Culture" => r.Culture, "Military" => r.Military, "Disaster" => r.Disaster, _ => 0,
    };
}
