using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace WindowDisplayLib
{
    /// <summary>
    /// Our own take on vanilla's <c>Display</c> block behaviour.
    ///
    /// Parses <c>psurface*</c> elements out of the block shape and owns the grid
    /// of placement pads, exactly like BlockBehaviorDisplay — but keeps the frame
    /// and pane boxes in the same place, and hands all box queries to the block
    /// entity so open/closed state can drive them.
    ///
    /// Implements <see cref="ICustomSelectionBoxRender"/> so the green/red ghost
    /// preview of the held item still appears.
    /// </summary>
    public class BlockBehaviorWindowSurfaces : StrongBlockBehavior, ICustomSelectionBoxRender
    {
        public WindowPlacementSurface[] Surfaces { get; private set; } = Array.Empty<WindowPlacementSurface>();

        /// <summary>Grid pads, in block-local coordinates, unrotated.</summary>
        public CuboidfWithId[] GridBoxes { get; private set; } = Array.Empty<CuboidfWithId>();

        /// <summary>Element names to strip when tesselating, so the markers stay invisible.</summary>
        public string[] SurfaceElementNames { get; private set; } = Array.Empty<string>();

        public FrameBoxGroup[] FrameBoxGroups { get; private set; } = Array.Empty<FrameBoxGroup>();
        public CollisionBoxGroup[] CollisionBoxGroups { get; private set; } = Array.Empty<CollisionBoxGroup>();

        protected int maxXDivisions = 32;
        protected int maxZDivisions = 32;

        private ICoreClientAPI capi;

        public static readonly Size3f DefaultItemSize = new Size3f(6f, 4f, 6f);

        /// <summary>
        /// Vanilla's own display category, and the fallback when a psurface marker names
        /// none. Also what an unpatched item's <c>displayable</c> entry is keyed under, so
        /// it doubles as the source to inherit sizing and stacking from.
        /// </summary>
        public const string DefaultDisplayCategory = "shelf";

        public BlockBehaviorWindowSurfaces(Block block) : base(block)
        {
        }

        public override void Initialize(JsonObject properties)
        {
            base.Initialize(properties);
            maxXDivisions = properties["maxXDivisions"].AsInt(32);
            maxZDivisions = properties["maxZDivisions"].AsInt(32);
        }

        public override void OnLoaded(ICoreAPI api)
        {
            base.OnLoaded(api);
            capi = api as ICoreClientAPI;

            // NO event bus registration here. It used to register OnGetTransform per block
            // TYPE, which is bounded and so far less harmful than the per-block-entity case
            // — but event bus listeners cannot be unregistered at all in this API, so it
            // still accumulated across world loads within one client session. The mod
            // system registers once instead; see WindowDisplayLibMod.StartClientSide.

            FrameBoxGroups = block.Attributes?["frameBoxGroups"].AsObject<FrameBoxGroup[]>() ?? Array.Empty<FrameBoxGroup>();
            CollisionBoxGroups = block.Attributes?["collisionBoxGroups"].AsObject<CollisionBoxGroup[]>() ?? Array.Empty<CollisionBoxGroup>();

            ParseSurfaces(api);
        }

        private void ParseSurfaces(ICoreAPI api)
        {
            AssetLocation shapePath = block.Shape?.Base?.Clone()
                .WithPathPrefixOnce("shapes/")
                .WithPathAppendixOnce(".json");

            if (shapePath == null) return;

            Shape shape = api.Assets.TryGet(shapePath)?.ToObject<Shape>();
            if (shape?.Elements == null)
            {
                api.Logger.Warning("[WindowDisplayLib] Could not load shape '{0}' for block {1}; no placement surfaces.", shapePath, block.Code);
                return;
            }

            var surfaces = new List<WindowPlacementSurface>();
            var boxes = new List<CuboidfWithId>();

            // Only top-level elements, matching vanilla.
            foreach (ShapeElement element in shape.Elements)
            {
                WindowPlacementSurface surface = WindowPlacementSurface.TryParse(element, api.Logger);
                if (surface == null) continue;

                surfaces.Add(surface);
                boxes.AddRange(surface.BuildGridBoxes(maxXDivisions, maxZDivisions));
            }

            Surfaces = surfaces.OrderBy(s => s.Index).ToArray();
            GridBoxes = boxes.ToArray();
            SurfaceElementNames = Surfaces.Select(s => s.ElementName).ToArray();
        }

        /// <summary>
        /// The surface whose NAME carries this index, e.g. 2 for
        /// <c>psurface2-w14-h7-d6-windowdisplay</c>.
        ///
        /// Matched on the parsed index rather than array position on purpose. Slot ids
        /// encode <c>surface.Index</c> (see BuildGridBoxes) and
        /// <c>showSurfacesWhenStacked</c> lists the same numbers, so a positional lookup
        /// silently disagreed with both as soon as the indices in a shape were not
        /// contiguous from 0 — psurface4 with no psurface3 would return the wrong surface
        /// or none, and the pads would stop working with nothing logged.
        /// </summary>
        public WindowPlacementSurface GetSurface(int index)
        {
            for (int i = 0; i < Surfaces.Length; i++)
            {
                if (Surfaces[i].Index == index) return Surfaces[i];
            }
            return null;
        }

        public string GetDisplayCategory(int surfaceIndex)
            => GetSurface(surfaceIndex)?.DisplayCategory ?? "shelf";

        // ── Box routing ─────────────────────────────────────────────────────
        // PreventDefault on both: the block entity returns the complete set
        // (frame + grid + placed items), so the JSON boxes must not be appended.

        public override Cuboidf[] GetSelectionBoxes(IBlockAccessor blockAccessor, BlockPos pos, ref EnumHandling handled)
        {
            if (blockAccessor.GetBlockEntity(pos) is BEWindowDisplay be)
            {
                handled = EnumHandling.PreventDefault;
                return be.GetSelectionBoxes();
            }
            return base.GetSelectionBoxes(blockAccessor, pos, ref handled);
        }

        public override Cuboidf[] GetCollisionBoxes(IBlockAccessor blockAccessor, BlockPos pos, ref EnumHandling handled)
        {
            if (blockAccessor.GetBlockEntity(pos) is BEWindowDisplay be)
            {
                handled = EnumHandling.PreventDefault;
                return be.GetCollisionBoxes();
            }
            return base.GetCollisionBoxes(blockAccessor, pos, ref handled);
        }

        public override bool DoPartialSelection(IWorldAccessor world, BlockPos pos, ref EnumHandling handled)
        {
            handled = EnumHandling.PreventDefault;
            return true;
        }

        /// <summary>
        /// Rotates the block-breaking decal to match the placed rotation. Without this
        /// the crack overlay is built from the unrotated shape and sits at whatever
        /// angle the block was authored at, not the angle it was placed at.
        /// </summary>
        public override void OnDecalTesselation(IWorldAccessor world, MeshData decalMesh, BlockPos pos, ref EnumHandling handled)
        {
            handled = EnumHandling.PreventDefault;

            if (world.BlockAccessor.GetBlockEntity(pos) is BEWindowDisplay be && be.MeshAngleRad != 0f)
            {
                decalMesh.Rotate(new Vec3f(0.5f, 0.5f, 0.5f), 0f, be.MeshAngleRad, 0f);
            }
        }

        // ── Particles from stored items ─────────────────────────────────────

        /// <summary>
        /// Opts the block into client particle ticks while something stored on a surface
        /// can emit — a candle or an oil lamp on the sill.
        ///
        /// <c>handling</c> is left PassThrough when the answer is no, so the block's own
        /// ParticleProperties branch still gets its say. Only a yes claims the decision:
        /// vanilla treats any non-PassThrough behaviour as having answered for the block.
        ///
        /// The client re-evaluates this on a **block change**, not a block-entity one.
        /// That costs nothing here because anything with a flame also emits light, and
        /// BEWindowDisplay.ApplyLight already exchanges the block on placement — so the
        /// ticker registers the moment a candle goes down. An item that emitted particles
        /// but no light would wait for the periodic rescan instead.
        ///
        /// **Not main-thread.** The rescan calls this from `OnSeperateThreadGameTick`, on
        /// the "blockticking" thread — see the note on HasParticleEmittingItem.
        /// </summary>
        public override bool ShouldReceiveClientParticleTicks(IWorldAccessor world, IPlayer byPlayer, BlockPos pos, ref EnumHandling handling)
        {
            try
            {
                if (world.BlockAccessor.GetBlockEntity(pos) is not BEWindowDisplay be) return false;
                if (!be.HasParticleEmittingItem()) return false;
            }
            catch (Exception e)
            {
                WarnOnceOffThread(world, e);
                return false;
            }

            handling = EnumHandling.PreventDefault;
            return true;
        }

        /// <summary>
        /// Runs on the async particle thread, so the block entity is fetched through
        /// <c>manager.BlockAccess</c> rather than the world accessor — vanilla's own
        /// BlockOilLamp reaches for Api.World here, which is not thread-safe.
        /// </summary>
        public override void OnAsyncClientParticleTick(IAsyncParticleManager manager, BlockPos pos, float windAffectednessAtPos, float secondsTicking)
        {
            try
            {
                if (manager.BlockAccess.GetBlockEntity(pos) is BEWindowDisplay be)
                {
                    be.SpawnStoredItemParticles(manager, pos, windAffectednessAtPos);
                }
            }
            catch (Exception e)
            {
                WarnOnceOffThread(null, e);
            }
        }

        private static bool _warnedOffThread;

        /// <summary>
        /// Both particle hooks run on client threads where nothing catches for us — an
        /// escaping exception ends the thread and drops the player to the main menu, as an
        /// NPE in ResolveBlocksOrItems did. Particles are decoration, so they are never
        /// worth that. Logged once rather than per tick, since these fire continuously.
        /// </summary>
        private void WarnOnceOffThread(IWorldAccessor world, Exception e)
        {
            if (_warnedOffThread) return;
            _warnedOffThread = true;

            ILogger logger = (world ?? capi?.World)?.Logger;
            logger?.Warning("[WindowDisplayLib] Stored-item particles failed and were skipped: {0}", e);
        }

        // ── Item sizing ─────────────────────────────────────────────────────

        /// <summary>
        /// Resolves the placement footprint / transform for a held or stored item.
        /// Same resolution order as vanilla so items authored for cabinets and
        /// shelves work unchanged.
        /// </summary>
        public static DisplayableAttributes GetDisplayableAttributes(ItemSlot slot, string displayType)
        {
            CollectibleObject collectible = slot?.Itemstack?.Collectible;
            if (collectible == null) return null;

            DisplayableAttributes fromInterface = collectible
                .GetCollectibleInterface<IDisplayableProps>()?.GetDisplayableProps(slot, displayType);
            if (fromInterface != null) return fromInterface;

            DisplayableAttributes fromAttributes = collectible.Attributes?["displayable"][displayType].AsObject<DisplayableAttributes>();
            if (fromAttributes != null) return fromAttributes;

            // Same inheritance for the interface path. Clutter declares its sizes through a
            // TypedDisplayableProps behaviour rather than plain attributes, and that class
            // does a bare TryGetValue(displayType) with no fallback — so asking for
            // "windowdisplay" returns null and every piece of clutter was rejected outright.
            // Its map also lives in behaviour properties, so the attributes fallback below
            // cannot see it either. Asking the interface again for "shelf" covers all of it,
            // and any other mod using the same behaviour, without patching ~100 entries.
            if (displayType != DefaultDisplayCategory)
            {
                DisplayableAttributes shelfFromInterface = collectible
                    .GetCollectibleInterface<IDisplayableProps>()
                    ?.GetDisplayableProps(slot, DefaultDisplayCategory);
                if (shelfFromInterface != null) return shelfFromInterface;
            }

            // Inherit vanilla's own shelf entry when this item has no windowdisplay one.
            //
            // Worth the extra step because that object carries far more than a transform:
            // Size, Behavior, Category and RandYRotAngle. The flag branches below can only
            // synthesise a Size and a Transform, leaving Behavior at Default — so a bowl,
            // which vanilla declares as `behavior: "Stacking"` with `size: {5,2,5}`, lost
            // both its stacking and its real footprint and fell back to a generic 6x4x6.
            //
            // Anything authored for a vanilla shelf therefore works here untouched, and a
            // patched `displayable.windowdisplay` still wins outright above. Note it wins
            // WHOLESALE, not merged: an override that wants stacking has to say so itself.
            if (displayType != DefaultDisplayCategory)
            {
                DisplayableAttributes fromShelf = collectible.Attributes?["displayable"][DefaultDisplayCategory]
                    .AsObject<DisplayableAttributes>();
                if (fromShelf != null) return fromShelf;
            }

            if (collectible.Attributes?.IsTrue("shelvable") == true)
            {
                return new DisplayableAttributes
                {
                    Size = DefaultItemSize,
                    Transform = collectible.Attributes["onshelfTransform"].AsObject<ModelTransform>()
                                ?? collectible.Attributes["onDisplayTransform"].AsObject<ModelTransform>()
                };
            }

            // Window-specific opt-in, so content can allow items vanilla would reject
            if (collectible.Attributes?.IsTrue("windowdisplayable") == true)
            {
                return new DisplayableAttributes
                {
                    Size = DefaultItemSize,
                    Transform = collectible.Attributes[TransformTarget].AsObject<ModelTransform>()
                };
            }

            return null;
        }

        /// <summary>
        /// The flat attribute name the in-game transform editor edits under. Vanilla's
        /// equivalent is "onshelfTransform".
        /// </summary>
        public const string TransformTarget = "onWindowDisplayTransform";

        /// <summary>
        /// Feeds the transform editor the currently resolved transform.
        ///
        /// The editor works in terms of a flat attribute name, but the real value lives
        /// at displayable.&lt;category&gt;.transform. This pair of listeners bridges the two,
        /// the same way vanilla bridges "onshelfTransform" to displayable.shelf.transform
        /// — which is why cabinets have no editor entry of their own to copy.
        /// </summary>
        /// <summary>
        /// Static so the mod system can register ONE listener rather than one per block
        /// type. It needs no particular block: the value comes from the HELD item, and the
        /// category is the same across every surface this mod ships.
        /// </summary>
        /// <summary>
        /// Which <c>displayable.&lt;category&gt;</c> entry the editor is working on: the one
        /// belonging to the window being aimed at, or the default when aiming at nothing.
        ///
        /// **Both editor handlers MUST use this.** They used to resolve it separately — the
        /// get side from the aimed block, the set side hardcoded to
        /// <see cref="DefaultDisplayCategory"/> — so the editor READ from
        /// `displayable.windowdisplay` (what every shipped marker declares) and WROTE to
        /// `displayable.shelf`. Editing an item therefore did nothing at all, or, on an item
        /// that happens to have a vanilla shelf entry, quietly edited THAT instead.
        ///
        /// One derivation, one place. This codebase's recurring bug is a value computed in
        /// more than one spot, and this is the fourth time it has been paid for.
        /// </summary>
        public static string ResolveEditorCategory(ICoreClientAPI capi)
        {
            BlockPos aimed = capi?.World.Player?.CurrentBlockSelection?.Position;
            if (aimed != null
                && capi.World.BlockAccessor.GetBlock(aimed)?.GetBehavior<BlockBehaviorWindowSurfaces>()
                   is BlockBehaviorWindowSurfaces bh)
            {
                return bh.EditorCategory;
            }

            return DefaultDisplayCategory;
        }

        /// <summary>
        /// Writes the edited transform back to <c>displayable.&lt;category&gt;.transform</c>
        /// on the held item, creating whatever part of that structure does not exist yet.
        ///
        /// **Creating it matters — that is the authoring case.** The old code only wrote when
        /// the entry was already there, so tuning an item that has no patch yet, which is
        /// exactly what the editor is for, silently did nothing.
        ///
        /// Every level is created explicitly rather than assumed. Vanilla's own firepit
        /// handler is the cautionary tale here: it creates the `attributes` object and then
        /// does `Token["inFirePitProps"]["transform"] = ...` on a key that does not exist,
        /// which is a NullReferenceException out of the GUI thread for any item that is not
        /// already firepit-renderable.
        /// </summary>
        public static void HandleSetTransform(ICoreClientAPI capi, TreeAttribute tree)
        {
            CollectibleObject collectible =
                capi?.World.Player?.InventoryManager?.ActiveHotbarSlot?.Itemstack?.Collectible;
            if (collectible == null) return;

            if (collectible.Attributes?.Token is not JObject root)
            {
                root = new JObject();
                collectible.Attributes = new JsonObject(root);
            }

            if (root["displayable"] is not JObject displayable)
            {
                displayable = new JObject();
                root["displayable"] = displayable;
            }

            string category = ResolveEditorCategory(capi);
            if (displayable[category] is not JObject entry)
            {
                entry = new JObject();
                displayable[category] = entry;
            }

            entry["transform"] = JToken.FromObject(ModelTransform.CreateFromTreeAttribute(tree));
        }

        public static void HandleGetTransform(ICoreClientAPI capi, TreeAttribute tree, ref EnumHandling handling)
        {
            ItemSlot held = capi?.World.Player?.InventoryManager?.ActiveHotbarSlot;
            if (held == null || held.Empty) return;

            DisplayableAttributes dattr = GetDisplayableAttributes(held, ResolveEditorCategory(capi));
            if (dattr?.Transform == null) return;

            handling = EnumHandling.PreventDefault;
            tree.SetBool("preventDefault", true);
            dattr.Transform.ToTreeAttribute(tree);
        }

        /// <summary>Category the editor reads and writes. First surface wins; they normally all match.</summary>
        public string EditorCategory => Surfaces.Length > 0 ? (Surfaces[0].DisplayCategory ?? "shelf") : "shelf";

        public static Size3f GetItemSize(ItemSlot slot, string displayType)
            => GetDisplayableAttributes(slot, displayType)?.Size;

        /// <summary>
        /// Items are positioned by their centre, so the mesh origin sits half a
        /// footprint back from the clicked pad.
        /// </summary>
        public static Vec3f GetCentreOffset(Size3f size) => new Vec3f(-size.Width / 2f, 0f, -size.Length / 2f);

        /// <summary>
        /// An item's width and length as they actually sit once its own rotation is applied.
        ///
        /// The declared <c>Size</c> describes the item unrotated. Everything reasoning about
        /// the space an item occupies — its selection box, what it collides with, whether it
        /// fits — has to use the turned footprint, or a rotated item looks one way and
        /// behaves another. That was the case for a long time: rotation was applied in
        /// <c>BuildSlotMatrix</c> to the MESH only, so wrenching a letter or a hammer turned
        /// the model while its box stayed in the original orientation. Invisible on a square
        /// item, plainly wrong on a long one.
        ///
        /// Snapped to the nearest quarter turn deliberately. A footprint is an axis-aligned
        /// box, so an item at 45° would need the bounding box of a rotated rectangle — which
        /// is LARGER than either orientation, meaning things would fit worse the more you
        /// turned them. That is the opposite of what rotating is for. Under 45° of turn keeps
        /// the original footprint; past it, width and length swap.
        /// </summary>
        public static Size3f RotatedFootprint(Size3f size, float rotDeg)
        {
            if (size == null) return null;

            float a = ((rotDeg % 180f) + 180f) % 180f;      // 0..180; a half turn is symmetric
            return a > 45f && a < 135f
                ? new Size3f(size.Length, size.Height, size.Width)
                : size;
        }

        // ── Placement preview ───────────────────────────────────────────────

        public void RenderSelectionBoxes(BlockSelection blockSel, RenderBoxDelegate renderBoxHandler)
        {
            float lineWidth = 1.6f * capi.Settings.Float["wireframethickness"];

            if (capi.World.BlockAccessor.GetBlockEntity(blockSel.Position) is not BEWindowDisplay be)
            {
                renderBoxHandler(Cuboidf.Default(), lineWidth, block.GetSelectionColor(capi, blockSel.Position));
                return;
            }

            Cuboidf[] selectionBoxes = block.GetSelectionBoxes(capi.World.BlockAccessor, blockSel.Position);
            if (blockSel.SelectionBoxIndex >= selectionBoxes.Length) return;

            Cuboidf hovered = selectionBoxes[blockSel.SelectionBoxIndex];
            string boxId = blockSel.SelectionBoxId;
            WindowSlotId loc = WindowSlotId.Decode(boxId);

            ItemSlot heldSlot = capi.World.Player.Entity.RightHandItemSlot;

            // Not aiming at a placement pad, or nothing placeable in hand — plain outline
            if (loc == null || loc.IsPlacedItem || heldSlot == null || heldSlot.Empty)
            {
                // The outline around an item already on the sill can be switched off. Note
                // this deliberately does NOT cover loc == null, which is the frame and pane
                // boxes — those are how you open a window and swap it, and hiding them would
                // leave the block with nothing to aim at at all.
                if (loc is { IsPlacedItem: true } && !ShouldDrawPlacedItemBox()) return;

                renderBoxHandler(hovered, lineWidth, block.GetSelectionColor(capi, blockSel.Position));
                return;
            }

            WindowPlacementSurface surface = GetSurface(loc.SurfaceIndex);
            if (surface == null)
            {
                renderBoxHandler(hovered, lineWidth, block.GetSelectionColor(capi, blockSel.Position));
                return;
            }

            Size3f itemSize = GetItemSize(heldSlot, surface.DisplayCategory ?? "shelf");
            if (itemSize == null)
            {
                renderBoxHandler(hovered, lineWidth, block.GetSelectionColor(capi, blockSel.Position));
                return;
            }

            // Preview the TURNED footprint. This is the whole value of rotating before
            // placing: you can see a long item swing across the sill and go from red to
            // black before you commit, rather than placing and discovering it did not fit.
            itemSize = RotatedFootprint(itemSize, PreviewRotationDeg(be, surface));

            Vec3f offset = GetCentreOffset(itemSize);
            bool fits = FitsAt(be, surface, loc, itemSize);

            // Undo the block rotation, build the preview in local space, rotate back
            Cuboidf local = hovered.RotatedCopyRad(0f, -be.MeshAngleRad, 0f, new Vec3d(0.5, 0.0, 0.5));

            Cuboidf preview = new Cuboidf(0f, 0.0625f, 0f,
                                          itemSize.Width / 16f,
                                          itemSize.Height / 16f + 0.0625f,
                                          itemSize.Length / 16f)
                .Translate(local.Start)
                .Translate(offset.X / 16f, 0f, offset.Z / 16f)
                .RotatedCopyRad(0f, be.MeshAngleRad, 0f, new Vec3d(0.5, 0.0, 0.5));

            if (!ShouldDrawFootprintBox()) return;

            renderBoxHandler(preview, lineWidth,
                fits ? new Vec4f(0f, 0f, 0f, 0.5f) : new Vec4f(0.5f, 0f, 0f, 0.5f));
        }

        /// <summary>
        /// Whether to draw the footprint outline, per the `PlacementBox` setting:
        /// 0 never, 1 only when the item will not fit, 2 always.
        ///
        /// The ghost made the box arguably redundant — it shows the item where it will land
        /// and reddens the same way — but the two do not say quite the same thing. The ghost
        /// shows the ITEM; the box shows the FOOTPRINT, and those genuinely differ: seashells
        /// declare a deliberately tight box so more fit along a sill, and the ruler is 12.8
        /// wide but paper thin. Judging where the next item can go is the box's job.
        ///
        /// Hence a setting rather than a decision made for everyone, and hence mode 1 —
        /// which shows the box exactly when it is telling you something, and gets out of the
        /// way otherwise.
        ///
        /// **Never hidden while the ghost is off.** Turning both off would leave a bare pad
        /// grid with no indication of what is about to happen anywhere, which is not a state
        /// worth letting anyone configure themselves into by accident.
        /// </summary>
        /// <summary>
        /// Live override from the toggle key, for lining something up precisely without
        /// going to the config and back. Client-side and deliberately not persisted: it is
        /// a "show me while I do this" switch, not a preference — the preference is
        /// `PlacementBox`, and this returns to it next session.
        /// </summary>
        public static bool ForceFootprintBox;

        /// <summary>
        /// Whether to outline an item already on the sill. Same toggle key overrides it, and
        /// the same safety rule applies: never hidden while the ghost is off, since then
        /// nothing at all would indicate what you are pointing at.
        /// </summary>
        private static bool ShouldDrawPlacedItemBox()
        {
            if (ForceFootprintBox) return true;

            WindowDisplayLibConfig cfg = WindowDisplayLibConfig.Current;
            if (cfg?.PlacementGhost != true) return true;

            return cfg.ShowPlacedItemBox;
        }

        private static bool ShouldDrawFootprintBox()
        {
            if (ForceFootprintBox) return true;

            WindowDisplayLibConfig cfg = WindowDisplayLibConfig.Current;
            if (cfg?.PlacementGhost != true) return true;

            return cfg.ShowPlacementBox;
        }

        /// <summary>
        /// The angle the item in hand would be placed at on this surface: the player's
        /// pending quarter turn plus the surface's own facing.
        ///
        /// Shared by the outline box and the ghost mesh deliberately. A value worked out
        /// in two places is this codebase's recurring bug — the mesh, the footprint and
        /// the particles each derived an item's angle separately and produced three
        /// distinct faults before they were unified on ItemRotationDeg.
        /// </summary>
        public float PreviewRotationDeg(BEWindowDisplay be, WindowPlacementSurface surface)
            => PlacementRotation.For(capi, capi.World.Player) + be.SurfaceFacingDeg(surface);

        /// <summary>
        /// Whether an item of this ALREADY-ROTATED footprint can be placed centred on
        /// <paramref name="loc"/>. Three independent gates, in the order they bite:
        /// the item must fit wholly inside the surface from that centre, it must not
        /// exceed the surface in any dimension at all, and it must not overlap something
        /// already there.
        ///
        /// Extracted so the outline box and the ghost mesh cannot drift apart about what
        /// "fits" means — they colour themselves from the same answer.
        /// </summary>
        public static bool FitsAt(BEWindowDisplay be, WindowPlacementSurface surface,
                                  WindowSlotId loc, Size3f rotatedSize)
        {
            if (be == null || surface == null || loc == null || rotatedSize == null) return false;

            Vec3f offset = GetCentreOffset(rotatedSize);

            if (loc.X + offset.X < 0f || loc.Z + offset.Z < 0f
                || loc.X + offset.X > surface.Size.Width - rotatedSize.Width
                || loc.Z + offset.Z > surface.Size.Length - rotatedSize.Length) return false;

            if (surface.Size.Width < rotatedSize.Width
                || surface.Size.Height < rotatedSize.Height
                || surface.Size.Length < rotatedSize.Length) return false;

            return be.GetCollidingSlotId(loc, new Cuboidf(rotatedSize)) == null;
        }

        /// <summary>
        /// Everything the placement ghost needs, resolved from what the player is aiming
        /// at — or false when there is nothing to preview.
        ///
        /// Deliberately mirrors the early-outs of <see cref="RenderSelectionBoxes"/> so the
        /// ghost appears exactly when the footprint box does and never on its own.
        /// </summary>
        public bool TryGetGhost(BlockSelection blockSel, ItemSlot heldSlot,
                                out BEWindowDisplay be, out WindowSlotId loc,
                                out DisplayableAttributes dattr, out float rotDeg, out bool fits)
        {
            be = null; loc = null; dattr = null; rotDeg = 0f; fits = false;

            if (blockSel == null || heldSlot == null || heldSlot.Empty) return false;

            be = capi.World.BlockAccessor.GetBlockEntity(blockSel.Position) as BEWindowDisplay;
            if (be == null) return false;

            loc = WindowSlotId.Decode(blockSel.SelectionBoxId);
            if (loc == null || loc.IsPlacedItem) return false;

            WindowPlacementSurface surface = GetSurface(loc.SurfaceIndex);
            if (surface == null) return false;

            dattr = GetDisplayableAttributes(heldSlot, surface.DisplayCategory ?? "shelf");
            if (dattr?.Size == null) return false;

            rotDeg = PreviewRotationDeg(be, surface);
            fits = FitsAt(be, surface, loc, RotatedFootprint(dattr.Size, rotDeg));
            return true;
        }

        // ── Swap aim point ──────────────────────────────────────────────────

        /// <summary>
        /// Whether the box under the cursor is somewhere a ctrl+wrench block swap may be
        /// taken from.
        ///
        /// Normally that is the frame or a pane, which is what a player sees and aims at.
        /// A block may legitimately declare NO frame boxes at all, though — the custom
        /// chiselled window does, so that the only selection box in the block is the
        /// chiselled block the player put there — and then the swap had no aim point
        /// anywhere and was simply unreachable, silently.
        ///
        /// So with no frame boxes declared, any box except a placed item stands in for the
        /// frame. Grid pads do nothing on a ctrl+wrench otherwise, so nothing is taken away.
        ///
        /// The placed-item exclusion is kept, but its ORIGINAL reason is gone: it used to
        /// stop the swap colliding with ctrl+wrench reverse-rotation, which was removed on
        /// 2026-07-30 when rotation moved to the mouse wheel. It stays because swapping the
        /// whole block by clicking an item sitting on it would be a surprising thing to do,
        /// not because anything else now competes for that click.
        ///
        /// Deliberately NOT the same question as <c>onFrame</c> at the call site, which
        /// also gates pane toggling and the fall-through to item placement — widening that
        /// would make a pad click return early and stop items being placed at all.
        /// </summary>
        public static bool IsSwapAimPoint(BlockBehaviorWindowSurfaces bh, string boxId)
        {
            if (boxId == null
                || boxId.StartsWith("pane", StringComparison.Ordinal)
                || boxId.StartsWith("frame", StringComparison.Ordinal))
            {
                return true;
            }

            if (bh == null || bh.HasAnyFrameBox) return false;

            return !boxId.StartsWith(WindowSlotId.PlacedPrefix, StringComparison.Ordinal);
        }

        /// <summary>
        /// Whether any frame box is actually produced, rather than whether the attribute
        /// exists — a group declaring an empty <c>staticFrameBoxes</c> and no open/closed
        /// pane box contributes nothing to aim at, same as no group at all.
        /// </summary>
        public bool HasAnyFrameBox
        {
            get
            {
                for (int i = 0; i < FrameBoxGroups.Length; i++)
                {
                    FrameBoxGroup g = FrameBoxGroups[i];
                    if (g.ClosedFrameBox != null || g.OpenFrameBox != null) return true;
                    if (g.StaticFrameBoxes != null && g.StaticFrameBoxes.Length > 0) return true;
                }
                return false;
            }
        }

        // ── Interaction help ────────────────────────────────────────────────

        public override WorldInteraction[] GetPlacedBlockInteractionHelp(IWorldAccessor world, BlockSelection selection, IPlayer forPlayer, ref EnumHandling handling)
        {
            WorldInteraction[] help = base.GetPlacedBlockInteractionHelp(world, selection, forPlayer, ref handling);

            string boxId = selection.SelectionBoxId;
            bool onFrame = boxId == null
                           || boxId.StartsWith("pane", StringComparison.Ordinal)
                           || boxId.StartsWith("frame", StringComparison.Ordinal);
            if (!onFrame && !IsSwapAimPoint(this, boxId)) return help;

            if (onFrame && block.Attributes?["canOpen"].AsBool(false) == true)
            {
                help = help.Append(new WorldInteraction
                {
                    ActionLangCode = "windowdisplaylib:blockhelp-window-toggle",
                    MouseButton = EnumMouseButton.Right
                });
            }

            if (WindowSwapHelper.HasSwapTargets(block))
            {
                if (BlockBehaviorWrenchOrientable.wrenchItems == null)
                {
                    BlockBehaviorWrenchOrientable.loadWrenchItems(world);
                }

                help = help.Append(new WorldInteraction
                {
                    ActionLangCode = "windowdisplaylib:blockhelp-window-swap",
                    // Must match the modifier BEWindowDisplay actually reads for the swap
                    // (controls.CtrlKey). These drifted apart once already — the help said
                    // ctrl while the code took sprint-OR-ctrl, so which key worked depended
                    // on the player's bindings and looked intermittent. Change both together.
                    HotKeyCode = "ctrl",
                    MouseButton = EnumMouseButton.Right,
                    Itemstacks = BlockBehaviorWrenchOrientable.wrenchItems
                });
            }

            return help;
        }
    }
}
