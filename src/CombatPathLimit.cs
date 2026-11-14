using System;
using System.IO;
using System.Runtime.InteropServices;

namespace DiceModders.LobbyRules;

/// <summary>
/// Big islands: soldiers who land and sail straight home without a fight.
///
/// The fight's soldiers find their way with <c>Pathfinding.MoveTowards</c> (A* on the combat grid, twice the island grid per side). It gives up after
/// 4000 steps: it logs "early exit" and returns no direction, the soldier stands still on its landing tile, and once every soldier of a deployment
/// stands still on the landing tiles <c>CombatGrid.StepDeployment</c> sends them back to the ship (Deployment.Leave). 4000 steps are plenty on the
/// game's 98 x 98 combat grid; on 201 islands (402 x 402) a building far inland behind walls needs more, so some attacks ended at once.
///
/// The search runs as Burst code (lib_burst_generated.dll, one AVX2 and one SSE2 copy): <c>cmp eax, 3999; lea eax, [rax+1]; ja exit</c>. The limit
/// is raised with the island area (4000 x (side / 49)^2) while a match has bigger islands and put back after it. Every computer simulates the
/// fights, so every computer uses the same limit: it follows the island grid rule, which every player has in the same version.
/// </summary>
internal static unsafe class CombatPathLimit
{
    private const int GameLimit = 4000;
    // cmp eax, imm32 (3999) ; lea eax, [rax+1]
    private static readonly byte[] Signature = { 0x3D, 0x9F, 0x0F, 0x00, 0x00, 0x8D, 0x40, 0x01 };

    private static byte*[] _sites;     // the imm32 of each copy
    private static bool _searched;
    private static int _current = GameLimit;

    /// <summary>The limit for a grid of <paramref name="side"/> tiles; <paramref name="gameSide"/> is the game's own island side.</summary>
    internal static void Set(int side, int gameSide)
    {
        int limit = side > gameSide && gameSide > 0 ? (int)Math.Ceiling(GameLimit * ((double)side / gameSide) * ((double)side / gameSide)) : GameLimit;
        Write(limit);
    }

    internal static void Reset() => Write(GameLimit);

    private static void Write(int limit)
    {
        if (limit == _current) return;
        try
        {
            if (!_searched) Search();
            if (_sites == null || _sites.Length == 0) return;
            foreach (var at in _sites)
            {
                if (*(int*)at != _current - 1) { Plugin.L.LogWarning($"[combat] path limit: unexpected value {*(int*)at} (left as it is)"); return; }
            }
            foreach (var at in _sites)
            {
                if (!VirtualProtect((IntPtr)at, (UIntPtr)4, 0x40, out uint old)) throw new InvalidOperationException("VirtualProtect failed");
                *(int*)at = limit - 1;
                VirtualProtect((IntPtr)at, (UIntPtr)4, old, out _);
                FlushInstructionCache(GetCurrentProcess(), (IntPtr)at, (UIntPtr)4);
            }
            Plugin.L.LogInfo($"[combat] soldier path search limit {limit} steps (game {GameLimit})");
            _current = limit;
        }
        catch (Exception e) { Plugin.L.LogWarning("[combat] path limit: " + e.Message); }
    }

    private static void Search()
    {
        _searched = true;
        IntPtr module = GetModuleHandleW("lib_burst_generated.dll");
        if (module == IntPtr.Zero)
            module = LoadLibraryW(Path.Combine(UnityEngine.Application.dataPath, "Plugins", "x86_64", "lib_burst_generated.dll"));
        if (module == IntPtr.Zero) { Plugin.L.LogWarning("[combat] path limit: Burst library not found"); return; }

        byte* start = TextSection(module, out int size);
        var found = new System.Collections.Generic.List<IntPtr>();
        for (int i = 0, end = size - Signature.Length; i <= end; i++)
        {
            if (start[i] != Signature[0]) continue;
            int j = 1;
            while (j < Signature.Length && start[i + j] == Signature[j]) j++;
            if (j == Signature.Length) found.Add((IntPtr)(start + i + 1));
        }
        // the game build of 2024-04 has exactly two (AVX2 and SSE2); anything else is another game version: leave it alone
        if (found.Count != 2) { Plugin.L.LogWarning($"[combat] path limit: found {found.Count} places instead of 2, game version changed? (not raised)"); return; }
        _sites = new byte*[found.Count];
        for (int k = 0; k < found.Count; k++) _sites[k] = (byte*)found[k];
    }

    private static byte* TextSection(IntPtr module, out int size)
    {
        byte* b = (byte*)module;
        int pe = *(int*)(b + 0x3C);
        int sections = *(ushort*)(b + pe + 6);
        int optional = *(ushort*)(b + pe + 20);
        byte* section = b + pe + 24 + optional;
        for (int i = 0; i < sections; i++, section += 40)
            if (section[0] == '.' && section[1] == 't' && section[2] == 'e' && section[3] == 'x' && section[4] == 't')
            {
                size = *(int*)(section + 8);
                return b + *(int*)(section + 12);
            }
        throw new InvalidOperationException("no .text section");
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string name);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] private static extern IntPtr LoadLibraryW(string path);
    [DllImport("kernel32")] private static extern bool VirtualProtect(IntPtr address, UIntPtr size, uint protect, out uint old);
    [DllImport("kernel32")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32")] private static extern bool FlushInstructionCache(IntPtr process, IntPtr address, UIntPtr size);
}
