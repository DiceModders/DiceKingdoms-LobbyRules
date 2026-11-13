using System;
using DiceModders.ModCore;
using DiceModders.ModCore.Tools;
using Unity.Mathematics;

namespace DiceModders.LobbyRules;

using Core = DiceModders.ModCore.ModCore;
using GameGrid = DiceKingdoms.Game.IslandGrid;
using GameState = DiceKingdoms.Game.GameState;
using Camera = DiceKingdoms.Interface.CameraMovement;
using IslandParameters = DiceKingdoms.Game.IslandGeneration.Parameters;

/// <summary>
/// The island rules: "Island grid size" (tiles per side) and "Land size" (how much of the grid is land).
///
/// How the game makes islands: the host's <c>GameState.SpawnIslands</c> generates every island (IslandGeneration, read from the grid's
/// generationParameters), places them in a growing ellipse so that no two grids overlap (it uses the island PREFAB's grid size for that), spawns them
/// on the network and syncs the tiles. Clients make their copies from the prefab and load the synced tiles, which only works when their grid has the
/// same size, so both are lobby rules and every computer applies them. ModCore fixes the rule values when the islands start to spawn and turns a rule
/// off for everybody when one member lacks this mod, so every computer decides the same.
///
/// Generation: altitude = noise * noiseVerticalScale + verticalOffset - (distance / distance to the grid corner) * distanceVerticalScale, land where it
/// is high enough and inside |dx|^power + |dy|^power &lt;= hardLimit.radius^power. The falloff already follows the grid size, so a bigger grid only needs
/// the radius scaled with it. "Land size" scales the radius and divides the falloff by the same factor, so the land really reaches the new limit.
///
/// Camera: the default view frames the whole island (CameraMovement.Focus: radius from the grid size, height = radius / tan(fov / 2)), which would put a
/// big island's view far away. That view is capped at a normal island's distance, so building feels the same; the zoom limits stay the game's. Only the
/// panning area (baseHorizontalBound, around the map centre) grows with the grid, because the islands lie further apart.
/// </summary>
internal static class IslandSize
{
    // above 119 the game's island generation needs ModCore's SeaLevelFill (its own fill overflows the job thread's stack); 201 is tested
    private const int MaxGrid = 201;
    // the normal range ends here; 121 .. MaxGrid is the extended range, chosen only after a warning
    private const int NormalMax = 119;

    private const int NatureLayerOffset = 0x128;   // IslandGrid.natureLayer (IslandGrid.Layer<Nullable<NatureTile>>)
    private const int FishTile = 5;                // NatureTile.FISH
    private static PatchSwitch _fishPatch;
    private static int _fishTarget;                // fish per island on bigger grids (0 = the game's own placement)

    private static DiceModders.UiKit.UiScope _scope;
    private static bool _extendedOk;       // the host accepted the warning once this game session
    private static bool _asking;

    private static LobbyRule<int> _grid;
    private static LobbyRule<int> _land;

    // the game's own values, read once from the first grid (every island comes from the same prefab)
    private static int2 _gameSize;
    private static IslandParameters _game;
    private static int _gameRadius;

    private static GameGrid _prefab;       // host: GameState.islandPrefab.grid, changed for the spacing and put back after the match
    private static int _side;              // this match's grid side (0 = the game's)
    private static float _normalFocus;     // camera focus radius of a normal-size island, this match

    private static Camera _camera;
    private static float _gameBound;
    private static float _gameMinHeight;   // CameraMovement.minHeight (4): raised on big grids so tall models cannot reach the pushed-out HUD
    private static bool _inPosition;
    private static PatchSwitch _positionPatch;

    // local, visual only (no lobby rule): to find what makes the windows flicker on big islands
    private static BepInEx.Configuration.ConfigEntry<bool> _capCamera, _widenSea;

    internal static void Bind(BepInEx.Configuration.ConfigFile config)
    {
        _capCamera = config.Bind("Big islands", "NormalCameraDistance", true,
            "On islands bigger than the game's, the start view and focus views keep a normal island's distance. Read at the start of a match.");
        _widenSea = config.Bind("Big islands", "WidenSea", true,
            "On islands bigger than the game's, the sea plane is widened so the outer islands do not lie past its edge. Read at the start of a match.");
    }

