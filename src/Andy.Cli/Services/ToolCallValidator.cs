using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Andy.Tools.Core;

namespace Andy.Cli.Services;

/// <summary>
/// Validation result for a tool call
/// </summary>
public class ValidationResult
{
    public bool IsValid { get; set; }
    public List<ValidationError> Errors { get; set; } = new();
    public List<ValidationWarning> Warnings { get; set; } = new();
    public ModelToolCall? RepairedCall { get; set; }
    
    public bool HasErrors => Errors.Any();
    public bool HasWarnings => Warnings.Any();
}

/// <summary>
/// Validation error details
/// </summary>
public class ValidationError
{
    public string ParameterName { get; set; } = "";
    public string Message { get; set; } = "";
    public ValidationErrorType Type { get; set; }
}

/// <summary>
/// Validation warning details
/// </summary>
public class ValidationWarning
{
    public string ParameterName { get; set; } = "";
    public string Message { get; set; } = "";
    public ValidationWarningType Type { get; set; }
}

public enum ValidationErrorType
{
    MissingRequired,
    InvalidType,
    InvalidValue,
    UnknownParameter,
    InvalidFormat
}

public enum ValidationWarningType
{
    DeprecatedParameter,
    UnusualValue,
    TypeCoerced,
    DefaultUsed
}

/// <summary>
/// Validates and repairs tool calls against registered tool schemas
/// </summary>
public interface IToolCallValidator
{
    /// <summary>
    /// Validate a tool call against its metadata
    /// </summary>
    ValidationResult Validate(ModelToolCall call, ToolMetadata metadata);
    
    /// <summary>
    /// Attempt to repair a tool call's parameters
    /// </summary>
    ModelToolCall RepairParameters(ModelToolCall call, ToolMetadata metadata);
    
    /// <summary>
    /// Check if a tool exists in the registry
    /// </summary>
    bool ToolExists(string toolId);
}

public class ToolCallValidator : IToolCallValidator
{
    private readonly IToolRegistry _toolRegistry;
    private readonly ILogger<ToolCallValidator>? _logger;
    
    public ToolCallValidator(
        IToolRegistry toolRegistry,
        ILogger<ToolCallValidator>? logger = null)
    {
        _toolRegistry = toolRegistry;
        _logger = logger;
    }
    
    public ValidationResult Validate(ModelToolCall call, ToolMetadata metadata)
    {
        var result = new ValidationResult { IsValid = true };
        
        if (call == null || metadata == null)
        {
            result.IsValid = false;
            result.Errors.Add(new ValidationError
            {
                Message = "Tool call or metadata is null",
                Type = ValidationErrorType.InvalidValue
            });
            return result;
        }
        
        // Validate required parameters
        foreach (var param in metadata.Parameters.Where(p => p.Required))
        {
            if (!call.Parameters.ContainsKey(param.Name) || 
                call.Parameters[param.Name] == null)
            {
                result.IsValid = false;
                result.Errors.Add(new ValidationError
                {
                    ParameterName = param.Name,
                    Message = $"Required parameter '{param.Name}' is missing",
                    Type = ValidationErrorType.MissingRequired
                });
            }
        }
        
        // Validate parameter types and values
        foreach (var kvp in call.Parameters)
        {
            var paramName = kvp.Key;
            var paramValue = kvp.Value;
            
            // Skip internal parameters (like _callId)
            if (paramName.StartsWith("_"))
                continue;
            
            var paramMetadata = metadata.Parameters.FirstOrDefault(p => 
                p.Name.Equals(paramName, StringComparison.OrdinalIgnoreCase));
            
            if (paramMetadata == null)
            {
                // Check for similar parameter names (typos)
                var similarParam = FindSimilarParameter(paramName, metadata.Parameters);
                if (similarParam != null)
                {
                    result.Warnings.Add(new ValidationWarning
                    {
                        ParameterName = paramName,
                        Message = $"Unknown parameter '{paramName}'. Did you mean '{similarParam.Name}'?",
                        Type = ValidationWarningType.UnusualValue
                    });
                }
                else
                {
                    result.Errors.Add(new ValidationError
                    {
                        ParameterName = paramName,
                        Message = $"Unknown parameter '{paramName}'",
                        Type = ValidationErrorType.UnknownParameter
                    });
                    result.IsValid = false;
                }
                continue;
            }
            
            // Validate type
            if (!IsValidType(paramValue, paramMetadata.Type))
            {
                // Try type coercion
                var coercedValue = TryCoerceType(paramValue, paramMetadata.Type);
                if (coercedValue != null)
                {
                    result.Warnings.Add(new ValidationWarning
                    {
                        ParameterName = paramName,
                        Message = $"Parameter '{paramName}' type was coerced from {paramValue?.GetType().Name} to {paramMetadata.Type}",
                        Type = ValidationWarningType.TypeCoerced
                    });
                }
                else
                {
                    result.Errors.Add(new ValidationError
                    {
                        ParameterName = paramName,
                        Message = $"Parameter '{paramName}' has invalid type. Expected {paramMetadata.Type}, got {paramValue?.GetType().Name}",
                        Type = ValidationErrorType.InvalidType
                    });
                    result.IsValid = false;
                }
            }
            
            // Validate constraints using ToolParameter properties
            var constraintErrors = ValidateToolParameter(paramValue, paramMetadata, paramName);
            if (constraintErrors.Any())
            {
                result.Errors.AddRange(constraintErrors);
                result.IsValid = false;
            }
        }
        
        // If validation failed but we can repair, create repaired version
        if (!result.IsValid)
        {
            result.RepairedCall = RepairParameters(call, metadata);
        }
        
        _logger?.LogDebug("Validation result for {ToolId}: Valid={IsValid}, Errors={ErrorCount}, Warnings={WarningCount}",
            call.ToolId, result.IsValid, result.Errors.Count, result.Warnings.Count);
        
        return result;
    }
    
