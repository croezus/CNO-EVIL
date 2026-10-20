using System;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace CnoEvil.Core
{
    public static class FastReflection
    {
        /// <summary>
        /// Emits a fast getter for a (usually private) game field. Returns null, and logs a warning once,
        /// when the field does not exist or its type cannot be read as TField, so a game update that
        /// renames a field shows up in the BepInEx log instead of silently disabling a feature.
        /// </summary>
        public static Func<TTarget, TField> CreateFieldGetter<TTarget, TField>(string fieldName)
        {
            string label = typeof(TTarget).Name + "." + fieldName;
            try
            {
                FieldInfo fi = AccessTools.Field(typeof(TTarget), fieldName);
                if (fi == null)
                {
                    Warn(label + " not found");
                    return null;
                }
                if (!typeof(TField).IsAssignableFrom(fi.FieldType))
                {
                    Warn(string.Format("{0} is {1}, cannot be read as {2}", label, fi.FieldType.Name, typeof(TField).Name));
                    return null;
                }

                DynamicMethod dm = new DynamicMethod("Get_" + typeof(TTarget).Name + "_" + fieldName,
                    typeof(TField), new Type[] { typeof(TTarget) }, typeof(TTarget), true);
                ILGenerator il = dm.GetILGenerator();
                if (fi.IsStatic)
                {
                    il.Emit(OpCodes.Ldsfld, fi);
                }
                else
                {
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldfld, fi);
                }
                if (fi.FieldType.IsValueType && !typeof(TField).IsValueType)
                {
                    il.Emit(OpCodes.Box, fi.FieldType);
                }
                il.Emit(OpCodes.Ret);
                return (Func<TTarget, TField>)dm.CreateDelegate(typeof(Func<TTarget, TField>));
            }
            catch (Exception ex)
            {
                Warn(label + " getter failed: " + ex.Message);
                return null;
            }
        }

        private static void Warn(string message)
        {
            if (CnoEvilPlugin.Log != null)
            {
                CnoEvilPlugin.Log.LogWarning("FastReflection: " + message);
            }
        }
    }
}