    internal static void Install(Patcher patcher, DiceModders.UiKit.UiScope ui)
    {
        _scope = ui;
        var group = Core.Lobby.Groups.Get("lobbyrules.islands", "Islands");
        _grid = Core.Lobby.Rules.Number("lobbyrules.islandGrid", "Island grid size", 49, 49, MaxGrid, 2,
            $"Tiles per side of every island. 49 = the game's islands. The land, its hills, forests, rocks and fish, and the space between islands grow with it. " +
            $"Up to {NormalMax} plays normally; {NormalMax + 2} .. {MaxGrid} is experimental and asks first.")
            .InGroup(group);
        _grid.Changed += OnGridChanged;
        _land = Core.Lobby.Rules.Number("lobbyrules.landSize", "Land size %", 100, 70, 110, 5,
            "How much of the island grid is land. 100 = like the game's islands.")
            .InGroup(group);

        patcher.Patch(typeof(GameState), nameof(GameState.SpawnIslands), typeof(IslandSize), prefix: nameof(SpawnPrefix), postfix: nameof(SpawnPostfix));
        // only in the game while the host spawns bigger islands (see ThinFishPrefix)
        _fishPatch = patcher.Switch(typeof(GameGrid), "UpdateNatureVisuals", typeof(IslandSize), prefix: nameof(ThinFishPrefix));
        patcher.Patch(typeof(GameGrid), nameof(GameGrid.Awake), typeof(IslandSize), prefix: nameof(AwakePrefix), postfix: nameof(AwakePostfix));
        // the camera asks for positions every frame: this postfix is only in the game during a match with bigger islands
        _positionPatch = patcher.Switch(typeof(Camera), nameof(Camera.Position), typeof(IslandSize), postfix: nameof(PositionPostfix));
        // the harvest dice: on while the HUD is pushed out (see DicePostfix)
        _dicePatch = patcher.Switch(typeof(DiceKingdoms.Interface.HarvestInterface), "CreateDices", typeof(IslandSize), postfix: nameof(DicePostfix));
        Core.Match.Started += WidenCamera;
        Core.Match.Rejoined += WidenCamera;   // a rejoined player may not get Started; WidenCamera can run twice
        Core.Match.Reset += Restore;   // GameState.FullReset: quit, disconnect, joining another lobby
        Core.Match.Ended += Restore;
    }

    private static float Scale => _side > 0 && _gameSize.x > 0 ? (float)_side / _gameSize.x : 1f;

    /// <summary>
    /// The host stepped past the normal range: the game's dialog asks once per game session. Cancel puts the grid back to the normal maximum.
    /// Other players only see the value change.
    /// </summary>
    private static void OnGridChanged(int value)
    {
        if (value <= NormalMax || _extendedOk || _asking || !_grid.Editable) return;
        string text = $"Island grid {value} is in the experimental range ({NormalMax + 2} .. {MaxGrid}).\n\n" +
                      "Expect a long freeze while the islands are built and slower computer turns, and every player's PC needs to keep up. " +
                      "Use it?";
        _asking = true;
        bool shown = _scope != null && _scope.Native.Dialogs.Confirm(text,
            () => { _asking = false; _extendedOk = true; },
            () => { _asking = false; _grid.TrySet(NormalMax); }, DiceModders.UiKit.Native.DialogsApi.WarningIcon);
        if (shown) return;
        // no dialog on this screen: keep the normal range rather than skip the warning
        _asking = false;
        _grid.TrySet(NormalMax);
        Plugin.L.LogInfo($"[island] grid {value} needs the warning dialog, which is not available here: set to {NormalMax}");
    }

    // ------------------------------------------------------------------ islands

    /// <summary>Host: the prefab decides the spacing of the islands, so it gets the match size before they are placed.</summary>
    internal static void SpawnPrefix(GameState __instance)
    {
        try
        {
            var prefab = __instance?.islandPrefab?.grid;
            if (prefab == null) return;
            _prefab = prefab;
            Remember(prefab);
            Apply(prefab, "island prefab");
            if (_fishTarget > 0) _fishPatch.On = true;
        }
        catch (Exception e) { Plugin.L.LogWarning("[island] spawn: " + e.Message); }
    }

    internal static void SpawnPostfix() => _fishPatch.On = false;

