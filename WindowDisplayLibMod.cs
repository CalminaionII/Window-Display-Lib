using System;
using System.Linq;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.Client.NoObf;

[assembly: ModInfo("Window Display", "windowdisplaylib",
    Description = "Adds functional window storage blocks with animations and environmental sounds",
    Authors = new[] { "Calminaion" })]

namespace WindowDisplayLib
{
    /// <summary>
    /// Tells every client to move one pane on a whole stack at once.
    ///
    /// Per-block state sync alone is not enough for tall windows: each segment reaches
    /// FromTreeAttributes on its own schedule, so the segments visibly start their
    /// animation at slightly different moments. One packet for the whole chain starts
    /// them in the same client frame.
    ///
    /// **THE ProtoContract IS REQUIRED. It was missing until 2026-08-01 and that shipped
    /// in 1.0.1 as a server-breaking bug.** This file used to claim, in the comment on
    /// RotateStoredItemPacket, that this type "survives without one because of how it is
    /// registered and broadcast". It does not. On a dedicated server the first
    /// BroadcastPacket throws
    ///
    ///     Type is not expected, and no contract can be inferred: LinkedOpenPacket
    ///
    /// out of ProtoBuf.Meta.TypeModel.ThrowUnexpectedType, which the server treats as the
    /// player having thrown an exception and KICKS THEM. `BroadcastLinkedPane` runs from
    /// TogglePane unconditionally, so this was not limited to tall windows — **opening any
    /// window at all disconnected the player.**
    ///
    /// Singleplayer never showed it, which is why testing missed it entirely: the
    /// integrated server does not put the packet through protobuf on that path. Anything
    /// that only crosses a real network needs testing on a real server, exactly as the
    /// render path needs testing in game.
    /// </summary>
    [ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
    public class LinkedOpenPacket
    {
        public int[] Positions;   // flat: x,y,z per segment
        public int PaneIndex;
        public bool IsOpen;
    }

    /// <summary>
    /// Tells the server the angle this player wants their next item placed at.
    ///
    /// The ProtoContract is required, not decoration. protobuf-net cannot infer a contract
    /// for a plain class and throws "Type is not expected, and no contract can be inferred"
    /// the first time it is sent — which took the client down on the first key press.
    /// </summary>
    [ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
    public class PlacementRotationPacket
    {
        public int Deg;
    }

    /// <summary>
    /// Turn an item that is ALREADY on a surface, from the mouse wheel.
    ///
    /// A packet because this is server state: the wrench path reaches the same code through
    /// OnBlockInteractStart, which the engine runs on both sides for us. A wheel notch is
    /// purely a client input, so it has to be sent.
    ///
    /// ProtoContract for the same reason PlacementRotationPacket needs one — protobuf-net
    /// cannot infer a contract for a plain class and the first send throws "Type is not
    /// expected".
    ///
    /// This comment used to add that LinkedOpenPacket "survives without one because of how
    /// it is registered and broadcast". **That was wrong and it cost a shipped release.**
    /// EVERY packet type needs the attribute; there is no exemption for broadcasts. See the
    /// note on LinkedOpenPacket.
    /// </summary>
    [ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
    public class RotateStoredItemPacket
    {
        public int X, Y, Z;
        public string SlotId;
        public bool Reverse;
    }

    /// <summary>
    /// The angle a player wants their next placed item turned to, in quarter turns.
    ///
    /// It has to TRAVEL with the interaction rather than be worked out twice. The client
    /// previews from its own state while the server places from the BlockSelection it
    /// receives, and anything derived independently on the two sides drifts apart — which is
    /// exactly why sub-voxel placement was reverted, and why the session log's conclusion was
    /// that the value must be sent. So the client owns it, sends it whenever it changes, and
    /// the server remembers the last one per player.
    ///
    /// **Free rotation since 2026-07-29**, sharing `RotationStepDegrees` with the wrench so
    /// one setting governs turning an item before and after it is placed.
    ///
    /// It was quarter turns only, because a footprint is an axis-aligned box and
    /// `RotatedFootprint` swaps width and length only past 45° — so at any other angle the
    /// fit test measures the item square while the model sits turned, and "fits" becomes an
    /// approximation. That restriction existed because nothing SHOWED the discrepancy.
    ///
    /// The ghost removed the reason for it. The player can now see the item overhang, which
    /// is better feedback than a rule preventing them from trying. The wrench has always
    /// worked this way on already-placed items and nobody has minded.
    /// </summary>
    public static class PlacementRotation
    {
        /// <summary>
        /// Degrees per wheel notch, from the config. Clamped rather than trusted: a zero
        /// would make the gesture do nothing at all and look broken.
        /// </summary>
        public static int Step =>
            Math.Clamp(WindowDisplayLibConfig.Current?.RotationStepDegrees ?? 15, 5, 90);

        /// <summary>Client side: this player's own pending angle.</summary>
        public static int Local;

        private static readonly System.Collections.Generic.Dictionary<string, int> ByPlayer =
            new System.Collections.Generic.Dictionary<string, int>();

        public static int Normalise(int deg) => ((deg % 360) + 360) % 360;

        public static void Remember(string playerUid, int deg)
        {
            if (playerUid != null) ByPlayer[playerUid] = Normalise(deg);
        }

        /// <summary>
        /// Back to 0 once an item has been placed, so a turn applies to ONE placement rather
        /// than silently to everything afterwards — the angle persisting invisibly was the
        /// main confusion in testing.
        ///
        /// Safe only because stacking inherits the angle of the item it is stacking onto: a
        /// pile still comes out uniform without the key being pressed again. Before that
        /// existed, resetting would have meant re-pressing for every item of a stack.
        ///
        /// Both sides run the placement, so each resets its own copy and they stay in step
        /// without another packet.
        /// </summary>
        public static void ResetAfterPlacing(ICoreAPI api, IPlayer player)
        {
            if (api?.Side == EnumAppSide.Client)
            {
                Local = 0;
                return;
            }

            if (player == null) return;
            ByPlayer.Remove(player.PlayerUID);

            // TELL THE CLIENT. Relying on both sides to reset themselves does not work: the
            // client's PlaceItem can bail out before this point (its TryPutInto returns 0 on a
            // predicted placement), so the server cleared its copy while the client kept the
            // old angle — the preview and the info panel then said 90 while the server would
            // have placed at 0. Sending it makes the client's value follow the authority,
            // which is the same reasoning as sending the angle in the first place.
            if (player is IServerPlayer sp)
            {
                WindowDisplayLibMod.ServerChannel?.SendPacket(new PlacementRotationPacket { Deg = 0 }, sp);
            }
        }

        /// <summary>
        /// The angle to use for a placement. A player who has never sent one gets 0, which is
        /// the previous behaviour — nothing changes until someone actually uses the key.
        /// </summary>
        public static int For(ICoreAPI api, IPlayer player)
        {
            if (api?.Side == EnumAppSide.Client) return Normalise(Local);
            return player != null && ByPlayer.TryGetValue(player.PlayerUID, out int d) ? d : 0;
        }

        /// <summary>
        /// Drop every remembered angle. Called from <c>StartServerSide</c>, because a static
        /// outlives a world: a player who turned an item and then left WITHOUT placing it
        /// leaves an entry behind, and the same UID rejoining a different world would have
        /// its first placement silently arrive turned.
        ///
        /// Same trap as <c>PlacementRotation.Local</c> and <c>ForceFootprintBox</c> on the
        /// client, which are cleared in <c>StartClientSide</c> for exactly this reason.
        /// Server start is the honest point to do it: no player can have a pending angle yet.
        /// </summary>
        public static void ForgetAll() => ByPlayer.Clear();
    }

    public class WindowDisplayLibMod : ModSystem
    {
        public static IServerNetworkChannel ServerChannel;
        public static IClientNetworkChannel ClientChannel;

        private PlacementGhostRenderer ghostRenderer;
        private ICoreClientAPI capi;

        /// <summary>
        /// Sprint + wheel. "Sprint" and "ctrl" are the same physical key for most players —
        /// ClientSettings clones keyMapping["ctrl"] from keyMapping["sprint"] unless
        /// separateCtrlKeyForMouse is on — so this is the same modifier as ctrl+wrench and
        /// there is nothing new to learn. Reading the KEY rather than the ACTION is the
        /// settled rule: ORing the two made the swap modifier depend on the player's own
        /// keybinds and look intermittent.
        /// </summary>
        private void OnMouseWheel(MouseWheelEventArgs args)
        {
            if (args.delta == 0 || capi == null) return;
            if (capi.World.Player?.Entity?.Controls?.CtrlKey != true) return;

            // Same modifier, two jobs, chosen by what is under the crosshair: a pad turns the
            // item you are ABOUT to place, an item already there turns THAT. Reads as one
            // gesture, and neither can be reached while pointing at the other.
            if (!TryWheelRotate(capi, args.delta > 0)) return;

            // MUST be handled, or the wheel rotates the item AND changes hotbar slot on every
            // notch. Only reached with the modifier down and the aim on one of our boxes, so
            // the wheel is never taken globally.
            args.SetHandled(true);
        }

        /// <summary>
        /// Changing what you are holding drops the pending angle.
        ///
        /// Reported 2026-07-31: turn a shirt to 90°, wheel across to a wrench, and the wrench
        /// is at 90° too — the angle belonged to the *player* rather than to the thing being
        /// placed. It is the same complaint that produced the reset-after-placing rule, which
        /// exists so a turn applies to ONE placement instead of silently to everything
        /// afterwards; picking up something else ends that placement just as surely as making
        /// it does. The angle was simply outliving the only event that was thought to end it.
        ///
        /// **The send is not optional and the guard on it is not either.** The server places
        /// from the last angle it was told, so clearing only the client's copy would leave the
        /// two disagreeing — the exact fault that made `ResetAfterPlacing` push a packet back.
        /// But players scroll the hotbar constantly, so this sends ONLY when there is
        /// something to clear.
        /// </summary>
        private void OnActiveSlotChanged(ActiveSlotChangeEventArgs args)
        {
            if (PlacementRotation.Local == 0) return;

            PlacementRotation.Local = 0;
            ClientChannel?.SendPacket(new PlacementRotationPacket { Deg = 0 });
        }

        public override void Start(ICoreAPI api)
        {
            base.Start(api);

            WindowDisplayLibConfig.Load(api);

            api.Logger.Notification("[WindowDisplayLib] Mod loaded successfully");
            api.Logger.Debug("[WindowDisplayLib] Config values:");
            api.Logger.Debug("  Animation Speed: {0}", WindowDisplayLibConfig.Current.AnimationSpeed);

            // The pre-psurface classes are deliberately not registered here. They still
            // exist under src/legacy/ but are excluded from the build: Window Storage Lib
            // registers those same class names, and two mods claiming one name collide
            // if a player has both installed.
            api.RegisterBlockClass("BlockWindowDisplay", typeof(BlockWindowDisplay));
            api.RegisterBlockEntityClass("BEWindowDisplay", typeof(BEWindowDisplay));
            api.RegisterBlockBehaviorClass("WindowSurfaces", typeof(BlockBehaviorWindowSurfaces));
        }

        public const string ChannelName = "windowdisplaylib-linkedopen";

        public override void StartServerSide(ICoreServerAPI api)
        {
            // Server-side statics survive a world change just as the client ones do — see
            // ForgetAll for what carries over if this is skipped.
            PlacementRotation.ForgetAll();

            ServerChannel = api.Network.RegisterChannel(ChannelName)
                .RegisterMessageType<LinkedOpenPacket>()
                .RegisterMessageType<PlacementRotationPacket>()
                .RegisterMessageType<RotateStoredItemPacket>()
                .SetMessageHandler<PlacementRotationPacket>((player, packet) =>
                    PlacementRotation.Remember(player?.PlayerUID, packet?.Deg ?? 0))
                .SetMessageHandler<RotateStoredItemPacket>((player, packet) =>
                {
                    if (packet?.SlotId == null) return;

                    var pos = new BlockPos(packet.X, packet.Y, packet.Z);

                    // Range check, because this is a client-supplied position: without it a
                    // malformed or hostile packet could turn an item in a window anywhere in
                    // the world. 12 blocks is comfortably beyond normal reach.
                    if (player?.Entity == null || player.Entity.Pos.AsBlockPos.DistanceTo(pos) > 12)
                    {
                        return;
                    }

                    if (api.World.BlockAccessor.GetBlockEntity(pos) is BEWindowDisplay be)
                    {
                        be.RotateStoredItem(packet.SlotId, player, packet.Reverse);
                    }
                });
        }

        public override void StartClientSide(ICoreClientAPI api)
        {
            // Client-only: guards an IndexOutOfRange crash inside vanilla's own
            // BlockBehaviorDisplay.placingItemsPreview, which our three-surface blocks
            // make reachable. See VanillaDisplayBoundsPatch for the mechanism.
            VanillaDisplayBoundsPatch.Apply(api);

            // THE R KEY IS GONE, removed 2026-07-31 — sprint + mouse wheel replaces it.
            //
            // It was the original way to turn an item before placing it and the wheel was
            // added beside it, so for a while there were two. Free rotation is what settled
            // it: at the default 15° step a key needs 24 presses to get round, where the
            // wheel is one gesture and turns both ways. Keeping a worse duplicate costs a
            // hotkey slot in a list players scroll through and a second path to keep in step.
            //
            // Nothing else referred to it — the interaction help never advertised it, and
            // the lang entry it used (`placement-rotation`) is still read by the info-panel
            // fallback in BEWindowDisplay.GetBlockInfo, so it stays.

            // Show the footprint outline on demand, whatever PlacementBox is set to. For
            // lining something up precisely without going to the config and back — the
            // setting is the preference, this is "show me while I do this", so it is not
            // persisted and returns to the configured behaviour next session.
            api.Input.RegisterHotKey("windowdisplaybox", "Window Display: show placement outline",
                GlKeys.B, HotkeyType.CharacterControls);
            api.Input.SetHotKeyHandler("windowdisplaybox", _ =>
            {
                BlockBehaviorWindowSurfaces.ForceFootprintBox =
                    !BlockBehaviorWindowSurfaces.ForceFootprintBox;

                api.TriggerIngameError(this, "windowdisplaybox",
                    Lang.Get(BlockBehaviorWindowSurfaces.ForceFootprintBox
                        ? "windowdisplaylib:placement-box-on"
                        : "windowdisplaylib:placement-box-off"));
                return true;
            });

            // Sprint + mouse wheel — the ONLY way to turn an item, held or placed, since the
            // R key went. Free rotation is why: 24 presses to get round at the default 15°,
            // against one gesture that also goes both ways.
            //
            // A NAMED handler, not a lambda, so Dispose can unsubscribe it. An anonymous
            // subscription with no matching -= accumulates if this mod system is ever set up
            // twice in one process, and the symptom would be the wheel turning an item two
            // or three steps per notch after rejoining a world.
            capi = api;
            api.Event.MouseWheelMove += OnMouseWheel;

            // Both named handlers with a matching -= in Dispose, for the reason above.
            api.Event.AfterActiveSlotChanged += OnActiveSlotChanged;

            // Client-side statics survive leaving a world, so they are cleared here rather
            // than left holding the last world's state: a pending placement angle would
            // otherwise carry over into a new world, and the outline override would still be
            // on with nothing having toggled it.
            PlacementRotation.Local = 0;
            BlockBehaviorWindowSurfaces.ForceFootprintBox = false;
            BEWindowDisplay.LiveClientInstances.Clear();
            WindowSoundHandler.ResetShared();

            RegisterTransformEditor(api);

            // A see-through copy of the held item inside the outline box. The ONLY per-frame
            // renderer in the mod, so it is behind a config switch and unregisters cleanly.
            if (WindowDisplayLibConfig.Current?.PlacementGhost == true)
            {
                ghostRenderer = new PlacementGhostRenderer(api);
                api.Event.RegisterRenderer(ghostRenderer, EnumRenderStage.Opaque, "windowdisplayghost");
            }

            ClientChannel = api.Network.RegisterChannel(ChannelName)
                .RegisterMessageType<LinkedOpenPacket>()
                .RegisterMessageType<PlacementRotationPacket>()
                // Registered here as well as on the server: a message type has to be known
                // to BOTH channels. Missing on this side, SendPacket goes nowhere and fails
                // silently — which is exactly what "the wheel does nothing on placed items"
                // looked like.
                .RegisterMessageType<RotateStoredItemPacket>()
                // The server owns the value after a placement and pushes it back
                .SetMessageHandler<PlacementRotationPacket>(packet =>
                    PlacementRotation.Local = PlacementRotation.Normalise(packet?.Deg ?? 0))
                .SetMessageHandler<LinkedOpenPacket>(packet =>
                {
                    if (packet?.Positions == null) return;

                    for (int i = 0; i + 2 < packet.Positions.Length; i += 3)
                    {
                        var pos = new BlockPos(packet.Positions[i], packet.Positions[i + 1], packet.Positions[i + 2]);
                        if (api.World.BlockAccessor.GetBlockEntity(pos) is BEWindowDisplay be)
                        {
                            be.ApplyLinkedPane(packet.PaneIndex, packet.IsOpen);
                        }
                    }
                });
        }

        /// <summary>
        /// Wires up the in-game transform editor: its tab, and the two event bus listeners
        /// that bridge the editor's flat attribute name to the real value at
        /// <c>displayable.&lt;category&gt;.transform</c>.
        ///
        /// **ALL OF IT REGISTERED EXACTLY ONCE, HERE.** `IEventAPI` has no way to remove an
        /// event bus listener — `UnregisterCallback` and `UnregisterGameTickListener` exist,
        /// nothing for the bus — so anything registered per block entity accumulates for the
        /// whole session and holds every block entity it captured. That is what this used to
        /// do: `BEWindowDisplay.Initialize` registered one per window, on every chunk load,
        /// for ever. Found by audit 2026-07-30 after RAM was seen climbing across chunk
        /// reloads.
        ///
        /// The set-handler walks <see cref="BEWindowDisplay.LiveClientInstances"/>, which is
        /// maintained by Initialize and Cleanup and therefore reflects what is loaded now.
        /// </summary>
        private static void RegisterTransformEditor(ICoreClientAPI api)
        {
            // The editor's tab strip is fixed width and every mod registering a transform
            // competes for it, so the title is short on purpose — long ones overflow and the
            // tab renders invisible until hovered.
            if (!GuiDialogTransformEditor.extraTransforms.Any(
                    x => x.AttributeName == BlockBehaviorWindowSurfaces.TransformTarget))
            {
                GuiDialogTransformEditor.extraTransforms.Add(new TransformConfig
                {
                    Title = "Window",
                    AttributeName = BlockBehaviorWindowSurfaces.TransformTarget
                });
            }

            api.Event.RegisterEventBusListener((string eventName, ref EnumHandling handling, IAttribute data) =>
            {
                if (data is not TreeAttribute tree) return;
                if (tree.GetString("target") != BlockBehaviorWindowSurfaces.TransformTarget) return;

                BlockBehaviorWindowSurfaces.HandleGetTransform(api, tree, ref handling);
            }, 0.5, "ongettransform");

            api.Event.RegisterEventBusListener((string eventName, ref EnumHandling handling, IAttribute data) =>
            {
                if (data is not TreeAttribute tree) return;
                if (tree.GetString("target") != BlockBehaviorWindowSurfaces.TransformTarget) return;

                // Written to the collectible ONCE — it is shared by every stack of that
                // item, so doing it per window was always redundant work.
                //
                // The write itself lives beside HandleGetTransform rather than here, so the
                // two cannot disagree about which entry they are editing. They did: the get
                // side resolved the category from the aimed window while this side hardcoded
                // the default, and every shipped marker declares `-windowdisplay` — so the
                // editor read `displayable.windowdisplay` and wrote `displayable.shelf`.
                BlockBehaviorWindowSurfaces.HandleSetTransform(api, tree);

                // ToArray so a window unloading mid-iteration cannot invalidate the set.
                foreach (BEWindowDisplay be in BEWindowDisplay.LiveClientInstances.ToArray())
                {
                    be.ApplyTransformEdit();
                }
            }, 0.5, "onsettransform");
        }

        /// <summary>
        /// Steps the pending angle and tells the server. <paramref name="direction"/> is
        /// +1 or -1. Sprint + wheel is the only caller since the R key was removed on
        /// 2026-07-31; it stays a separate method because the send must not be forgotten.
        ///
        /// The server places from what it was last told, so it is told now rather than at
        /// placement time — the placement interaction carries no room for extra data. That
        /// is the same reasoning that made this a packet rather than something each side
        /// works out for itself.
        /// </summary>
        private static void RotatePlacement(ICoreClientAPI api, int direction)
        {
            PlacementRotation.Local = PlacementRotation.Normalise(
                PlacementRotation.Local + direction * PlacementRotation.Step);

            ClientChannel?.SendPacket(new PlacementRotationPacket { Deg = PlacementRotation.Local });

            // Say it in words ONLY when the ghost is switched off, because then nothing else
            // shows the angle and the gesture is completely invisible. This lived on the R
            // key until that was removed; the reasoning for it belongs to whatever does the
            // turning, not to the key that used to, so it came here rather than going with it.
            if (WindowDisplayLibConfig.Current?.PlacementGhost != true)
            {
                api?.TriggerIngameError(api, "windowdisplayrotate",
                    Lang.Get("windowdisplaylib:placement-rotation", PlacementRotation.Local));
            }
        }

        /// <summary>
        /// Handles one wheel notch, and reports whether it was ours to handle — which is
        /// also the gate on consuming the event. Without that gate the mod would swallow
        /// sprint+wheel everywhere in the world, so this answers NO for anything that is
        /// not one of our surfaces.
        /// </summary>
        private static bool TryWheelRotate(ICoreClientAPI api, bool forward)
        {
            BlockSelection sel = api.World.Player?.CurrentBlockSelection;
            if (sel == null) return false;

            Block block = api.World.BlockAccessor.GetBlock(sel.Position);
            if (block?.GetBehavior<BlockBehaviorWindowSurfaces>() == null) return false;

            WindowSlotId loc = WindowSlotId.Decode(sel.SelectionBoxId);
            if (loc == null) return false;   // frame or pane box — leave the wheel alone

            if (loc.IsPlacedItem)
            {
                // Turning something already on the sill is server state, so it travels as a
                // packet. Sent without waiting for the wrench's usual interaction path,
                // which a wheel notch never enters.
                ClientChannel?.SendPacket(new RotateStoredItemPacket
                {
                    X = sel.Position.X,
                    Y = sel.Position.Y,
                    Z = sel.Position.Z,
                    SlotId = loc.Encoded,
                    Reverse = !forward,
                });
                return true;
            }

            // A pad: turn what is about to be placed, which needs something in hand.
            ItemSlot held = api.World.Player?.Entity?.RightHandItemSlot;
            if (held == null || held.Empty) return false;

            RotatePlacement(api, forward ? 1 : -1);
            return true;
        }

        public override void Dispose()
        {
            // Unsubscribe what was subscribed. Hotkey handlers replace by name, but an
            // event += and a RegisterRenderer both have to be undone by hand.
            if (capi != null)
            {
                capi.Event.MouseWheelMove -= OnMouseWheel;
                capi.Event.AfterActiveSlotChanged -= OnActiveSlotChanged;

                // TAKE THE RENDERER OUT OF THE LOOP BEFORE DISPOSING IT, and note this runs
                // while capi is still live, which is why it sits inside this block.
                //
                // There was no UnregisterRenderer call here at all — only a Dispose() under
                // a comment claiming the renderer "unregisters cleanly". It does not;
                // `IClientEventAPI.UnregisterRenderer(IRenderer, EnumRenderStage)` is the
                // matching call. RegisterRenderer with nothing undoing it is the same shape
                // as the MouseWheelMove lambda that was fixed on 2026-07-30.
                //
                // The ORDER is the part that could actually misbehave: Dispose() releases the
                // uploaded MeshRefs while the renderer is still registered, so a frame drawn
                // between the two finds an empty cache and uploads fresh GPU meshes during
                // teardown that nothing will ever free.
                if (ghostRenderer != null)
                {
                    capi.Event.UnregisterRenderer(ghostRenderer, EnumRenderStage.Opaque);
                }

                capi = null;
            }

            // Explicit: the renderer owns uploaded GPU meshes, and a leaked MeshRef is a
            // crash source rather than merely untidy.
            ghostRenderer?.Dispose();
            ghostRenderer = null;

            VanillaDisplayBoundsPatch.Remove();
            base.Dispose();
        }
    }
}