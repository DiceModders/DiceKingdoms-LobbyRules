using System;
using System.Collections.Generic;
using System.Globalization;
using DiceModders.UiKit;
using DiceModders.UiKit.Custom;
using DiceModders.UiKit.Native;
using UnityEngine;

namespace DiceModders.LobbyRules;

using Core = DiceModders.ModCore.ModCore;
using GameBuildingTypeName = DiceKingdoms.Game.BuildingTypeName;
using GameTechnologyName = DiceKingdoms.Game.TechnologyName;
using GameUnitType = DiceKingdoms.Game.Unit.Type;

/// <summary>
/// The Game values window (Settings > Lobby > Lobby Rules > Game values). Tabs: Units (one section per civilization, plus "Every civilization"),
/// Buildings and Technologies. A section opens when its name is clicked (one at a time, so the list stays short). The host changes values with
/// - / +; "Game" puts a value back. Everybody else sees the host's values. The window redraws its numbers when <see cref="GameValues.Changed"/> fires.
/// </summary>
internal static class ValuesWindow
{
    private static readonly string[] BuiltInCivs = { "Europe", "Middle East", "Viking", "Ancient", "Maya" };
    private static readonly string[] TabNames = { "Units", "Buildings", "Technologies" };
    private static readonly Color Changed = new(1f, 0.82f, 0.45f, 1f);
    private static readonly Color Normal = new(0.97f, 0.94f, 0.89f, 1f);

    private static UiScope _ui;
    private static UiWindow _win;
    private static UiScroll _scroll;
    private static UiText _status;
    private static readonly List<UiButton> Tabs = new();
    private static readonly List<Action> Refreshers = new();
    private static readonly List<UiButton> EditButtons = new();
    private static int _tab;
    private static string _open;

    internal static void Install(UiScope ui)
    {
        _ui = ui;
        GameValues.Changed += Refresh;
        Core.Lobby.Changed += Refresh;   // the host can change, a match can start: who may edit changes with it
        ui?.Native.Settings.AddBlock(new SettingsBlockSpec
        {
            Id = Plugin.Guid + ".lobby",
            Page = SettingsPage.Lobby,
            Title = "Lobby Rules",
            Order = 30,
            Build = block => block.AddButton("Game values", "Open", Toggle),
        });
    }

    internal static void Toggle() { if (_win != null) _win.Close(); else Open(); }

    private static void Open()
    {
        _win = _ui?.Custom.Window(w =>
        {
            w.Title("Game values").Size(760f, 800f).Position(80f, 40f).Draggable().Closable().Fullscreenable().FullscreenButton();
            w.OnClose(() => { _win = null; _scroll = null; _status = null; Tabs.Clear(); Refreshers.Clear(); EditButtons.Clear(); });
            w.Body.Spacing(6f);
            w.Body.Row(r =>
            {
                for (int i = 0; i < TabNames.Length; i++)
                {
                    int tab = i;
                    var b = r.Button(TabNames[i], () => { _tab = tab; _open = null; Rebuild(); })?.FillWidth();
                    if (b != null) Tabs.Add(b);
                }
                var reset = r.Button("Reset all", () => GameValues.ResetAll())?.FillWidth();
                if (reset != null) EditButtons.Add(reset);
            })?.Height(36f);
            _status = w.Body.Text("");
            _scroll = w.Body.Scroll(_ => { })?.FillWidth().FillHeight();
        });
        Rebuild();
    }

    // ---- building the open tab -------------------------------------------------------------------------------------------------------

    private static void Rebuild()
    {
        if (_win == null || _scroll == null) return;
        for (int i = 0; i < Tabs.Count; i++) Tabs[i].Selected(i == _tab);
        var reset = EditButtons.Count > 0 ? EditButtons[0] : null;
        EditButtons.Clear();
        if (reset != null) EditButtons.Add(reset);
        Refreshers.Clear();
        var c = _scroll.Content;
        c.Clear();
        c.Spacing(4f);
        try
        {
            if (_tab == 0) BuildUnits(c);
            else if (_tab == 1) BuildBuildings(c);
            else BuildTechnologies(c);
        }
        catch (Exception e) { Plugin.L.LogError("game values window: " + e); }
        Refresh();
        _scroll.ToTop();
    }

    private static void BuildUnits(UiContainer c)
    {
        var civs = new List<(int id, string name)> { (-1, "Every civilization") };
        for (int i = 0; i < BuiltInCivs.Length; i++) civs.Add((i, BuiltInCivs[i]));
        foreach (var m in Core.Civilizations.All) civs.Add((m.Id, m.Name));
        foreach (var (civ, name) in civs)
        {
            Section(c, "civ:" + civ, name, s =>
            {
                for (int unit = 0; unit < 10; unit++)
                {
                    int u = unit;
                    s.Text(Nice(((GameUnitType)u).ToString()))?.Size(20f).Color(Changed);
                    foreach (var stat in GameValues.UnitStats)
                    {
                        if ((stat == "Range" || stat == "MissChance") && !GameValues.IsRanged(u)) continue;
                        bool pct = Array.IndexOf(GameValues.Percent, stat) >= 0;
                        int number = civ >= 0 ? Core.Civilizations.ToLobbyNumber(civ) : civ;   // the host's number: another computer may number a mod civilization differently
                        string key = $"u.{(number == -1 && civ >= 0 ? civ : number)}.{u}.{stat}";
                        string all = $"u.-1.{u}.{stat}";
                        int Base() => civ >= 0 ? GameValues.Get(all) ?? GameValues.UnitBase(civ, u, stat) : GameValues.UnitBase(-1, u, stat);
                        Stepper(s, Nice(stat) + (pct ? " %" : ""), key, Base, pct ? 5 : 1, 0, pct ? 100 : 99);
                    }
                }
            });
        }
    }

