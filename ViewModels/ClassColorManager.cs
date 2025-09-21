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
        private readonly Brush[] _availableColors = {
            Brushes.Gold,
            Brushes.DarkGray,
            Brushes.White,
            Brushes.HotPink,
            Brushes.LightBlue
        };
        private int _nextColorIndex = 0;

        /// <summary>
        /// Gets the color assigned to a specific car class.
        /// If the class hasn't been seen before, assigns it the next available color.
        /// </summary>
        /// <param name="classID">The car class ID</param>
        /// <returns>The brush color for this class</returns>
        public Brush GetClassColor(int classID)
        {
            // Handle unknown/invalid class
            if (classID == 0)
                return Brushes.White;

            // Return existing assignment if we have one
            if (_classColorMap.TryGetValue(classID, out var existingColor))
                return existingColor;

            // Assign next available color
            var newColor = _availableColors[_nextColorIndex % _availableColors.Length];
            _classColorMap[classID] = newColor;
            _nextColorIndex++;

            System.Diagnostics.Debug.WriteLine($"[ClassColorManager] Assigned {GetColorName(newColor)} to class {classID}");

            return newColor;
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
            _nextColorIndex = 0;
            System.Diagnostics.Debug.WriteLine("[ClassColorManager] Reset - all color assignments cleared");
        }

        /// <summary>
        /// Gets the number of classes that have been assigned colors.
        /// </summary>
        public int AssignedClassCount => _classColorMap.Count;

        /// <summary>
        /// Helper method to get a readable name for debugging.
        /// </summary>
        private string GetColorName(Brush brush)
        {
            if (brush == Brushes.Gold) return "Gold";
            if (brush == Brushes.DarkGray) return "DarkGray";
            if (brush == Brushes.White) return "White";
            if (brush == Brushes.HotPink) return "HotPink";
            if (brush == Brushes.LightBlue) return "LightBlue";
            return "Unknown";
        }
    }
}