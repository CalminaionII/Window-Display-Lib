using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace WindowDisplayLib
{
    /// <summary>
    /// In-place block swapping driven by the <c>wrenchSwapTo</c> block attribute.
    ///
    /// <code>
    /// "wrenchSwapTo": "windowstorage:windowstorage-awningedge-{state}"
    /// "wrenchSwapTo": ["windowstorage:windowstorage-awningedge-{state}",
    ///                  "windowstorage:windowstorage-hopper-{state}"]
    /// </code>
    ///
    /// Any <c>{variantcode}</c> placeholder is filled from the current block's own
    /// variants, so <c>{state}</c> preserves open/closed across the swap.
    ///
    /// Triggered by Ctrl (sprint) + wrench + right-click on a frame or pane box.
    /// Refuses while anything is stored: the target block has different psurface
    /// geometry, so the string slot ids would not survive the exchange.
    /// </summary>
    public static class WindowSwapHelper
    {
        private static readonly Regex PlaceholderPattern = new Regex(@"\{([a-zA-Z0-9_]+)\}", RegexOptions.Compiled);

        public static bool HasSwapTargets(Block block) => GetSwapTargets(block).Length > 0;

        public static string[] GetSwapTargets(Block block)
        {
            JsonObject attr = block?.Attributes?["wrenchSwapTo"];
            if (attr == null || !attr.Exists) return Array.Empty<string>();

            // Accept a bare string or an array of strings
            string single = attr.AsString(null);
            if (single != null) return new[] { single };

            return attr.AsArray<string>(null) ?? Array.Empty<string>();
        }

        /// <summary>Substitutes {variant} placeholders using the source block's own variants.</summary>
        public static string ResolveCode(Block sourceBlock, string template)
        {
            return PlaceholderPattern.Replace(template, match =>
            {
                string variantCode = match.Groups[1].Value;
                string value = sourceBlock.Variant[variantCode];
                return value ?? match.Value;
            });
        }

        /// <summary>
        /// Resolves the swap target for the current block: the FIRST listed template that
        /// resolves to a real block other than this one. Returns null when nothing usable
        /// is configured.
        ///
        /// An array is a FALLBACK LIST, not a cycle — the current block's position in it is
        /// never consulted, so `["a", "b"]` always yields "a" while "a" exists. This method
        /// used to claim it cycled, which is wrong and cost a wrong turn once.
        ///
        /// Cycles are expressed across BLOCKS instead, each with a single target pointing at
        /// the next: `left → leftedge → right → rightedge → left` is four one-line
        /// attributes and needs no array. That is how every cycle in the content is built.
        /// </summary>
        public static Block GetNextTarget(IWorldAccessor world, Block currentBlock)
        {
            string[] templates = GetSwapTargets(currentBlock);
            if (templates.Length == 0) return null;

            var resolved = new List<Block>();
            foreach (string template in templates)
            {
                Block candidate = world.GetBlock(new AssetLocation(ResolveCode(currentBlock, template)));
                if (candidate != null && candidate.Id != currentBlock.Id) resolved.Add(candidate);
            }

            return resolved.Count > 0 ? resolved[0] : null;
        }

        /// <summary>
        /// Performs the swap. Server-side only; the client gets it via the block
        /// update. Returns true when the interaction was consumed, whether or not
        /// the swap actually happened — a refusal still counts as handled.
        /// </summary>
        public static bool TrySwap(IWorldAccessor world, BlockPos pos, IPlayer byPlayer, BEWindowDisplay be)
        {
            if (be == null) return false;

            Block currentBlock = be.Block;
            if (!HasSwapTargets(currentBlock)) return false;

            // A tall window swaps as one unit, so every segment has to be empty —
            // otherwise half the stack would move and the rest would refuse
            List<BlockPos> chain = WindowChain.Enumerate(world, pos);
            foreach (BlockPos at in chain)
            {
                if (world.BlockAccessor.GetBlockEntity(at) is BEWindowDisplay segment && !segment.Inventory.Empty)
                {
                    (world.Api as ICoreClientAPI)?.TriggerIngameError(be, "notempty",
                        Lang.Get("windowdisplaylib:swap-notempty"));
                    return true;
                }
            }

            if (world.Side != EnumAppSide.Server) return true;

            // Resolve every target before touching the world, so a chain with one
            // unresolvable segment does not end up half swapped
            var targets = new List<(BlockPos pos, Block target)>();
            foreach (BlockPos at in chain)
            {
                Block segBlock = world.BlockAccessor.GetBlock(at);
                Block segTarget = GetNextTarget(world, segBlock);
                if (segTarget == null)
                {
                    world.Logger.Warning("[WindowDisplayLib] {0} at {1} has no resolvable wrenchSwapTo; chain swap aborted.",
                        segBlock.Code, at);
                    return true;
                }
                targets.Add((at.Copy(), segTarget));
            }

            // synchronize: false, relight: true — do NOT change this.
            //
            // Setting synchronize to true was tried on 2026-07-27, on the theory that a
            // stale client-side block ID was why a swap updated the shape but not the
            // block's name. It did NOT fix the name, and it made the swap visibly
            // non-instant, so it is a straight regression. Reverted.
            IBlockAccessor relightAccessor = world.GetBlockAccessor(false, true, false);

            foreach (var (at, segTarget) in targets)
            {
                if (world.BlockAccessor.GetBlockEntity(at) is not BEWindowDisplay segment) continue;

                // Snapshot before the exchange; ExchangeBlock keeps the block entity
                ITreeAttribute carried = new TreeAttribute();
                segment.ToTreeAttributes(carried);

                relightAccessor.ExchangeBlock(segTarget.Id, at);

                if (relightAccessor.GetBlockEntity(at) is BEWindowDisplay newBe)
                {
                    newBe.FromTreeAttributes(carried, world);
                    newBe.ClampPaneStatesToBlock();
                    newBe.RefreshAfterSwap();
                    newBe.MarkDirty(true);
                }
            }

            // dualCallByPlayer is NULL here, and that is the opposite of what the rest of
            // this mod passes — deliberately, because this call site is different.
            //
            // From the API docs: "If this call is made on client AND on server, set this to
            // the causing player to prevent double playing... dualCall will play the sound on
            // the client, and send it to all other players except source client." Right for
            // the put/take/toggle sounds in BEWindowDisplay, which really do run on both
            // sides through OnBlockInteractStart.
            //
            // This one does not. The client returned at the `world.Side != Server` line
            // above, long before here — so naming the swapping player suppressed the sound
            // for the ONE person who caused it, while everybody else heard it. Passing null
            // sends it to everyone, the acting player included.
            world.PlaySoundAt(new AssetLocation("game", "sounds/block/planks"),
                pos.X + 0.5, pos.Y + 0.5, pos.Z + 0.5, null, randomizePitch: true, range: 16f);

            return true;
        }
    }
}