    /// <summary>
    /// Host, bigger islands, right after the generated tiles are copied in (IslandGrid.SetGenerateOutput) and before they are drawn and sent to the
    /// other players. The game places fish row by row from the bottom and stops at its limit, so on a big island the limit is reached long before the
    /// top. The limit is lifted during generation (<see cref="Apply"/>) and the fish are thinned here to the target, evenly over the whole coast.
    /// Clients receive the finished island (ApplySync), so only the host thins.
    /// </summary>
    internal static unsafe void ThinFishPrefix(GameGrid __instance)
    {
        try
        {
            if (_fishTarget <= 0 || __instance == null) return;
            IntPtr layer = *(IntPtr*)((byte*)__instance.Pointer + NatureLayerOffset);
            if (layer == IntPtr.Zero) return;
            IntPtr array = *(IntPtr*)((byte*)layer + 0x10);
            if (array == IntPtr.Zero) return;
            int length = *(int*)((byte*)array + 0x18);
            byte* items = (byte*)array + 0x20;                       // Nullable<NatureTile>: hasValue byte, then the int value (8 bytes)

            var fish = new System.Collections.Generic.List<int>();
            for (int i = 0; i < length; i++)
                if (items[i * 8] != 0 && *(int*)(items + i * 8 + 4) == FishTile) fish.Add(i);
            if (fish.Count <= _fishTarget) return;

            // keep a random _fishTarget of them (partial shuffle), clear the rest
            var random = new System.Random(unchecked(length * 31 + fish.Count));
            for (int k = 0; k < _fishTarget; k++)
            {
                int j = random.Next(k, fish.Count);
                (fish[k], fish[j]) = (fish[j], fish[k]);
            }
            for (int k = _fishTarget; k < fish.Count; k++) *(long*)(items + fish[k] * 8) = 0;
            *((byte*)layer + 0x20) = 1;                               // Layer.modified
            Plugin.L.LogInfo($"[island] fish spread evenly: {_fishTarget} of {fish.Count}");
        }
        catch (Exception e) { Plugin.L.LogWarning("[island] fish: " + e.Message); }
    }

    /// <summary>Every computer, every island: its tile layers are made from <c>size</c> right here.</summary>
    internal static void AwakePrefix(GameGrid __instance)
    {
        try
        {
            if (!Core.Lobby.Rules.Fixed) return;   // menu and editor islands stay normal
            Remember(__instance);
            Apply(__instance, "island");
        }
        catch (Exception e) { Plugin.L.LogWarning("[island] awake: " + e.Message); }
    }

    /// <summary>The grid's layers exist now: the camera's focus radius of this island, divided by the scale, is the one of a normal island.</summary>
    internal static void AwakePostfix(GameGrid __instance)
    {
        try
        {
            if (_normalFocus > 0 || Scale <= 1f || !Core.Lobby.Rules.Fixed) return;
            if (!_capCamera.Value) return;
            _normalFocus = new Camera.Focus(__instance, 0f, false).Radius / Scale;
            if (_positionPatch != null) _positionPatch.On = true;
        }
        catch (Exception e) { Plugin.L.LogWarning("[island] focus size: " + e.Message); }
    }

    private static void Remember(GameGrid grid)
    {
        if (_gameRadius != 0) return;
        _game = grid.generationParameters;
        _gameSize = grid.size;
        _gameRadius = _game.hardLimit.radius;
    }

