using System.Collections.Generic;
using System.Windows.Media;
using VISOR.Diagnostics;

namespace VISOR.ViewModels
{
    /// <summary>
    /// Centralized service for managing car class color assignments.
    /// Ensures consistent colors across all UI components (Relative display, Radar, etc.).
    /// Uses CarClassColor from iRacing YAML data for accurate class representation.
    /// </summary>
    public class ClassColorManager
    {
        private readonly Dictionary<int, Brush> _classColorMap = new();

        // iRacing reports no class colour in some session types (custom league races, offline
        // events) — either the class is absent from the colour data or it comes back as a literal
        // 0x000000. Fall back to a light grey so radar cars stay visible and car numbers stay
        // legible against the relative display's dark background instead of rendering transparent.
        private static readonly SolidColorBrush DefaultClassBrush = CreateFrozenBrush(Color.FromRgb(0xDD, 0xDD, 0xDD));

        private static SolidColorBrush CreateFrozenBrush(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        /// <summary>
        /// Gets the color assigned to a specific car class.
        /// Uses CarClassColor from YAML data for accurate iRacing class colors.
        /// </summary>
        /// <param name="classID">The car class ID</param>
        /// <param name="carClassColors">Array of hex colors from YAML (indexed by carIdx)</param>
        /// <param name="carClassIDs">Array of class IDs (indexed by carIdx)</param>
        /// <returns>The brush color for this class</returns>
        public Brush GetClassColor(int classID, int[]? carClassColors = null, int[]? carClassIDs = null)
        {
            // Class ID 0 is a real class here, not a missing one: online Test sessions and offline
            // custom races are single-class and iRacing reports every driver as CarClassID 0. It
            // used to short-circuit to Transparent, which left the car-number swatch unpainted in
            // exactly those sessions. Callers only ask about cars that have YAML driver data, so
            // there is no empty-slot case to guard against.

            if (_classColorMap.TryGetValue(classID, out var existingColor))
                return existingColor;

            if (carClassColors != null && carClassIDs != null)
            {
                for (int i = 0; i < carClassIDs.Length; i++)
                {
                    // 0x000000 means this entry carries no colour for the class (and every unused
                    // car slot reads as class 0 / colour 0, so for class 0 those entries match
                    // here). Keep scanning for a real colour rather than giving up on the first
                    // blank one.
                    if (carClassIDs[i] != classID || carClassColors[i] == 0)
                        continue;

                    var brush = ConvertHexColorToBrush(carClassColors[i]);
                    _classColorMap[classID] = brush;

                    Log.Debug($"[ClassColorManager] Assigned YAML color 0x{carClassColors[i]:X6} to class {classID}");
                    return brush;
                }
            }

            // Expected for class 0 (single-class sessions have no class colour to report);
            // genuinely unexpected for a real class ID.
            string message = $"[ClassColorManager] No YAML color found for class {classID}, using default light grey";
            if (classID == 0)
                Log.Info(message);
            else
                Log.Warning(message);

            _classColorMap[classID] = DefaultClassBrush;
            return DefaultClassBrush;
        }

        /// <summary>
        /// Converts iRacing hex color format (0xRRGGBB) to WPF SolidColorBrush.
        /// </summary>
        /// <param name="hexColor">Hex color value from YAML (e.g., 0xff5888)</param>
        /// <returns>SolidColorBrush for WPF rendering</returns>
        private SolidColorBrush ConvertHexColorToBrush(int hexColor)
        {
            byte r = (byte)((hexColor >> 16) & 0xFF);
            byte g = (byte)((hexColor >> 8) & 0xFF);
            byte b = (byte)(hexColor & 0xFF);

            // Frozen to match DefaultClassBrush: these are cached for the session and handed
            // straight to the renderer, so there is nothing to gain from keeping them mutable.
            return CreateFrozenBrush(Color.FromArgb(255, r, g, b));
        }

        /// <summary>
        /// Checks if a class has been assigned a color yet.
        /// </summary>
        /// <param name="classID">The car class ID</param>
        /// <returns>True if the class has a color assignment</returns>
        public bool HasColorAssignment(int classID)
        {
            return _classColorMap.ContainsKey(classID);
        }

        /// <summary>
        /// Gets all current class color assignments.
        /// Useful for debugging or displaying class legends.
        /// </summary>
        /// <returns>Dictionary of class ID to color mappings</returns>
        public Dictionary<int, Brush> GetAllAssignments()
        {
            return new Dictionary<int, Brush>(_classColorMap);
        }

        /// <summary>
        /// Resets all color assignments.
        /// Call this when starting a new session or changing tracks.
        /// </summary>
        public void Reset()
        {
            _classColorMap.Clear();
            Log.Info("[ClassColorManager] Reset - all color assignments cleared");
        }

        /// <summary>
        /// Gets the number of classes that have been assigned colors.
        /// </summary>
        public int AssignedClassCount => _classColorMap.Count;
    }
}