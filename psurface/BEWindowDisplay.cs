using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AttributeRenderingLibrary;
using Vintagestory.Client.NoObf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace WindowDisplayLib
{
    /// <summary>
    /// psurface-based window storage block entity.
    ///
    /// Derives from <see cref="BlockEntityContainer"/> rather than hosting vanilla's
    /// <c>BEBehaviorDisplay</c>, for three reasons vanilla cannot cover:
    ///
    ///  * <see cref="IContainedInteractable"/> takes a concrete BlockEntityContainer,
    ///    so cooking pots / crocks can only be interacted with from one
    ///  * stored items drive block light, which needs a relight on content change
    ///  * the block mesh is built through AttributeRenderingLibrary
    ///
    /// The psurface *format* is kept identical to vanilla — shape element names and
    /// string slot ids — so shapes authored for cabinets work unchanged.
    /// </summary>
    public class BEWindowDisplay : BlockEntityContainer, ITexPositionSource
    {
        // ── Inventory ────────────────────────────────────────────────────────
        protected InventoryInfinite inv;
        public override InventoryBase Inventory => inv;
        public override string InventoryClassName => "windowstorage";

        // ── Placement / render state ─────────────────────────────────────────
        public float MeshAngleRad;
        public bool[] paneStates = Array.Empty<bool>();
        public Dictionary<string, float> customRotationDegBySlot;
        public Dictionary<string, float[]> TfMatrices = new Dictionary<string, float[]>();

        protected ICoreClientAPI capi;
        protected MeshData blockMesh;
        protected bool meshesGenerated;

        private Cuboidf[] _padOnlyBoxes;
        private Cuboidf[] _allBoxes;
        private Cuboidf[] _collisionBoxes;

        private BlockEntityAnimationUtil animUtil;
        private WindowSoundHandler soundHandler;
        private byte[] _cachedLightHsv;
        private bool _removed;

        // What the current AnimatableRenderer was actually built for. Rotation is
        // baked in at renderer construction and ExchangeBlock keeps the existing
        // block entity, so neither a placement nor a swap can be detected by
        // watching for a *change* — we have to compare against what was built.
        private string _animatorShapeKey;
        private float _animatorAngleRad = float.NaN;
        private int _meshBlockId = -1;
        private int _paneStateBlockId = -1;
        private long _clientTickListener;

        private CollectibleObject _nowTesselatingObj;
        private Shape _nowTesselatingShape;

        public byte[] CachedLightHsv => _cachedLightHsv;

        public BlockBehaviorWindowSurfaces SurfaceBehavior => Block?.GetBehavior<BlockBehaviorWindowSurfaces>();

        public string AttributeTransformCode => BlockBehaviorWindowSurfaces.TransformTarget;

        protected Dictionary<string, MeshData> MeshCache =>
            ObjectCacheUtil.GetOrCreate(Api, "windowdisplaylib-itemmeshes", () => new Dictionary<string, MeshData>());

        /// <summary>
        /// Defaults to true. A group with no openFrameBox/closedFrameBox already cannot
        /// toggle, so styles that are purely static need no attribute — this flag is only
        /// needed to seal a window that *does* have openable pane groups.
        ///
        /// Note it gates the whole block: setting it false stops every pane, including
        /// any isWindow:false interior door.
        /// </summary>
        public bool CanOpen => Block?.Attributes?["canOpen"].AsBool(true) ?? true;

        /// <summary>
        /// Suppresses the two deliberate irregularities an item gets on placement: the
        /// random resting angle from its <c>RandYRotAngle</c> and the small vertical
        /// scale wobble in <see cref="BuildSlotMatrix"/>. Both exist so a row of stored
        /// clutter does not look machine-stamped, which is right for a sill of jars and
        /// wrong for anything meant to read as part of the building.
        ///
        /// Declared on the BLOCK rather than per item, because it is a property of what
        /// the surface is for: the custom chiselled window holds a block the player
        /// aligned by hand, so a 15 degree lean or a 3% stretch is a defect there. An
        /// item's own <c>randYRotAngle: 0</c> cannot cover it — a chiselled block gets
        /// its DisplayableAttributes from <c>BlockMicroBlock</c>'s IDisplayableProps
        /// implementation, which wins over attributes in the resolution order, so there
        /// is no JSON on the item side to patch.
        /// </summary>
        public bool NoPlacementJitter => Block?.Attributes?["noPlacementJitter"].AsBool(false) ?? false;

        /// <summary>
        /// Whether the random resting angle is off for this block, by config or block flag.
        ///
        /// This also gated a vertical scale wobble until that was removed on 2026-07-28 —
        /// see BuildSlotMatrix. The two were briefly gated separately, which left stacks
        /// gapping and z-fighting with the config off, so if anything random is ever added
        /// back it reads THIS and not a switch of its own.
        /// </summary>
        private bool JitterSuppressed =>
            WindowDisplayLibConfig.Current?.PlacementJitter == false || NoPlacementJitter;

        public bool IsAnyWindowPaneOpen
        {
            get
            {
                var groups = SurfaceBehavior?.FrameBoxGroups;
                if (groups == null || paneStates == null) return false;
                for (int i = 0; i < paneStates.Length && i < groups.Length; i++)
                {
                    if (groups[i].IsWindow && paneStates[i]) return true;
                }
                return false;
            }
        }

        public BEWindowDisplay()
        {
            inv = new InventoryInfinite((slotId, invBase) =>
            {
                WindowSlotId loc = WindowSlotId.Decode(slotId);
                string category = SurfaceBehavior?.GetDisplayCategory(loc?.SurfaceIndex ?? 0) ?? "shelf";
                return new ItemSlotDisplay(invBase, category);
            });
        }

        // ── Lifecycle ────────────────────────────────────────────────────────

        public override void Initialize(ICoreAPI api)
        {
            base.Initialize(api);
            capi = api as ICoreClientAPI;
            _removed = false;

            int paneCount = SurfaceBehavior?.FrameBoxGroups.Length ?? 0;
            if (paneStates == null || paneStates.Length != paneCount) ClampPaneStatesToBlock();

            inv.OnAcquireTransitionSpeed += OnAcquireTransitionSpeed;

            // NOTE: deliberately not subscribing to inv.SlotModified.
            // InventoryInfinite overrides DidModifyItemSlot to an empty method, so
            // SlotModified never fires for this inventory type. Content changes are
            // signalled explicitly through OnContentsChanged() instead.

            UpdateLightCache();

            if (capi != null)
            {
                animUtil = new BlockEntityAnimationUtil(capi, this);
                soundHandler = new WindowSoundHandler(this, capi);
                EnsureAnimatorCurrent();
                UpdateAnimationState();

                // Main-thread safety net: catches client-side placement (where the
                // angle is set after Initialize) and wrenchSwapTo (where the block
                // changes under a surviving block entity).
                _clientTickListener = RegisterGameTickListener(OnClientTick, 250);

                // Join the live registry instead of registering an event bus listener.
                // See the comment on LiveClientInstances for why that distinction matters.
                LiveClientInstances.Add(this);
            }
        }

        /// <summary>
        /// Every client-side window currently loaded.
        ///
        /// EXISTS BECAUSE EVENT BUS LISTENERS CANNOT BE UNREGISTERED. `IEventAPI` offers
        /// `UnregisterCallback` and `UnregisterGameTickListener` and nothing for the event
        /// bus — `RegisterEventBusListener` is one-way. This block entity used to register
        /// one per instance for the transform editor, so every window on every chunk load
        /// added a permanent listener holding that block entity alive: memory that was
        /// never released, and a handler list that grew for the whole session.
        ///
        /// Now the mod system registers ONE listener and walks this set, which is
        /// maintained honestly — added in Initialize, removed in Cleanup, and Cleanup runs
        /// from all three teardown paths. So it tracks what is actually loaded rather than
        /// everything that ever was.
        ///
        /// Client only, main thread only: Initialize and Cleanup both run there, and so
        /// does the event bus.
        /// </summary>
        public static readonly HashSet<BEWindowDisplay> LiveClientInstances = new HashSet<BEWindowDisplay>();

        /// <summary>
        /// Applies an edited transform to this window, if it is close enough to be worth
        /// redrawing. Driven by the mod system's single event bus listener.
        ///
        /// Writing the value onto the collectible is global and only needs doing once, so
        /// the caller does that; this is the per-window half — dropping the cached meshes
        /// and boxes so the change shows.
        /// </summary>
        public void ApplyTransformEdit()
        {
            if (capi?.World.Player?.Entity == null || Pos == null) return;
            if (Pos.DistanceTo(capi.World.Player.Entity.Pos.XYZ) > 20f) return;

            MeshCache.Clear();
            InvalidateBoxes();   // an edited transform can change an item's footprint
            MarkMeshesDirty();
        }

        private void OnClientTick(float dt)
        {
            if (_removed || capi == null || Block == null) return;

            // Only the poll advances the give-up budget, so a hot path like tesselation
            // cannot burn through it and defeat the readiness gate
            if (!ArlVariantsReady) _variantWaitTicks++;

            // A wrenchSwapTo target can have a different number of pane groups
            if (_paneStateBlockId != Block.Id)
            {
                _paneStateBlockId = Block.Id;
                ClampPaneStatesToBlock();
            }

            EnsureAnimatorCurrent();

            // Idempotent — only starts or stops an animation when the active set does
            // not match pane state, so it is safe to run every tick and lets the panes
            // converge if any single update was missed.
            UpdateAnimationState();

            // Drives the shared rain-on-glass loop (EnableRainSound / Volume / Range)
            soundHandler?.Tick();
        }

        public override void OnBlockPlaced(ItemStack byItemStack = null)
        {
            // base runs the ARL behaviour, which is where Variants get filled from the
            // stack. Everything texture-dependent has to happen after this line, not in
            // Initialize — that runs first, with no variants, and is why a freshly
            // placed window used to show default textures before correcting itself.
            base.OnBlockPlaced(byItemStack);

            ClampPaneStatesToBlock();

            if (Api?.Side == EnumAppSide.Server) MarkDirty(true);
            else RefreshAnimator();
        }

        public override void OnBlockRemoved()
        {
            _removed = true;
            RemoveLight();
            Cleanup();
            base.OnBlockRemoved();
        }

        public override void OnBlockUnloaded()
        {
            _removed = true;
            Cleanup();
            base.OnBlockUnloaded();
        }

        public override void OnBlockBroken(IPlayer byPlayer = null)
        {
            _removed = true;
            RemoveLight();

            // Cleanup here too, matching OnBlockRemoved and OnBlockUnloaded. In practice
            // breaking sets the block to air and OnBlockRemoved follows, which cleaned up
            // anyway — but that was an assumption about ordering rather than something
            // guaranteed, and the asymmetry read as an oversight. Cleanup is idempotent:
            // the unsubscribe is safe twice, the tick listener is guarded on != 0, and the
            // disposals null their own fields.
            Cleanup();

            base.OnBlockBroken(byPlayer);
        }

        private void Cleanup()
        {
            // Leaving the registry is the whole point of it — see LiveClientInstances.
            // Cleanup runs from OnBlockRemoved, OnBlockUnloaded and OnBlockBroken, so a
            // window cannot stay in the set after it has gone.
            LiveClientInstances.Remove(this);

            inv.OnAcquireTransitionSpeed -= OnAcquireTransitionSpeed;
            if (_clientTickListener != 0)
            {
                UnregisterGameTickListener(_clientTickListener);
                _clientTickListener = 0;
            }
            animUtil?.Dispose();
            animUtil = null;
            soundHandler?.Dispose();
            soundHandler = null;
            blockMesh = null;
            meshesGenerated = false;
            _animatorShapeKey = null;
            _animatorAngleRad = float.NaN;
        }

        /// <summary>Resizes pane state to match the current block's frameBoxGroups, preserving overlap.</summary>
        public void ClampPaneStatesToBlock()
        {
            int paneCount = SurfaceBehavior?.FrameBoxGroups.Length ?? 0;
            bool[] resized = new bool[paneCount];
            if (paneStates != null)
            {
                Array.Copy(paneStates, resized, Math.Min(paneStates.Length, paneCount));
            }
            paneStates = resized;
            InvalidateBoxes();
        }

        /// <summary>
        /// Rebuilds the animator right now, on the caller's thread.
        ///
        /// Called client-side from TryPlaceBlock the moment MeshAngleRad is set. The
        /// block entity is created during base.TryPlaceBlock, so Initialize builds
        /// the renderer while the angle is still 0; without this the correction only
        /// lands on the next client tick and you see the window snap into place.
        /// </summary>
        public void RefreshAnimator()
        {
            if (capi == null) return;
            EnsureAnimatorCurrent();
            UpdateAnimationState();
        }

        /// <summary>Called after a wrenchSwapTo exchange to rebuild everything block-derived.</summary>
        public void RefreshAfterSwap()
        {
            InvalidateBoxes();
            MarkMeshesDirty();
            if (capi != null)
            {
                InitializeAnimator();
                UpdateAnimationState();
            }
            UpdateLightCache();
        }

        // ── Perish rate ──────────────────────────────────────────────────────

        private float OnAcquireTransitionSpeed(EnumTransitionType type, ItemStack stack, float baseMul)
        {
            if (type != EnumTransitionType.Perish || Api == null) return baseMul;

            Room room = container.Room;

            bool effectivelyOpen =
                WindowDisplayLibConfig.Current != null && WindowDisplayLibConfig.Current.RoomSafeOpening
                    ? room != null && room.ExitCount > 0
                    : IsAnyWindowPaneOpen || (room != null && room.ExitCount > 0);

            return baseMul * (effectivelyOpen ? 2.0f : 0.5f);
        }

        /// <summary>
        /// Must be called after every inventory mutation. InventoryInfinite never
        /// raises SlotModified (DidModifyItemSlot is an empty override), so nothing
        /// else will do this for us.
        /// </summary>
        public void OnContentsChanged()
        {
            InvalidateBoxes();
            MarkMeshesDirty();

            // Both sides, matching vanilla BlockEntityGroundStorage, which calls
            // LightUpdate straight out of its interaction handler with no side check.
            // Light is computed client-side for rendering as well as server-side; if
            // only the server relights, the client shows nothing until the chunk is
            // reloaded — which is exactly the "only updates on reload" behaviour.
            if (Api != null)
            {
                byte[] oldLight = _cachedLightHsv;
                UpdateLightCache();

                bool changed = (oldLight == null) != (_cachedLightHsv == null)
                    || (oldLight != null && _cachedLightHsv != null &&
                        (oldLight[0] != _cachedLightHsv[0] || oldLight[1] != _cachedLightHsv[1] || oldLight[2] != _cachedLightHsv[2]));

                if (changed)
                {
                    // ONLY strip the old light when there is no new one to overwrite it.
                    //
                    // This used to be an unconditional RemoveBlockLight followed immediately
                    // by ApplyLight — which is, literally, turn the light off and then turn
                    // it back on, and the player sees the gap between the two as a flash.
                    // Vanilla does not do it: BlockEntityGroundStorage.LightUpdate is a bare
                    // ExchangeBlock(Block.Id, Pos) with no removal, because the exchange
                    // re-registers the block's light and overwrites whatever was there.
                    //
                    // The removal is still needed in exactly one case — the last light-
                    // emitting item leaving. ApplyLight early-returns when _cachedLightHsv is
                    // null, so with nothing to re-register the old light would otherwise sit
                    // there until the chunk reloaded.
                    if (oldLight != null && _cachedLightHsv == null)
                    {
                        Api.World.BlockAccessor.RemoveBlockLight(oldLight, Pos);
                    }

                    ApplyLight();
                }
            }

            MarkDirty(true);
        }

        // ── Light from stored items ──────────────────────────────────────────

        private void UpdateLightCache()
        {
            if (Api == null) return;

            // Resolve first, as every path that reads slot.Itemstack.Collectible must: an
            // unresolved stack reads Collectible as null and is silently skipped, so a
            // lantern would contribute no light and the window would stay dark until the
            // next OnContentsChanged — "the light only appears once you touch it".
            //
            // Both callers (Initialize and OnContentsChanged) are on the main thread, so
            // this is safe here. The ONE place that must not resolve is the off-thread
            // particle gate — see HasParticleEmittingItem, which explains why.
            inv.ResolveBlocksOrItems();

            byte bestH = 0, bestS = 0, bestV = 0;
            foreach (ItemSlot slot in inv)
            {
                ItemStack stack = slot?.Itemstack;
                if (stack?.Collectible == null) continue;

                // GetLightHsv(..., stack), not the raw LightHsv field. For lanterns and
                // anything else whose brightness depends on stack attributes the field
                // is null and only the method returns the real value.
                byte[] hsv = stack.Collectible.GetLightHsv(Api.World.BlockAccessor, null, stack);
                if (hsv == null || hsv.Length < 3) continue;
                if (hsv[2] > bestV) { bestH = hsv[0]; bestS = hsv[1]; bestV = hsv[2]; }
            }
            _cachedLightHsv = bestV > 0 ? new[] { bestH, bestS, bestV } : null;
        }

        private void RemoveLight()
        {
            if (Api != null && _cachedLightHsv != null)
            {
                Api.World.BlockAccessor.RemoveBlockLight(_cachedLightHsv, Pos);
            }
        }

        /// <summary>
        /// Forces the engine to re-read GetLightHsv by exchanging the block with
        /// itself — the same thing vanilla BlockEntityGroundStorage.LightUpdate does.
        ///
        /// This must run synchronously. Deferring it through EnqueueMainThreadTask
        /// leaves the relight out of the block-change batch, so the new light level
        /// only appears when the chunk is next loaded from disk. The interaction
        /// handler is already on the main thread, so there is nothing to defer for.
        ///
        /// No state snapshot is needed either: ExchangeBlock keeps the existing
        /// block entity, so `this` survives the call.
        /// </summary>
        private void ApplyLight()
        {
            if (Api == null || Pos == null || _cachedLightHsv == null) return;
            if (_removed) return;
            if (Api.World.BlockAccessor.GetBlock(Pos).Id != Block.Id) return;

            Api.World.BlockAccessor.ExchangeBlock(Block.Id, Pos);
        }

        // ── Particles from stored items ──────────────────────────────────────

        /// <summary>
        /// True when anything stored here can emit particles, so the block should be
        /// registered for client particle ticks.
        ///
        /// **Runs off the main thread.** `SystemClientTickingBlocks` asks this from two
        /// places — `OnBlockChanged` on the main thread, but also its periodic rescan from
        /// `OnSeperateThreadGameTick`, on the "blockticking" thread. Assuming main-thread
        /// only crashed the client straight to the main menu.
        ///
        /// So this must NOT call <c>ResolveBlocksOrItems</c>, despite that being the rule
        /// everywhere else in this class. That method dereferences the inventory's own
        /// <c>Api</c>, which is null until the inventory is initialised — the actual NPE —
        /// and it also *writes* (<c>slot.Itemstack = null</c> for stacks it cannot
        /// resolve), which is not something to do from a background thread at all. Vanilla
        /// agrees: <c>BlockGroundStorage.ShouldReceiveClientParticleTicks</c> enumerates
        /// and null-checks without resolving.
        ///
        /// An unresolved stack therefore just reads as null and registers nothing this
        /// pass. That self-corrects — the rescan runs every ~20s and on player movement,
        /// and the normal BE paths resolve on load — so the cost is a late flame, not a
        /// missing one.
        /// </summary>
        public bool HasParticleEmittingItem()
        {
            foreach (var kv in SlotsSnapshot())
            {
                if (ParticlesFor(kv.Value, out _) != null) return true;
            }
            return false;
        }

        /// <summary>
        /// Copy of the stored (id, slot) pairs, for the two background threads that drive
        /// particles to read without tripping over the main thread.
        ///
        /// <c>InventoryInfinite</c> creates slots on demand, so the backing dictionary can
        /// grow while it is being enumerated — which throws, and a throw on either of
        /// those threads takes the client down rather than being caught anywhere. The copy
        /// itself can lose that race too, hence the catch: dropping one pass is fine, the
        /// next tick picks it up.
        /// </summary>
        private KeyValuePair<string, ItemSlot>[] SlotsSnapshot()
        {
            try
            {
                var slots = inv?.SlotsByslotId;
                if (slots == null) return Array.Empty<KeyValuePair<string, ItemSlot>>();
                return slots.ToArray();
            }
            catch (Exception)
            {
                return Array.Empty<KeyValuePair<string, ItemSlot>>();
            }
        }

        /// <summary>
        /// Our own key inside <c>displayable.windowdisplay</c>, in VOXELS, naming where an
        /// item's flame actually sits relative to the centre of its box.
        ///
        /// **Presence of this key is what switches particles on at all.** Emitting for
        /// anything that merely declares particleProperties looked wrong far more often
        /// than right, because most emitters are not centre-wicked and nothing in JSON
        /// says where their wick is — vanilla hardcodes those positions in C#
        /// (<c>BlockOilLamp</c>'s -5/32, <c>BlockBunchOCandles.candleWickPositions</c>) and
        /// <c>Block.TopMiddlePos</c> is only a box centre. So this is authored per item,
        /// the same way sizes are, and anything untuned stays silent — which is also what
        /// vanilla shelves do, so nothing can look broken by default.
        ///
        /// Read straight off the JSON because <c>DisplayableAttributes</c> is a vanilla
        /// POCO with no field for it; it ignores keys it does not know, so ours rides
        /// along harmlessly. <c>particleOffsetByType</c> works for free — Vintage Story's
        /// own solveByType resolves ByType keys recursively before we ever read this.
        ///
        /// Authored UNROTATED: the offset is turned by the item's own placement rotation
        /// at spawn time, so one value stays on the wick however the item or the window is
        /// turned. Y is measured from the top of the box, where particles already spawn,
        /// so 0 means "top of the item".
        /// </summary>
        private Vec3f ParticleOffsetFor(CollectibleObject collectible)
        {
            JsonObject node = collectible?.Attributes?["displayable"]?["windowdisplay"]?["particleOffset"];
            if (node == null || !node.Exists) return null;

            return new Vec3f(node["x"].AsFloat(0f), node["y"].AsFloat(0f), node["z"].AsFloat(0f));
        }

        /// <summary>
        /// The particle set a stored stack should emit, or null.
        ///
        /// Read off the collectible's own <c>particleProperties</c> rather than through
        /// vanilla's <see cref="IGroundStoredParticleEmitter"/>. That interface exists,
        /// but every implementation of it is written for ground storage: BlockOilLamp
        /// hardcodes a -5/32 quadrant correction and reads
        /// <c>BlockEntityGroundStorage.MeshAngle</c>, which at our position is simply
        /// absent — so the flame would land about 2.5 voxels off and ignore the window's
        /// own rotation. We know where the item is far better than it does.
        ///
        /// Reading the JSON directly also generalises: <c>particleProperties</c> is
        /// content, not code, so any modded block that emits particles works with no
        /// patch from us, where the interface only covers mods that deliberately
        /// implemented it. The trade is that a particle effect driven by code rather
        /// than JSON is missed — BlockMeal's steam is the one vanilla case.
        ///
        /// <see cref="IGroundStoredParticleEmitter.ShouldSpawnGSParticles"/> is still
        /// honoured when present, so "only while lit / only while hot" conditions hold.
        ///
        /// Opt-in: nothing emits without an authored <c>particleOffset</c>. See
        /// <see cref="ParticleOffsetFor"/> for why.
        /// </summary>
        private AdvancedParticleProperties[] ParticlesFor(ItemSlot slot, out Vec3f offsetVoxels)
        {
            offsetVoxels = null;

            CollectibleObject collectible = slot?.Itemstack?.Collectible;
            if (collectible == null) return null;

            offsetVoxels = ParticleOffsetFor(collectible);
            if (offsetVoxels == null) return null;

            Block block = collectible as Block ?? BlockBehindItem(collectible);
            if (block?.ParticleProperties == null || block.ParticleProperties.Length == 0) return null;

            var gate = collectible.GetCollectibleInterface<IGroundStoredParticleEmitter>();
            if (gate != null && Api != null && !gate.ShouldSpawnGSParticles(Api.World, slot.Itemstack)) return null;

            return block.ParticleProperties;
        }

        /// <summary>Resolved item -> block lookups. Blocks never change after load.</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Block> _blockBehindItem
            = new System.Collections.Concurrent.ConcurrentDictionary<string, Block>();

        /// <summary>
        /// The block an item stands for, so an ITEM can be asked for particle properties
        /// that only ever live on a Block.
        ///
        /// A sill candle is `item/candle`, not `blocktypes/wax/candle`, and the two do NOT
        /// reliably share a code — vanilla's pair happens to, which made this look like it
        /// worked when it did not generalise at all:
        ///
        ///   vanilla candle          -> "bunchocandles"
        ///   censership candle       -> "censership:bunchocandlesceremonial"
        ///   colorfulcandles candle  -> "colorfulcandles:dyedbunchocandles{type}"
        ///
        /// All three declare it as <c>blockfirstcodepart</c>, the vanilla attribute an item
        /// uses to name the block it places, with variant placeholders already substituted
        /// by the time we read it. Following that is what makes modded candles work with no
        /// per-mod knowledge. It is a code PREFIX — "bunchocandles" has to find
        /// "bunchocandles-1" — so an exact lookup is tried first and a prefix scan second.
        /// </summary>
        private Block BlockBehindItem(CollectibleObject collectible)
        {
            if (Api?.World == null || collectible?.Code == null) return null;

            return _blockBehindItem.GetOrAdd(collectible.Code.ToShortString(), _ =>
            {
                Block exact = Api.World.GetBlock(collectible.Code);
                if (exact != null) return exact;

                string first = collectible.Attributes?["blockfirstcodepart"].AsString(null);
                if (first == null) return null;

                var loc = new AssetLocation(first);
                exact = Api.World.GetBlock(loc);
                if (exact != null) return exact;

                foreach (Block candidate in Api.World.Blocks)
                {
                    if (candidate?.Code != null
                        && candidate.Code.Domain == loc.Domain
                        && candidate.Code.Path.StartsWith(loc.Path, StringComparison.Ordinal))
                    {
                        return candidate;
                    }
                }
                return null;
            });
        }

        /// <summary>
        /// Spawns particles for every stored item that emits them, at that item's own
        /// position on its surface.
        ///
        /// Runs on the async particle thread, so it touches nothing but the inventory
        /// and the surface geometry — no mesh state, no relight, and no
        /// ResolveBlocksOrItems (an unresolved stack simply emits nothing this tick;
        /// HasParticleEmittingItem has already resolved on the main thread).
        /// </summary>
        public void SpawnStoredItemParticles(IAsyncParticleManager manager, BlockPos pos, float windAffectednessAtPos)
        {
            if (inv == null || SurfaceBehavior == null) return;

            foreach (var kv in SlotsSnapshot())
            {
                AdvancedParticleProperties[] props = ParticlesFor(kv.Value, out Vec3f offsetVoxels);
                if (props == null) continue;

                Vec3f local = SlotEmitPosition(kv.Key, kv.Value, offsetVoxels);
                if (local == null) continue;

                foreach (AdvancedParticleProperties prop in props)
                {
                    prop.WindAffectednesAtPos = windAffectednessAtPos;
                    prop.basePos.X = pos.X + local.X;
                    prop.basePos.Y = pos.InternalY + local.Y;
                    prop.basePos.Z = pos.Z + local.Z;
                    manager.Spawn(prop);
                }
            }
        }

        /// <summary>
        /// Where a stored item's particles should come from, in block-local units
        /// (0..1), rotated to match how the block was placed.
        ///
        /// Deliberately NOT taken from <see cref="BuildSlotMatrix"/>: that method has a
        /// history of leaving TfMatrices short an entry when anything throws, and it is
        /// not worth reshaping for this. The two agree by construction instead —
        /// centring cancels, since BuildSlotMatrix offsets by -Size/2 and then back by
        /// +Size/2, leaving the item's centre at surface + slot. The same
        /// Translate/RotateY chain is reused verbatim so the rotation convention cannot
        /// drift from the renderer's.
        ///
        /// Height is the top of the item's declared box, which is where a flame sits:
        /// for a vanilla oil lamp that is 2/16, against the 9/64 vanilla itself uses —
        /// a quarter of a voxel apart.
        ///
        /// <paramref name="offsetVoxels"/> is the authored wick offset, applied in the
        /// item's OWN space — rotated by the item's placement rotation before the block's,
        /// exactly as BuildSlotMatrix rotates the mesh about its own centre. That is what
        /// keeps a flame on the wick after a wrench turn or on a rotated window. A zero
        /// offset reduces to the plain centre, which is what candles want.
        /// </summary>
        private Vec3f SlotEmitPosition(string slotId, ItemSlot slot, Vec3f offsetVoxels)
        {
            WindowSlotId loc = WindowSlotId.Decode(slotId);
            WindowPlacementSurface surface = SurfaceBehavior.GetSurface(loc?.SurfaceIndex ?? -1);
            if (surface == null) return null;

            DisplayableAttributes dattr = BlockBehaviorWindowSurfaces.GetDisplayableAttributes(
                slot, surface.DisplayCategory ?? BlockBehaviorWindowSurfaces.DefaultDisplayCategory);
            if (dattr?.Size == null) return null;

            Vec3f surfacePos = surface.VoxelPosition;
            float stackY = loc.Y * dattr.Size.Height;

            // The item's own resting angle, same three terms and same order as
            // BuildSlotMatrix: placement jitter, then any wrench override, then the
            // surface facing added last so wrench steps stay relative to it.
            // ItemRotationDeg, not a local copy of the rule — it is chosen + jitter + facing,
            // and the mesh, the footprint and this must all read the SAME number.
            float rotDeg = ItemRotationDeg(slotId, dattr, surface);

            return new Matrixf()
                .Translate(0.5f, 0f, 0.5f)
                .RotateYDeg(Block.Shape?.rotateY ?? 0f)
                .RotateY(MeshAngleRad)
                .Translate(
                    (surfacePos.X + loc.X) / 16f - 0.5f,
                    (surfacePos.Y + stackY + dattr.Size.Height) / 16f,
                    (surfacePos.Z + loc.Z) / 16f - 0.5f)
                .RotateY(GameMath.DEG2RAD * rotDeg)
                .TransformVector(new Vec4f(
                    (offsetVoxels?.X ?? 0f) / 16f,
                    (offsetVoxels?.Y ?? 0f) / 16f,
                    (offsetVoxels?.Z ?? 0f) / 16f, 1f)).XYZ;
        }

        // ── Selection / collision boxes ──────────────────────────────────────

        // ── Stacking ─────────────────────────────────────────────────────────

        /// <summary>True when another window sits directly below this one.</summary>
        public bool IsStacked =>
            Pos != null && Api?.World.BlockAccessor.GetBlockEntity(Pos.DownCopy()) is BEWindowDisplay;

        /// <summary>
        /// Which surfaces stay usable when this window is stacked on another, from the
        /// <c>showSurfacesWhenStacked</c> block attribute. Values are psurface indices.
        ///
        /// Opt-in by design: with the attribute absent every surface hides, because a
        /// window sitting on another window normally has its sills obstructed. A style
        /// that wants them anyway declares which ones.
        ///
        /// <code>
        /// // absent                          -> all surfaces hidden when stacked
        /// "showSurfacesWhenStacked": [0]      // only psurface0 stays usable
        /// "showSurfacesWhenStacked": [0, 2]   // psurface0 and psurface2 stay usable
        /// </code>
        ///
        /// Named for surfaces rather than groups: the pre-psurface content used
        /// <c>stackingHideGroups</c>, indexing <c>slotGroups</c> and listing what to HIDE.
        /// Neither groups nor that inversion survive here — this lists what to SHOW, by
        /// psurface index.
        /// </summary>
        public bool IsSurfaceStacked(int surfaceIndex)
            => IsStacked && IsSurfaceHiddenWhenStacked(surfaceIndex, ShownSurfacesWhenStacked());

        /// <summary>
        /// The declared <c>showSurfacesWhenStacked</c> list, or null when absent — which
        /// means every surface hides. Split out so a loop can read the attribute once
        /// instead of per item.
        /// </summary>
        private int[] ShownSurfacesWhenStacked()
        {
            JsonObject attr = Block?.Attributes?["showSurfacesWhenStacked"];
            return attr != null && attr.Exists ? attr.AsArray<int>(null) : null;
        }

        /// <summary>
        /// Whether a surface hides, given an already-resolved list. Callers that have
        /// established the block IS stacked use this directly; the list is tiny, so the
        /// scan is cheaper than re-reading the attribute.
        /// </summary>
        private static bool IsSurfaceHiddenWhenStacked(int surfaceIndex, int[] shown)
            => shown == null || Array.IndexOf(shown, surfaceIndex) < 0;

        /// <summary>
        /// Which way items on a surface start facing, in degrees about Y.
        ///
        /// Normally worked out from the surface's own geometry — see
        /// <see cref="WindowPlacementSurface.FacingRotationDeg"/> — so nothing needs
        /// declaring. The optional <c>surfaceFacingDeg</c> block attribute overrides that
        /// per psurface index, for a surface whose side the geometry cannot infer (one
        /// centred on the block, or straddling the middle) or that simply wants a
        /// deliberate angle.
        ///
        /// <code>
        /// // absent                       -> every surface derives its own facing
        /// "surfaceFacingDeg": { "1": 90 }  // psurface1 faces east, the rest still derive
        /// </code>
        ///
        /// Keys are psurface indices as written in the element name, so they stay correct
        /// even when a shape's indices are not contiguous.
        /// </summary>
        public float SurfaceFacingDeg(WindowPlacementSurface surface)
        {
            JsonObject attr = Block?.Attributes?["surfaceFacingDeg"];
            if (attr != null && attr.Exists)
            {
                JsonObject one = attr[surface.Index.ToString()];
                if (one != null && one.Exists) return one.AsFloat(surface.FacingRotationDeg);
            }
            return surface.FacingRotationDeg;
        }

        private bool IsSlotSurfaceStacked(string slotId)
        {
            WindowSlotId loc = WindowSlotId.Decode(slotId);
            return loc != null && IsSurfaceStacked(loc.SurfaceIndex);
        }

        public void InvalidateBoxes()
        {
            _padOnlyBoxes = null;
            _allBoxes = null;
            _collisionBoxes = null;
        }

        private CuboidfWithId Rotated(Cuboidf box, string id)
        {
            var rotated = new CuboidfWithId(box.RotatedCopyRad(0f, MeshAngleRad, 0f, new Vec3d(0.5, 0.0, 0.5)))
            {
                Id = id ?? (box as CuboidfWithId)?.Id
            };
            return rotated;
        }

        /// <summary>
        /// Never returns null, and never reads a field twice.
        ///
        /// This is called from the RENDER thread while InvalidateBoxes can fire from the
        /// network thread on any MarkDirty — placing, rotating or a pane sync. The old form
        /// tested `_padOnlyBoxes` for null, skipped the rebuild, and then returned `_allBoxes`,
        /// so an invalidation landing between those two statements returned **null** straight
        /// into vanilla's `BlockGeneric.GetSelectionBoxes`, which dereferences it: an
        /// instant client crash from the render thread, with nothing logged because the
        /// throw is in vanilla's frame, not ours.
        ///
        /// Both fields are snapshotted into locals first so a concurrent invalidation cannot
        /// pull one out from under the check, either is enough to force a rebuild, and an
        /// empty array is returned rather than null if it still comes back empty-handed.
        /// Boxes are cheap to rebuild and a crash is not.
        /// </summary>
        public Cuboidf[] GetSelectionBoxes()
        {
            Cuboidf[] pads = _padOnlyBoxes;
            Cuboidf[] all = _allBoxes;

            if (pads == null || all == null)
            {
                RegenSelectionBoxes();
                pads = _padOnlyBoxes;
                all = _allBoxes;
            }

            return (IsPlacingPreview() ? pads : all) ?? Array.Empty<Cuboidf>();
        }

        private void RegenSelectionBoxes()
        {
            BlockBehaviorWindowSurfaces bh = SurfaceBehavior;
            var boxes = new List<Cuboidf>();

            // Resolve BEFORE building, for the same reason OnTesselation does: the item
            // loop below reads slot.Itemstack.Collectible through GetDisplayableAttributes,
            // and on an unresolved stack that is null, so dattr comes back null and the
            // slot's box is skipped without a word. Boxes are rebuilt off the raycast, not
            // off tesselation, so after a sync they could be built while stacks were still
            // unresolved — an item then had no box, the ray passed through it and hit the
            // one below, and the box only appeared once something else forced a rebuild.
            inv.ResolveBlocksOrItems();

            // Stacking is constant for the whole rebuild, so it is resolved once here and
            // shared by both loops below. Per call it costs a BlockPos allocation, a block
            // entity lookup and a JSON attribute read — and the pad loop runs 224 times on
            // a typical window, 308 on a display unit, since maxXDivisions/maxZDivisions
            // default to 32 and a surface grids to width x depth. Unstacked, which is the
            // usual case, the pad loop now skips its slot-id string parse entirely.
            bool stacked = IsStacked;
            int[] shownWhenStacked = stacked ? ShownSurfacesWhenStacked() : null;

            if (bh != null)
            {
                // Frame and pane boxes — ids let interaction tell them apart
                for (int i = 0; i < bh.FrameBoxGroups.Length; i++)
                {
                    FrameBoxGroup group = bh.FrameBoxGroups[i];

                    if (group.StaticFrameBoxes != null)
                    {
                        for (int n = 0; n < group.StaticFrameBoxes.Length; n++)
                        {
                            boxes.Add(Rotated(group.StaticFrameBoxes[n], "frame" + i + "-" + n));
                        }
                    }

                    bool open = i < paneStates.Length && paneStates[i];
                    Cuboidf paneBox = open ? group.OpenFrameBox : group.ClosedFrameBox;
                    if (paneBox != null) boxes.Add(Rotated(paneBox, "pane" + i));
                }

                // Placement pads, flattened to a 1-voxel lip like vanilla.
                // A stacked surface contributes none, so it cannot be aimed at — the
                // window above covers it.
                foreach (CuboidfWithId pad in bh.GridBoxes)
                {
                    if (stacked)
                    {
                        // An id that will not decode keeps its pad, as it did before —
                        // dropping it would silently remove a placement spot
                        WindowSlotId padLoc = WindowSlotId.Decode(pad.Id);
                        if (padLoc != null
                            && IsSurfaceHiddenWhenStacked(padLoc.SurfaceIndex, shownWhenStacked))
                        {
                            continue;
                        }
                    }

                    CuboidfWithId rotated = Rotated(pad, pad.Id);
                    rotated.Y1 -= 0.0625f;
                    rotated.Y2 = rotated.Y1 + 0.0625f;
                    boxes.Add(rotated);
                }
            }

            _padOnlyBoxes = boxes.ToArray();

            // Outlines of already-placed items, on top of the pads
            var withItems = new List<Cuboidf>(boxes);
            if (bh != null)
            {
                foreach (var kv in inv.SlotsByslotId)
                {
                    ItemSlot slot = kv.Value;
                    if (slot.Empty) continue;

                    WindowSlotId loc = WindowSlotId.Decode(kv.Key);
                    WindowPlacementSurface surface = bh.GetSurface(loc?.SurfaceIndex ?? -1);
                    if (surface == null) continue;

                    // Items on a stacked surface stay rendered but stop being selectable
                    if (stacked && IsSurfaceHiddenWhenStacked(loc.SurfaceIndex, shownWhenStacked))
                    {
                        continue;
                    }

                    DisplayableAttributes dattr = BlockBehaviorWindowSurfaces
                        .GetDisplayableAttributes(slot, surface.DisplayCategory ?? "shelf");
                    if (dattr == null) continue;

                    // Same rule as ContentCuboids: the box follows the item's own rotation,
                    // so a wrenched item is clickable where it actually is.
                    Size3f shown = BlockBehaviorWindowSurfaces.RotatedFootprint(
                        dattr.Size, ItemRotationDeg(kv.Key, dattr, surface));

                    Vec3f offset = BlockBehaviorWindowSurfaces.GetCentreOffset(shown);
                    float stackY = loc.Y * dattr.Size.Height;

                    var itemBox = new CuboidfWithId(0f, 0f, 0f,
                            shown.Width / 16f, shown.Height / 16f, shown.Length / 16f)
                        .Translate(
                            (surface.VoxelPosition.X + loc.X + offset.X) / 16f,
                            (surface.VoxelPosition.Y + stackY) / 16f,
                            (surface.VoxelPosition.Z + loc.Z + offset.Z) / 16f);

                    withItems.Add(Rotated(itemBox, WindowSlotId.PlacedPrefix + kv.Key));
                }
            }

            _allBoxes = withItems.ToArray();
        }

        /// <summary>
        /// True while the player is lining up a placeable item, in which case only
        /// the pads are exposed so placed items do not block aiming.
        /// </summary>
        private bool IsPlacingPreview()
        {
            if (capi == null) return false;

            ItemSlot held = capi.World.Player?.InventoryManager?.ActiveHotbarSlot;
            BlockSelection sel = capi.World.Player?.CurrentBlockSelection;
            if (held == null || held.Empty || sel?.SelectionBoxId == null) return false;

            // The wrench was excluded here so its boxes stayed exposed for rotating a stored
            // item. Rotation moved to the mouse wheel on 2026-07-30 and the wrench became an
            // ordinary placeable item, so the exclusion had turned into a nuisance: placed
            // items kept blocking the pad you were aiming at while trying to put a wrench
            // down. It now behaves like anything else in hand.
            //
            // This does NOT affect swapping. Despite the name, _padOnlyBoxes carries the
            // frame and pane boxes too — only PLACED ITEM boxes are withheld — so
            // ctrl+wrench on a frame is unchanged either way. Checked before removing it.

            WindowSlotId loc = WindowSlotId.Decode(sel.SelectionBoxId);
            if (loc == null) return false;

            string category = SurfaceBehavior?.GetDisplayCategory(loc.SurfaceIndex) ?? "shelf";
            DisplayableAttributes dattr = BlockBehaviorWindowSurfaces
                .GetDisplayableAttributes(held, category);
            if (dattr == null) return false;

            // Expose the item boxes when the held item stacks and something is already at
            // the aim point, as vanilla does. Pads lie flattened at the surface floor, so a
            // stack buries them: without this you had to aim at the very base of a stack to
            // add to it. With the item boxes visible, aiming at ANY item in the stack finds
            // it by collision and TryStack walks up to the first free level.
            if (dattr.Behavior == EnumDisplayableBehavior.Stacking
                && GetCollidingSlotId(loc, new Cuboidf(dattr.Size)) != null)
            {
                return false;
            }

            return true;
        }

        public Cuboidf[] GetCollisionBoxes()
        {
            // Snapshot for the same reason as GetSelectionBoxes: this is read off the main
            // thread while InvalidateBoxes can null the field from the network thread.
            Cuboidf[] cached = _collisionBoxes;
            if (cached != null) return cached;

            var result = new List<Cuboidf>();
            CollisionBoxGroup[] groups = SurfaceBehavior?.CollisionBoxGroups;

            if (groups != null && groups.Length > 0)
            {
                for (int i = 0; i < groups.Length; i++)
                {
                    CollisionBoxGroup group = groups[i];

                    if (group.StaticCollisionBoxes != null)
                    {
                        foreach (Cuboidf box in group.StaticCollisionBoxes) result.Add(Rotated(box, null));
                    }

                    bool open = i < paneStates.Length && paneStates[i];
                    Cuboidf dynamicBox = open ? group.OpenCollisionBox : group.ClosedCollisionBox;
                    if (dynamicBox != null) result.Add(Rotated(dynamicBox, null));
                }
            }
            else if (Block?.CollisionBoxes != null)
            {
                foreach (Cuboidf box in Block.CollisionBoxes) result.Add(Rotated(box, null));
            }

            // Return the local, not the field — the field can be nulled by a concurrent
            // InvalidateBoxes between the assignment and the return.
            cached = result.ToArray();
            _collisionBoxes = cached;
            return cached;
        }

        // ── Content geometry helpers ─────────────────────────────────────────

        private List<KeyValuePair<Cuboidf, string>> ContentCuboids(int surfaceIndex)
        {
            var result = new List<KeyValuePair<Cuboidf, string>>();
            BlockBehaviorWindowSurfaces bh = SurfaceBehavior;
            if (bh == null) return result;

            // Same reason as RegenSelectionBoxes: GetItemSize reads the collectible, and an
            // unresolved stack contributes no cuboid — which would make an occupied slot
            // look free to collision, stacking and take-out alike.
            inv.ResolveBlocksOrItems();

            foreach (var kv in inv.SlotsByslotId)
            {
                if (kv.Value.Empty) continue;

                WindowSlotId loc = WindowSlotId.Decode(kv.Key);
                if (loc == null || loc.SurfaceIndex != surfaceIndex) continue;

                Size3f size = BlockBehaviorWindowSurfaces.GetItemSize(kv.Value, bh.GetDisplayCategory(surfaceIndex));
                if (size == null) continue;

                // The space an item takes follows how it is turned, not how it was authored
                size = BlockBehaviorWindowSurfaces.RotatedFootprint(size,
                    ItemRotationDeg(kv.Key,
                        BlockBehaviorWindowSurfaces.GetDisplayableAttributes(kv.Value, bh.GetDisplayCategory(surfaceIndex)),
                        bh.GetSurface(surfaceIndex)));

                result.Add(new KeyValuePair<Cuboidf, string>(
                    new Cuboidf(loc.X - size.Width / 2f, loc.Y, loc.Z - size.Length / 2f,
                                loc.X + size.Width / 2f, loc.Y + 1f, loc.Z + size.Length / 2f),
                    kv.Key));
            }

            return result;
        }

        public string GetCollidingSlotId(WindowSlotId at, Cuboidf placeBox)
        {
            if (at == null) return null;
            foreach (var entry in ContentCuboids(at.SurfaceIndex))
            {
                if (entry.Key.Intersects(placeBox, at.X, at.Y, at.Z)) return entry.Value;
            }
            return null;
        }

        public string GetSelectedNonEmptySlotId(BlockSelection blockSel)
        {
            WindowSlotId loc = WindowSlotId.Decode(blockSel?.SelectionBoxId);
            if (loc == null) return null;

            // A "p-" box already names its slot
            if (loc.IsPlacedItem)
            {
                string direct = blockSel.SelectionBoxId.Substring(WindowSlotId.PlacedPrefix.Length);
                if (inv[direct] != null && !inv[direct].Empty) return direct;
            }

            // Falling back from a pad box, which always reports Y 0. Content cuboids span
            // one stack level each (loc.Y .. loc.Y + 1), so probing Y 0.1 only ever found
            // the BOTTOM of a stack — wrenching or taking while aimed at a pad would reach
            // past the items above it. Take the highest occupied level at this x/z instead,
            // which is the one actually visible and the one TryStack would have added.
            string topmost = null;
            float topmostY = float.NegativeInfinity;

            foreach (var entry in ContentCuboids(loc.SurfaceIndex))
            {
                if (!entry.Key.Contains(loc.X, entry.Key.Y1 + 0.1f, loc.Z)) continue;
                if (entry.Key.Y1 <= topmostY) continue;

                topmostY = entry.Key.Y1;
                topmost = entry.Value;
            }

            return topmost;
        }

        // ── Interaction ──────────────────────────────────────────────────────

        public bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
        {
            string boxId = blockSel?.SelectionBoxId;
            ItemSlot held = byPlayer.InventoryManager.ActiveHotbarSlot;

            // WorldData.EntityControls, NOT Entity.Controls — vanilla's own
            // BEBehaviorDisplay.TryRotate reads exactly this for the same job. The two are
            // different pipelines: SystemPlayerControl fills Entity.Controls from the local
            // keyboard and it reaches the server through the synced flags int, so client
            // and server can disagree about a modifier for a frame. WorldData's set is the
            // one both sides agree on, and a divergence here shows up as "the tooltip says
            // one key but a different key works".
            EntityControls controls = byPlayer.WorldData.EntityControls;
            bool isWrench = held?.Itemstack?.Collectible?.GetTool(held) == EnumTool.Wrench;

            // Ctrl, and only ctrl — what vanilla trains people to reach for, and it must
            // stay in step with the HotKeyCode the interaction help advertises.
            //
            // This used to be `controls.Sprint || controls.CtrlKey`, which is the bug: it
            // ORed a rebindable movement ACTION (Sprint) with a raw modifier KEY (CtrlKey),
            // so which key actually swapped depended on the player's own bindings, and the
            // behaviour looked intermittent. EntityControls keeps all four separate —
            // Sneak/Sprint are movement actions, ShiftKey/CtrlKey are modifier keys — and
            // vanilla reads the KEY for modified block interactions (BEBehaviorMannequin,
            // BEBehaviorCabinetDoors). It is network-synced: EnumEntityAction.CtrlKey is 13
            // and EntityControls.ToInt serialises flags[13], so it is safe server-side.
            //
            // Reverse-rotation shares the flag, which is fine and always was: swapping
            // needs the FRAME, rotating needs an ITEM box, so they can never both fire.
            bool ctrl = controls.CtrlKey;

            bool onFrame = boxId == null
                           || boxId.StartsWith("pane", StringComparison.Ordinal)
                           || boxId.StartsWith("frame", StringComparison.Ordinal);

            // 1. Ctrl + wrench on the frame → swap the whole block
            if (BlockBehaviorWindowSurfaces.IsSwapAimPoint(SurfaceBehavior, boxId) && ctrl && isWrench)
            {
                return WindowSwapHelper.TrySwap(world, Pos, byPlayer, this);
            }

            // 2. Frame or pane click → toggle that pane
            if (onFrame)
            {
                if (!CanOpen) return false;

                int paneIndex = 0;
                if (boxId != null && boxId.StartsWith("pane", StringComparison.Ordinal))
                {
                    int.TryParse(boxId.Substring(4), out paneIndex);
                }
                else if (boxId != null && boxId.StartsWith("frame", StringComparison.Ordinal))
                {
                    int dash = boxId.IndexOf('-');
                    int.TryParse(dash > 5 ? boxId.Substring(5, dash - 5) : boxId.Substring(5), out paneIndex);
                }

                if (world.Side == EnumAppSide.Server) TogglePane(paneIndex, byPlayer);
                return true;
            }

            // 3. Something already stored under the cursor
            string occupiedSlotId = GetSelectedNonEmptySlotId(blockSel);
            if (occupiedSlotId != null)
            {
                ItemSlot storedSlot = inv[occupiedSlotId];

                // Cooking pots, crocks, bowls — the thing vanilla psurface cannot do
                var contained = storedSlot.Itemstack?.Collectible?.GetCollectibleInterface<IContainedInteractable>();
                if (contained != null && contained.OnContainedInteractStart(this, storedSlot, byPlayer, blockSel))
                {
                    OnContentsChanged();
                    return true;
                }

                // WRENCH NO LONGER ROTATES A STORED ITEM. Removed 2026-07-30: sprint plus
                // the mouse wheel does the same job better — any angle, both directions, no
                // swapping to a tool first — so two ways of doing one thing was all the
                // wrench was still adding here.
                //
                // Deliberately NOT special-cased any more either. Briefly it consumed the
                // interaction, which stopped the wrench being placed on a sill like any
                // other item — and with rotation gone there is no reason for it to behave
                // differently from anything else in your hand. Its remaining job is
                // ctrl+wrench on a FRAME to swap the window, which is handled well before
                // this point.
            }

            if (held == null || held.Empty) return TryTake(byPlayer, blockSel);
            return TryPut(byPlayer, blockSel);
        }

        protected bool TryPut(IPlayer byPlayer, BlockSelection blockSel)
        {
            BlockBehaviorWindowSurfaces bh = SurfaceBehavior;
            if (bh == null) return false;

            // A placed-item box is a valid aim point now: its decoded X/Y/Z is that item's
            // own position, so the collision test below finds it and TryStack adds on top.
            // Rejecting it here was the other half of "you must aim at the bottom one".
            WindowSlotId loc = WindowSlotId.Decode(blockSel?.SelectionBoxId);
            if (loc == null) return false;

            WindowPlacementSurface surface = bh.GetSurface(loc.SurfaceIndex);
            if (surface == null) return false;
            if (IsSurfaceStacked(loc.SurfaceIndex)) return false;

            ItemSlot held = byPlayer.InventoryManager.ActiveHotbarSlot;
            string category = surface.DisplayCategory ?? "shelf";
            DisplayableAttributes dattr = BlockBehaviorWindowSurfaces.GetDisplayableAttributes(held, category);
            if (dattr == null) return false;

            Size3i surfaceSize = surface.Size;

            // The player may have turned the item before placing it, which swaps width and
            // length on a quarter turn — that is the whole point, letting a long item be
            // fitted across a sill it will not fit along. Every check below therefore works
            // on the TURNED footprint, and the same angle is stored on the slot afterwards
            // so the model, the box and the collision all agree from the first frame.
            int placeRot = PlacementRotation.For(Api, byPlayer);

            // AIMING AT AN ITEM ALREADY THERE MEANS STACKING ONTO IT, so inherit that item's
            // angle — and inherit it HERE, before the fit checks below, not later in
            // TryStack.
            //
            // That ordering is the whole bug: the bounds check runs on the held item's
            // footprint, and a base item turned 90° sits at a depth that only its turned
            // footprint fits. An unrotated item aimed at it therefore failed "out of bounds"
            // and never reached the stacking path at all — reported as "no room" and as the
            // stack not detecting the item below. Deciding the angle first makes every check
            // below measure the shape the item will actually be placed as.
            if (loc.IsPlacedItem && inv.SlotsByslotId.TryGetValue(loc.Encoded, out ItemSlot baseSlot)
                && !baseSlot.Empty)
            {
                DisplayableAttributes baseAttr = BlockBehaviorWindowSurfaces
                    .GetDisplayableAttributes(baseSlot, category);
                // The CHOSEN angle, not StoredRotationDeg — that one adds jitter, and this
                // value is about to be stored as the new item's chosen angle, which would
                // then have jitter added again on every read. Each item keeps its own nudge.
                if (baseAttr != null) placeRot = (int)Math.Round(ChosenRotationDeg(loc.Encoded));
            }

            Size3f itemSize = BlockBehaviorWindowSurfaces.RotatedFootprint(
                dattr.Size, placeRot + SurfaceFacingDeg(surface));

            if (surfaceSize.Width < itemSize.Width || surfaceSize.Height < itemSize.Height || surfaceSize.Length < itemSize.Length)
            {
                (Api as ICoreClientAPI)?.TriggerIngameError(this, "toolarge", Lang.Get("shelfhelp-toolarge-error"));
                return true;
            }

            Vec3f offset = BlockBehaviorWindowSurfaces.GetCentreOffset(itemSize);
            if (loc.X + offset.X < 0f || loc.Z + offset.Z < 0f
                || loc.X + offset.X > surfaceSize.Width - itemSize.Width
                || loc.Z + offset.Z > surfaceSize.Length - itemSize.Length)
            {
                (Api as ICoreClientAPI)?.TriggerIngameError(this, "outofbounds", Lang.Get("shelfhelp-outofbounds-error"));
                return true;
            }

            string collidingSlotId = GetCollidingSlotId(loc, new Cuboidf(itemSize));
            if (collidingSlotId != null)
            {
                if (dattr.Behavior == EnumDisplayableBehavior.Stacking) return TryStack(byPlayer, collidingSlotId, dattr, placeRot);

                (Api as ICoreClientAPI)?.TriggerIngameError(this, "shelffull", Lang.Get("shelfhelp-shelffull-error"));
                return true;
            }

            return PlaceItem(byPlayer, held, loc.Encoded, placeRot);
        }

        /// <summary>
        /// Adds the held item on top of the stack starting at <paramref name="collidingSlotId"/>.
        ///
        /// <paramref name="rotDeg"/> is carried through so a stacked item keeps the angle the
        /// player chose, exactly as a ground-level one does. Without it, everything added to
        /// a pile landed unrotated no matter what the preview showed — which reads in play as
        /// "rotation does not work when stacking".
        /// </summary>
        private bool TryStack(IPlayer byPlayer, string collidingSlotId, DisplayableAttributes dattr, int rotDeg)
        {
            WindowSlotId loc = WindowSlotId.Decode(collidingSlotId);
            WindowPlacementSurface surface = SurfaceBehavior?.GetSurface(loc?.SurfaceIndex ?? -1);
            if (surface == null) return false;

            // rotDeg arrives already resolved by TryPut: the player's pending angle for a
            // fresh pile, or the base item's angle when stacking onto one. Deciding it there
            // rather than here matters, because TryPut's bounds and collision checks have to
            // measure the same footprint the item will end up with.

            WindowSlotId cursor = loc;
            while (true)
            {
                ItemSlot slot = inv[cursor.Encoded];
                if (slot == null || slot.Empty) break;
                cursor = cursor.UpCopy();
            }

            if (cursor.Y * dattr.Size.Height >= surface.Size.Height) return false;

            return PlaceItem(byPlayer, byPlayer.InventoryManager.ActiveHotbarSlot, cursor.Encoded, rotDeg);
        }

        private bool PlaceItem(IPlayer byPlayer, ItemSlot heldSlot, string targetSlotId, int rotDeg = 0)
        {
            inv.Allocate(targetSlotId);
            ItemSlot target = inv[targetSlotId];
            int moved = heldSlot.TryPutInto(Api.World, target, 1);

            if (moved <= 0) return false;

            // Record the angle it was placed at, so the mesh, the selection box and the
            // collision all use it immediately. Only when the player actually turned it —
            // storing 0 would suppress the placement jitter and make a shelf of jars look
            // machine-stamped.
            //
            // The CHOSEN angle only. Jitter is added by StoredRotationDeg on every read, so
            // it stays governed by the config/block/item switches rather than being frozen in
            // here. A turned item still gets the same natural variation an untouched one does.
            if (rotDeg != 0)
            {
                customRotationDegBySlot ??= new Dictionary<string, float>();
                customRotationDegBySlot[targetSlotId] = rotDeg;
            }

            // One turn, one placement. Stacking inherits from the item below, so a pile is
            // still uniform without pressing the key again.
            PlacementRotation.ResetAfterPlacing(Api, byPlayer);

            OnContentsChanged();

            Api.World.PlaySoundAt(target.Itemstack?.Block?.Sounds?.Place ?? GlobalConstants.DefaultBuildSound, byPlayer.Entity, byPlayer);
            Api.World.Logger.Audit("{0} put 1x{1} into window storage at {2}, slot {3}.",
                byPlayer.PlayerName, target.Itemstack?.Collectible.Code, Pos, targetSlotId);
            return true;
        }

        protected bool TryTake(IPlayer byPlayer, BlockSelection blockSel)
        {
            string slotId = GetSelectedNonEmptySlotId(blockSel);
            if (slotId == null) return false;
            if (IsSlotSurfaceStacked(slotId)) return false;

            ItemSlot slot = inv[slotId];
            if (slot == null || slot.Empty) return false;

            ItemStack taken = slot.TakeOut(1);
            if (byPlayer.InventoryManager.TryGiveItemstack(taken))
            {
                Api.World.PlaySoundAt(taken?.Block?.Sounds?.Place ?? GlobalConstants.DefaultBuildSound, byPlayer.Entity, byPlayer);
            }
            else if (taken != null && taken.StackSize > 0)
            {
                Api.World.SpawnItemEntity(taken, Pos);
            }

            // Drop the taken item's angle FIRST. Doing it after the collapse below deleted
            // the angle of whatever had just moved down into this slot instead.
            customRotationDegBySlot?.Remove(slotId);

            // Collapse anything stacked above into the gap.
            //
            // The angle has to travel with the item. customRotationDegBySlot is keyed by SLOT,
            // so moving only the Itemstack left the angle behind on the old key and the item
            // fell back to its placement jitter — reported as "take the bottom one and the
            // next one up reverts to its original position".
            WindowSlotId gap = WindowSlotId.Decode(slotId);
            WindowSlotId above = gap.UpCopy();
            while (true)
            {
                ItemSlot aboveSlot = inv[above.Encoded];
                if (aboveSlot == null || aboveSlot.Empty) break;

                inv[gap.Encoded].Itemstack = aboveSlot.Itemstack;
                aboveSlot.Itemstack = null;

                if (customRotationDegBySlot != null)
                {
                    customRotationDegBySlot.Remove(gap.Encoded);
                    if (customRotationDegBySlot.TryGetValue(above.Encoded, out float carried))
                    {
                        customRotationDegBySlot[gap.Encoded] = carried;
                        customRotationDegBySlot.Remove(above.Encoded);
                    }
                }

                gap = gap.UpCopy();
                above = above.UpCopy();
            }

            OnContentsChanged();
            return true;
        }

        /// <summary>
        /// The random resting angle an item is given on placement, from its
        /// <c>RandYRotAngle</c> (30 by default, so ±15°). Deterministic from the position
        /// and slot, so it never needs storing and survives a reload unchanged.
        /// </summary>
        private float PlacementJitterDeg(string slotId, DisplayableAttributes dattr)
        {
            // Three levels, coarsest first, all meeting at this one choke point:
            //   config PlacementJitter=false  -> the whole mod sits square
            //   block  noPlacementJitter      -> that block sits square (the chiselled window)
            //   item   randYRotAngle: 0       -> that item sits square, set in the patches mod
            if (JitterSuppressed) return 0f;
            if (dattr == null || dattr.RandYRotAngle <= 0) return 0f;

            return GameMath.MurmurHash3Mod(Pos.X, Pos.Y + slotId.GetHashCode(), Pos.Z,
                       dattr.RandYRotAngle + 1) - dattr.RandYRotAngle / 2;
        }

        /// <summary>Same value, resolved from the slot when the caller has no dattr to hand.</summary>
        private float PlacementJitterDeg(string slotId)
        {
            if (!inv.SlotsByslotId.TryGetValue(slotId, out ItemSlot slot) || slot.Empty) return 0f;

            WindowSlotId loc = WindowSlotId.Decode(slotId);
            WindowPlacementSurface surface = SurfaceBehavior?.GetSurface(loc?.SurfaceIndex ?? -1);
            if (surface == null) return 0f;

            return PlacementJitterDeg(slotId, BlockBehaviorWindowSurfaces
                .GetDisplayableAttributes(slot, surface.DisplayCategory ?? "shelf"));
        }

        /// <summary>
        /// The angle an item in this slot actually sits at, in the surface's own frame:
        /// its wrenched angle if it has one, otherwise its placement jitter, plus the
        /// surface's facing. Exactly the terms <see cref="BuildSlotMatrix"/> feeds the mesh,
        /// which is the point — the footprint has to be derived from the same number the
        /// model is, or the two disagree.
        ///
        /// The surface facing is included for correctness rather than for present need: no
        /// shipped surface faces 90° or 270°, so today it never changes a footprint. One that
        /// did would turn every item on it a quarter turn, and the boxes must follow.
        /// </summary>
        /// <summary>
        /// The angle STORED against a slot — its wrenched value if it has one, otherwise its
        /// placement jitter. Deliberately excludes the surface facing, because that is added
        /// separately by everything that renders or measures; this is the number that would
        /// be written into <c>customRotationDegBySlot</c>, so it is what a new item copying
        /// an existing one should take.
        /// </summary>
        /// <summary>
        /// The angle explicitly chosen for a slot — placed-at or wrenched-to — with no jitter.
        /// This is what is stored, and what one item should copy from another.
        /// </summary>
        private float ChosenRotationDeg(string slotId)
        {
            return customRotationDegBySlot != null && customRotationDegBySlot.TryGetValue(slotId, out float custom)
                ? custom : 0f;
        }

        private float StoredRotationDeg(string slotId, DisplayableAttributes dattr)
        {
            // Jitter is ADDED HERE, at read time, never baked into the stored value.
            //
            // It used to be folded in when the item was placed, which froze it: an item put
            // down at an angle kept its random nudge for good, so switching the jitter config
            // off left every rotated and wrenched item exactly as it was while untouched ones
            // went square. Reported as "the ones placed at the angles aren't covered by the
            // config", and it was right.
            //
            // Deriving it on every read means the config, the block flag and the item's own
            // randYRotAngle all apply RETROACTIVELY to everything already placed — turn
            // jitter off and the whole world lines up, turn it back on and it returns.
            //
            // The stored value is therefore the CHOSEN angle alone: 0 for an untouched item,
            // 90 for one placed turned, whatever the wrench has stepped to. That also makes
            // wrench steps exact multiples rather than multiples plus a frozen offset.
            return ChosenRotationDeg(slotId) + PlacementJitterDeg(slotId, dattr);
        }

        private float ItemRotationDeg(string slotId, DisplayableAttributes dattr, WindowPlacementSurface surface)
        {
            return StoredRotationDeg(slotId, dattr) + SurfaceFacingDeg(surface);
        }

        /// <summary>
        /// Whether the item in this slot would still fit if turned to <paramref name="rotDeg"/>.
        /// Checks the same two things placement does — inside the surface, and clear of the
        /// other items — against the ROTATED footprint.
        ///
        /// Its own cuboid is in that list, so a self-hit is ignored; anything else is a real
        /// overlap. Returns true when the item, surface or size cannot be resolved: refusing
        /// to rotate because something failed to resolve would be a worse failure than
        /// allowing it.
        /// </summary>
        private bool RotationFits(string slotId, float rotDeg)
        {
            if (!inv.SlotsByslotId.TryGetValue(slotId, out ItemSlot slot) || slot.Empty) return true;

            WindowSlotId loc = WindowSlotId.Decode(slotId);
            WindowPlacementSurface surface = SurfaceBehavior?.GetSurface(loc?.SurfaceIndex ?? -1);
            if (loc == null || surface == null) return true;

            DisplayableAttributes dattr = BlockBehaviorWindowSurfaces
                .GetDisplayableAttributes(slot, surface.DisplayCategory ?? "shelf");
            if (dattr?.Size == null) return true;

            Size3f size = BlockBehaviorWindowSurfaces.RotatedFootprint(
                dattr.Size, rotDeg + SurfaceFacingDeg(surface));

            if (surface.Size.Width < size.Width || surface.Size.Length < size.Length) return false;

            Vec3f offset = BlockBehaviorWindowSurfaces.GetCentreOffset(size);
            if (loc.X + offset.X < 0f || loc.Z + offset.Z < 0f
                || loc.X + offset.X > surface.Size.Width - size.Width
                || loc.Z + offset.Z > surface.Size.Length - size.Length)
            {
                return false;
            }

            // ONE LEVEL TALL, not size.Height. ContentCuboids measures Y in stack levels —
            // loc.Y to loc.Y + 1 — while Size.Height is in voxels. Passing the voxel height
            // made an item at level 1 span levels 1..1+height and "collide" with everything
            // stacked above it, so only the bottom of a stack could ever be turned. Rotation
            // only ever competes for space on its OWN level.
            string hit = GetCollidingSlotId(loc, new Cuboidf(0f, 0f, 0f, size.Width, 1f, size.Length));
            return hit == null || hit == slotId;
        }

        /// <summary>
        /// Entry point for the mouse-wheel path, which arrives as a packet rather than
        /// through OnBlockInteractStart. Same code as the wrench so the two cannot behave
        /// differently — including refusing when the turn would not fit.
        /// </summary>
        public bool RotateStoredItem(string slotId, IPlayer byPlayer, bool reverse)
        {
            if (slotId == null || inv[slotId] is not { Empty: false }) return false;
            return TryRotateItem(slotId, byPlayer, reverse);
        }

        private bool TryRotateItem(string slotId, IPlayer byPlayer, bool reverse)
        {
            customRotationDegBySlot ??= new Dictionary<string, float>();

            float step = WindowDisplayLibConfig.Current?.RotationStepDegrees ?? 22.5f;

            // Seed from the CHOSEN angle, which is 0 for an item that has never been turned.
            // The jitter no longer needs seeding in: StoredRotationDeg adds it on every read,
            // so the rendered angle is chosen + jitter and stepping the chosen part by 15
            // moves what you see by exactly 15. That is what the old jitter-seeding was
            // working around, less directly.
            customRotationDegBySlot.TryGetValue(slotId, out float current);

            float wanted = (current + (reverse ? -step : step) + 360f) % 360f;

            // Turning an item can change the space it needs — a quarter turn swaps width and
            // length — so it may no longer fit where it sits. Check before committing, or a
            // rotated item silently overlaps its neighbour or hangs off the sill.
            if (!RotationFits(slotId, wanted))
            {
                (Api as ICoreClientAPI)?.TriggerIngameError(this, "norotateroom",
                    Lang.Get("windowdisplaylib:rotate-noroom"));
                return true;      // handled: refusing is a result, not a fall-through
            }

            customRotationDegBySlot[slotId] = wanted;

            // The selection boxes are derived from item state, so they have to go with the
            // meshes. Without this the client kept a stale set from the moment of rotating
            // until the server's packet arrived and FromTreeAttributes invalidated for it —
            // which is why rotating made boxes disappear and re-opening a pane, which does
            // invalidate, brought them all back.
            InvalidateBoxes();
            MarkMeshesDirty();
            MarkDirty(true);

            // NO RELIGHT HERE — an `ApplyLight()` call was tried on 2026-07-31 and REVERTED.
            //
            // The theory was decent: a candle does not flash when a window is opened or
            // closed, but does when the candle itself is rotated, and the only difference
            // between those two paths is that the toggle relights via SyncStateVariant. So
            // the relight looked like the thing CLEANING UP after the re-tesselation rather
            // than the thing causing the flash.
            //
            // It made no difference at all in game. The likelier explanation for the
            // toggle/rotate difference is far duller: **opening a window moves a large
            // animated pane and plays a sound, which masks a subtle flicker; rotating an item
            // changes almost nothing else on screen, so the flicker is the only thing moving
            // and the eye goes straight to it.**
            //
            // Do not reinstate this. It relights on every wheel notch, which is real work for
            // no observed benefit.
            Api.World.PlaySoundAt(new AssetLocation("game", "sounds/block/cloth"), Pos, 0, byPlayer, true, 16f, 0.8f);
            return true;
        }

        // ── Panes ────────────────────────────────────────────────────────────

        /// <summary>
        /// Sets one pane without touching the rest of the stack. TogglePane drives the
        /// linked segments through this, so the sync cannot recurse back into itself.
        /// </summary>
        public void SetPaneState(int index, bool open)
        {
            if (paneStates == null || index < 0 || index >= paneStates.Length) return;
            if (paneStates[index] == open) return;

            paneStates[index] = open;
            InvalidateBoxes();
            MarkMeshesDirty();
            MarkDirty(true);

            bool roomSafe = WindowDisplayLibConfig.Current?.RoomSafeOpening ?? false;
            FrameBoxGroup[] groups = SurfaceBehavior?.FrameBoxGroups;
            if (groups != null && index < groups.Length && groups[index].IsWindow && !roomSafe)
            {
                SyncStateVariant();
            }
        }

        /// <summary>
        /// Client-side application of a linked pane change: sets the state and starts the
        /// animation immediately, rather than waiting for this segment's own block entity
        /// sync. That is what keeps a tall window's segments moving together.
        /// </summary>
        public void ApplyLinkedPane(int index, bool open)
        {
            if (paneStates == null || index < 0 || index >= paneStates.Length) return;

            paneStates[index] = open;
            InvalidateBoxes();
            UpdateAnimationState();
            soundHandler?.PlaySlideSound();
        }

        /// <summary>Server-side: tells every nearby client to move this pane on the whole stack at once.</summary>
        private void BroadcastLinkedPane(int index, bool open)
        {
            if (Api?.Side != EnumAppSide.Server || WindowDisplayLibMod.ServerChannel == null) return;

            List<BlockPos> chain = WindowChain.Enumerate(Api.World, Pos);
            var flat = new int[chain.Count * 3];
            for (int i = 0; i < chain.Count; i++)
            {
                flat[i * 3] = chain[i].X;
                flat[i * 3 + 1] = chain[i].Y;
                flat[i * 3 + 2] = chain[i].Z;
            }

            WindowDisplayLibMod.ServerChannel.BroadcastPacket(new LinkedOpenPacket
            {
                Positions = flat,
                PaneIndex = index,
                IsOpen = open
            });
        }

        public void TogglePane(int index, IPlayer byPlayer = null)
        {
            FrameBoxGroup[] groups = SurfaceBehavior?.FrameBoxGroups;
            if (groups == null || index < 0 || index >= groups.Length) return;

            FrameBoxGroup group = groups[index];
            // A group with only static boxes has nothing to open
            if (group.OpenFrameBox == null && group.ClosedFrameBox == null) return;

            if (index >= paneStates.Length) ClampPaneStatesToBlock();
            if (index >= paneStates.Length) return;

            SetPaneState(index, !paneStates[index]);

            // Tall windows open as one unit
            WindowChain.SyncPane(Api.World, Pos, index, paneStates[index]);
            BroadcastLinkedPane(index, paneStates[index]);

            // The open/closed variant sync lives in SetPaneState, so every path that
            // changes a pane — including the linked segments above — keeps it in step
            if (!group.IsWindow)
            {
                Api.World.PlaySoundAt(new AssetLocation("game", "sounds/block/door"), Pos, 0, byPlayer, true, 16f);
            }
        }

        private void SyncStateVariant()
        {
            if (Api?.Side != EnumAppSide.Server || Block.Variant["state"] == null) return;

            string targetState = IsAnyWindowPaneOpen ? "open" : "closed";
            if (Block.Variant["state"] == targetState) return;

            Block targetBlock = Api.World.GetBlock(Block.CodeWithVariant("state", targetState));
            if (targetBlock == null || targetBlock.Id == Block.Id) return;

            ITreeAttribute snapshot = new TreeAttribute();
            ToTreeAttributes(snapshot);

            // `(synchronize, relight, strict)` — synchronize FALSE, relight TRUE. BOTH of
            // those have been changed and changed back; neither is a free choice.
            //
            // synchronize:true was tried 2026-07-27 and is a straight regression — the same
            // note sits in WindowSwapHelper.
            //
            // **relight:false was tried 2026-07-31 and REVERTED. Do not retry it without
            // reading this.** The reasoning for it was sound and is still true: a relight
            // here cannot change a single light value. `ChunkIlluminator` (decompiled,
            // 1.22.5) propagates from `GetLightAbsorptionAt` and never reads `SideSolid` or
            // `SideOpaque` at all — 13 references to absorption, zero to either — and every
            // window blocktype declares `lightAbsorption: 0` with `sideopaque: all false` in
            // BOTH states. What the open/closed variants differ in is `sideSolid`, which
            // drives room sealing and attachment, not light.
            //
            // It was aimed at the open/close light FLASH and did not fix it. The flash turned
            // out not to live here at all: **rotating a stored item does it too**, and
            // `TryRotateItem` touches no light code whatever — the one thing the two share is
            // `MarkDirty(true)`. Worse, the author reported the flash reading *more*
            // noticeable without the relight. That is plausible rather than surprising: with
            // it ON the engine recomputes and pushes correct values straight after the
            // exchange, so a stale client-side value is actively corrected instead of being
            // left to resolve on its own.
            //
            // The observation beats the reasoning, as it has every other time in this project.
            // `(synchronize, relight, strict)` — synchronize FALSE, relight TRUE. Both have
            // been changed and changed back; neither is a free choice.
            //
            // synchronize:true was tried 2026-07-27 and is a straight regression — same note
            // in WindowSwapHelper.
            //
            // **relight:false was tried TWICE on 2026-07-31 and reverted both times. The
            // second attempt was MEASURED. Do not try it a third time:**
            //
            //   | open/close | relight:true | relight:false |
            //   | frames at block-light 0 | 4 and 2 | 4 and 5 |
            //
            // No better opening, two and a half times worse closing. The author had already
            // called that by eye on the first attempt, before any measurement existed.
            //
            // The reasoning FOR removing it was sound and is still true — a relight here
            // cannot change a light VALUE, since ChunkIlluminator propagates from
            // `GetLightAbsorptionAt` and never reads `SideSolid`/`SideOpaque`, and every
            // window blocktype is `lightAbsorption: 0` in both states. It is simply not the
            // whole story: the relight also re-spreads light that `MarkDirty(true)` has
            // already knocked out, so removing it leaves the hole open for longer.
            //
            // "This work cannot change the result" and "removing this work is free" are
            // different claims, and this is what the difference costs.
            IBlockAccessor relightAccessor = Api.World.GetBlockAccessor(false, true, false);
            relightAccessor.ExchangeBlock(targetBlock.Id, Pos);

            if (relightAccessor.GetBlockEntity(Pos) is BEWindowDisplay newBe && !ReferenceEquals(newBe, this))
            {
                newBe.FromTreeAttributes(snapshot, Api.World);
                newBe.UpdateLightCache();
                newBe.MarkDirty(true);
            }
        }

        // ── Animation ────────────────────────────────────────────────────────

        /// <summary>
        /// Rebuilds the animator when it no longer matches the block or the mesh
        /// angle it was built for. Returns true if it re-initialised.
        ///
        /// Change-detection on FromTreeAttributes is not enough: the client runs
        /// TryPlaceBlock itself and sets MeshAngleRad locally, so by the time the
        /// server's value arrives there is no change left to notice — and the
        /// renderer keeps the rotation of 0 it was constructed with. ExchangeBlock
        /// has the mirror problem, keeping the block entity across a block change.
        /// </summary>
        /// <summary>
        /// Turns <c>animatedElements</c> into the path form SelectiveElements needs.
        ///
        /// The two lists use different matching rules, which is easy to get wrong:
        ///
        ///  * IgnoreElements — a bare name drops the element and its whole subtree,
        ///    because the tesselator `continue`s without recursing.
        ///  * SelectiveElements — a bare name matches only that element and hands its
        ///    children an EMPTY haystack, and an empty (non-null) haystack matches
        ///    nothing. So `["Animation Origin"]` keeps just the origin marker, which
        ///    has no faces, and the animated mesh comes out empty.
        ///
        /// `"Name/*"` is the form that keeps the element and everything under it —
        /// the same convention vanilla's cabinet uses for its doorElements.
        /// Content therefore writes plain names and this adds the suffix.
        /// </summary>
        private static string[] ToSelectiveElementPaths(string[] elementNames)
        {
            if (elementNames == null || elementNames.Length == 0) return null;

            var paths = new string[elementNames.Length];
            for (int i = 0; i < elementNames.Length; i++)
            {
                string name = elementNames[i];
                paths[i] = name.EndsWith("/*", StringComparison.Ordinal) || name == "*"
                    ? name
                    : name + "/*";
            }
            return paths;
        }

        /// <summary>
        /// Everything the animator mesh depends on: which shape it draws, and which
        /// ARL variants textured it. Same for the open and closed block variants, so
        /// toggling a pane does not rebuild it.
        ///
        /// The variants matter because the animation shapes declare a default glass
        /// texture (leaded) that ARL overwrites via BakeVariantTextures — but Variants
        /// are filled from the placed stack in OnBlockPlaced, which runs *after*
        /// Initialize. The first build therefore textures with empty variants and
        /// falls back to that default. Every non-north facing happened to hide this,
        /// because the placement angle changed and forced a rebuild; north is angle 0,
        /// matched what was already built, and kept the leaded glass.
        /// </summary>
        private string ResolveAnimatorShapeKey()
        {
            // Keyed on the block's OWN shape. There used to be an `animationShapePath`
            // branch for content keeping its animations in a separate file; every shape in
            // this mod is single-file — static geometry, psurface markers and animations
            // together, split at tesselation time by `animatedElements` — so it was dead.
            // Removed 2026-07-30.
            string variantKey = GetBehavior<BlockEntityBehaviorShapeTexturesFromAttributes>()?.Variants?.ToString() ?? "";
            return "block:" + Block.Shape?.Base + "|" + variantKey;
        }

        /// <summary>
        /// Whether ARL has its wood/glass variants yet.
        ///
        /// Variants arrive in OnBlockPlaced or FromTreeAttributes, both of which run
        /// *after* Initialize. Building the animator or the block mesh before then bakes
        /// the shape's default textures in, and the corrected rebuild a frame or two
        /// later is the visible pop from generic to matching material.
        ///
        /// The attempt budget stops a block whose variants never arrive — ARL present
        /// but placed without attributes — from staying invisible forever.
        /// </summary>
        private bool ArlVariantsReady
        {
            get
            {
                var arlBe = GetBehavior<BlockEntityBehaviorShapeTexturesFromAttributes>();
                if (arlBe == null) return true;
                if (arlBe.Variants?.Any == true) return true;
                return _variantWaitTicks > MaxVariantWaitTicks;
            }
        }

        private const int MaxVariantWaitTicks = 8;   // ~2s at the 250ms client tick
        private int _variantWaitTicks;

        private bool EnsureAnimatorCurrent()
        {
            if (capi == null || Block == null) return false;
            if (!ArlVariantsReady) return false;

            // Keyed on the animation shape, NOT the block id. Toggling a pane runs
            // SyncStateVariant, which exchanges the block for its open/closed variant —
            // a different block id but the same animation shape. Rebuilding on block id
            // therefore tore the animator down mid-animation and restarted it from the
            // default pose, which is the close-bounce-open-close stutter.
            bool shapeChanged = ResolveAnimatorShapeKey() != _animatorShapeKey;
            bool angleChanged = float.IsNaN(_animatorAngleRad) || Math.Abs(_animatorAngleRad - MeshAngleRad) > 0.001f;
            if (!shapeChanged && !angleChanged) return false;

            InitializeAnimator();

            // The pose right after a build is placement or chunk load, not a player
            // opening something — jump straight to it instead of animating from the
            // default pose. Scoped to the first update after a build so ordinary
            // toggles still ease normally.
            _snapNextAnimationUpdate = true;
            return true;
        }

        private bool _snapNextAnimationUpdate;

        private void InitializeAnimator()
        {
            if (capi == null || Block == null) return;

            // Recorded up front so a shape that fails to load does not retry every tick
            _animatorShapeKey = ResolveAnimatorShapeKey();
            _animatorAngleRad = MeshAngleRad;

            animUtil?.Dispose();
            animUtil = new BlockEntityAnimationUtil(capi, this);

            try
            {
                string[] animatedElements = Block.Attributes?["animatedElements"].AsArray<string>(null);

                // The block's OWN shape, always. The `animationShapePath` branch that used
                // to sit here loaded a separate animation file; every shape in this mod is
                // single-file, so it never ran. Removed 2026-07-30.
                AssetLocation loc = Block.Shape?.Base?.Clone().WithPathPrefixOnce("shapes/").WithPathAppendixOnce(".json");
                Shape animShape = loc == null ? null : capi.Assets.TryGet(loc)?.ToObject<Shape>();
                string cacheKey = "windowdisplaylib-" + Block.Code;

                if (animShape == null)
                {
                    Api.Logger.Warning("[WindowDisplayLib] No animation shape for {0}; panes will not animate.", Block.Code);
                    return;
                }

                ITexPositionSource texSource = BuildBlockTextureSource(animShape, Block.Shape?.Base?.ToString());

                var meta = new TesselationMetaData
                {
                    TexSource = texSource,
                    IgnoreElements = SurfaceBehavior?.SurfaceElementNames,
                    SelectiveElements = ToSelectiveElementPaths(animatedElements)
                };

                MeshData animMesh = animUtil.CreateMesh(Block.Code.ToString(), animShape, out Shape resultingShape, texSource, meta);
                animUtil.InitializeAnimator(cacheKey, animMesh, resultingShape,
                    new Vec3f(0f, MeshAngleRad * GameMath.RAD2DEG, 0f));
            }
            catch (Exception e)
            {
                Api.Logger.Error("[WindowDisplayLib] Error initializing animation for {0}: {1}", Block?.Code, e.Message);
            }
        }

        /// <summary>
        /// Drives one animation per pane group at the configured speed.
        ///
        /// There is deliberately no "instant" mode. An earlier version forced
        /// AnimationSpeed/EaseIn/EaseOut to 100 when re-initialising, which is what
        /// made every pane snap instead of slide — the animation itself was running
        /// at 100x, not just the correction.
        /// </summary>
        private void UpdateAnimationState()
        {
            if (animUtil == null) return;

            FrameBoxGroup[] groups = SurfaceBehavior?.FrameBoxGroups;
            if (groups == null) return;

            bool snap = _snapNextAnimationUpdate;
            _snapNextAnimationUpdate = false;

            // Ease speeds are deliberately near-instant, and the motion comes from
            // AnimationSpeed alone.
            //
            // These panes are authored as complete transitions — "closed" frame 0 is the
            // open pose — so the keyframes already describe the whole movement. Blending
            // them in gradually on top of BlendMode.Add double-counts: a window held open
            // contributes its -15 while the incoming close animation contributes -15 from
            // its own frame 0, swinging to -30 before settling. That is the bounce.
            // Easing out faster instead leaves a gap where the pose collapses to default,
            // which is the snap. Full weight on both sides avoids both.
            float speed = snap ? 1000f : (WindowDisplayLibConfig.Current?.AnimationSpeedValue ?? 1f);
            const float easeIn = 100f;
            const float easeOut = 100f;

            for (int i = 0; i < groups.Length && i < paneStates.Length; i++)
            {
                FrameBoxGroup group = groups[i];
                if (string.IsNullOrEmpty(group.AnimOpen) || string.IsNullOrEmpty(group.AnimClose)) continue;

                string target = paneStates[i] ? group.AnimOpen : group.AnimClose;
                string opposite = paneStates[i] ? group.AnimClose : group.AnimOpen;

                if (animUtil.activeAnimationsByAnimCode.ContainsKey(opposite)) animUtil.StopAnimation(opposite);

                if (!animUtil.activeAnimationsByAnimCode.ContainsKey(target))
                {
                    animUtil.StartAnimation(new AnimationMetaData
                    {
                        Animation = target,
                        Code = target,
                        AnimationSpeed = speed,
                        EaseInSpeed = easeIn,
                        EaseOutSpeed = easeOut,
                        Weight = 1f,
                        BlendMode = EnumAnimationBlendMode.Add
                    });
                }
            }
        }

        // ── Meshing ──────────────────────────────────────────────────────────

        public void MarkMeshesDirty()
        {
            meshesGenerated = false;
            if (Api?.Side == EnumAppSide.Client) Api.World.BlockAccessor.MarkBlockDirty(Pos);
        }

        public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tessThreadTesselator)
        {
            if (capi == null) return false;

            // Resolve BEFORE generating. GenerateMeshes reads slot.Itemstack.Collectible
            // for every slot; on an unresolved stack that is null, GetDisplayableAttributes
            // returns null, the slot is skipped, and meshesGenerated is still set to true —
            // so the item stays invisible until something else happens to invalidate.
            // That is the "disappears on open/close, sometimes comes back" behaviour.
            inv.ResolveBlocksOrItems();

            // ExchangeBlock (open/closed variant sync, and wrenchSwapTo) swaps the block
            // out from under a surviving block entity without any callback, so compare
            // against what the meshes were actually built for.
            if (_meshBlockId != Block.Id)
            {
                _meshBlockId = Block.Id;
                meshesGenerated = false;
                InvalidateBoxes();
            }

            if (!meshesGenerated) GenerateMeshes();

            foreach (var kv in inv.SlotsByslotId)
            {
                ItemSlot slot = kv.Value;
                if (slot.Empty || slot.Itemstack?.Collectible?.Code == null) continue;

                // Recompute rather than skip when the transform is missing: a slot
                // dropped during generation would otherwise stay invisible until the
                // next invalidation, which is what happened on the first interaction
                // after a world load.
                if (!TfMatrices.TryGetValue(kv.Key, out float[] matrix))
                {
                    matrix = BuildSlotMatrix(kv.Key, slot, out _);
                    if (matrix == null) continue;
                    TfMatrices[kv.Key] = matrix;
                }

                MeshData mesh = GetCachedItemMesh(slot);

                // The cache key includes the stack's contents for anything
                // implementing IContainedMeshSource, so emptying a bowl changes the
                // key. Build on demand rather than silently drawing nothing — that
                // is what left items invisible but still selectable.
                if (mesh == null)
                {
                    DisplayableAttributes dattr = BlockBehaviorWindowSurfaces
                        .GetDisplayableAttributes(slot, DisplayCategoryFor(kv.Key));
                    if (dattr != null) mesh = GetOrCreateItemMesh(slot, dattr);
                }

                if (mesh != null) mesher.AddMeshData(mesh, matrix);
            }

            // blockMesh excludes the animated elements by construction (see
            // BuildBlockMesh), so it can be added unconditionally. Gating this on
            // "is an animation currently playing" is racy: OnTesselation runs on
            // the tesselation thread and can fire before the animator exists,
            // baking a static copy into the chunk that then never moves — the
            // classic leftover-ghost when static and animated geometry share a
            // shape file.
            if (blockMesh != null)
            {
                float[] blockMatrix = new Matrixf()
                    .Translate(0.5f, 0f, 0.5f)
                    .RotateY(MeshAngleRad)
                    .Translate(-0.5f, 0f, -0.5f)
                    .Values;
                mesher.AddMeshData(blockMesh, blockMatrix);
            }

            return true;
        }

        /// <summary>
        /// Transform for one stored item, or null if the slot cannot be placed yet.
        ///
        /// Split out of GenerateMeshes so OnTesselation can recompute a missing entry
        /// on the spot. Any transient failure during generation — an unresolved stack,
        /// a surface not parsed yet, a throw out of the block mesh build — used to
        /// leave TfMatrices short an entry, and the render loop skipped that item
        /// outright until something else invalidated the meshes. That is the item
        /// vanishing on the first interaction after a world load.
        /// </summary>
        /// <summary>
        /// The mesh and transform the placement ghost should draw, or false if there is
        /// nothing to draw. Thin wrapper on purpose: the ghost must go through the SAME
        /// mesh builder and the SAME matrix as the real placement, or it will show the
        /// player something they are not going to get.
        ///
        /// <paramref name="rotDeg"/> is the player's PENDING angle rather than the stored
        /// one — nothing is stored yet — which is the only reason BuildSlotMatrix takes an
        /// override at all.
        /// </summary>
        public bool TryBuildGhost(string slotId, ItemSlot heldSlot, DisplayableAttributes dattr,
                                  float rotDeg, out MeshData mesh, out float[] matrix)
        {
            mesh = null;
            matrix = BuildSlotMatrix(slotId, heldSlot, out _, rotDeg);
            if (matrix == null) return false;

            mesh = GetOrCreateItemMesh(heldSlot, dattr);
            return mesh != null;
        }

        private float[] BuildSlotMatrix(string slotId, ItemSlot slot, out DisplayableAttributes dattr,
                                        float? rotationOverrideDeg = null)
        {
            dattr = null;

            BlockBehaviorWindowSurfaces bh = SurfaceBehavior;
            if (bh == null || slot.Empty || slot.Itemstack?.Collectible?.Code == null) return null;

            WindowSlotId loc = WindowSlotId.Decode(slotId);
            WindowPlacementSurface surface = bh.GetSurface(loc?.SurfaceIndex ?? -1);
            if (surface == null) return null;

            dattr = BlockBehaviorWindowSurfaces.GetDisplayableAttributes(slot, surface.DisplayCategory ?? "shelf");
            if (dattr == null) return null;

            Vec3f surfacePos = surface.VoxelPosition;
            Vec3f offset = BlockBehaviorWindowSurfaces.GetCentreOffset(dattr.Size);
            float stackY = loc.Y * dattr.Size.Height;

            // ONE definition of the angle, shared with the footprint and the particles.
            // All three used to compute it separately, and this one REPLACED the jitter with
            // the chosen angle rather than adding to it — so a rotated item rendered exactly
            // square while its box was measured at chosen + jitter. Model and box disagreeing
            // is the same class of bug as wrenching turning the model but not its footprint.
            //
            // ItemRotationDeg is chosen + jitter + surface facing, the facing added last so
            // wrench steps stay relative to it: changing a surface's facing turns everything
            // already on it and keeps relative angles.
            //
            // The override exists only for the placement ghost, which is previewing an item
            // that has no stored angle yet. It is a parameter rather than a second copy of
            // the arithmetic for the reason spelled out above.
            float rotDeg = rotationOverrideDeg ?? ItemRotationDeg(slotId, dattr, surface);

            // NO HEIGHT JITTER. There used to be a +/-3.3% vertical scale wobble here,
            // removed 2026-07-28: randomness in an item's ANGLE reads as natural, randomness
            // in its HEIGHT does not — two identical ingots are not different heights. It
            // also cost more than it gave, since a stack of items at slightly different
            // heights shows gaps where they fall short and z-fights where they overlap.
            // Only the resting angle is random now.
            return new Matrixf()
                .Translate(0.5f, 0f, 0.5f)
                .RotateYDeg(Block.Shape?.rotateY ?? 0f)
                .RotateY(MeshAngleRad)
                .Translate(
                    (surfacePos.X + loc.X + offset.X) / 16f,
                    (surfacePos.Y + stackY) / 16f,
                    (surfacePos.Z + loc.Z + offset.Z) / 16f)
                .Translate(-0.5f + dattr.Size.Width / 2f / 16f, 0f, -0.5f + dattr.Size.Length / 2f / 16f)
                .RotateY(GameMath.DEG2RAD * rotDeg)
                .Translate(-0.5f, 0f, -0.5f)
                .Values;
        }

        private void GenerateMeshes()
        {
            if (SurfaceBehavior == null) return;

            // Leaving meshesGenerated false means the next frame retries, so the first
            // mesh anyone sees is already textured with the right wood and glass
            if (!ArlVariantsReady) return;

            // A throw here must not cost us the item transforms below, and must not
            // latch meshesGenerated — leaving it false means the next frame retries.
            try
            {
                blockMesh = BuildBlockMesh();
            }
            catch (Exception e)
            {
                Api?.Logger.Warning("[WindowDisplayLib] Block mesh build failed at {0}, retrying next frame: {1}", Pos, e.Message);
                blockMesh = null;
            }

            TfMatrices.Clear();

            foreach (var kv in inv.SlotsByslotId)
            {
                float[] matrix = BuildSlotMatrix(kv.Key, kv.Value, out DisplayableAttributes dattr);
                if (matrix == null) continue;

                GetOrCreateItemMesh(kv.Value, dattr);
                TfMatrices[kv.Key] = matrix;
            }

            meshesGenerated = true;
        }

        /// <summary>
        /// Static frame mesh, built through ARL so per-variant wood/glass textures
        /// apply. psurface marker elements are stripped; ARL appends
        /// cshape.IgnoreElements in every branch, so they survive to the tesselator.
        ///
        /// Also strips <c>animatedElements</c>, which is what keeps a single-file
        /// shape from rendering its moving parts twice — once here in the chunk
        /// mesh and once through the animator.
        /// </summary>
        private MeshData BuildBlockMesh()
        {
            string[] ignore = (SurfaceBehavior?.SurfaceElementNames ?? Array.Empty<string>())
                .Append(Block.Attributes?["animatedElements"].AsArray<string>(null))
                .Append(Block.Shape?.IgnoreElements);

            var arlBlock = Block.GetBehavior<BlockBehaviorShapeTexturesFromAttributes>();
            var arlBe = GetBehavior<BlockEntityBehaviorShapeTexturesFromAttributes>();

            if (arlBlock != null && arlBe != null)
            {
                CompositeShape cshape = (Block.Attributes?["shape"].AsObject<CompositeShape>() ?? Block.Shape)?.Clone();
                if (cshape == null) return null;

                cshape.IgnoreElements = ignore;
                return arlBlock.GetOrCreateMesh(arlBe.Variants, cshape, Pos, "windowstorage-psurface")?.Clone();
            }

            Shape shape = capi.TesselatorManager.GetCachedShape(Block.Shape.Base);
            if (shape == null) return null;

            capi.Tesselator.TesselateShape(new TesselationMetaData
            {
                TexSource = capi.Tesselator.GetTextureSource(Block),
                IgnoreElements = ignore
            }, shape, out MeshData mesh);

            return mesh;
        }

        private ITexPositionSource BuildBlockTextureSource(Shape shape, string nameForLogging)
        {
            var arlBlock = Block.GetBehavior<BlockBehaviorShapeTexturesFromAttributes>();
            var arlBe = GetBehavior<BlockEntityBehaviorShapeTexturesFromAttributes>();

            if (arlBlock == null || arlBe == null) return capi.Tesselator.GetTextureSource(Block);

            var texSource = new UniversalShapeTextureSource(capi, capi.BlockTextureAtlas, shape, nameForLogging ?? Block.Code.ToString());
            foreach (var kv in Block.Textures) texSource.textures[kv.Key] = kv.Value;

            ShapeOverlayHelper.BakeVariantTextures(capi, texSource, arlBe.Variants, arlBlock.texturesByType);
            return texSource;
        }

        /// <summary>
        /// Keyed by display category as well as the item, because the transform comes
        /// from the category. Without it, the same item shown under two categories
        /// would share one cached mesh and whichever tesselated first would win.
        /// </summary>
        private string GetItemMeshCacheKey(ItemSlot slot)
        {
            string category = (slot as ItemSlotDisplay)?.DisplayCategory ?? "shelf";
            string itemKey = slot.Itemstack.Collectible?.GetCollectibleInterface<IContainedMeshSource>()?.GetMeshCacheKey(slot)
                             ?? slot.Itemstack.Collectible.Code.ToString();
            return category + "|" + itemKey;
        }

        private MeshData GetCachedItemMesh(ItemSlot slot)
        {
            MeshCache.TryGetValue(GetItemMeshCacheKey(slot), out MeshData mesh);
            return mesh;
        }

        /// <summary>Display category of the surface a given slot id sits on.</summary>
        private string DisplayCategoryFor(string slotId)
        {
            WindowSlotId loc = WindowSlotId.Decode(slotId);
            return SurfaceBehavior?.GetDisplayCategory(loc?.SurfaceIndex ?? 0) ?? "shelf";
        }

        /// <summary>
        /// Takes a plain ItemSlot rather than ItemSlotDisplay: a slot that arrives
        /// from anywhere other than our own allocator would otherwise be skipped
        /// and render as nothing.
        /// </summary>
        private MeshData GetOrCreateItemMesh(ItemSlot slot, DisplayableAttributes dattr)
        {
            if (slot == null || slot.Empty) return null;

            MeshData cached = GetCachedItemMesh(slot);
            if (cached != null) return cached;

            CollectibleObject collectible = slot.Itemstack.Collectible;

            // A displayable entry may name its own shape to render instead of the item's,
            // which is how clothing shows as a folded pile rather than a worn garment —
            // `displayable.shelf.shape` on upperbody.json points at upperbody-folded. Ignoring
            // it meant the item tesselated from its normal shape and looked wrong.
            //
            // Cached globally on collectible code + shape, not per block entity: the same
            // garment folds identically in every window, and vanilla keys it the same way.
            MeshData mesh = null;
            CompositeShape customShape = dattr?.Shape;
            if (customShape != null)
            {
                string shapeKey = "windowdisplaylib-shape-" + collectible.Code + "-" + customShape;
                mesh = ObjectCacheUtil.GetOrCreate(capi, shapeKey, () =>
                    capi.TesselatorManager.CreateMesh("window displayed item shape", customShape,
                        (shape, name) => BuildContainedTextureSource(shape, slot, collectible)));
            }

            mesh ??= collectible?.GetCollectibleInterface<IContainedMeshSource>()?.GenMesh(slot, capi.BlockTextureAtlas, Pos);

            if (mesh == null)
            {
                ItemStack stack = slot.Itemstack;
                if (stack.Class == EnumItemClass.Block)
                {
                    mesh = capi.TesselatorManager.GetDefaultBlockMesh(stack.Block).Clone();
                }
                else
                {
                    _nowTesselatingObj = stack.Collectible;
                    _nowTesselatingShape = stack.Item.Shape?.Base != null
                        ? capi.TesselatorManager.GetCachedShape(stack.Item.Shape.Base)
                        : null;

                    capi.Tesselator.TesselateItem(stack.Item, out mesh, this);
                    mesh.RenderPassesAndExtraBits.Fill((short)EnumChunkRenderPass.OpaqueNoCull);
                    _nowTesselatingObj = null;
                    _nowTesselatingShape = null;
                }
            }

            if (dattr?.Transform != null)
            {
                dattr.Transform.EnsureDefaultValues();
                mesh.ModelTransform(dattr.Transform);
            }
            else if (slot.Itemstack.Class == EnumItemClass.Item &&
                     (slot.Itemstack.Item.Shape == null || slot.Itemstack.Item.Shape.VoxelizeTexture))
            {
                mesh.Rotate(GameMath.PIHALF, 0f, 0f);
                mesh.Scale(0.33f, 0.33f, 0.33f);
                mesh.Translate(0f, -15f / 32f, 0f);
            }

            MeshCache[GetItemMeshCacheKey(slot)] = mesh;
            return mesh;
        }

        /// <summary>
        /// Texture resolver for a displayable's own shape: the shape's textures, overlaid
        /// with the itemstack's, so a folded garment picks up that garment's cloth rather
        /// than whatever the shared folded shape happens to declare.
        /// </summary>
        private ITexPositionSource BuildContainedTextureSource(Shape shape, ItemSlot slot,
                                                              CollectibleObject collectible)
        {
            var textures = new Dictionary<string, AssetLocation>(shape.Textures);

            IDictionary<string, CompositeTexture> own = slot.Itemstack.Class == EnumItemClass.Item
                ? slot.Itemstack.Item.Textures
                : slot.Itemstack.Block.Textures;

            if (own != null)
            {
                foreach (KeyValuePair<string, CompositeTexture> kv in own)
                {
                    textures[kv.Key] = kv.Value.Base;
                }
            }

            return new ContainedTextureSource(capi, capi.BlockTextureAtlas, textures,
                                              "For window displayed item " + collectible.Code);
        }

        // ── ITexPositionSource, for the item tesselation fallback ────────────

        public Size2i AtlasSize => capi.BlockTextureAtlas.Size;

        public TextureAtlasPosition this[string textureCode]
        {
            get
            {
                IDictionary<string, CompositeTexture> textures = _nowTesselatingObj is Item item
                    ? (IDictionary<string, CompositeTexture>)item.Textures
                    : (_nowTesselatingObj as Block)?.Textures;

                AssetLocation path = null;
                if (textures != null)
                {
                    if (textures.TryGetValue(textureCode, out CompositeTexture ct)) path = ct.Baked.BakedName;
                    else if (textures.TryGetValue("all", out CompositeTexture all)) path = all.Baked.BakedName;
                }

                if (path == null) _nowTesselatingShape?.Textures.TryGetValue(textureCode, out path);
                path ??= new AssetLocation(textureCode);

                TextureAtlasPosition texPos = capi.BlockTextureAtlas[path];
                if (texPos == null && !capi.BlockTextureAtlas.GetOrInsertTexture(path, out _, out texPos))
                {
                    return capi.BlockTextureAtlas.UnknownTexturePosition;
                }
                return texPos;
            }
        }

        // ── Persistence ──────────────────────────────────────────────────────

        // LEGACY SLOT MIGRATION REMOVED 2026-07-30, and it is worth knowing why it was
        // never needed and was not merely useless.
        //
        // It converted a pre-psurface inventory — the old block entity used an
        // InventoryGeneric, so slots were saved under integer keys ("0", "1", …) which
        // WindowSlotId.Decode rejects. That situation cannot arise here: Window Display
        // ships as a SEPARATE mod from Window Storage with its own block codes, nothing is
        // remapped, and no windowdisplay block has ever written an integer slot id. It was
        // already recorded as "not applicable" rather than merely untested.
        //
        // It also ran from FromTreeAttributes, so it re-decoded every slot id of every
        // window on every load and every server sync, to reach a `return` every time.
        //
        // And it was destructive before it validated:
        //
        //     inv.SlotsByslotId.Remove(key);            // removed FIRST
        //     if (stack == null) continue;
        //     if (!int.TryParse(key, out int index)) continue;   // <- stack dropped here
        //
        // Any slot id that was neither a valid WindowSlotId nor an integer had its slot
        // removed and its itemstack discarded — silent item loss, with no path back. That
        // is a poor trade for a migration that cannot fire, which is what settled removing
        // it rather than fixing the ordering.

        public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldForResolving)
        {
            base.FromTreeAttributes(tree, worldForResolving);

            bool[] previousPanes = paneStates != null ? (bool[])paneStates.Clone() : null;

            MeshAngleRad = tree.GetFloat("meshAngleRad");

            string states = tree.GetString("paneStates", "");
            if (!string.IsNullOrEmpty(states))
            {
                paneStates = new bool[states.Length];
                for (int i = 0; i < states.Length; i++) paneStates[i] = states[i] == '1';
            }

            // ALWAYS replaced, including with null when the tree carries no rotations.
            //
            // Only assigning when the attribute is present left stale angles behind, because
            // ToTreeAttributes omits "rotation" entirely once the dictionary is empty. Take
            // the last rotated item off a window and the server sends no rotation data at
            // all, so the client kept its old map — and the next item placed in that slot
            // rendered at the previous occupant's angle on the client while the server had
            // it square. The tree is authoritative; treat it as such.
            customRotationDegBySlot = null;
            if (tree.HasAttribute("rotation"))
            {
                customRotationDegBySlot = new Dictionary<string, float>();
                foreach (var kv in tree.GetTreeAttribute("rotation"))
                {
                    if (kv.Value is FloatAttribute f) customRotationDegBySlot[kv.Key] = f.value;
                }
            }

            InvalidateBoxes();
            MarkMeshesDirty();

            if (worldForResolving.Side == EnumAppSide.Client && Api != null)
            {
                bool reinitialised = EnsureAnimatorCurrent();

                bool panesChanged = previousPanes == null || paneStates == null || previousPanes.Length != paneStates.Length;
                if (!panesChanged && previousPanes != null)
                {
                    for (int i = 0; i < paneStates.Length && i < previousPanes.Length; i++)
                    {
                        if (previousPanes[i] != paneStates[i]) { panesChanged = true; break; }
                    }
                }

                if (panesChanged || reinitialised) UpdateAnimationState();

                if (panesChanged) soundHandler?.PlaySlideSound();
                soundHandler?.FadeSharedSoundsToCurrentTarget();
            }
        }

        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            base.ToTreeAttributes(tree);
            tree.SetFloat("meshAngleRad", MeshAngleRad);

            if (paneStates != null && paneStates.Length > 0)
            {
                var chars = new char[paneStates.Length];
                for (int i = 0; i < paneStates.Length; i++) chars[i] = paneStates[i] ? '1' : '0';
                tree.SetString("paneStates", new string(chars));
            }

            if (customRotationDegBySlot != null && customRotationDegBySlot.Count > 0)
            {
                var rotTree = new TreeAttribute();
                foreach (var kv in customRotationDegBySlot) rotTree[kv.Key] = new FloatAttribute(kv.Value);
                tree["rotation"] = rotTree;
            }
        }

        // ── Info ─────────────────────────────────────────────────────────────

        public override void GetBlockInfo(IPlayer forPlayer, StringBuilder dsc)
        {
            bool roomSafe = WindowDisplayLibConfig.Current?.RoomSafeOpening ?? false;
            if (CanOpen && !roomSafe)
            {
                dsc.AppendLine(IsAnyWindowPaneOpen ? Lang.Get("Open") : Lang.Get("Closed"));
            }

            container.ReloadRoom();

            // container.GetPerishRate() only accounts for the room. The open/closed
            // multiplier lives in our OnAcquireTransitionSpeed delegate, so it has to
            // be applied here too or the tooltip never moves when a pane opens.
            float perishRate = container.GetPerishRate() * OnAcquireTransitionSpeed(EnumTransitionType.Perish, null, 1f);
            dsc.AppendLine(Lang.Get("Stored food perish speed: {0}x", Math.Round(perishRate, 2)));

            // The pending placement angle used to be shown here because it was persistent
            // but otherwise invisible — you saw it when you pressed the key and never
            // again, so items quietly went on sideways later.
            //
            // THE GHOST REPLACED THAT REASON. It draws the item at its actual pending angle
            // for as long as you are aiming, which is strictly better than a number: you
            // see the orientation rather than reading it. Dropped from the panel on
            // 2026-07-29, once the preview was confirmed working in game.
            //
            // Still shown when the ghost is switched off, because the angle is invisible
            // again then. Same fallback as the key-press message, and the same reason.
            //
            // (Kept out of the interaction help, then and now: composed help tooltips are
            // CACHED and not rebuilt — the same reason the ctrl/shift label goes stale after
            // a rebind — so a live value there would lie. This panel is rebuilt while aiming
            // at any part of the window, including bare sill.)
            if (WindowDisplayLibConfig.Current?.PlacementGhost != true)
            {
                int pendingRot = PlacementRotation.For(Api, forPlayer);
                if (pendingRot != 0 && forPlayer?.InventoryManager?.ActiveHotbarSlot?.Empty == false)
                {
                    dsc.AppendLine(Lang.Get("windowdisplaylib:placement-rotation", pendingRot));
                }
            }

            string slotId = GetSelectedNonEmptySlotId(forPlayer?.CurrentBlockSelection);
            if (slotId != null && inv[slotId] is { Empty: false } slot)
            {
                AppendStoredItemInfo(dsc, slot, perishRate);
            }
        }

        /// <summary>
        /// The aimed item's name, plus its spoilage and temperature when it has them.
        ///
        /// Spoilage is formatted by <c>BlockEntityShelf.PerishableInfoCompact</c> — vanilla's
        /// own helper, public and static, and what a shelf uses for the same job. Reusing it
        /// keeps the wording, rounding and "fresh / X days" thresholds identical to every
        /// other container in the game rather than inventing a second dialect. It already
        /// includes the item name, so the plain name is only appended when the item has no
        /// transitionable properties at all.
        ///
        /// <paramref name="perishRate"/> is passed through as the ripen rate so the figure
        /// reflects THIS window — the room, and whether a pane is open — instead of a generic
        /// estimate. That is the same number the "Stored food perish speed" line reports.
        /// </summary>
        private void AppendStoredItemInfo(StringBuilder dsc, ItemSlot slot, float perishRate)
        {
            ItemStack stack = slot.Itemstack;
            if (stack?.Collectible == null) return;

            TransitionableProperties[] props =
                stack.Collectible.GetTransitionableProperties(Api.World, stack, null);

            if (props != null && props.Length > 0)
            {
                // The third argument is a RIPEN rate, not a perish rate — vanilla's own
                // BlockEntityShelf derives it as a clamped 0..1 "how good a spot is this for
                // ripening", and PerishableInfoCompact uses it only in the Ripen branch, as
                // (TransitionHours - TransitionedHours) / HoursPerDay / ripenRate.
                //
                // Passing our perish rate here was wrong: it runs to ~4.8, so anything that
                // RIPENS reported a "days left to ripen" several times too short. The perish
                // side was unaffected — that branch divides by the collectible's own
                // GetTransitionRateMul and never looks at this argument.
                float ripenRate = GameMath.Clamp((1f - container.GetPerishRate() - 0.5f) * 3f, 0f, 1f);
                dsc.Append(BlockEntityShelf.PerishableInfoCompact(Api, slot, ripenRate));
            }
            else
            {
                // Matches vanilla's fallback: a crock or meal names its own contents
                dsc.AppendLine(
                    stack.Collectible.GetCollectibleInterface<IContainedCustomName>()?.GetContainedInfo(slot)
                    ?? stack.GetName());
            }

            // Only when the stack actually carries a temperature — otherwise every stored
            // item would report the ambient value, which is noise on a jar or a candle.
            if (stack.Attributes?.GetTreeAttribute("temperature") != null)
            {
                float temp = stack.Collectible.GetTemperature(Api.World, stack);
                dsc.AppendLine(Lang.Get("Temperature: {0}°C", (int)Math.Round(temp)));
            }
        }
    }
}
