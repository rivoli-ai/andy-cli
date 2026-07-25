using System;
using System.Collections.Generic;
using System.Linq;
using Andy.Cli.Services;

namespace Andy.Cli.Widgets.Tools
{
    /// <summary>
    /// Resolves the presenter for a tool id (issue #249).
    ///
    /// This replaces the previous arrangement, where one widget carried every tool's display
    /// logic as a chain of <c>_toolName.Contains(...)</c> branches and the tool-specific parts
    /// were spread across the executor, the tracker and the view. A tool family now owns one
    /// presenter, and adding a family means adding a class rather than editing a shared widget.
    ///
    /// Lookup is by the NORMALIZED tool id, so the execution counter the UI appends
    /// ("read_file_1") never has to be handled by individual presenters.
    /// </summary>
    public sealed class ToolPresenterRegistry
    {
        private readonly List<IToolPresenter> _presenters;
        private readonly IToolPresenter _fallback;

        /// <summary>Create a registry over an explicit presenter list.</summary>
        /// <param name="presenters">Checked in order; the first that claims the tool wins.</param>
        /// <param name="fallback">Used when nothing claims the tool.</param>
        public ToolPresenterRegistry(IEnumerable<IToolPresenter> presenters, IToolPresenter? fallback = null)
        {
            _presenters = presenters?.ToList() ?? throw new ArgumentNullException(nameof(presenters));
            _fallback = fallback ?? new GenericToolPresenter();
        }

        /// <summary>
        /// The registry the feed uses. Ordered most specific first; the generic presenter is not
        /// in the list because it is the fallback and would claim everything.
        /// </summary>
        public static ToolPresenterRegistry Default { get; } = new(new IToolPresenter[]
        {
            new ShellToolPresenter(),
            new ReadFileToolPresenter(),
            new SearchTextToolPresenter(),
            new ListDirectoryToolPresenter(),
            new FileMutationToolPresenter(),
            new WriteFileToolPresenter(),
            new ReplaceTextToolPresenter(),
        });

        /// <summary>Find the presenter for a tool id, never returning null.</summary>
        public IToolPresenter Resolve(string? toolName) => TryResolve(toolName) ?? _fallback;

        /// <summary>
        /// Find the DEDICATED presenter for a tool id, or null when only the fallback would apply.
        ///
        /// The feed uses this to migrate one tool family at a time: a tool with a presenter renders
        /// through the new path, and everything else keeps its existing rendering until its own
        /// presenter lands. Without that split, adopting the new item would regress every tool
        /// whose presenter has not been written yet.
        /// </summary>
        public IToolPresenter? TryResolve(string? toolName)
        {
            var normalized = ToolCallSummarizer.NormalizeToolName(toolName);
            foreach (var presenter in _presenters)
            {
                if (presenter.CanPresent(normalized)) return presenter;
            }
            return null;
        }
    }
}
