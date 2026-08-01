using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace WindowDisplayLib
{
    /// <summary>
    /// Draws a see-through copy of the held item where it would land, inside the outline
    /// box that already shows whether it fits.
    ///
    /// The box answers "will this go here"; the ghost answers "what will it look like".
    /// Both are wanted, so this adds to the box rather than replacing it, and both take
    /// their colour from the SAME fit test — red when it will not place.
    ///
    /// THIS IS THE ONLY THING IN THE MOD THAT DRAWS PER FRAME. Everything else goes
    /// through the chunk mesher or vanilla's AnimatableRenderer. That makes it the only
    /// code here that can cost frames or take the client down, which is why it is behind
    /// a config switch, why it never throws out of OnRenderFrame, and why it owns its GPU
    /// meshes explicitly.
    ///
    /// Vanilla does NOT do this — <c>BlockBehaviorDisplay</c> draws the same wireframe
    /// cuboid we did and nothing more, and its <c>placingItemsPreview</c> is only a
    /// boolean gate despite the name. So there was no vanilla implementation to follow.
    /// </summary>
    public class PlacementGhostRenderer : IRenderer
    {
        private readonly ICoreClientAPI capi;

        /// <summary>
        /// GPU meshes, uploaded once and reused. Uploading per frame is not an option, and
        /// these are NOT the block entity's cached MeshData — that lives CPU-side for the
        /// chunk mesher. Every one of these must be disposed; a leaked MeshRef is a real
        /// crash source rather than merely untidy.
        /// </summary>
        private readonly Dictionary<string, MultiTextureMeshRef> uploaded =
            new Dictionary<string, MultiTextureMeshRef>();

        /// <summary>
        /// Render-loop exceptions are logged ONCE. The particle hooks taught this the hard
        /// way: something that fires continuously and logs every time buries the log and
        /// costs more than the feature is worth. A ghost is decoration and is never worth
        /// a crash or a flooded log.
        /// </summary>
        private bool loggedFailure;

        /// <summary>
        /// Discard threshold while the ghost is drawn, and the value the standard shader is
        /// put back to afterwards. <c>DefaultAlphaTest</c> is not a guess — it is the
        /// initialiser in the shipped shader source, <c>assets/game/shaders/standard.fsh</c>:
        /// <code>uniform float alphaTest = 0.001;</code>
        /// </summary>
        private const float GhostAlphaTest = 0.05f;
        private const float DefaultAlphaTest = 0.001f;

        public double RenderOrder => 0.5;
        public int RenderRange => 24;

        public PlacementGhostRenderer(ICoreClientAPI capi)
        {
            this.capi = capi;
        }

        public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
        {
            if (WindowDisplayLibConfig.Current?.PlacementGhost != true) return;

            try
            {
                Render();
            }
            catch (Exception e)
            {
                if (!loggedFailure)
                {
                    loggedFailure = true;
                    capi.Logger.Warning(
                        "[WindowDisplayLib] Placement ghost failed and is disabled for this session. " +
                        "Set PlacementGhost false in the config to silence this. {0}", e);
                }
            }
        }

        private void Render()
        {
            if (loggedFailure) return;

            IClientPlayer player = capi.World.Player;
            BlockSelection blockSel = player?.CurrentBlockSelection;
            if (blockSel == null) return;

            // The wrench used to be excluded here, on the grounds that holding one meant a
            // swap or a rotate rather than a placement. Both halves of that stopped being
            // true on 2026-07-30: rotation moved to the mouse wheel, and the wrench is now
            // an ordinary placeable item with its own patch. Swapping is a CTRL+wrench click
            // on a frame box, which never reaches the ghost anyway, so nothing is gained by
            // the exclusion and a wrench with no preview is simply inconsistent.
            ItemSlot held = player.Entity?.RightHandItemSlot;
            if (held == null || held.Empty) return;

            Block block = capi.World.BlockAccessor.GetBlock(blockSel.Position);
            var bh = block?.GetBehavior<BlockBehaviorWindowSurfaces>();
            if (bh == null) return;

            if (!bh.TryGetGhost(blockSel, held, out BEWindowDisplay be, out WindowSlotId loc,
                                out DisplayableAttributes dattr, out float rotDeg, out bool fits))
            {
                return;
            }

            if (!be.TryBuildGhost(blockSel.SelectionBoxId, held, dattr, rotDeg,
                                  out MeshData meshData, out float[] slotMatrix))
            {
                return;
            }

            MultiTextureMeshRef meshRef = GetOrUpload(held, dattr, meshData);

            // LAST RESORT: preview the container EMPTY.
            //
            // A filled liquid container's mesh cannot be uploaded to the GPU at all — not a
            // texture-id problem, which is what it looked like at first. Both routes die in
            // vanilla code on a mesh that renders perfectly well through the chunk mesher:
            //
            //   MeshData.AddMeshData(data, filter)      <- the multi-texture split
            //   ClientPlatformWindows.UploadMesh(data)  <- the raw upload, even single-id
            //
            // Nothing here can rebuild vertex data it does not have. But the ghost exists to
            // show WHERE and WHICH WAY ROUND the thing goes, and the container's own shape
            // answers both — the liquid inside it is detail the player gets back the instant
            // it is placed. So strip the contents and preview the vessel.
            //
            // Falls out neatly: an emptied stack keys to the same cache entry as a genuinely
            // empty one, which already uploads fine, so this usually costs no upload at all.
            if (meshRef == null)
            {
                ItemSlot bare = EmptiedCopy(held);
                if (bare != null
                    && be.TryBuildGhost(blockSel.SelectionBoxId, bare, dattr, rotDeg,
                                        out MeshData bareMesh, out float[] bareMatrix))
                {
                    meshRef = GetOrUpload(bare, dattr, bareMesh);
                    if (meshRef != null) slotMatrix = bareMatrix;
                }
            }

            if (meshRef == null || meshRef.Disposed) return;

            Draw(blockSel.Position, slotMatrix, meshRef, fits);
        }

        /// <summary>
        /// Keyed the way the block entity keys its own item meshes — collectible code plus
        /// any <c>displayable.shape</c> override — because that is exactly what decides the
        /// geometry. Keying on the code alone would show a garment worn rather than folded.
        /// </summary>
        private MultiTextureMeshRef GetOrUpload(ItemSlot held, DisplayableAttributes dattr, MeshData meshData)
        {
            // Contents are part of the geometry, so they have to be part of the key. A
            // container's own IContainedMeshSource.GetMeshCacheKey encodes them — it is what
            // the block entity keys its mesh cache on, and keying on the collectible code
            // alone made an empty bowl and a bowl of food share one uploaded mesh, so
            // whichever was aimed at first won for both.
            string contentKey =
                held.Itemstack.Collectible?.GetCollectibleInterface<IContainedMeshSource>()?.GetMeshCacheKey(held)
                ?? held.Itemstack.Collectible?.Code?.ToString()
                ?? "?";

            string key = contentKey + "|" + (dattr?.Shape?.ToString() ?? "");

            if (uploaded.TryGetValue(key, out MultiTextureMeshRef existing))
            {
                // A cached NULL means "this one cannot be uploaded" — see below.
                if (existing == null) return null;
                if (!existing.Disposed) return existing;
                uploaded.Remove(key);
            }

            // A FAILURE HERE IS ABOUT ONE ITEM, NOT ABOUT THE FEATURE.
            //
            // Not every MeshData the block entity produces is uploadable as a
            // multi-texture mesh — they are built for the chunk mesher, which has
            // different requirements — and UploadMultiTextureMesh throws a bare
            // NullReferenceException when the mesh lacks what it needs.
            //
            // The first version let that reach the outer handler, which logs once and
            // disables the ghost for the WHOLE SESSION. So a single unsupported item
            // killed the preview for every other item too, and it read in game as "the
            // ghost just stopped working" with no obvious trigger. Caching the failure
            // per item keeps the feature alive for everything else and stops it retrying
            // an upload that cannot succeed on every single frame.
            try
            {
                MultiTextureMeshRef fresh = capi.Render.UploadMultiTextureMesh(Uploadable(meshData));
                uploaded[key] = fresh;
                return fresh;
            }
            catch (Exception first)
            {
                // SECOND CHANCE: force the mesh down to a single texture id and try again.
                //
                // A FILLED liquid container reaches here even though its mesh passes the
                // checks in Uploadable — it has several texture ids AND a per-face map, so
                // it looks well formed, and the split still throws somewhere inside. Rather
                // than keep guessing at which field is missing, collapse it the same way an
                // empty container is collapsed (which does work) and see if it uploads.
                //
                // Only ever tried after a genuine failure, so a healthy mesh never takes
                // this path and never loses its multi-texture split.
                try
                {
                    MeshData flattened = meshData.Clone();
                    flattened.TextureIds = new[]
                    {
                        meshData.TextureIds != null && meshData.TextureIds.Length > 0
                            ? meshData.TextureIds[0]
                            : capi.BlockTextureAtlas.AtlasTextures[0].TextureId
                    };

                    MultiTextureMeshRef retry = capi.Render.UploadMultiTextureMesh(flattened);
                    uploaded[key] = retry;

                    // Debug, not Notification: this goes to client-debug.log rather than
                    // the main log, so it is there when a modded container misbehaves and
                    // invisible the rest of the time. Kept after the temporary perf probe
                    // was removed because it names the item and describes its mesh, which
                    // is the whole diagnosis for this class of fault.
                    capi.Logger.Debug(
                        "[WindowDisplayLib] ghost for {0} needed the single-texture fallback " +
                        "({1} ids, indices {2}, {3} vertices)",
                        key, meshData.TextureIds?.Length ?? 0,
                        meshData.TextureIndices == null ? "NULL" : "present",
                        meshData.VerticesCount);

                    return retry;
                }
                catch (Exception second)
                {
                    uploaded[key] = null;

                    // FULL exception, not just the message. Logging only e.Message cost a
                    // whole round trip here: it proved something threw without saying where,
                    // and the frame it names is the entire diagnosis.
                    // Worded for what actually happens next: a CONTAINER goes on to be
                    // previewed empty and the player still gets a ghost, so this must not
                    // read as "the feature is broken for this item" when it usually is not.
                    capi.Logger.Warning(
                        "[WindowDisplayLib] Placement ghost could not upload a mesh for {0}. " +
                        "If it holds something it will be previewed empty; otherwise it has no " +
                        "ghost. Everything else is unaffected." +
                        "\nfirst attempt: {1}\nfallback attempt: {2}", key, first, second);
                    return null;
                }
            }
        }

        /// <summary>
        /// The same item with whatever is inside it taken out, or null if there was nothing
        /// to take out.
        ///
        /// `contents` is the attribute vanilla containers keep their inventory in — liquid
        /// containers, crocks and meals all use it — so removing it gives the empty vessel
        /// without needing to know which kind of container this is.
        ///
        /// Returning null when there is no `contents` matters: without that check a failure
        /// unrelated to contents would retry an identical mesh, fail identically, and do it
        /// again on the next frame.
        /// </summary>
        private static ItemSlot EmptiedCopy(ItemSlot held)
        {
            ItemStack stack = held?.Itemstack?.Clone();
            if (stack?.Attributes == null) return null;
            if (!stack.Attributes.HasAttribute("contents")) return null;

            stack.Attributes.RemoveAttribute("contents");
            return new DummySlot(stack);
        }

        /// <summary>
        /// Gives a mesh the texture-id tracking that <c>UploadMultiTextureMesh</c> requires,
        /// if it arrived without any. Returns the mesh unchanged when it is already fine,
        /// which is the overwhelming majority.
        ///
        /// WHY THIS IS NEEDED. `UploadMultiTextureMesh` calls `MeshData.SplitByTextureId`,
        /// whose first line is
        ///
        ///     MeshData[] array = new MeshData[TextureIds.Length];
        ///
        /// so a mesh with a null `TextureIds` is a bare NullReferenceException with nothing
        /// saying which field was missing. **Liquid containers are the case that hits it** —
        /// a bucket with water in it. Contained meshes in general are fine: a bowl of food
        /// goes through the same `IContainedMeshSource` path and ghosts correctly, so this
        /// is about how the liquid content mesh is built, not about contained meshes.
        ///
        /// Chunk meshing never noticed because `mesher.AddMeshData` does not split by
        /// texture id — only uploading as a standalone GPU mesh does.
        ///
        /// THE CLONE IS LOAD-BEARING. That MeshData is the block entity's own cached item
        /// mesh, reused for chunk rendering; writing `TextureIds` onto it would reach back
        /// into geometry we do not own. Shared mutable state is exactly the kind of bug
        /// this codebase keeps paying for.
        ///
        /// The single id assumes the mesh sits on the FIRST block-atlas page. True unless a
        /// modpack has enough textures to spill the atlas across several pages, in which
        /// case a liquid ghost could show the wrong page's texture — visibly odd, but not a
        /// crash, and only on the ghost.
        /// </summary>
        private MeshData Uploadable(MeshData meshData)
        {
            int idCount = meshData.TextureIds?.Length ?? 0;

            // Already fine: one id short-circuits, and several ids WITH the per-face map
            // split correctly.
            if (idCount == 1) return meshData;
            if (idCount > 1 && meshData.TextureIndices != null) return meshData;

            capi.Logger.Debug(
                "[WindowDisplayLib] ghost mesh given a texture id: had {0} ids, indices {1}, {2} vertices",
                idCount, meshData.TextureIndices == null ? "NULL" : "present", meshData.VerticesCount);

            MeshData copy = meshData.Clone();

            // Collapse to ONE texture id. Two cases land here and both are fixed by it:
            //   * no ids at all       -> nothing to split by
            //   * ids but no indices  -> several ids with no per-face mapping, which is
            //                            what a liquid container produces. That is the
            //                            `else` branch of SplitByTextureId, and it
            //                            dereferences TextureIndices inside its lambda.
            //
            // One id makes SplitByTextureId short-circuit to `this` and never read
            // TextureIndices at all, so the mesh uploads whole and renders bound to that
            // single atlas page.
            //
            // Keeping the mesh's OWN first id where it has one, rather than assuming page
            // 0, so a container already pointing at the right page keeps pointing at it.
            copy.TextureIds = new[]
            {
                idCount > 0 ? meshData.TextureIds[0] : capi.BlockTextureAtlas.AtlasTextures[0].TextureId
            };

            return copy;
        }

        /// <summary>
        /// The slot matrix from the block entity is block-local (0..1 inside the block), so
        /// it has to be lifted into world space relative to the camera before rendering —
        /// the same job the chunk mesher does for us everywhere else.
        /// </summary>
        private void Draw(BlockPos pos, float[] slotMatrix, MultiTextureMeshRef meshRef, bool fits)
        {
            IRenderAPI rpi = capi.Render;
            Vec3d camPos = capi.World.Player.Entity.CameraPos;

            IStandardShaderProgram prog = null;

            // TRY/FINALLY IS NOT OPTIONAL HERE, and this crashed the client once for the
            // want of it. PreparedStandardShader BINDS the standard shader and Stop()
            // releases it, so anything throwing in between leaves it bound for good — the
            // outer catch then swallows the exception and the NEXT system to call Use()
            // dies instead of us:
            //
            //   InvalidOperationException: Already a different shader (standard) in use!
            //     at ShaderProgramBase.Use()
            //     at SystemRenderParticles.OnRenderFrame3D
            //
            // Nothing in that report names this mod. Global GL state has to be handed back
            // on every path out, including the failing ones.
            try
            {
                rpi.GlToggleBlend(true);

                prog = rpi.PreparedStandardShader(pos.X, pos.Y, pos.Z);

                // Red when it will not place, matching the outline box exactly. Both read
                // the same fit test, so they can never disagree about it.
                float alpha = WindowDisplayLibConfig.Current?.PlacementGhostAlpha ?? 0.5f;
                prog.RgbaTint = fits
                    ? new Vec4f(1f, 1f, 1f, alpha)
                    : new Vec4f(1f, 0.35f, 0.35f, alpha);

                // No shading variation on a preview: it is meant to read as a hologram of
                // the item rather than as the item itself sitting there.
                //
                // Which of these have to be PUT BACK afterwards is not a matter of taste —
                // it is whatever `PreparedStandardShader` does not reset for the next caller.
                // Decompiled (RenderAPIGame, 1.22.5) it resets RgbaTint, RgbaAmbientIn,
                // RgbaLightIn, RgbaFogIn, NormalShaded, ExtraGlow, FogMinIn, FogDensityIn,
                // DontWarpVertices, AddRenderFlags, ExtraZOffset, OverlayOpacity,
                // DamageEffect, ExtraGodray and ProjectionMatrix — so everything above this
                // line is safe to leave dirty and is cleaned up for the next user.
                //
                // It does NOT reset alphaTest or ssaoAttn, and the standard shader is a
                // SINGLE SHARED PROGRAM whose uniforms persist for the rest of the session.
                prog.NormalShaded = 0;
                prog.ExtraGodray = 0f;

                // ssaoAttn is already 0 at rest — `standard.fsh` declares
                // `uniform float ssaoAttn = 0;` and vanilla's own SystemRenderInsideBlock
                // sets 1 and then puts 0 back — so this writes the resting value and leaks
                // nothing. Kept explicit rather than removed, because relying on another
                // system to have tidied up is how the shader-left-bound crash happened.
                prog.SsaoAttn = 0f;

                // alphaTest is the one that DOES leak, and it is set deliberately: the ghost
                // is drawn at ~0.5 tint alpha, so the shader's own 0.001 default keeps a
                // fringe of nearly invisible texels that read as dirt around the preview.
                // 0.05 discards them. Restored in the finally.
                prog.AlphaTest = GhostAlphaTest;

                prog.ModelMatrix = new Matrixf()
                    .Translate(
                        (float)(pos.X - camPos.X),
                        (float)(pos.Y - camPos.Y),
                        (float)(pos.Z - camPos.Z))
                    .Mul(slotMatrix)
                    .Values;

                prog.ViewMatrix = rpi.CameraMatrixOriginf;
                prog.ProjectionMatrix = rpi.CurrentProjectionMatrix;

                // "tex" — the sampler THIS program declares. Read from the shipped shader
                // source, `assets/game/shaders/standard.fsh`:
                //
                //     uniform sampler2D tex;
                //     uniform sampler2D tex2dOverlay;
                //
                // The name is looked up in the bound program's uniform dictionary, so a
                // wrong one is a KeyNotFoundException, not a silent no-draw. Two earlier
                // guesses were wrong: "tex2d2d" (exists nowhere) and then "tex2d" — which
                // DOES appear in the assemblies, but belongs to other shader programs.
                // Grepping the binaries proved the string exists somewhere; only the
                // shader source proves it belongs to this program. Do not "verify" a
                // uniform name any other way.
                //
                // Note there is deliberately no `prog.Tex2D = ...` before this, despite the
                // property being named that: RenderMultiTextureMesh binds each of the
                // mesh's textures itself, so setting it by hand was redundant, and reaching
                // into BlockTextureAtlas.AtlasTextures[0] to do it was a throw waiting to
                // happen inside the shader-bound window above.
                rpi.RenderMultiTextureMesh(meshRef, "tex");
            }
            finally
            {
                if (prog != null)
                {
                    // PUT alphaTest BACK, and note the order: a uniform can only be written
                    // while its program is bound, because ShaderProgramBase.Uniform calls
                    // CheckShaderIsActive() first. So this has to happen BEFORE Stop().
                    //
                    // Its own try/catch is not defensive clutter — if writing the uniform
                    // threw out of the finally, Stop() would be skipped and the standard
                    // shader would be left bound, which is precisely the crash this whole
                    // block exists to prevent. Handing the shader back matters more than
                    // handing this one value back.
                    try { prog.AlphaTest = DefaultAlphaTest; } catch { /* Stop() still runs */ }

                    prog.Stop();
                }

                rpi.GlToggleBlend(false);
            }
        }

        public void Dispose()
        {
            foreach (MultiTextureMeshRef meshRef in uploaded.Values)
            {
                if (meshRef != null && !meshRef.Disposed) meshRef.Dispose();
            }
            uploaded.Clear();
        }
    }
}