    /// <summary>
    /// Absolute values from the game's own ones, so applying twice (prefab, then the copy) gives the same result.
    ///
    /// The game's amounts are fixed numbers, not per tile: <c>innerCliffs.numberMains</c> (hill groups; each gets <c>numberExtensions</c> more
    /// pieces), <c>nature.trees/rocks.count</c> (forests and rock fields; <c>size</c> is the tiles of one) and <c>nature.fish.maxCount</c> (fish
    /// along the coast). On a bigger land they grow with it: hills, forests and rocks with the land area. The size of one hill, forest or rock field
    /// stays the game's. Fish need <c>fish.maximumDistance</c> too: the game only puts them within that distance of the grid centre, which on a big grid
    /// lies inside the land; it grows with the radius, and the fish cap with the area (the game fills the cap row by row from the bottom).
    /// </summary>
    private static void Apply(GameGrid grid, string what)
    {
        if (_gameRadius <= 0 || _gameSize.x <= 0) return;
        bool on = Plugin.On && Core.Lobby.Rules.Fixed;
        int side = on ? Odd(Math.Clamp(_grid.Value, _gameSize.x, MaxGrid)) : _gameSize.x;
        int percent = on ? _land.Value : 100;
        var size = side == _gameSize.x ? _gameSize : new int2(side, side);
        int radius = Math.Clamp((int)Math.Round(_gameRadius * (float)side / _gameSize.x * percent / 100f), 1, side / 2);
        _side = on ? side : 0;

        float length = (float)radius / _gameRadius, area = length * length;
        var p = _game;
        p.hardLimit.radius = radius;
        p.altitude.distanceVerticalScale = _game.altitude.distanceVerticalScale * 100f / percent;
        p.innerCliffs.numberMains = Times(_game.innerCliffs.numberMains, area);
        p.nature.trees.count = Times(_game.nature.trees.count, area);
        p.nature.rocks.count = Times(_game.nature.rocks.count, area);
        // fish only go within maximumDistance of the grid CENTRE (22 = just past a normal coast): it has to reach the new coast
        // (+ 15 %: a bigger land reaches its limit more often, so part of its coast lies at or past the scaled circle). The game walks the tiles row by
        // row from the bottom and stops at maxCount, which on a big island is reached long before the top: the limit is lifted here and the fish are
        // thinned evenly to _fishTarget after generation (ThinFishPrefix).
        p.nature.fish.maximumDistance = length > 1f ? _game.nature.fish.maximumDistance * length * 1.15f : _game.nature.fish.maximumDistance;
        p.nature.fish.maxCount = length > 1f ? 1_000_000 : _game.nature.fish.maxCount;
        _fishTarget = on && length > 1f ? (int)Math.Round(_game.nature.fish.maxCount * area) : 0;

        bool changed = grid.size.x != size.x || grid.size.y != size.y || grid.generationParameters.hardLimit.radius != radius;
        grid.generationParameters = p;
        grid.size = size;
        if (changed && on)
            Plugin.L.LogInfo($"[island] {what}: grid {size.x}x{size.y}, land radius {radius}, hill groups {p.innerCliffs.numberMains.x}-{p.innerCliffs.numberMains.y}, " +
                             $"forests ~{p.nature.trees.count.average:0.#}, rock fields ~{p.nature.rocks.count.average:0.#}, fish ~{_fishTarget}");
    }

    private static int2 Times(int2 range, float factor) => new((int)Math.Round(range.x * factor), (int)Math.Round(range.y * factor));

    // a count drawn from a normal distribution: n times the area has n times the average and sqrt(n) times the spread
    private static IslandParameters.Gaussian Times(IslandParameters.Gaussian count, float factor) =>
        new() { average = count.average * factor, deviation = count.deviation * MathF.Sqrt(factor) };

    private static int Odd(int n) => n % 2 == 0 ? n + 1 : n;

    // ------------------------------------------------------------------ camera

    /// <summary>The islands lie further from the map centre on a bigger grid: the panning area grows with it.</summary>
    private static void WidenCamera()
    {
        try
        {
            float scale = Scale;
            if (scale <= 1f) return;
            if (_camera == null) _camera = UnityEngine.Object.FindObjectOfType<Camera>();
            if (_camera == null) return;
            if (_gameBound == 0) _gameBound = _camera.baseHorizontalBound;
            _camera.baseHorizontalBound = _gameBound * scale;
            Plugin.L.LogInfo($"[island] camera panning area x{scale:0.##}");
            if (_widenSea.Value) WidenSea(scale);
            float hud = PushUi(scale);
            // the HUD now sits up to ~3.5 units in front of the camera (201): at the game's lowest height (4) the castle and hills reached into it
            if (_gameMinHeight == 0) _gameMinHeight = _camera.minHeight;
            _camera.minHeight = Math.Max(_gameMinHeight, _gameMinHeight + hud * 1.5f);
            Plugin.L.LogInfo($"[island] lowest camera height {_camera.minHeight:0.0} (game {_gameMinHeight:0.0})");
        }
        catch (Exception e) { Plugin.L.LogWarning("[island] camera: " + e.Message); }
    }

    // the scene's sea: large renderers with a water material, widened on a big grid and put back after the match
    private static readonly System.Collections.Generic.List<(UnityEngine.Transform t, UnityEngine.Vector3 scale)> _sea = new();

