namespace CnoEvil.Core
{
    /// <summary>
    /// Display name of a unit for log lines and labels.
    /// </summary>
    public static class UnitNames
    {
        /// <summary>
        /// Unit.unitName, else the GameObject name. "none" for a null unit.
        /// </summary>
        public static string NameOf(Unit unit)
        {
            if (unit == null) return "none";
            return !string.IsNullOrEmpty(unit.unitName) ? unit.unitName : unit.name;
        }
    }
}