    public ModelToolCall RepairParameters(ModelToolCall call, ToolMetadata metadata)
    {
        var repaired = new ModelToolCall
        {
            ToolId = call.ToolId,
            Parameters = new Dictionary<string, object?>()
        };
        
        // Copy internal parameters
        foreach (var kvp in call.Parameters.Where(p => p.Key.StartsWith("_")))
        {
            repaired.Parameters[kvp.Key] = kvp.Value;
        }
        
        // Process each parameter in metadata
        foreach (var paramMeta in metadata.Parameters)
        {
            object? value = null;
            
            // Try to find parameter (case-insensitive)
            var matchingKey = call.Parameters.Keys.FirstOrDefault(k => 
                k.Equals(paramMeta.Name, StringComparison.OrdinalIgnoreCase));
            
            if (matchingKey != null)
            {
                value = call.Parameters[matchingKey];
                
                // Try type coercion if needed
                if (!IsValidType(value, paramMeta.Type))
                {
                    value = TryCoerceType(value, paramMeta.Type);
                }
            }
            else if (paramMeta.Required)
            {
                // Use default value from parameter or generate one
                value = paramMeta.DefaultValue ?? GetDefaultValue(paramMeta.Type);
                _logger?.LogDebug("Using default value for missing required parameter '{Name}'", paramMeta.Name);
            }
            
            if (value != null || paramMeta.Required)
            {
                repaired.Parameters[paramMeta.Name] = value;
            }
        }
        
        // Add any remaining parameters that might be valid but not in metadata
        foreach (var kvp in call.Parameters)
        {
            if (!kvp.Key.StartsWith("_") && !repaired.Parameters.ContainsKey(kvp.Key))
            {
                // Check if this might be a typo of an existing parameter
                var similarParam = FindSimilarParameter(kvp.Key, metadata.Parameters);
                if (similarParam != null && !repaired.Parameters.ContainsKey(similarParam.Name))
                {
                    // Use the correct parameter name
                    var value = kvp.Value;
                    if (!IsValidType(value, similarParam.Type))
                    {
                        value = TryCoerceType(value, similarParam.Type);
                    }
                    repaired.Parameters[similarParam.Name] = value;
                    _logger?.LogDebug("Corrected parameter name from '{Old}' to '{New}'", 
                        kvp.Key, similarParam.Name);
                }
            }
        }
        
        return repaired;
    }
    
    public bool ToolExists(string toolId)
    {
        return _toolRegistry.GetTool(toolId) != null;
    }
    
    private bool IsValidType(object? value, string expectedType)
    {
        if (value == null)
            return !expectedType.Equals("required", StringComparison.OrdinalIgnoreCase);
        
        return expectedType.ToLowerInvariant() switch
        {
            "string" => value is string,
            "int" or "integer" => value is int or long or short or byte,
            "long" => value is long or int,
            "double" or "float" or "decimal" => value is double or float or decimal or int or long,
            "bool" or "boolean" => value is bool,
            "array" or "list" => value is System.Collections.IEnumerable,
            "object" or "dictionary" => value is System.Collections.IDictionary or not null,
            _ => true // Unknown type, assume valid
        };
    }
    