    private static void BuildBuildings(UiContainer c)
    {
        foreach (GameBuildingTypeName b in Enum.GetValues(typeof(GameBuildingTypeName)))
        {
            var building = b;
            Section(c, "b:" + (int)b, Nice(b.ToString()), s =>
            {
                var d = Core.Balance.DefaultBuilding(building);
                if (d == null) { s.Text("This building's values are not loaded yet (open the window again in the lobby)."); return; }
                string prefix = "b." + (int)building + ".";
                Stepper(s, "Health", prefix + "Health", () => d.Health, 1, 1, 999);
                if (d.IsTower)
                {
                    Stepper(s, "Tower range", prefix + "TowerRange", () => d.TowerRange, 1, 1, 99);
                    Stepper(s, "Tower period", prefix + "TowerPeriod", () => d.TowerPeriod, 1, 1, 999);
                }
                foreach (var r in GameValues.Resources)
                {
                    string res = r;
                    Stepper(s, "Cost: " + res, prefix + res, () => GameValues.Resource(d.Cost, res), 1, 0, 99);
                }
            });
        }
    }

    private static void BuildTechnologies(UiContainer c)
    {
        foreach (GameTechnologyName t in Enum.GetValues(typeof(GameTechnologyName)))
        {
            var tech = t;
            Section(c, "t:" + (int)t, Nice(t.ToString()), s =>
            {
                var d = Core.Balance.DefaultTechnologyCost(tech);
                if (d == null) { s.Text("This technology's cost is not loaded yet (open the window again in the lobby)."); return; }
                foreach (var r in GameValues.Resources)
                {
                    string res = r;
                    Stepper(s, "Cost: " + res, "t." + (int)tech + "." + res, () => GameValues.Resource(d, res), 1, 0, 99);
                }
            });
        }
    }

    /// <summary>A heading button; its rows are made only while it is the open one (one section at a time).</summary>
    private static void Section(UiContainer c, string key, string title, Action<UiContainer> build)
    {
        bool open = _open == key;
        c.Button((open ? "- " : "+ ") + title, () => { _open = open ? null : key; Rebuild(); })?.FillWidth().Height(34f);
        if (!open) return;
        c.Column(s => { s.Spacing(2f); s.Padding(16f, 4f); build(s); })?.FillWidth();
    }

    /// <summary>"label   [-] value [+] [Game]": the value is the host's, or the base value (shown plain) when the host did not change it.</summary>
    private static void Stepper(UiContainer c, string label, string key, Func<int> baseValue, int step, int min, int max)
    {
        UiText value = null;
        c.Row(r =>
        {
            r.Spacing(4f);
            r.Text(label)?.FillWidth();
            var minus = r.Button("-", () => Step(key, baseValue, -step, min, max))?.FixedW(36f);
            value = r.Text("")?.FixedW(140f);
            var plus = r.Button("+", () => Step(key, baseValue, step, min, max))?.FixedW(36f);
            var game = r.Button("Game", () => GameValues.Set(key, null))?.FixedW(70f);
            foreach (var b in new[] { minus, plus, game }) if (b != null) EditButtons.Add(b);
        })?.Height(30f);
        Refreshers.Add(() =>
        {
            if (value == null) return;
            int b = baseValue();
            int? v = GameValues.Get(key);
            value.Value = v == null ? b.ToString(CultureInfo.InvariantCulture) : v.Value + "   (was " + b + ")";
            value.Color(v == null ? Normal : Changed);
        });
    }

    private static void Step(string key, Func<int> baseValue, int delta, int min, int max)
    {
        int b = baseValue();
        int next = Math.Clamp((GameValues.Get(key) ?? b) + delta, min, max);
        if (!GameValues.Set(key, next == b ? null : next)) Status("Only the host can change values, before the match starts.");
    }

    // ---- refreshing ----------------------------------------------------------------------------------------------------------------

    private static void Refresh()
    {
        if (_win == null) return;
        foreach (var r in Refreshers) r();
        bool edit = GameValues.Editable;
        foreach (var b in EditButtons) b?.Interactable(edit);
        Status((edit ? "You are the host: changes reach every player. " : "The host's values (read only). ") +
               (GameValues.Count == 0 ? "Nothing is changed." : GameValues.Count + " value(s) changed.") +
               " They are used only when every player has this mod.");
    }

    private static void Status(string text) { if (_status != null) _status.Value = text; }

    /// <summary>"ARCHERY_RANGE" / "AttackPeriod" -> "Archery range" / "Attack period".</summary>
    private static string Nice(string name)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            char ch = name[i];
            if (ch == '_') { sb.Append(' '); continue; }
            if (i > 0 && char.IsUpper(ch) && char.IsLower(name[i - 1])) sb.Append(' ');
            sb.Append(sb.Length == 0 ? char.ToUpperInvariant(ch) : char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }
}
