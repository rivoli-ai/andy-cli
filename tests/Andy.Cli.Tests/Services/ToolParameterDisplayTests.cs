using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using Andy.Cli.Widgets;

namespace Andy.Cli.Tests.Services
{
    public class ToolParameterDisplayTests
    {
        [Fact]
        public void RunningToolItem_Should_Display_Parameters_Not_Loading()
        {
            // Arrange
            var parameters = new Dictionary<string, object?>
            {
                { "operation", "now" },
                { "timezone", "UTC" }
            };

            var toolItem = new RunningToolItem("test_id", "datetime_tool", parameters);

            // Act - Use reflection to call the private method
            var getParameterDisplayMethod = typeof(RunningToolItem).GetMethod(
                "GetParameterDisplay",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            var display = (string)getParameterDisplayMethod?.Invoke(toolItem, null);

            // Assert
            Assert.NotNull(display);
            Assert.NotEqual("loading...", display);
            Assert.Contains("op:now", display);
            Assert.Contains("tz:UTC", display);
        }

        [Fact]
        public void RunningToolItem_Without_Parameters_Should_Show_Loading()
        {
            // Arrange
            var emptyParameters = new Dictionary<string, object?>();
            var toolItem = new RunningToolItem("test_id", "datetime_tool", emptyParameters);

            // Act - Use reflection to call the private method
            var getParameterDisplayMethod = typeof(RunningToolItem).GetMethod(
                "GetParameterDisplay",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            var display = (string)getParameterDisplayMethod?.Invoke(toolItem, null);

            // Assert
            Assert.NotNull(display);
            Assert.Equal("loading...", display);
        }

        [Fact]
        public void RunningToolItem_UpdateParameters_Should_Change_Display()
        {
            // Arrange
            var initialParameters = new Dictionary<string, object?>();
            var toolItem = new RunningToolItem("test_id", "datetime_tool", initialParameters);

            // Act - Update with real parameters
            var newParameters = new Dictionary<string, object?>
            {
                { "operation", "day_of_week" },
                { "format", "full" }
            };
            toolItem.UpdateParameters(newParameters);

            // Get display using reflection
            var getParameterDisplayMethod = typeof(RunningToolItem).GetMethod(
                "GetParameterDisplay",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            var display = (string)getParameterDisplayMethod?.Invoke(toolItem, null);

            // Assert
            Assert.NotNull(display);
            Assert.NotEqual("loading...", display);
            // For datetime_tool it should show special formatting
            Assert.Contains("op:day_of_week", display);
        }
    }
}