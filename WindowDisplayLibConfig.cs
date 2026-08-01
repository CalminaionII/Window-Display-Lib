using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace WindowDisplayLib
{
    public class WindowDisplayLibConfig
    {
        // ====================== Rain ======================
        [Category("Rain on Window")]
        [Display(Name = "Rain Comment", Description = "0 = off, 1 = very quiet, 5 = normal, 10 = loud")]
        public string RainComment { get; set; } = "0 = off, 1 = very quiet, 5 = normal, 10 = loud";


        [Category("Rain on Window")]
        [Display(Name = "Volume", Description = "0 = off, 1 = very quiet, 5 = normal, 10 = loud")]
        [Range(0, 10)]
        public int RainSoundVolume { get; set; } = 5;

        [Category("Rain on Window")]
        [Display(Name = "Rain Sound Range Comment", Description = "How many blocks away you can hear rain on windows (1 to 64)")]
        public string RainSoundRangeComment { get; set; } = "How many blocks away you can hear rain on windows (1 to 64)";

        [Category("Rain on Window")]
        [Display(Name = "Range", Description = "How many blocks away you can hear rain on windows (1 to 64)")]
        [Range(1, 64)]
        public int RainSoundRange { get; set; } = 12;

        // ====================== Animation ======================
        [Category("Window Animation")]
        [Display(Name = "Animation Comment", Description = "1 = very slow, 3 = normal, 5 = very fast")]
        public string AnimationComment { get; set; } = "1 = very slow, 3 = normal, 5 = very fast";

        [Category("Window Animation")]
        [Display(Name = "Speed", Description = "1 = very slow, 3 = normal, 5 = very fast")]
        [Range(1, 5)]
        public int AnimationSpeed { get; set; } = 3;

    
        // ====================== Rotation ======================
        [Category("Slot Rotation")]
        [Display(Name = "Rotation Comment", Description = "Rotation Degrees (5 to 90)")]
        public string RotationComment { get; set; } = "Rotation Degrees (5 to 90)";

        [Category("Slot Rotation")]
        [Display(Name = "Degrees", Description = "Rotation Degrees (5 to 90)")]
        [Range(5, 90)]
        public int RotationStepDegrees { get; set; } = 15;

        [Category("Slot Rotation")]
        [Display(Name = "Placement Jitter Comment", Description = "Slight random resting ANGLE on placed items. false = everything sits perfectly square")]
        public string PlacementJitterComment { get; set; } = "Slight random resting ANGLE on placed items. false = everything sits perfectly square";

        [Category("Slot Rotation")]
        [Display(Name = "Placement Jitter", Description = "Slight random resting ANGLE on placed items. false = everything sits perfectly square")]
        public bool PlacementJitter { get; set; } = true;

        // ====================== Placement preview ======================
        [Category("Placement Preview")]
        [Display(Name = "Placement Ghost Comment", Description = "Show a see-through copy of the held item where it would land. The outline box is shown either way")]
        public string PlacementGhostComment { get; set; } = "Show a see-through copy of the held item where it would land. The outline box is shown either way";

        /// <summary>
        /// The kill switch for <c>PlacementGhostRenderer</c>. This is the only thing in the
        /// mod that draws per frame, so it is the only thing that can cost frames or take
        /// the client down — turning it off leaves the wireframe box exactly as it was and
        /// removes the renderer from the render loop entirely, with no rebuild.
        /// </summary>
        [Category("Placement Preview")]
        [Display(Name = "Placement Ghost", Description = "Show a see-through copy of the held item where it would land. The outline box is shown either way")]
        public bool PlacementGhost { get; set; } = true;

        [Category("Placement Preview")]
        [Display(Name = "Placement Ghost Opacity Comment", Description = "How solid the ghost item looks, 1 (barely visible) to 10 (solid)")]
        public string PlacementGhostOpacityComment { get; set; } = "How solid the ghost item looks, 1 (barely visible) to 10 (solid)";

        [Category("Placement Preview")]
        [Display(Name = "Placement Ghost Opacity", Description = "How solid the ghost item looks, 1 (barely visible) to 10 (solid)")]
        [Range(1, 10)]
        public int PlacementGhostOpacity { get; set; } = 5;

        [Category("Placement Preview")]
        [Display(Name = "Show Placement Box Comment", Description = "Outline showing where the held item will land. false hides it and leaves just the ghost")]
        public string ShowPlacementBoxComment { get; set; } = "Outline showing where the held item will land. false hides it and leaves just the ghost";

        /// <summary>
        /// The box and the ghost overlap in what they tell you, but not completely: the
        /// ghost shows the ITEM, the box shows the FOOTPRINT, and those genuinely differ —
        /// seashells declare a deliberately tight box so more fit along a sill, and the
        /// ruler is 12.8 wide but paper thin. Turning the box off is a real choice rather
        /// than pure tidying, so it is a setting rather than a decision made for everyone.
        ///
        /// RENAMED from an int `PlacementBox` (0/1/2) on 2026-07-30. Renaming rather than
        /// changing the type on purpose: an existing file holds `"PlacementBox": 2`, and
        /// deserialising 2 into a bool throws — which Load() catches by falling back to
        /// defaults, silently resetting every other setting the player had. A new NAME is
        /// simply an added key and a removed one, which SchemaDiffers reconciles by design.
        /// </summary>
        [Category("Placement Preview")]
        [Display(Name = "Show Placement Box", Description = "Outline showing where the held item will land. false hides it and leaves just the ghost")]
        public bool ShowPlacementBox { get; set; } = true;

        [Category("Placement Preview")]
        [Display(Name = "Show Placed Item Box Comment", Description = "Outline around an item already on the sill when you aim at it. false hides it")]
        public string ShowPlacedItemBoxComment { get; set; } = "Outline around an item already on the sill when you aim at it. false hides it";

        /// <summary>
        /// Separate from <see cref="ShowPlacementBox"/> because they answer different questions:
        /// that one is "where will this go", this one is "what am I pointing at".
        ///
        /// Worth hiding for a reason the placement box does not have: a selection box is a
        /// `Cuboidf`, which is axis-aligned by definition, so it CANNOT follow a freely
        /// rotated item. At any angle off a quarter turn the outline visibly disagrees with
        /// the item inside it, and that mismatch is the whole argument for turning it off.
        ///
        /// The cost is losing the only indication of which item you are about to take or
        /// turn, which is why it defaults to true and has a toggle key.
        /// </summary>
        [Category("Placement Preview")]
        [Display(Name = "Show Placed Item Box", Description = "Outline around an item already on the sill when you aim at it. false hides it")]
        public bool ShowPlacedItemBox { get; set; } = true;


        [Category("General")]
        [Display(Name = "Room Safe Opening Comment", Description = "Close windows before changing, if true will disable open/closed states")]
        public string RoomSafeOpeningComment { get; set; } = "Close windows before changing, if true will disable open/closed states";

        [Category("General")]
        [Display(Name = "Room Safe Opening", Description = "Close windows before changing, if true will disable open/closed states")]
        public bool RoomSafeOpening { get; set; } = false;

        // ====================== Computed values (hidden) ======================
        [Newtonsoft.Json.JsonIgnore]
        [Browsable(false)]
        public float RainSoundVolumeValue => Math.Clamp(RainSoundVolume, 0, 10) * 0.1f;

        [Newtonsoft.Json.JsonIgnore]
        [Browsable(false)]
        public float PlacementGhostAlpha => Math.Clamp(PlacementGhostOpacity, 1, 10) * 0.1f;

        [Newtonsoft.Json.JsonIgnore]
        [Browsable(false)]
        public float AnimationSpeedValue => AnimationSpeed switch
        {
            1 => 0.25f,
            2 => 0.5f,
            3 => 1.0f,
            4 => 2.0f,
            5 => 4.0f,
            _ => 1.0f
        };


        public static WindowDisplayLibConfig Current { get; private set; }

        public static void Load(ICoreAPI api)
        {
            try
            {
                string configFilename = "windowdisplaylibconfig.json";
                Current = api.LoadModConfig<WindowDisplayLibConfig>(configFilename);

                if (Current == null)
                {
                    api.Logger.Notification("[WindowDisplayLib] No config file found, creating default config");
                    Current = new WindowDisplayLibConfig();
                    Save(api);
                }
                else
                {
                    api.Logger.Notification("[WindowDisplayLib] Config loaded successfully");

                    // Rewrite the file whenever its shape no longer matches the class,
                    // so options added or removed in an update are reconciled automatically
                    try
                    {
                        // Vintage Story saves mod configs in the ModConfig data folder
                        string configPath = System.IO.Path.Combine(GamePaths.ModConfig, configFilename);
                        if (System.IO.File.Exists(configPath))
                        {
                            string rawJson = System.IO.File.ReadAllText(configPath);
                            if (SchemaDiffers(rawJson, out string added, out string removed))
                            {
                                api.Logger.Notification(
                                    "[WindowDisplayLib] Config schema changed (added: {0}; removed: {1}), rewriting file...",
                                    string.IsNullOrEmpty(added) ? "none" : added,
                                    string.IsNullOrEmpty(removed) ? "none" : removed);
                                Save(api);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        api.Logger.Warning("[WindowDisplayLib] Could not verify config file structure on disk: {0}", ex.Message);
                    }

                    Current.RainSoundVolume = Math.Clamp(Current.RainSoundVolume, 0, 10);
                    Current.RainSoundRange = Math.Clamp(Current.RainSoundRange, 1, 64);
                    Current.AnimationSpeed = Math.Clamp(Current.AnimationSpeed, 1, 5);
                    Current.RotationStepDegrees = Math.Clamp(Current.RotationStepDegrees, 5, 90);
                    Current.PlacementGhostOpacity = Math.Clamp(Current.PlacementGhostOpacity, 1, 10);
                }
            }
            catch (Exception e)
            {
                api.Logger.Error("[WindowDisplayLib] Failed to load config, using defaults. Error: {0}", e.Message);
                Current = new WindowDisplayLibConfig();
            }
        }

        /// <summary>
        /// Compares the property names on disk against the ones this class actually
        /// serialises. Any difference in either direction means the file is stale.
        ///
        /// Deliberately not a string search for a known option name: that only catches
        /// additions, has to be updated by hand every time an option is added, and
        /// silently leaves removed options sitting in the file forever.
        ///
        /// Values are untouched — Current was already deserialised from this file, so
        /// re-saving keeps every setting the user had and only reconciles the shape.
        /// </summary>
        private static bool SchemaDiffers(string rawJson, out string added, out string removed)
        {
            added = removed = null;

            var onDisk = Newtonsoft.Json.Linq.JObject.Parse(rawJson)
                .Properties().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

            var inClass = typeof(WindowDisplayLibConfig)
                .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                .Where(p => p.CanRead && p.CanWrite
                            && p.GetCustomAttribute<Newtonsoft.Json.JsonIgnoreAttribute>() == null)
                .Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

            if (onDisk.SetEquals(inClass)) return false;

            added = string.Join(", ", inClass.Except(onDisk));
            removed = string.Join(", ", onDisk.Except(inClass));
            return true;
        }

        public static void Save(ICoreAPI api)
        {
            if (Current == null)
            {
                api.Logger.Warning("[WindowDisplayLib] Cannot save config - Current is null");
                return;
            }

            try
            {
                api.StoreModConfig(Current, "windowdisplaylibconfig.json");
                api.Logger.Notification("[WindowDisplayLib] Config saved successfully");
            }
            catch (Exception e)
            {
                api.Logger.Error("[WindowDisplayLib] Failed to save config. Error: {0}", e.Message);
            }
        }
    }
}