using Vintagestory.API.MathTools;

namespace WindowDisplayLib
{
    // Shared by the psurface path. Lifted out of the legacy block entity so that
    // file can be excluded from the build without taking these with it.
    public class FrameBoxGroup
    {
        public string AnimOpen { get; set; }
        public string AnimClose { get; set; }
        public bool IsWindow { get; set; } = true;
        public Cuboidf ClosedFrameBox { get; set; }
        public Cuboidf OpenFrameBox { get; set; }
        public Cuboidf[] StaticFrameBoxes { get; set; }
    }

    public class CollisionBoxGroup
    {
        public Cuboidf ClosedCollisionBox { get; set; }
        public Cuboidf OpenCollisionBox { get; set; }
        public Cuboidf[] StaticCollisionBoxes { get; set; }
    }
}
