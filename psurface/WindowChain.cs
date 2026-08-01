using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace WindowDisplayLib
{
    /// <summary>
    /// Multi-block tall windows, driven by two block attributes carried over from the
    /// pre-psurface content:
    ///
    /// <code>
    /// "placeWith":  "windowdisplay:windowdisplay-tallcasementtop2-{state}"  // place above me
    /// "pairedWith": "below"                                                // I belong to the one below
    /// "linkedOpen": true                                                   // panes toggle together
    /// </code>
    ///
    /// A stack is authored bottom-up: the bottom names the middle, the middle names the
    /// top. Only the bottom is obtainable; the rest are placed with it, break with it,
    /// and swap with it.
    /// </summary>
    public static class WindowChain
    {
        /// <summary>Guards against a content loop where placeWith eventually points back at itself.</summary>
        private const int MaxSegments = 8;

        public static bool IsPairedBelow(Block block)
            => block?.Attributes?["pairedWith"].AsString(null) == "below";

        public static bool IsLinkedOpen(Block block)
            => block?.Attributes?["linkedOpen"].AsBool(false) ?? false;

        public static string PlaceWithTemplate(Block block)
            => block?.Attributes?["placeWith"].AsString(null);

        /// <summary>
        /// The blocks this one wants stacked above it, resolved bottom-up.
        /// Does not look at the world — this is what *should* exist.
        /// </summary>
        public static List<Block> ResolveSegmentsAbove(IWorldAccessor world, Block baseBlock)
        {
            var segments = new List<Block>();
            Block current = baseBlock;

            while (segments.Count < MaxSegments)
            {
                string template = PlaceWithTemplate(current);
                if (template == null) break;

                Block next = world.GetBlock(new AssetLocation(WindowSwapHelper.ResolveCode(current, template)));
                if (next == null)
                {
                    world.Logger.Warning("[WindowDisplayLib] {0} placeWith does not resolve to a block: {1}",
                        current.Code, template);
                    break;
                }

                segments.Add(next);
                current = next;
            }

            return segments;
        }

        /// <summary>Walks down to the bottom of the stack this position belongs to.</summary>
        public static BlockPos FindBottom(IWorldAccessor world, BlockPos pos)
        {
            BlockPos cursor = pos.Copy();

            for (int i = 0; i < MaxSegments; i++)
            {
                if (!IsPairedBelow(world.BlockAccessor.GetBlock(cursor))) break;
                BlockPos below = cursor.DownCopy();
                if (world.BlockAccessor.GetBlockEntity(below) is not BEWindowDisplay) break;
                cursor = below;
            }

            return cursor;
        }

        /// <summary>
        /// Every position in the stack containing <paramref name="pos"/>, bottom first.
        /// Always returns at least the position itself, so callers can treat a single
        /// window as a one-segment chain.
        /// </summary>
        public static List<BlockPos> Enumerate(IWorldAccessor world, BlockPos pos)
        {
            BlockPos bottom = FindBottom(world, pos);
            var chain = new List<BlockPos> { bottom };

            BlockPos cursor = bottom;
            for (int i = 0; i < MaxSegments; i++)
            {
                if (PlaceWithTemplate(world.BlockAccessor.GetBlock(cursor)) == null) break;

                BlockPos above = cursor.UpCopy();
                if (world.BlockAccessor.GetBlockEntity(above) is not BEWindowDisplay) break;

                chain.Add(above);
                cursor = above;
            }

            return chain;
        }

        /// <summary>True when every cell the stack needs above <paramref name="basePos"/> is free.</summary>
        public static bool HasRoomFor(IWorldAccessor world, BlockPos basePos, List<Block> segments)
        {
            for (int i = 0; i < segments.Count; i++)
            {
                BlockPos at = basePos.UpCopy(i + 1);
                if (!world.BlockAccessor.GetBlock(at).IsReplacableBy(segments[i])) return false;
            }
            return true;
        }

        /// <summary>
        /// Places the upper segments, carrying the placement angle and the placing
        /// itemstack onto each.
        ///
        /// The stack matters: AttributeRenderingLibrary reads its wood/glass variants in
        /// OnBlockPlaced(byItemStack). Placing with the plain SetBlock overload gives it
        /// nothing, so the upper segments fall back to default textures and a tall window
        /// ends up with mismatched materials between its own segments.
        /// </summary>
        public static void PlaceSegments(IWorldAccessor world, BlockPos basePos, List<Block> segments,
                                         float meshAngleRad, ItemStack withStack)
        {
            for (int i = 0; i < segments.Count; i++)
            {
                BlockPos at = basePos.UpCopy(i + 1);

                if (withStack != null)
                {
                    // Same attributes, re-pointed at the segment's own block
                    var segStack = new ItemStack(segments[i]);
                    if (withStack.Attributes != null) segStack.Attributes.MergeTree(withStack.Attributes);
                    world.BlockAccessor.SetBlock(segments[i].Id, at, segStack);
                }
                else
                {
                    world.BlockAccessor.SetBlock(segments[i].Id, at);
                }

                if (world.BlockAccessor.GetBlockEntity(at) is BEWindowDisplay be)
                {
                    be.MeshAngleRad = meshAngleRad;
                    be.MarkMeshesDirty();
                    be.InvalidateBoxes();

                    if (world.Side == EnumAppSide.Server) be.MarkDirty(true);
                    // Same frame as the base block, so the segments neither lag behind
                    // it nor render before their variants have arrived
                    else be.RefreshAnimator();
                }
            }
        }

        /// <summary>
        /// Removes every other segment of the stack and drops their contents.
        ///
        /// Only the bottom segment is a real item — everything above is `pairedWith:
        /// below` and returns no drops. So when the player breaks one of those instead,
        /// the bottom's item has to be spawned here: the normal break path never runs
        /// for it, and clearing it with SetBlock(0) drops nothing. Breaking the top of
        /// a two-high window is the common case, since that is the one at eye level.
        /// </summary>
        public static void BreakChain(IWorldAccessor world, BlockPos brokenPos, IPlayer byPlayer)
        {
            if (world.Side != EnumAppSide.Server) return;

            List<BlockPos> chain = Enumerate(world, brokenPos);
            if (chain.Count <= 1) return;

            BlockPos bottom = chain[0];
            bool creative = byPlayer?.WorldData?.CurrentGameMode == EnumGameMode.Creative;

            foreach (BlockPos at in chain)
            {
                if (at.Equals(brokenPos)) continue;

                if (world.BlockAccessor.GetBlockEntity(at) is BEWindowDisplay be)
                {
                    be.Inventory.DropAll(at.ToVec3d().Add(0.5, 0.5, 0.5));
                }

                // Read the drops while the block is still there
                if (at.Equals(bottom) && !creative)
                {
                    Block bottomBlock = world.BlockAccessor.GetBlock(at);
                    ItemStack[] drops = bottomBlock.GetDrops(world, at, byPlayer);
                    if (drops != null)
                    {
                        foreach (ItemStack stack in drops)
                        {
                            world.SpawnItemEntity(stack, at.ToVec3d().Add(0.5, 0.5, 0.5));
                        }
                    }
                }

                world.BlockAccessor.SetBlock(0, at);
            }
        }

        /// <summary>
        /// Syncs one pane index across the stack. Only travels through segments that
        /// opt in with linkedOpen, so a plain window stacked on a tall one is unaffected.
        /// </summary>
        public static void SyncPane(IWorldAccessor world, BlockPos fromPos, int paneIndex, bool open)
        {
            if (!IsLinkedOpen(world.BlockAccessor.GetBlock(fromPos))) return;

            foreach (BlockPos at in Enumerate(world, fromPos))
            {
                if (at.Equals(fromPos)) continue;
                if (!IsLinkedOpen(world.BlockAccessor.GetBlock(at))) continue;
                if (world.BlockAccessor.GetBlockEntity(at) is BEWindowDisplay be)
                {
                    be.SetPaneState(paneIndex, open);
                }
            }
        }
    }
}
