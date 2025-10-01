using System.Collections.Generic;
using System.Windows.Media;

namespace VISOR.ViewModels
{
    /// <summary>
    /// Centralized service for managing car class color assignments.
    /// Ensures consistent colors across all UI components (Relative display, Radar, etc.).
    /// </summary>
    public class ClassColorManager
    {
        private readonly Dictionary<int, Brush> _classColorMap = new();

        // OLD LOGIC - Commented out for potential revert
        // private readonly Brush[] _availableColors = {
        //     Brushes.Gold,
        //     Brushes.DarkGray,
        //     Brushes.White,
        //     Brushes.HotPink,
        //     Brushes.LightBlue
        // };
        // private int _nextColorIndex = 0;

        /// <summary>
        /// Gets the color assigned to a specific car class.
        /// New implementation: Uses CarClassColor from YAML data.
        /// </summary>
        /// <param name="classID">The car class ID</param>
        /// <param name="carClassColors">Array of hex colors from YAML (indexed by carIdx)</param>
        /// <param name="carClassIDs">Array of class IDs (indexed by carIdx)</param>
        /// <returns>The brush color for this class</returns>
        public Brush GetClassColor(int classID, int[] carClassColors = null, int[] carClassIDs = null)
        {
            // Handle unknown/invalid class
            if (classID == 0)
                return Brushes.Transparent;

            // Return existing assignment if we have one
            if (_classColorMap.TryGetValue(classID, out var existingColor))
                return existingColor;

            // Try to find this class's color from YAML data
            if (carClassColors != null && carClassIDs != null)
            {
                for (int i = 0; i < carClassIDs.Length; i++)
                {
                    if (carClassIDs[i] == classID && carClassColors[i] != 0)
                    {
                        // Found a driver with this class - extract their color
                        var brush = ConvertHexColorToBrush(carClassColors[i]);
                        _classColorMap[classID] = brush;

                        System.Diagnostics.Debug.WriteLine($"[ClassColorManager] Assigned YAML color 0x{carClassColors[i]:X6} to class {classID}");
                        return brush;
                    }
                }
            }

            // Fallback: No color found in YAML, use transparent
            System.Diagnostics.Debug.WriteLine($"[ClassColorManager] No YAML color found for class {classID}, using Transparent");
            _classColorMap[classID] = Brushes.Transparent;
            return Brushes.Transparent;

            // OLD LOGIC - Commented out for potential revert
            // Assign next available color from hardcoded palette
            // var newColor = _availableColors[_nextColorIndex % _availableColors.Length];
            // _classColorMap[classID] = newColor;
            // _nextColorIndex++;
            // System.Diagnostics.Debug.WriteLine($"[ClassColorManager] Assigned {GetColorName(newColor)} to class {classID}");
            // return newColor;
        }

        /// <summary>
        /// Converts iRacing hex color format (0xRRGGBB) to WPF SolidColorBrush.
        /// </summary>
        /// <param name="hexColor">Hex color value from YAML (e.g., 0xff5888)</param>
        /// <returns>SolidColorBrush for WPF rendering</returns>
        private SolidColorBrush ConvertHexColorToBrush(int hexColor)
        {
            // Extract RGB components from hex value
            byte r = (byte)((hexColor >> 16) & 0xFF);
            byte g = (byte)((hexColor >> 8) & 0xFF);
            byte b = (byte)(hexColor & 0xFF);

            // Create WPF color with full opacity
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
            // _nextColorIndex = 0; // OLD LOGIC
            System.Diagnostics.Debug.WriteLine("[ClassColorManager] Reset - all color assignments cleared");
        }

        /// <summary>
        /// Gets the number of classes that have been assigned colors.
        /// </summary>
        public int AssignedClassCount => _classColorMap.Count;

        // OLD LOGIC - Commented out for potential revert
        // /// <summary>
        // /// Helper method to get a readable name for debugging.
        // /// </summary>
        // private string GetColorName(Brush brush)
        // {
        //     if (brush == Brushes.Gold) return "Gold";
        //     if (brush == Brushes.DarkGray) return "DarkGray";
        //     if (brush == Brushes.White) return "White";
        //     if (brush == Brushes.HotPink) return "HotPink";
        //     if (brush == Brushes.LightBlue) return "LightBlue";
        //     return "Unknown";
        // }
    }
}