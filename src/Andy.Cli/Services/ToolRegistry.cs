using System;
using System.Collections.Generic;
using System.Linq;
using Andy.Tools.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Andy.Cli.Services;

/// <summary>
/// Default implementation of IToolRegistry for managing tool registrations
/// </summary>
public class ToolRegistry : IToolRegistry
{
    private readonly Dictionary<string, ToolRegistration> _tools = new();
    private readonly object _lock = new();

    public IReadOnlyList<ToolRegistration> Tools
    {
        get
        {
            lock (_lock)
            {
                return _tools.Values.ToList();
            }
        }
    }

    public event EventHandler<ToolRegisteredEventArgs>? ToolRegistered;
    public event EventHandler<ToolUnregisteredEventArgs>? ToolUnregistered;

    public ToolRegistration RegisterTool<T>(Dictionary<string, object?>? configuration = null) where T : class, ITool
    {
        return RegisterTool(typeof(T), configuration);
    }

    public ToolRegistration RegisterTool(Type toolType, Dictionary<string, object?>? configuration = null)
    {
        if (!typeof(ITool).IsAssignableFrom(toolType))
        {
            throw new ArgumentException($"Type {toolType.Name} does not implement ITool interface");
        }

        // Create a temporary instance to get metadata
        var tempInstance = (ITool)Activator.CreateInstance(toolType)!;
        var metadata = tempInstance.Metadata;

        return RegisterTool(metadata, sp => (ITool)ActivatorUtilities.CreateInstance(sp, toolType), configuration);
    }

    public ToolRegistration RegisterTool(ToolMetadata metadata, Func<IServiceProvider, ITool> factory, Dictionary<string, object?>? configuration = null)
    {
        lock (_lock)
        {
            if (_tools.ContainsKey(metadata.Id))
            {
                throw new InvalidOperationException($"Tool with ID '{metadata.Id}' is already registered");
            }

            var registration = new ToolRegistration
            {
                Metadata = metadata,
                ToolType = typeof(object), // We don't have the actual type when using factory
                Factory = factory,
                Configuration = configuration ?? new Dictionary<string, object?>(),
                IsEnabled = true,
                RegisteredAt = DateTimeOffset.UtcNow,
                Source = "manual",
                AssemblyName = factory.Method.DeclaringType?.Assembly.GetName().Name
            };

            _tools[metadata.Id] = registration;
            
            ToolRegistered?.Invoke(this, new ToolRegisteredEventArgs(registration));
            
            return registration;
        }
    }

    public bool UnregisterTool(string toolId)
    {
        lock (_lock)
        {
            if (_tools.TryGetValue(toolId, out var registration))
            {
                _tools.Remove(toolId);
                ToolUnregistered?.Invoke(this, new ToolUnregisteredEventArgs(toolId, registration));
                return true;
            }
            return false;
        }
    }

    public ToolRegistration? GetTool(string toolId)
    {
        lock (_lock)
        {
            return _tools.TryGetValue(toolId, out var registration) ? registration : null;
        }
    }

    public IReadOnlyList<ToolRegistration> GetTools(
        ToolCategory? category = null,
        ToolCapability? capabilities = null,
        IEnumerable<string>? tags = null,
        bool enabledOnly = true)
    {
        lock (_lock)
        {
            var query = _tools.Values.AsEnumerable();

            if (enabledOnly)
            {
                query = query.Where(t => t.IsEnabled);
            }

            if (category.HasValue)
            {
                query = query.Where(t => t.Metadata.Category == category.Value);
            }

            if (capabilities.HasValue)
            {
                query = query.Where(t => (t.Metadata.RequiredCapabilities & capabilities.Value) == capabilities.Value);
            }

            if (tags != null && tags.Any())
            {
                var tagSet = new HashSet<string>(tags, StringComparer.OrdinalIgnoreCase);
                query = query.Where(t => t.Metadata.Tags.Any(tag => tagSet.Contains(tag)));
            }

            return query.ToList();
        }
    }

    public IReadOnlyList<ToolRegistration> SearchTools(string searchTerm, bool enabledOnly = true)
    {
        lock (_lock)
        {
            var query = _tools.Values.AsEnumerable();

            if (enabledOnly)
            {
                query = query.Where(t => t.IsEnabled);
            }

            if (!string.IsNullOrWhiteSpace(searchTerm))
            {
                query = query.Where(t =>
                    t.Metadata.Name.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ||
                    t.Metadata.Description.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ||
                    t.Metadata.Tags.Any(tag => tag.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)));
            }

            return query.ToList();
        }
    }

    public ITool? CreateTool(string toolId, IServiceProvider serviceProvider)
    {
        lock (_lock)
        {
            if (!_tools.TryGetValue(toolId, out var registration))
            {
                return null;
            }

            if (!registration.IsEnabled)
            {
                return null;
            }

            if (registration.Factory != null)
            {
                return registration.Factory(serviceProvider);
            }

            if (registration.ToolType != null && registration.ToolType != typeof(object))
            {
                return (ITool)ActivatorUtilities.CreateInstance(serviceProvider, registration.ToolType);
            }

            return null;
        }
    }

    public bool SetToolEnabled(string toolId, bool enabled)
    {
        lock (_lock)
        {
            if (_tools.TryGetValue(toolId, out var registration))
            {
                registration.IsEnabled = enabled;
                return true;
            }
            return false;
        }
    }

    public bool UpdateToolConfiguration(string toolId, Dictionary<string, object?> configuration)
    {
        lock (_lock)
        {
            if (_tools.TryGetValue(toolId, out var registration))
            {
                registration.Configuration = configuration;
                return true;
            }
            return false;
        }
    }

    public ToolRegistryStatistics GetStatistics()
    {
        lock (_lock)
        {
            var tools = _tools.Values.ToList();
            
            return new ToolRegistryStatistics
            {
                TotalTools = tools.Count,
                EnabledTools = tools.Count(t => t.IsEnabled),
                DisabledTools = tools.Count(t => !t.IsEnabled),
                ByCategory = tools.GroupBy(t => t.Metadata.Category)
                    .ToDictionary(g => g.Key, g => g.Count()),
                BySource = tools.GroupBy(t => t.Source)
                    .ToDictionary(g => g.Key, g => g.Count()),
                ByCapabilities = Enum.GetValues<ToolCapability>()
                    .Where(cap => cap != ToolCapability.None)
                    .ToDictionary(
                        cap => cap,
                        cap => tools.Count(t => (t.Metadata.RequiredCapabilities & cap) == cap)),
                GeneratedAt = DateTimeOffset.UtcNow
            };
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _tools.Clear();
        }
    }
}