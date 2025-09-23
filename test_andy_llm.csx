#r "nuget: Andy.Llm, 2025.9.19-rc.15"

using System;
using System.Linq;
using System.Reflection;

var assembly = AppDomain.CurrentDomain.GetAssemblies()
    .FirstOrDefault(a => a.GetName().Name == "Andy.Llm");

if (assembly == null)
{
    assembly = Assembly.Load("Andy.Llm");
}

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

Console.WriteLine("\nTypes in Andy.Llm.Models:");
var modelTypes = assembly.GetTypes()
    .Where(t => t.Namespace == "Andy.Llm.Models")
    .OrderBy(t => t.Name);

foreach (var type in modelTypes)
{
    Console.WriteLine($"  {type.Name}");
}