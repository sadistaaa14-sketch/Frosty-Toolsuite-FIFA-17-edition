using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

/// <summary>
/// Prints the member shape of the FIFA 17 sound asset classes, read from the SDK the editor loads
/// them from. The chunk walk reads public members by reflection, so what it can reach depends on
/// exactly what these classes declare and how their lists are typed.
/// </summary>
internal static class SdkShape
{
    private static void Main(string[] args)
    {
        string path = args.Length != 0 ? args[0] : "FrostySdk/Profiles/Fifa17SDK.dll";
        Assembly assembly = Assembly.LoadFrom(System.IO.Path.GetFullPath(path));
        Console.WriteLine("loaded " + assembly.FullName);

        string[] wanted =
        {
            "SoundDataChunk", "NewWaveAsset", "SoundWaveAsset", "LocalizedWaveAsset",
            "SoundWaveRuntimeVariation", "NewWaveChunk", "SoundWaveVariation", "LocalizedWaveChunk"
        };

        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t != null).ToArray();
            Console.WriteLine("(types that failed to load: " + (ex.Types.Length - types.Length) + ")");
        }

        foreach (string name in wanted)
        {
            Type type = types.FirstOrDefault(t => t.Name == name);
            if (type == null)
            {
                Console.WriteLine();
                Console.WriteLine("=== " + name + ": NOT FOUND in the SDK");
                continue;
            }

            Console.WriteLine();
            Console.WriteLine("=== " + type.FullName + "  (base: " + (type.BaseType == null ? "none" : type.BaseType.FullName) + ")");
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                Console.WriteLine("    prop  " + Describe(property.PropertyType) + "  " + property.Name);
            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                Console.WriteLine("    field " + Describe(field.FieldType) + "  " + field.Name);
        }
    }

    private static string Describe(Type type)
    {
        if (!type.IsGenericType)
            return type.Name;

        return type.Name + "<" + string.Join(", ", type.GetGenericArguments().Select(Describe)) + ">";
    }
}