    /// <summary>
    /// The sea is a scene object sized for normal islands; on a bigger grid the outer islands lie past its edge. Widens every large renderer whose
    /// material is a water material by the grid scale (around its own centre). One search per match, at the start.
    /// </summary>
    private static void WidenSea(float scale)
    {
        if (_sea.Count > 0) return;
        var all = UnityEngine.Object.FindObjectsOfType<UnityEngine.MeshRenderer>();
        var big = new System.Collections.Generic.List<(UnityEngine.MeshRenderer r, float size)>();
        foreach (var r in all)
        {
            if (r == null) continue;
            var b = r.bounds;
            float size = Math.Max(b.size.x, b.size.z);
            if (size >= 100f) big.Add((r, size));
        }
        big.Sort((a, b) => b.size.CompareTo(a.size));
        foreach (var (r, size) in big)
        {
            string mat = r.sharedMaterial != null ? r.sharedMaterial.name : "";
            bool water = mat.IndexOf("water", StringComparison.OrdinalIgnoreCase) >= 0 || mat.IndexOf("ocean", StringComparison.OrdinalIgnoreCase) >= 0
                         || mat.IndexOf("sea", StringComparison.OrdinalIgnoreCase) >= 0;
            Plugin.L.LogInfo($"[island] large object '{r.gameObject.name}' material '{mat}' size {size:0}" + (water ? " -> sea, widened" : ""));
            if (!water) continue;
            var t = r.transform;
            _sea.Add((t, t.localScale));
            t.localScale = new UnityEngine.Vector3(t.localScale.x * scale, t.localScale.y, t.localScale.z * scale);
        }
    }

    // Screen Space - Camera canvases (game HUD, UiKit layers) changed for this match: their render mode, camera and distance before
    private static readonly System.Collections.Generic.List<(UnityEngine.Canvas c, UnityEngine.RenderMode mode, UnityEngine.Camera camera, float distance)> _ui = new();

    /// <summary>
    /// The game's HUD and UiKit's windows are Screen Space - Camera canvases 0.11 .. 10 units in front of the camera. At 0.11 one screen pixel is
    /// ~0.00008 world units, finer than a float can place a vertex once the camera is a few thousand units from the map centre (big grids put the
    /// islands that far out): every vertex snaps on its own and the windows shimmer while the camera moves. They move further out, in the same
    /// order; restored after the match. They must stay in camera space: the game stacks its HUD by distance ("Canvas Background", the 3D harvest
    /// dice, "Canvas Over Dice"); as screen overlays the dice disappeared, the harvest panel asserted every frame and the scoreboard went behind the
    /// grey background.
    /// </summary>
    private static float PushUi(float scale)
    {
        RestoreUi();
        // What stops the shimmer is the NEAREST distance: 0.11 x 6 x the grid scale (tested at 201: x4 and x12 still left some edges snapping). The
        // others only have to stay behind it in the same order, so they are packed just behind it (up to 30 % further) instead of being multiplied
        // too: multiplied, the farthest went to ~27 units and the island hid the HUD when the camera zoomed in close. The far ones (UiKit's Behind
        // layer, ~10) only move if they would end up in front of the HUD.
        var front = new System.Collections.Generic.List<UnityEngine.Canvas>();
        var far = new System.Collections.Generic.List<UnityEngine.Canvas>();
        // hidden windows too (the history panel is inactive when the match starts); scene objects only, not prefabs
        foreach (var c in UnityEngine.Resources.FindObjectsOfTypeAll<UnityEngine.Canvas>())
        {
            if (c == null || !c.gameObject.scene.IsValid() || c.renderMode != UnityEngine.RenderMode.ScreenSpaceCamera) continue;
            var parent = c.transform.parent;    // isRootCanvas is not kept up to date on inactive canvases
            if (parent != null && parent.GetComponentInParent<UnityEngine.Canvas>(true) != null) continue;
            _ui.Add((c, c.renderMode, c.worldCamera, c.planeDistance));
            (c.planeDistance >= 2f ? far : front).Add(c);
        }
        float lo = float.MaxValue, hi = 0f;
        foreach (var c in front) { lo = Math.Min(lo, c.planeDistance); hi = Math.Max(hi, c.planeDistance); }
        float nearest = Math.Max(0.11f, lo == float.MaxValue ? 0.11f : lo) * 6f * scale, farthest = nearest;
        _mapLo = lo == float.MaxValue ? 0.11f : lo; _mapHi = hi > _mapLo ? hi : _mapLo + 1f; _mapNear = nearest;
        foreach (var c in front)
        {
            float t = hi > lo ? (c.planeDistance - lo) / (hi - lo) : 0f;   // order kept
            c.planeDistance = nearest * (1f + 0.3f * t);
            farthest = Math.Max(farthest, c.planeDistance);
        }
        foreach (var c in far) c.planeDistance = Math.Max(c.planeDistance, farthest * 1.25f);
        Plugin.L.LogInfo($"[island] {_ui.Count} camera-space UI canvases moved away from the camera ({nearest:0.0} .. {farthest:0.0} units)");
        if (_dicePatch != null) _dicePatch.On = front.Count > 0;
        return farthest;
    }

