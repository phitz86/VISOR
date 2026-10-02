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
        private readonly Dictionary<int, Brush> _classTextMap = new();

        // Perceived-brightness cut-off (0-255) between black and white car numbers. Chosen so the
        // light class colours (white, yellow, green, cyan) get black text while the saturated
        // mid-tones (purple, pink, teal, orange) keep the white-with-shadow look they always had.
        private const double LIGHT_FILL_THRESHOLD = 150.0;

        private static readonly SolidColorBrush DarkTextBrush = CreateFrozenBrush(Colors.Black);
        private static readonly SolidColorBrush LightTextBrush = CreateFrozenBrush(Colors.White);

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
        /// Black or white, whichever reads better as the car number on this class's fill. Resolved
        /// from the same brush <see cref="GetClassColor"/> hands out, so the number always matches
        /// what is actually painted behind it, including the light-grey and white defaults.
        /// </summary>
        public Brush GetClassTextBrush(int classID, int[]? carClassColors = null, int[]? carClassIDs = null)
        {
            if (_classTextMap.TryGetValue(classID, out var cached))
                return cached;

            var fill = GetClassColor(classID, carClassColors, carClassIDs);
            Brush text = (fill is SolidColorBrush solid && IsLightFill(solid.Color))
                ? DarkTextBrush
                : LightTextBrush;

            _classTextMap[classID] = text;
            return text;
        }

        /// <summary>
        /// True for fills light enough that dark text reads better. Uses the standard weighted
        /// perceived-brightness formula, which tracks how bright a colour looks far better than
        /// averaging the channels (pure green is much brighter to the eye than pure blue).
        /// </summary>
        private static bool IsLightFill(Color color)
        {
            double brightness = (color.R * 299 + color.G * 587 + color.B * 114) / 1000.0;
            return brightness >= LIGHT_FILL_THRESHOLD;
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
            _classTextMap.Clear();
            Log.Info("[ClassColorManager] Reset - all color assignments cleared");
        }

        /// <summary>
        /// Gets the number of classes that have been assigned colors.
        /// </summary>
        public int AssignedClassCount => _classColorMap.Count;
    }
}