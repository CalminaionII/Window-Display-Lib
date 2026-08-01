using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.GameContent;

namespace WindowDisplayLib
{
    /// <summary>
    /// Guards a crash in vanilla's own <c>BlockBehaviorDisplay.placingItemsPreview</c>.
    ///
    /// That method reads the player's GLOBAL <c>CurrentBlockSelection</c> and indexes its own
    /// <c>PlacementSurfaces</c> with the surface index decoded from it:
    ///
    /// <code>
    /// SlotLocation loc = decodeSlotid(capi.World.Player.CurrentBlockSelection?.SelectionBoxId);
    /// if (loc == null) return true;
    /// string displayType = PlacementSurfaces[loc.PlacementSurfaceIndex].DisplayCategory ?? "shelf";
    /// </code>
    ///
    /// But the selection can belong to a DIFFERENT block than the one being evaluated — the
    /// raytrace calls GetSelectionBoxes on each candidate in turn while CurrentBlockSelection
    /// still holds whatever the player was last aimed at. Aim at a block with three placement
    /// surfaces, then let the trace evaluate one with fewer, and it indexes past the end:
    /// IndexOutOfRangeException from the render thread, which takes the client down.
    ///
    /// It is a latent bug in vanilla — two vanilla display blocks with differing surface
    /// counts can do it unaided. Our blocks make it far likelier: we deliberately kept our
    /// slot ids format-identical to vanilla's so its decoder parses them, and we ship
    /// three-surface blocks (the display unit and the vanilla pane shelf).
    ///
    /// The fix returns exactly what vanilla returns on its own unresolvable-slot path, and
    /// only when the index is genuinely out of range, so nothing else changes behaviour.
    /// Fixing it here rather than by changing the id format because those ids are inventory
    /// keys persisted in the block entity — reformatting them would orphan stored items.
    /// </summary>
    public static class VanillaDisplayBoundsPatch
    {
        private const string HarmonyId = "windowdisplaylib.vanilladisplaybounds";

        private static Harmony _harmony;
        private static ICoreClientAPI _capi;

        public static void Apply(ICoreClientAPI capi)
        {
            if (_harmony != null) return;

            _capi = capi;
            _harmony = new Harmony(HarmonyId);
            _harmony.PatchAll(typeof(VanillaDisplayBoundsPatch).Assembly);
        }

        public static void Remove()
        {
            _harmony?.UnpatchAll(HarmonyId);
            _harmony = null;
            _capi = null;
        }

        [HarmonyPatch(typeof(BlockBehaviorDisplay), "placingItemsPreview")]
        public static class PlacingItemsPreviewGuard
        {
            public static bool Prefix(BlockBehaviorDisplay __instance, ref bool __result)
            {
                PlacementSurface[] surfaces = __instance?.PlacementSurfaces;
                if (surfaces == null) return true;

                string boxId = _capi?.World?.Player?.CurrentBlockSelection?.SelectionBoxId;
                if (boxId == null) return true;

                SlotLocation loc = BlockBehaviorDisplay.decodeSlotid(boxId);
                if (loc == null) return true;

                if (loc.PlacementSurfaceIndex >= 0 && loc.PlacementSurfaceIndex < surfaces.Length)
                {
                    return true;   // in range — let vanilla run untouched
                }

                // Same answer vanilla gives when it cannot resolve a slot at all
                __result = true;
                return false;
            }
        }
    }
}
