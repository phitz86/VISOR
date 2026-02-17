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

        /// <summary>
        /// Gets the color assigned to a specific car class.
        /// Uses CarClassColor from YAML data for accurate iRacing class colors.
        /// </summary>
        /// <param name="classID">The car class ID</param>
        /// <param name="carClassColors">Array of hex colors from YAML (indexed by carIdx)</param>
        /// <param name="carClassIDs">Array of class IDs (indexed by carIdx)</param>
        /// <returns>The brush color for this class</returns>
        public Brush GetClassColor(int classID, int[] carClassColors = null, int[] carClassIDs = null)
        {
            if (classID == 0)
                return Brushes.Transparent;

            if (_classColorMap.TryGetValue(classID, out var existingColor))
                return existingColor;

            if (carClassColors != null && carClassIDs != null)
            {
                for (int i = 0; i < carClassIDs.Length; i++)
                {
                    if (carClassIDs[i] == classID)
                    {
                        var brush = ConvertHexColorToBrush(carClassColors[i]);
                        _classColorMap[classID] = brush;

                        Log.Debug($"[ClassColorManager] Assigned YAML color 0x{carClassColors[i]:X6} to class {classID}");
                        return brush;
                    }
                }
            }

            Log.Warning($"[ClassColorManager] No YAML color found for class {classID}, using Transparent");
            _classColorMap[classID] = Brushes.Transparent;
            return Brushes.Transparent;
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

            var color = Color.FromArgb(255, r, g, b);
            return new SolidColorBrush(color);
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