using System;
using System.Reflection;
using System.Linq;

class ExploreAndyModel
{
    static void Main()
    {
        // Load the Andy.Model assembly
        var assemblyPath = "/Users/sami/.nuget/packages/andy.model/2025.9.18-rc.3/lib/net8.0/Andy.Model.dll";
        var assembly = Assembly.LoadFrom(assemblyPath);

        // Get all types in Andy.Model.Orchestration namespace
        var orchestrationTypes = assembly.GetTypes()
            .Where(t => t.Namespace != null && t.Namespace.StartsWith("Andy.Model.Orchestration"))
            .OrderBy(t => t.FullName)
            .ToList();

        Console.WriteLine("Types in Andy.Model.Orchestration:");
        foreach (var type in orchestrationTypes)
        {
            Console.WriteLine($"  {type.FullName}");
            if (type.Name == "Assistant" || type.Name.Contains("Assistant"))
            {
                Console.WriteLine("    Constructors:");
                foreach (var ctor in type.GetConstructors())
                {
                    var parameters = string.Join(", ", ctor.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                    Console.WriteLine($"      ({parameters})");
                }
                Console.WriteLine("    Methods:");
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    var parameters = string.Join(", ", method.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                    Console.WriteLine($"      {method.ReturnType.Name} {method.Name}({parameters})");
                }
            }
        }

        // Also check for interfaces
        Console.WriteLine("\nInterfaces in Andy.Model.Orchestration:");
        var interfaces = assembly.GetTypes()
            .Where(t => t.IsInterface && t.Namespace != null && t.Namespace.StartsWith("Andy.Model.Orchestration"))
            .OrderBy(t => t.FullName);

        foreach (var iface in interfaces)
        {
            Console.WriteLine($"  {iface.FullName}");
        }
    }
}