    // the distance map PushUi used for this match: old distance -> new distance (order kept)
    private static float _mapLo, _mapHi, _mapNear;
    private static PatchSwitch _dicePatch;
    private static readonly System.Collections.Generic.Dictionary<IntPtr, UnityEngine.Vector3> _diceScale = new();
    private static float MapDistance(float d) => _mapNear * (1f + 0.3f * (d - _mapLo) / (_mapHi - _mapLo));

    /// <summary>
    /// The harvest dice are 3D models in a holder in front of the camera; CreateDices puts the holder at a depth from the number of dice, which in
    /// the game lands between "Canvas Background" and "Canvas Over Dice". With those canvases pushed out the dice stayed near and drew over every
    /// window (the rules window, the scoreboard). The holder is moved with the same map as the canvases and scaled by the same factor, so the dice
    /// keep their size and place on screen (perspective) and sit between the same canvases as in the game.
    /// </summary>
    internal static void DicePostfix(DiceKingdoms.Interface.HarvestInterface __instance)
    {
        try
        {
            var holder = __instance != null ? __instance.dicesHolder : null;
            if (holder == null || _mapNear <= 0f) return;
            if (!_diceScale.TryGetValue(holder.Pointer, out var baseScale)) _diceScale[holder.Pointer] = baseScale = holder.localScale;
            var pos = holder.localPosition;
            if (pos.z <= 0f) return;
            float k = MapDistance(pos.z) / pos.z;
            holder.localPosition = new UnityEngine.Vector3(pos.x * k, pos.y * k, pos.z * k);
            holder.localScale = new UnityEngine.Vector3(baseScale.x * k, baseScale.y * k, baseScale.z * k);
        }
        catch (Exception e) { Plugin.L.LogWarning("[island] dice depth: " + e.Message); }
    }

    private static void RestoreUi()
    {
        foreach (var (c, mode, camera, distance) in _ui)
        {
            if (c == null) continue;
            c.renderMode = mode;
            c.worldCamera = camera;
            c.planeDistance = distance;
        }
        _ui.Clear();
    }

    private static void RestoreSea()
    {
        foreach (var (t, scale) in _sea) if (t != null) t.localScale = scale;
        _sea.Clear();
    }

    /// <summary>
    /// The camera position for a focus (start of the match, disasters, and the "which island is in view" test): a focus larger than a normal island is
    /// capped at the normal one, so a big island opens at the usual height, centred on it. Smaller focuses (disasters) stay as they are.
    /// </summary>
    internal static void PositionPostfix(Camera __instance, Camera.Focus focus, ref float3 __result)
    {
        if (_inPosition || _normalFocus <= 0 || focus.Radius <= _normalFocus) return;
        try
        {
            _inPosition = true;
            __result = __instance.Position(new Camera.Focus(focus.Position, _normalFocus, focus.EndTime, focus.StopAfterEnd));
        }
        catch (Exception e) { Plugin.L.LogWarning("[island] camera focus: " + e.Message); }
        finally { _inPosition = false; }
    }

    // ------------------------------------------------------------------ end

    /// <summary>The prefab and the camera live on after the match: back to the game's values for menus, the editor and the next match.</summary>
    private static void Restore()
    {
        try
        {
            if (_prefab != null) Apply(_prefab, "island prefab restored");   // rules are no longer fixed here: the game's values
            _side = 0;
            _normalFocus = 0;
            _asking = false;                                        // the dialog may have been closed by a scene change without a callback
            if (_positionPatch != null) _positionPatch.On = false;
            if (_dicePatch != null) _dicePatch.On = false;
            _mapNear = 0f;
            foreach (var h in UnityEngine.Resources.FindObjectsOfTypeAll<DiceKingdoms.Interface.HarvestInterface>())   // dice back to the game's scale
                if (h != null && h.dicesHolder != null && _diceScale.TryGetValue(h.dicesHolder.Pointer, out var s0)) h.dicesHolder.localScale = s0;
            _diceScale.Clear();
            if (_fishPatch != null) _fishPatch.On = false;          // normally off after SpawnIslands; stays on only if spawning threw
            if (_camera != null && _gameBound != 0) _camera.baseHorizontalBound = _gameBound;
            if (_camera != null && _gameMinHeight != 0) _camera.minHeight = _gameMinHeight;
            RestoreSea();
            RestoreUi();
        }
        catch (Exception e) { Plugin.L.LogWarning("[island] restore: " + e.Message); }
    }
}
