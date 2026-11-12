using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using DiceModders.ModCore.Tools;

namespace DiceModders.LobbyRules;

using Core = DiceModders.ModCore.ModCore;   // inside DiceModders.* the bare name would be the namespace

/// <summary>
/// DiceKingdoms Lobby Rules: gameplay rules the host sets in the lobby (Settings > Lobby > Rules, drawn by ModBridge). They change the simulation, so
/// ModCore only uses them when every player has this mod in the same version; otherwise the match is played with the game's values.
/// </summary>
[BepInPlugin(Guid, Name, Version)]
[BepInDependency("DiceModders.ModCore")]
[BepInDependency("DiceModders.UiKit")]
[BepInProcess("Dice-Kingdoms.exe")]
public sealed class Plugin : BasePlugin
{
    public const string Guid = "org.dicemodders.dicekingdoms.lobbyrules";
    public const string Name = "DiceKingdoms Lobby Rules";
    public const string Version = "1.0.0";

    internal static ManualLogSource L;
    /// <summary>The mod's own switch ([General] Enabled); while off its rules are announced as missing and everybody plays the game's values.</summary>
    internal static bool On;

    public override void Load()
    {
        L = Log;
        var patcher = new Patcher(Guid, Log);
        var ui = DiceModders.UiKit.UiKit.CreateScope(Guid);
        IslandSize.Bind(Config);
        IslandSize.Install(patcher, ui);
        patcher.Summary();
        GameValues.Install();
        ValuesWindow.Install(ui);
        Core.Mods.Switch(Config, affectsSimulation: true, description: "Turns every rule of this mod on or off.")
            .WhenOn(() => On = true, () => On = false);
    }
}
