using System;
using System.Collections.Generic;
using System.Globalization;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace WindowDisplayLib
{
    /// <summary>
    /// A free-placement surface parsed from a shape element named
    /// <c>psurface&lt;index&gt;-w&lt;width&gt;-h&lt;height&gt;-d&lt;length&gt;[-&lt;category&gt;]</c>.
    ///
    /// Deliberately mirrors vanilla's <c>PlacementSurface</c> naming and slot-id
    /// format so shapes authored for cabinets work here unchanged — but this is
    /// our own parse, so it can carry extra per-surface data and is not tied to
    /// BlockBehaviorDisplay.
    /// </summary>
    public class WindowPlacementSurface
    {
        public string ElementName;
        public int Index;
        public Size3i Size;

        /// <summary>Element <c>from</c>, in voxels.</summary>
        public Vec3f VoxelPosition;

        /// <summary>Element <c>to</c>, in voxels. Vanilla drops this; we keep it for grid sizing.</summary>
        public Vec3f VoxelEnd;

        /// <summary>5th name segment, defaults to <c>shelf</c> so vanilla shelvable items apply.</summary>
        public string DisplayCategory;

        /// <summary>
        /// Which way items on this surface start facing, in degrees about Y, worked out
        /// from where the surface sits rather than declared anywhere.
        ///
        /// A surface occupies one side of the block, so the side it is on is the side it
        /// faces: the larger of its X and Z displacement from the block centre wins, and
        /// its sign gives the direction. Items default to facing +Z, so +Z needs no turn
        /// and -Z needs half a turn.
        ///
        /// This reproduces what the pre-psurface content declared by hand — its
        /// <c>slotGroups</c> paired <c>zCenter: 0.281</c> with <c>rotateY: 0</c> and
        /// <c>zCenter: -0.28</c> with <c>rotateY: 180</c> — without the JSON.
        ///
        /// A surface centred on the block gets 0, having no side to face.
        /// </summary>
        public float FacingRotationDeg
        {
            get
            {
                const float centre = 8f;   // voxels
                float dx = (VoxelPosition.X + VoxelEnd.X) / 2f - centre;
                float dz = (VoxelPosition.Z + VoxelEnd.Z) / 2f - centre;

                if (Math.Abs(dx) > Math.Abs(dz)) return dx > 0f ? 90f : 270f;
                if (Math.Abs(dz) > 0f) return dz > 0f ? 0f : 180f;
                return 0f;
            }
        }

        public static bool IsSurfaceElement(string elementName)
            => elementName != null && elementName.StartsWith("psurface", StringComparison.Ordinal);

        /// <summary>
        /// Parses one element. Returns null when the name is malformed rather than
        /// throwing, so one bad element cannot break block loading.
        /// </summary>
        public static WindowPlacementSurface TryParse(ShapeElement element, ILogger logger)
        {
            string name = element?.Name;
            if (!IsSurfaceElement(name)) return null;

            string[] parts = name.Substring("psurface".Length).Split('-');
            if (parts.Length < 4)
            {
                logger?.Warning("[WindowDisplayLib] Shape element '{0}' looks like a psurface but has too few segments; expected psurface<i>-w<W>-h<H>-d<D>[-<category>].", name);
                return null;
            }

            if (!TryDim(parts[0], null, out int index) ||
                !TryDim(parts[1], 'w', out int width) ||
                !TryDim(parts[2], 'h', out int height) ||
                !TryDim(parts[3], 'd', out int length))
            {
                logger?.Warning("[WindowDisplayLib] Could not parse dimensions from psurface element '{0}'.", name);
                return null;
            }

            return new WindowPlacementSurface
            {
                ElementName = name,
                Index = index,
                Size = new Size3i(width, height, length),
                VoxelPosition = new Vec3f((float)element.From[0], (float)element.From[1], (float)element.From[2]),
                VoxelEnd = new Vec3f((float)element.To[0], (float)element.To[1], (float)element.To[2]),
                DisplayCategory = parts.Length > 4 ? parts[4] : null
            };
        }

        private static bool TryDim(string segment, char? prefix, out int value)
        {
            value = 0;
            if (string.IsNullOrEmpty(segment)) return false;
            string digits = prefix.HasValue ? segment.Substring(1) : segment;
            return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>
        /// Builds the flat 1-voxel-high pads a player can click to place an item.
        /// Box ids are <c>"&lt;surface&gt;-&lt;xOffset&gt;-&lt;zOffset&gt;"</c>, matching the slot id
        /// of whatever gets placed there.
        /// </summary>
        public IEnumerable<CuboidfWithId> BuildGridBoxes(int maxXDivisions, int maxZDivisions)
        {
            int xDiv = Math.Max(1, Math.Min(maxXDivisions, Size.Width));
            int zDiv = Math.Max(1, Math.Min(maxZDivisions, Size.Length));

            float stepX = (VoxelEnd.X - VoxelPosition.X) / xDiv;
            float stepZ = (VoxelEnd.Z - VoxelPosition.Z) / zDiv;

            for (int xi = 0; xi < xDiv; xi++)
            {
                float x0 = VoxelPosition.X + xi * stepX;
                float x1 = VoxelPosition.X + (xi + 1) * stepX;

                for (int zi = 0; zi < zDiv; zi++)
                {
                    float z0 = VoxelPosition.Z + zi * stepZ;
                    float z1 = VoxelPosition.Z + (zi + 1) * stepZ;

                    yield return new CuboidfWithId(x0 / 16f, VoxelPosition.Y / 16f, z0 / 16f,
                                                   x1 / 16f, VoxelPosition.Y / 16f, z1 / 16f)
                    {
                        Id = WindowSlotId.Encode(Index, xi * stepX, zi * stepZ, 0f)
                    };
                }
            }
        }
    }

    /// <summary>
    /// Position of one stored item on a surface.
    /// Encodes to <c>"&lt;surface&gt;-&lt;x&gt;-&lt;z&gt;"</c>, or <c>"…-&lt;y&gt;"</c> once stacked.
    /// Selection boxes belonging to an already-placed item are prefixed <c>"p-"</c>.
    ///
    /// All formatting is invariant-culture on purpose: vanilla uses the ambient
    /// culture here, which round-trips badly on comma-decimal locales.
    /// </summary>
    public class WindowSlotId
    {
        public int SurfaceIndex;
        public float X;
        public float Y;
        public float Z;
        public bool IsPlacedItem;

        public const string PlacedPrefix = "p-";

        public string Encoded => Encode(SurfaceIndex, X, Z, Y);

        public static string Encode(int surfaceIndex, float x, float z, float y)
        {
            string baseId = surfaceIndex.ToString(CultureInfo.InvariantCulture) + "-"
                          + x.ToString(CultureInfo.InvariantCulture) + "-"
                          + z.ToString(CultureInfo.InvariantCulture);

            return y > 0f ? baseId + "-" + y.ToString(CultureInfo.InvariantCulture) : baseId;
        }

        public static WindowSlotId Decode(string slotId)
        {
            if (string.IsNullOrEmpty(slotId)) return null;

            bool placed = slotId.StartsWith(PlacedPrefix, StringComparison.Ordinal);
            string body = placed ? slotId.Substring(PlacedPrefix.Length) : slotId;

            string[] parts = body.Split('-');
            if (parts.Length < 3) return null;

            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int surface)) return null;
            if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)) return null;
            if (!float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z)) return null;

            float y = 0f;
            if (parts.Length > 3) float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out y);

            return new WindowSlotId { SurfaceIndex = surface, X = x, Y = y, Z = z, IsPlacedItem = placed };
        }

        public WindowSlotId UpCopy() => new WindowSlotId
        {
            SurfaceIndex = SurfaceIndex,
            X = X,
            Y = Y + 1f,
            Z = Z,
            IsPlacedItem = IsPlacedItem
        };
    }
}
