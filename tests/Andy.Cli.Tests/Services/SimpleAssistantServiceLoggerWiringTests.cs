using System;
using System.Collections.Generic;
using Andy.Cli.Services;
using Andy.Cli.Widgets;
using Andy.Model.Llm;
using Andy.Tools.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Andy.Cli.Tests.Services
{
    /// <summary>
    /// Regression: SimpleAssistantService used to receive a single ILogger&lt;SimpleAssistantService&gt;
    /// and cast it with `as ILogger&lt;SimpleAgent&gt;` / `as ILogger&lt;UiUpdatingToolExecutor&gt;`, which
    /// always yields null (unrelated generic types) — so engine- and tool-level logs went nowhere in
    /// the real app. It now takes an ILoggerFactory and creates correctly-typed loggers.
    /// </summary>
    public class SimpleAssistantServiceLoggerWiringTests
    {
        [Fact]
        public void Constructor_CreatesTypedLoggers_ForEngineAndToolExecutor()
        {
            var factory = new RecordingLoggerFactory();

            var registry = new Mock<IToolRegistry>();
            registry.Setup(r => r.GetTools(
                    It.IsAny<ToolCategory?>(), It.IsAny<ToolCapability?>(),
                    It.IsAny<IEnumerable<string>?>(), It.IsAny<bool>()))
                .Returns(new List<ToolRegistration>());

            using var service = new SimpleAssistantService(
                Mock.Of<ILlmProvider>(),
                registry.Object,
                Mock.Of<IToolExecutor>(),
                new FeedView(),
                modelName: "model-x",
                providerName: "provider-x",
                tokenCounter: null,
                loggerFactory: factory);

            // The factory must have been asked for properly-typed loggers — the whole point of the
            // fix. Previously these collaborators silently got null.
            Assert.Contains(factory.Categories, c => c.Contains("SimpleAgent"));
            Assert.Contains(factory.Categories, c => c.Contains("UiUpdatingToolExecutor"));
            Assert.Contains(factory.Categories, c => c.Contains("SimpleAssistantService"));
        }

        [Fact]
        public void Constructor_DoesNotThrow_WhenLoggerFactoryIsNull()
        {
            var registry = new Mock<IToolRegistry>();
            registry.Setup(r => r.GetTools(
                    It.IsAny<ToolCategory?>(), It.IsAny<ToolCapability?>(),
                    It.IsAny<IEnumerable<string>?>(), It.IsAny<bool>()))
                .Returns(new List<ToolRegistration>());

            // The factory is optional; null must remain safe (no logging, no crash).
            using var service = new SimpleAssistantService(
                Mock.Of<ILlmProvider>(),
                registry.Object,
                Mock.Of<IToolExecutor>(),
                new FeedView(),
                modelName: "m",
                providerName: "p",
                tokenCounter: null,
                loggerFactory: null);

            Assert.NotNull(service);
        }

        private sealed class RecordingLoggerFactory : ILoggerFactory
        {
            public List<string> Categories { get; } = new();

            public ILogger CreateLogger(string categoryName)
            {
                Categories.Add(categoryName);
                return NullLogger.Instance;
            }

            public void AddProvider(ILoggerProvider provider) { }
            public void Dispose() { }
        }
    }
}
