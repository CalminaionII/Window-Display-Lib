using System;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace WindowDisplayLib
{
    /// <summary>
    /// Block class for the psurface window storage prototype.
    ///
    /// Derives from <see cref="BlockGeneric"/> — this is load-bearing, not
    /// cosmetic: only BlockGeneric walks the StrongBlockBehavior list and
    /// aggregates GetSelectionBoxes / GetCollisionBoxes / DoPartialSelection.
    /// Plain Block silently ignores them, which is why ARL's own box overrides
    /// never fired on the pre-psurface BlockWindowStorageLib.
    /// </summary>
    public class BlockWindowDisplay : BlockGeneric
    {
        public override bool TryPlaceBlock(IWorldAccessor world, IPlayer byPlayer, ItemStack itemstack, BlockSelection blockSel, ref string failureCode)
        {
            // Always place the closed variant, so pane state starts consistent
            Block closedBlock = world.GetBlock(CodeWithVariant("state", "closed"));
            ItemStack placeStack = itemstack;

            if (closedBlock != null && closedBlock.Id != Id)
            {
                placeStack = new ItemStack(closedBlock);
                if (itemstack?.Attributes != null) placeStack.Attributes.MergeTree(itemstack.Attributes);
            }

            // A tall window needs its whole stack to fit before any of it is placed,
            // otherwise a blocked ceiling leaves half a window behind
            Block baseBlock = closedBlock ?? this;
            var segments = WindowChain.ResolveSegmentsAbove(world, baseBlock);
            if (segments.Count > 0 && !WindowChain.HasRoomFor(world, blockSel.Position, segments))
            {
                failureCode = "requireair";
                return false;
            }

            if (!base.TryPlaceBlock(world, byPlayer, placeStack, blockSel, ref failureCode)) return false;

            float angle = SnapPlacementAngle(byPlayer, blockSel);

            if (world.BlockAccessor.GetBlockEntity(blockSel.Position) is BEWindowDisplay be)
            {
                be.MeshAngleRad = angle;
                be.MarkMeshesDirty();
                be.InvalidateBoxes();

                if (world.Side == EnumAppSide.Server) be.MarkDirty(true);
                else be.RefreshAnimator();   // same frame, so the window never renders unrotated
            }

            // placeStack, not itemstack — it carries the merged ARL variants
            if (segments.Count > 0) WindowChain.PlaceSegments(world, blockSel.Position, segments, angle, placeStack);

            return true;
        }

        /// <summary>Faces the window at the player, snapped to 90°.</summary>
        private static float SnapPlacementAngle(IPlayer byPlayer, BlockSelection blockSel)
        {
            BlockPos pos = blockSel.DidOffset ? blockSel.Position.AddCopy(blockSel.Face.Opposite) : blockSel.Position;
            double dx = byPlayer.Entity.Pos.X - (pos.X + blockSel.HitPosition.X);
            double dz = byPlayer.Entity.Pos.Z - (pos.Z + blockSel.HitPosition.Z);
            float yaw = (float)Math.Atan2(dx, dz);
            return (float)Math.Round(yaw / (GameMath.PI / 2f)) * (GameMath.PI / 2f);
        }

        public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
        {
            if (world.BlockAccessor.GetBlockEntity(blockSel?.Position) is BEWindowDisplay be
                && be.OnBlockInteractStart(world, byPlayer, blockSel))
            {
                return true;
            }

            return base.OnBlockInteractStart(world, byPlayer, blockSel);
        }

        /// <summary>
        /// A window placed or broken directly below changes which surfaces are covered,
        /// so the cached selection boxes have to be rebuilt. Runs on both sides.
        /// </summary>
        public override void OnNeighbourBlockChange(IWorldAccessor world, BlockPos pos, BlockPos neibpos)
        {
            base.OnNeighbourBlockChange(world, pos, neibpos);

            if (neibpos.X == pos.X && neibpos.Z == pos.Z && neibpos.Y == pos.Y - 1
                && world.BlockAccessor.GetBlockEntity(pos) is BEWindowDisplay be)
            {
                be.InvalidateBoxes();
                be.MarkMeshesDirty();
            }
        }

        /// <summary>Light comes from whatever is stored on the sills.</summary>
        public override byte[] GetLightHsv(IBlockAccessor blockAccessor, BlockPos pos, ItemStack stack = null)
        {
            if (pos != null)
            {
                try
                {
                    if (blockAccessor.GetBlockEntity(pos) is BEWindowDisplay be && be.CachedLightHsv != null)
                    {
                        return be.CachedLightHsv;
                    }
                }
                catch (Exception)
                {
                    // Light is non-critical and this runs off-thread during relight;
                    // fall through to the block default rather than take the world down.
                }
            }

            return base.GetLightHsv(blockAccessor, pos, stack);
        }

        /// <summary>
        /// True when this window appears in at least one creative tab. A variant only
        /// reachable by wrench-swapping — an edge position, say — declares its stacks
        /// with no tabs, so it reports false.
        ///
        /// Asked of the *closed* variant deliberately. Content keys creative stacks on
        /// "*-closed", so the open variants have none of their own; testing the block
        /// as-is made an open main window look unobtainable and drop its edge
        /// counterpart. Drops are always closed anyway, so the closed form is the one
        /// whose availability actually matters.
        /// </summary>
        private bool IsObtainableDirectly(IWorldAccessor world)
        {
            Block closed = world.GetBlock(CodeWithVariant("state", "closed")) ?? this;
            return closed.CreativeInventoryStacks?.Any(s => s.Tabs != null && s.Tabs.Length > 0) == true;
        }

        /// <summary>
        /// Resolves what this block should drop as.
        ///
        /// Derived rather than configured: a block that is in no creative tab cannot be
        /// obtained on its own, so breaking it hands back its wrenchSwapTo counterpart —
        /// the version that *is* obtainable. Otherwise a player could break an edge
        /// window and re-place it directly, sidestepping the wrench entirely.
        ///
        /// The optional <c>dropsAs</c> attribute overrides this, using the same
        /// {variant} substitution as wrenchSwapTo, for cases the rule gets wrong.
        /// </summary>
        private Block ResolveDropBlock(IWorldAccessor world)
        {
            string template = Attributes?["dropsAs"].AsString(null);
            if (template != null)
            {
                return world.GetBlock(new AssetLocation(WindowSwapHelper.ResolveCode(this, template))) ?? this;
            }

            if (!IsObtainableDirectly(world))
            {
                return WindowSwapHelper.GetNextTarget(world, this) ?? this;
            }

            return this;
        }

        /// <summary>Breaking any segment of a tall window takes the whole stack with it.</summary>
        public override void OnBlockBroken(IWorldAccessor world, BlockPos pos, IPlayer byPlayer, float dropQuantityMultiplier = 1f)
        {
            WindowChain.BreakChain(world, pos, byPlayer);
            base.OnBlockBroken(world, pos, byPlayer, dropQuantityMultiplier);
        }

        /// <summary>Drops the obtainable counterpart, always in its closed state.</summary>
        public override ItemStack[] GetDrops(IWorldAccessor world, BlockPos pos, IPlayer byPlayer, float dropQuantityMultiplier = 1f)
        {
            // Upper segments are not items — the bottom of the stack is what drops
            if (WindowChain.IsPairedBelow(this)) return Array.Empty<ItemStack>();

            Block dropBlock = ResolveDropBlock(world);
            Block closedBlock = world.GetBlock(dropBlock.CodeWithVariant("state", "closed")) ?? dropBlock;

            ItemStack[] drops = base.GetDrops(world, pos, byPlayer, dropQuantityMultiplier);
            if (drops == null || drops.Length == 0) return drops;
            if (closedBlock.Id == Id) return drops;

            // Keep ARL's variant attributes, just re-point at the resolved block
            var closedStack = new ItemStack(closedBlock);
            if (drops[0].Attributes != null) closedStack.Attributes.MergeTree(drops[0].Attributes);
            drops[0] = closedStack;
            return drops;
        }

        /// <summary>
        /// The name shown for a PLACED block — this block's own, not its drop's.
        ///
        /// Vanilla's <c>Block.GetPlacedBlockName</c> derives the name from
        /// <see cref="OnPickBlock"/>, and OnPickBlock here deliberately hands back the
        /// obtainable counterpart rather than this block. For an edge variant — which is
        /// never obtainable directly, so <c>ResolveDropBlock</c> falls through to
        /// <c>GetNextTarget</c> — that means the info box showed **the name of whatever the
        /// block swaps to**.
        ///
        /// It went unnoticed because each edge style swapped back to its own partner, which
        /// shares its lang entry: <c>leftedge</c> borrowed "Left Opening Window" from
        /// <c>left</c> and looked right. It only became visible once a swap chain crossed
        /// sides — which the left/right mirror was the first feature to do — at which point <c>leftedge</c> started
        /// announcing itself as the RIGHT window — reported as "the edge ones aren't
        /// updating their names", and misdiagnosed several times over as a lang problem, a
        /// stale cache, crossed shape files and a client sync issue. It was none of those:
        /// the server, the lang file and the swap were correct throughout.
        ///
        /// Drops and middle-click keep resolving to the obtainable block, which is
        /// deliberate and unchanged. Only the displayed name is corrected.
        /// </summary>
        public override string GetPlacedBlockName(IWorldAccessor world, BlockPos pos)
        {
            if (Code == null) return base.GetPlacedBlockName(world, pos);

            string name = Lang.GetMatching(Code.Domain + ":block-" + Code.Path);
            return string.IsNullOrEmpty(name) ? base.GetPlacedBlockName(world, pos) : name;
        }

        /// <summary>Middle-click mirrors the drop, so it cannot hand out an unobtainable variant either.</summary>
        public override ItemStack OnPickBlock(IWorldAccessor world, BlockPos pos)
        {
            ItemStack stack = base.OnPickBlock(world, pos);

            Block dropBlock = ResolveDropBlock(world);
            Block closedBlock = world.GetBlock(dropBlock.CodeWithVariant("state", "closed")) ?? dropBlock;
            if (stack == null || closedBlock.Id == Id) return stack;

            var picked = new ItemStack(closedBlock);
            if (stack.Attributes != null) picked.Attributes.MergeTree(stack.Attributes);
            return picked;
        }
    }
}
