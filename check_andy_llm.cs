using System;
using System.Reflection;
using System.Linq;

class CheckAndyLlm
{
    static void Main()
    {
        var assemblyPath = "/Users/sami/.nuget/packages/andy.llm/2025.9.19-rc.15/lib/net8.0/Andy.Llm.dll";
        var assembly = Assembly.LoadFrom(assemblyPath);

        Console.WriteLine("Namespaces in Andy.Llm:");
        var namespaces = assembly.GetTypes()
            .Select(t => t.Namespace)
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct()
            .OrderBy(n => n);

        foreach (var ns in namespaces)
        {
            Console.WriteLine($"  {ns}");
        }

        Console.WriteLine("\nTypes in root Andy.Llm namespace:");
        var rootTypes = assembly.GetTypes()
            .Where(t => t.Namespace == "Andy.Llm")
            .OrderBy(t => t.Name)
            .Take(20);

        foreach (var type in rootTypes)
        {
            Console.WriteLine($"  {type.Name}");
        }
    }
}