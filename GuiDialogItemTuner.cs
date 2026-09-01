using System;
using System.Globalization;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace UniversalDisplayLib
{
    /// <summary>
    /// One window for tuning a displayed item: its BOX (the declared size) and its TRANSFORM,
    /// on whatever is under the crosshair.
    ///
    /// **WHY THIS EXISTS RATHER THAN VANILLA'S TRANSFORM EDITOR.** That dialog is fine at what
    /// it does but three things make an authoring pass slow, and all three are outside its
    /// control rather than bugs in it:
    ///
    ///   * it cannot touch <c>size</c> at all, which is what every selection box is built from
    ///   * it will not open unless something is in your hotbar, and it renders THAT held item
    ///     as its preview rather than the one on the block
    ///   * Close and Apply ends the session, so the next item means retyping the command
    ///
    /// Ours needs nothing in hand, works on the item you are pointing at, keeps both halves in
    /// one place, and stays open while you move down a row.
    ///
    /// **IT IS NOT A REIMPLEMENTATION OF VANILLA'S DIALOG.** The value is written by
    /// `BlockBehaviorDisplaySurfaces.WriteTransform`, the same call vanilla's editor reaches
    /// through our event-bus handler, so both routes store the identical thing. Nothing here
    /// knows how to persist a transform; it only knows how to ask.
    ///
    /// **No preview widget on purpose.** Vanilla needs one because it renders a held item;
    /// the thing being tuned here is on a sill three feet away and updates live, which is a
    /// better preview than any inset render.
    /// </summary>
    public class GuiDialogItemTuner : GuiDialog
    {
        public override string ToggleKeyCombinationCode => null;

        /// <summary>
        /// True releases the cursor for the controls, and — per the API's own documentation —
        /// **holding Alt grabs the mouse back for normal look control**. That is the whole
        /// retargeting gesture: hold Alt, look at the next item, let go. No key to invent.
        ///
        /// It also makes <see cref="pinned"/> safe by construction. With the cursor released
        /// the camera cannot move at all, so the aim only changes while Alt is deliberately
        /// held, and an edit in progress can never be stolen by a drifting crosshair.
        /// </summary>
        public override bool PrefersUngrabbedMouse => true;

        private const string DialogKey = "windowdisplaytuner";

        private CollectibleObject target;

        /// <summary>
        /// Where the target was picked up. Remembered rather than re-read, because a pinned
        /// dialog keeps editing one item while the crosshair has moved on — refreshing "the
        /// block I am looking at now" would redraw the wrong one.
        /// </summary>
        private BlockPos targetPos;

        /// <summary>
        /// The display category of the surface the target was picked up from — `windowdisplay`,
        /// `shelf`, someone else's `box`. Captured with the target and NOT re-read, for the same
        /// reason as the position: a pinned dialog goes on editing one item while the crosshair
        /// has moved on, and the category has to stay with the item being edited.
        /// </summary>
        private string targetCategory = BlockBehaviorDisplaySurfaces.DefaultDisplayCategory;

        /// <summary>
        /// The target's TYPE, for items whose display data is keyed by type rather than by
        /// block code — clutter. Null for everything else, and every path below behaves
        /// exactly as it did when it is null, so the ordinary variant items are untouched.
        ///
        /// Captured with the target for the same reason the category is: a pinned dialog goes
        /// on editing one item while the crosshair has moved on.
        /// </summary>
        private string targetType;
        private ModelTransform tf = ModelTransform.NoTransform;
        private Size3f size = new Size3f(6f, 4f, 6f);

        /// <summary>
        /// Set the moment a control is touched, and cleared by aiming at a DIFFERENT item.
        ///
        /// The rule the author chose: follow the crosshair while merely looking, lock on as
        /// soon as an edit starts. Without it, glancing away mid-edit repopulates every field
        /// from another item and the half-typed number lands somewhere unintended.
        /// </summary>
        private bool pinned;

        private long retargetListener;

        public GuiDialogItemTuner(ICoreClientAPI capi) : base(capi) { }

        public override void OnGuiOpened()
        {
            base.OnGuiOpened();

            pinned = false;
            AdoptAimedTarget(force: true);

            // Polls rather than hooking a selection event, because there is no "aim changed"
            // event to hook. 100 ms is far below noticing and the work is a dictionary lookup.
            retargetListener = capi.Event.RegisterGameTickListener(_ => AdoptAimedTarget(force: false), 100);
        }

        public override void OnGuiClosed()
        {
            base.OnGuiClosed();

            if (retargetListener != 0)
            {
                capi.Event.UnregisterGameTickListener(retargetListener);
                retargetListener = 0;
            }

            // Registered in OnGuiOpened, so it has to go here rather than in Dispose: the
            // dialog can be opened and closed many times in one session and each open adds
            // another listener otherwise. Same shape as the event-bus leak the audit found.
        }

        /// <summary>
        /// Switches to whatever the crosshair is on, unless an edit has pinned us.
        ///
        /// Aiming at NOTHING deliberately leaves the current target alone. Otherwise looking
        /// away for a moment would blank the whole window, and with a released cursor that
        /// happens every time the player turns.
        /// </summary>
        private void AdoptAimedTarget(bool force)
        {
            if (!force && pinned) return;

            ItemSlot aimedSlot = BlockBehaviorDisplaySurfaces.ResolveEditorTarget(capi);
            CollectibleObject aimed = aimedSlot?.Itemstack?.Collectible;

            // THE TYPE IS PART OF THE IDENTITY, not just of the output. Every piece of clutter
            // is the same CollectibleObject, so comparing collectibles alone meant pointing at
            // a different pot never re-adopted - the dialog sat on the first one and quietly
            // edited that instead. Null for ordinary items, where this is the old comparison.
            string aimedType = BlockBehaviorDisplaySurfaces.TypedKeyOf(aimedSlot);
            if (aimed == null || (aimed == target && aimedType == targetType)) return;

            target = aimed;
            targetType = aimedType;
            targetPos = capi.World.Player?.CurrentBlockSelection?.Position?.Copy();
            targetCategory = BlockBehaviorDisplaySurfaces.ResolveEditorCategory(capi);
            pinned = false;
            ReadCurrentValues();
            Compose();
        }

        /// <summary>Reads the values actually in force for the target, so the fields start truthful.</summary>
        private void ReadCurrentValues()
        {
            ItemSlot slot = BlockBehaviorDisplaySurfaces.ResolveEditorTarget(capi);
            string category = BlockBehaviorDisplaySurfaces.ResolveEditorCategory(capi);

            DisplayableAttributes dattr = slot == null
                ? null
                : BlockBehaviorDisplaySurfaces.GetDisplayableAttributes(slot, category);

            size = dattr?.Size ?? new Size3f(6f, 4f, 6f);
            tf = dattr?.Transform?.Clone() ?? ModelTransform.NoTransform;

        }

        // Layout constants. An explicit Y CURSOR, not chained BelowCopy/RightCopy offsets —
        // the first version chained them and the rotation sliders landed on top of the
        // translation fields, because each chain advanced from a different base and the two
        // drifted apart. A running y is dull and impossible to get subtly wrong.
        private const int ColW = 145;     // size columns
        private const int ColGap = 10;
        private const int PairW = 205;    // translation / origin columns
        private const int PairX = 245;
        private const int FullW = 450;
        private const int RowH = 30;      // number input height
        private const int LabelH = 20;

        private void Compose()
        {
            ClearComposers();

            ElementBounds bg = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
            bg.BothSizing = ElementSizing.FitToChildren;

            ElementBounds dialog = ElementStdBounds.AutosizedMainDialog
                .WithAlignment(EnumDialogArea.RightMiddle)
                .WithFixedAlignmentOffset(-GuiStyle.DialogToScreenPadding, 0);

            // TITLE IS FIXED, AND THE ITEM CODE GOES IN THE BODY.
            //
            // The title bar is sized by the dialog, not by its text, so a long code was simply
            // cut off — and shortening the prefix would only move the cliff, since a modded
            // code like `aculinaryartillery:crock-burned` is longer than anything the bar can
            // hold whatever precedes it. Below the title it gets the full width of the window
            // and can say the whole thing.
            //
            // The name is the lib's, not a generic one: a tool living inside one mod's library
            // should say whose it is, which is why the command is `.udedit` too.
            var c = capi.Gui.CreateCompo(DialogKey, dialog)
                .AddShadedDialogBG(bg)
                .AddDialogTitleBar("Universal Display Editor", OnTitleBarClose)
                .BeginChildElements(bg);

            int y = 32;

            c.AddStaticText(target == null
                    ? "Nothing targeted - point at an item"
                    : (targetType == null ? target.Code.ToString()
                                          : target.Code + "  [" + targetType + "]"),
                CairoFont.WhiteSmallText(), Row(0, y, FullW, LabelH));
            y += 30;

            // ── Box size ──
            c.AddStaticText("Box size (voxels)", CairoFont.WhiteSmallText(), Row(0, y, FullW, LabelH));
            y += 26;

            c.AddStaticText("Width", CairoFont.WhiteDetailText(), Row(0, y, ColW, LabelH));
            c.AddStaticText("Height", CairoFont.WhiteDetailText(), Row(ColW + ColGap, y, ColW, LabelH));
            c.AddStaticText("Length", CairoFont.WhiteDetailText(), Row(2 * (ColW + ColGap), y, ColW, LabelH));
            y += 22;

            c.AddNumberInput(Row(0, y, ColW, RowH), OnSizeW, CairoFont.WhiteDetailText(), "sizew");
            c.AddNumberInput(Row(ColW + ColGap, y, ColW, RowH), OnSizeH, CairoFont.WhiteDetailText(), "sizeh");
            c.AddNumberInput(Row(2 * (ColW + ColGap), y, ColW, RowH), OnSizeL, CairoFont.WhiteDetailText(), "sizel");
            y += RowH + 24;

            // ── Transform ──
            c.AddStaticText("Transform", CairoFont.WhiteSmallText(), Row(0, y, FullW, LabelH));
            y += 26;

            c.AddStaticText("Translation", CairoFont.WhiteDetailText(), Row(0, y, PairW, LabelH));
            c.AddStaticText("Origin", CairoFont.WhiteDetailText(), Row(PairX, y, PairW, LabelH));
            y += 24;

            string[] axes = { "x", "y", "z" };
            Action<string>[] transHandlers = { OnTransX, OnTransY, OnTransZ };
            Action<string>[] originHandlers = { OnOriginX, OnOriginY, OnOriginZ };

            for (int i = 0; i < 3; i++)
            {
                c.AddNumberInput(Row(0, y, PairW, RowH), transHandlers[i], CairoFont.WhiteDetailText(), "trans" + axes[i]);
                c.AddNumberInput(Row(PairX, y, PairW, RowH), originHandlers[i], CairoFont.WhiteDetailText(), "origin" + axes[i]);
                y += RowH + 6;
            }
            y += 18;

            // ── Rotation and scale ──
            // Sliders are integer-only, so rotation is whole degrees and scale is x100 —
            // exactly what vanilla's own editor does (25-600 for 0.25x to 6x).
            ActionConsumable<int>[] rotHandlers = { OnRotX, OnRotY, OnRotZ };
            string[] rotNames = { "Rotation X", "Rotation Y", "Rotation Z" };

            for (int i = 0; i < 3; i++)
            {
                c.AddStaticText(rotNames[i], CairoFont.WhiteDetailText(), Row(0, y, FullW, LabelH));
                y += 22;
                c.AddSlider(rotHandlers[i], Row(0, y, FullW, 22), "rot" + axes[i]);
                y += 30;
            }

            y += 8;
            c.AddStaticText("Scale", CairoFont.WhiteDetailText(), Row(0, y, FullW, LabelH));
            y += 22;
            c.AddSlider(OnScale, Row(0, y, FullW, 22), "scale");
            y += 38;

            c.AddSwitch(OnFlipX, Row(0, y, 24, 24), "flipx", 24);
            c.AddStaticText("Flip on X", CairoFont.WhiteDetailText(), Row(34, y + 3, 200, LabelH));
            y += 40;

            c.AddSmallButton("Follow aim", OnFollowAim, Row(0, y, 200, 26));
            c.AddSmallButton("Write file", OnWriteFiles, Row(PairX, y, 200, 26));

            SingleComposer = c.EndChildElements().Compose();

            SetIntervals();
            PopulateFields();
        }

        /// <summary>
        /// Per-field arrow-click step, and fractional mode forced on.
        ///
        /// **Vanilla's shift/ctrl fine-step is unreliable in practice.** `GuiElementNumberInput`
        /// does divide `Interval` by 10 with shift and 100 with ctrl
        /// (`OnMouseDownOnElement`), but the author reports it has never worked, and the very
        /// next thing `UpdateValue` does is round the step to a whole number when `IntMode` is
        /// on — which would turn 0.1 back into 1 and matches the symptom exactly. `IntMode` is
        /// therefore set false explicitly here rather than assumed: cheap, and it removes the
        /// only mechanism in that class that can silently swallow a fractional step.
        ///
        /// The intervals matter more than the modifier anyway. A translation is tuned in
        /// hundredths and a box size in voxels, so one shared step could only suit one of them,
        /// and no modifier is needed to get the common case right.
        /// </summary>
        private void SetIntervals()
        {
            SetInterval("sizew", 1f); SetInterval("sizeh", 1f); SetInterval("sizel", 1f);
            SetInterval("transx", 0.05f); SetInterval("transy", 0.05f); SetInterval("transz", 0.05f);
            SetInterval("originx", 0.05f); SetInterval("originy", 0.05f); SetInterval("originz", 0.05f);
        }

        private void SetInterval(string key, float interval)
        {
            GuiElementNumberInput el = SingleComposer?.GetNumberInput(key);
            if (el == null) return;

            el.IntMode = false;
            el.Interval = interval;
        }

        private static ElementBounds Row(int x, int y, int w, int h) => ElementBounds.Fixed(x, y, w, h);

        private void PopulateFields()
        {
            if (SingleComposer == null) return;

            SingleComposer.GetTextInput("sizew").SetValue(TuningFiles.Fmt(size.Width));
            SingleComposer.GetTextInput("sizeh").SetValue(TuningFiles.Fmt(size.Height));
            SingleComposer.GetTextInput("sizel").SetValue(TuningFiles.Fmt(size.Length));

            SingleComposer.GetTextInput("transx").SetValue(TuningFiles.Fmt(tf.Translation.X));
            SingleComposer.GetTextInput("transy").SetValue(TuningFiles.Fmt(tf.Translation.Y));
            SingleComposer.GetTextInput("transz").SetValue(TuningFiles.Fmt(tf.Translation.Z));

            SingleComposer.GetTextInput("originx").SetValue(TuningFiles.Fmt(tf.Origin.X));
            SingleComposer.GetTextInput("originy").SetValue(TuningFiles.Fmt(tf.Origin.Y));
            SingleComposer.GetTextInput("originz").SetValue(TuningFiles.Fmt(tf.Origin.Z));

            SingleComposer.GetSlider("rotx").SetValues((int)tf.Rotation.X, -180, 180, 1);
            SingleComposer.GetSlider("roty").SetValues((int)tf.Rotation.Y, -180, 180, 1);
            SingleComposer.GetSlider("rotz").SetValues((int)tf.Rotation.Z, -180, 180, 1);
            SingleComposer.GetSlider("scale").SetValues((int)Math.Abs(100f * tf.ScaleXYZ.X), 10, 600, 1);

            SingleComposer.GetSwitch("flipx").On = tf.ScaleXYZ.X < 0f;
        }

        // ── Control handlers ─────────────────────────────────────────────────
        //
        // Every one of them pins first. Touching a control IS the signal that this is the item
        // being worked on, which is the rule the author picked over "always follow the aim".

        private float Parse(string val, float fallback)
            => float.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out float f) ? f : fallback;

        private void OnSizeW(string v) { pinned = true; size = new Size3f(Parse(v, size.Width), size.Height, size.Length); ApplySize(); }
        private void OnSizeH(string v) { pinned = true; size = new Size3f(size.Width, Parse(v, size.Height), size.Length); ApplySize(); }
        private void OnSizeL(string v) { pinned = true; size = new Size3f(size.Width, size.Height, Parse(v, size.Length)); ApplySize(); }

        private void OnTransX(string v) { pinned = true; tf.Translation.X = Parse(v, tf.Translation.X); ApplyTransform(); }
        private void OnTransY(string v) { pinned = true; tf.Translation.Y = Parse(v, tf.Translation.Y); ApplyTransform(); }
        private void OnTransZ(string v) { pinned = true; tf.Translation.Z = Parse(v, tf.Translation.Z); ApplyTransform(); }

        private void OnOriginX(string v) { pinned = true; tf.Origin.X = Parse(v, tf.Origin.X); ApplyTransform(); }
        private void OnOriginY(string v) { pinned = true; tf.Origin.Y = Parse(v, tf.Origin.Y); ApplyTransform(); }
        private void OnOriginZ(string v) { pinned = true; tf.Origin.Z = Parse(v, tf.Origin.Z); ApplyTransform(); }

        private bool OnRotX(int v) { pinned = true; tf.Rotation.X = v; ApplyTransform(); return true; }
        private bool OnRotY(int v) { pinned = true; tf.Rotation.Y = v; ApplyTransform(); return true; }
        private bool OnRotZ(int v) { pinned = true; tf.Rotation.Z = v; ApplyTransform(); return true; }

        private bool OnScale(int v)
        {
            pinned = true;

            // Sign is owned by the flip switch, so a scale change must not silently undo it.
            float sign = tf.ScaleXYZ.X < 0f ? -1f : 1f;
            float s = v / 100f;
            tf.ScaleXYZ = new FastVec3f(s * sign, s, s);
            ApplyTransform();
            return true;
        }

        private void OnFlipX(bool on)
        {
            pinned = true;
            tf.ScaleXYZ = new FastVec3f(Math.Abs(tf.ScaleXYZ.X) * (on ? -1f : 1f), tf.ScaleXYZ.Y, tf.ScaleXYZ.Z);
            ApplyTransform();
        }

        /// <summary>Unpins, so the next thing aimed at is adopted.</summary>
        private bool OnFollowAim()
        {
            pinned = false;
            AdoptAimedTarget(force: false);
            return true;
        }

        private bool OnWriteFiles()
        {
            TuningFiles.Save(capi, announce: true);
            return true;
        }

        private void OnTitleBarClose() => TryClose();

        // ── Applying ─────────────────────────────────────────────────────────

        private void ApplySize()
        {
            if (target == null) return;

            BlockBehaviorDisplaySurfaces.SizeOverrides[(target, targetType)] = size;
            TuningFiles.RecordSize(capi, target, size, targetCategory, targetType);
            RefreshDisplayBlocks();
        }

        private void ApplyTransform()
        {
            if (target == null) return;

            // A typed item is written into its own behaviour map; only fall back to the shared
            // attributes path when that is not what this item is. See WriteTypedTransform for
            // why the shared path is actively wrong for clutter.
            if (!BlockBehaviorDisplaySurfaces.WriteTypedTransform(target, targetType, targetCategory, tf))
            {
                BlockBehaviorDisplaySurfaces.WriteTransform(capi, target, tf);
            }

            TuningFiles.RecordTransform(capi, target, tf, targetCategory, targetType);

            RefreshDisplayBlocks();
        }

        /// <summary>
        /// Drops cached meshes and boxes on every loaded window, which is what makes an edit
        /// visible on the sill immediately. ToArray so a window unloading mid-iteration cannot
        /// invalidate the set.
        /// </summary>
        private void RefreshDisplayBlocks()
        {
            foreach (BEUniversalDisplay be in BEUniversalDisplay.LiveClientInstances.ToArray())
            {
                be.ApplyTransformEdit();
            }
        }
    }
}
