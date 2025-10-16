#!/usr/bin/env dotnet-script
#r "nuget: Andy.Tools, 2025.9.5-rc.15"

using System;
using System.Linq;
using System.Reflection;

var assembly = Assembly.Load("Andy.Tools");
var toolTypes = assembly.GetTypes()
    .Where(t => t.IsClass && !t.IsAbstract && t.Name.EndsWith("Tool"))
    .OrderBy(t => t.Namespace)
    .ThenBy(t => t.Name);

Console.WriteLine("Found tools:");
foreach (var type in toolTypes)
{
    Console.WriteLine($"  typeof({type.FullName?.Replace("Andy.Tools.", "Andy.Tools.")})");
}