    private object? TryCoerceType(object? value, string targetType)
    {
        if (value == null)
            return null;
        
        try
        {
            return targetType.ToLowerInvariant() switch
            {
                "string" => value.ToString(),
                "int" or "integer" => Convert.ToInt32(value),
                "long" => Convert.ToInt64(value),
                "double" => Convert.ToDouble(value),
                "float" => Convert.ToSingle(value),
                "decimal" => Convert.ToDecimal(value),
                "bool" or "boolean" => ParseBoolean(value),
                _ => value
            };
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to coerce value '{Value}' to type '{Type}'", value, targetType);
            return null;
        }
    }
    
    private bool ParseBoolean(object value)
    {
        if (value is bool b)
            return b;
        
        var str = value.ToString()?.ToLowerInvariant();
        return str == "true" || str == "yes" || str == "1" || str == "on";
    }
    
    private object? GetDefaultValue(string type)
    {
        return type.ToLowerInvariant() switch
        {
            "string" => "",
            "int" or "integer" or "long" => 0,
            "double" or "float" or "decimal" => 0.0,
            "bool" or "boolean" => false,
            "array" or "list" => new List<object>(),
            "object" or "dictionary" => new Dictionary<string, object?>(),
            _ => null
        };
    }
    
    private ToolParameter? FindSimilarParameter(string name, IList<ToolParameter> parameters)
    {
        // Simple similarity check - could be enhanced with Levenshtein distance
        return parameters.FirstOrDefault(p => 
            p.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith(p.Name, StringComparison.OrdinalIgnoreCase) ||
            p.Name.Replace("_", "").Equals(name.Replace("_", ""), StringComparison.OrdinalIgnoreCase));
    }
    
    private List<ValidationError> ValidateToolParameter(
        object? value, 
        ToolParameter parameter, 
        string paramName)
    {
        var errors = new List<ValidationError>();
        
        // Validate numeric constraints
        if (parameter.MinValue.HasValue && value is IComparable minComparable && 
            minComparable.CompareTo(parameter.MinValue.Value) < 0)
        {
            errors.Add(new ValidationError
            {
                ParameterName = paramName,
                Message = $"Value {value} is less than minimum {parameter.MinValue}",
                Type = ValidationErrorType.InvalidValue
            });
        }
        
        if (parameter.MaxValue.HasValue && value is IComparable maxComparable && 
            maxComparable.CompareTo(parameter.MaxValue.Value) > 0)
        {
            errors.Add(new ValidationError
            {
                ParameterName = paramName,
                Message = $"Value {value} is greater than maximum {parameter.MaxValue}",
                Type = ValidationErrorType.InvalidValue
            });
        }
        
        // Validate string length constraints
        if (value is string str)
        {
            if (parameter.MinLength.HasValue && str.Length < parameter.MinLength.Value)
            {
                errors.Add(new ValidationError
                {
                    ParameterName = paramName,
                    Message = $"String length {str.Length} is less than minimum {parameter.MinLength}",
                    Type = ValidationErrorType.InvalidValue
                });
            }
            
            if (parameter.MaxLength.HasValue && str.Length > parameter.MaxLength.Value)
            {
                errors.Add(new ValidationError
                {
                    ParameterName = paramName,
                    Message = $"String length {str.Length} is greater than maximum {parameter.MaxLength}",
                    Type = ValidationErrorType.InvalidValue
                });
            }
            
            // Validate pattern
            if (!string.IsNullOrEmpty(parameter.Pattern))
            {
                var regex = new System.Text.RegularExpressions.Regex(parameter.Pattern);
                if (!regex.IsMatch(str))
                {
                    errors.Add(new ValidationError
                    {
                        ParameterName = paramName,
                        Message = $"Value '{str}' does not match pattern '{parameter.Pattern}'",
                        Type = ValidationErrorType.InvalidFormat
                    });
                }
            }
        }
        
        // Validate allowed values
        if (parameter.AllowedValues?.Any() == true && value != null && !parameter.AllowedValues.Contains(value))
        {
            errors.Add(new ValidationError
            {
                ParameterName = paramName,
                Message = $"Value '{value}' is not in allowed values: {string.Join(", ", parameter.AllowedValues)}",
                Type = ValidationErrorType.InvalidValue
            });
        }
        
        return errors;
    }
}