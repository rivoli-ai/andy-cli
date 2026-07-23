using System;
using System.Collections.Generic;
using System.Linq;
using Andy.Cli.Widgets;
using Andy.Permissions.Model;
using DL = Andy.Tui.DisplayList;
using L = Andy.Tui.Layout;
using Xunit;

namespace Andy.Cli.Tests.Widgets;

/// <summary>
/// Tests for the inline approval prompt (issue #222): permission requests render in the prompt
/// area instead of a modal overlay. The widget must keep the old dialog's keyboard semantics
/// (Left/Right/Tab select, Enter/Space confirm, Esc/D/N deny, Deny preselected), suspend text
/// input by swallowing every other key, grow to fit the request body up to a cap (scrolling
/// beyond it), and stay inside the rect it is given so the transcript above is never obscured.
/// </summary>
public class InlineApprovalPromptTests
{
    private static PermissionRequest Request(string summary, params string[] commands)
    {
        var resources = commands
            .Select(c => new EvaluatedResource(
                new ResourceAccess(ResourceKind.Command, c), PermissionOutcome.Ask, null, true))
            .ToArray();
        return new PermissionRequest(
            "execute_command", "Execute Command", summary,
            new PermissionEvaluation(PermissionOutcome.Ask, resources));
    }

    private static ConsoleKeyInfo Key(ConsoleKey key, char ch = '\0') =>
        new(ch, key, shift: false, alt: false, control: false);

    private static List<DL.TextRun> Render(InlineApprovalPrompt widget, int width = 80, int height = 10, int x = 2, int y = 20)
    {
        var b = new DL.DisplayListBuilder();
        widget.Render(new L.Rect(x, y, width, height), new DL.DisplayListBuilder().Build(), b);
        return b.Build().Ops.OfType<DL.TextRun>().Where(r => !string.IsNullOrWhiteSpace(r.Content)).ToList();
    }

    [Fact]
    public void Begin_activates_with_deny_preselected()
    {
        var widget = new InlineApprovalPrompt();
        Assert.False(widget.IsActive);

        widget.Begin(Request("Run a shell command", "git status"));

        Assert.True(widget.IsActive);
        Assert.Equal(2, widget.SelectedIndex); // Deny is the safe default
        Assert.Equal(0, widget.ScrollOffset);
    }

    [Fact]
    public void Right_and_tab_cycle_selection_forward_with_wrap()
    {
        var widget = new InlineApprovalPrompt();
        widget.Begin(Request("Run a shell command", "git status"));

        Assert.Null(widget.HandleKey(Key(ConsoleKey.RightArrow)));
        Assert.Equal(0, widget.SelectedIndex); // wrapped from Deny(2) to Allow once(0)
        Assert.Null(widget.HandleKey(Key(ConsoleKey.Tab)));
        Assert.Equal(1, widget.SelectedIndex);
        Assert.Null(widget.HandleKey(Key(ConsoleKey.RightArrow)));
        Assert.Equal(2, widget.SelectedIndex);
    }

    [Fact]
    public void Left_cycles_selection_backward_with_wrap()
    {
        var widget = new InlineApprovalPrompt();
        widget.Begin(Request("Run a shell command", "git status"));

        Assert.Null(widget.HandleKey(Key(ConsoleKey.LeftArrow)));
        Assert.Equal(1, widget.SelectedIndex);
        Assert.Null(widget.HandleKey(Key(ConsoleKey.LeftArrow)));
        Assert.Equal(0, widget.SelectedIndex);
        Assert.Null(widget.HandleKey(Key(ConsoleKey.LeftArrow)));
        Assert.Equal(2, widget.SelectedIndex); // wraps around
    }

    [Theory]
    [InlineData(0, true, PersistScope.Once)]
    [InlineData(1, true, PersistScope.Session)]
    [InlineData(2, false, PersistScope.Once)]
    public void Enter_confirms_highlighted_choice_and_deactivates(int steps, bool allowed, PersistScope persist)
    {
        var widget = new InlineApprovalPrompt();
        widget.Begin(Request("Run a shell command", "git status"));
        for (int i = 0; i < steps + 1; i++) // +1 because Right wraps Deny -> Allow once first
        {
            widget.HandleKey(Key(ConsoleKey.RightArrow));
        }

        var decision = widget.HandleKey(Key(ConsoleKey.Enter));

        Assert.NotNull(decision);
        Assert.Equal(allowed, decision!.Allowed);
        Assert.Equal(persist, decision.Persist);
        Assert.False(widget.IsActive);
    }

    [Fact]
    public void Space_confirms_like_enter()
    {
        var widget = new InlineApprovalPrompt();
        widget.Begin(Request("Run a shell command", "git status"));
        widget.HandleKey(Key(ConsoleKey.RightArrow)); // -> Allow once

        var decision = widget.HandleKey(Key(ConsoleKey.Spacebar, ' '));

        Assert.NotNull(decision);
        Assert.True(decision!.Allowed);
        Assert.Equal(PersistScope.Once, decision.Persist);
        Assert.False(widget.IsActive);
    }

    [Theory]
    [InlineData(ConsoleKey.Escape, '\0')]
    [InlineData(ConsoleKey.D, 'd')]
    [InlineData(ConsoleKey.N, 'n')]
    public void Escape_d_and_n_deny_immediately_regardless_of_selection(ConsoleKey key, char ch)
    {
        var widget = new InlineApprovalPrompt();
        widget.Begin(Request("Run a shell command", "git status"));
        widget.HandleKey(Key(ConsoleKey.RightArrow)); // highlight Allow once

        var decision = widget.HandleKey(Key(key, ch));

        Assert.NotNull(decision);
        Assert.False(decision!.Allowed);
        Assert.False(widget.IsActive);
    }

    [Fact]
    public void Typing_keys_are_swallowed_without_deciding()
    {
        var widget = new InlineApprovalPrompt();
        widget.Begin(Request("Run a shell command", "git status"));

        Assert.Null(widget.HandleKey(Key(ConsoleKey.A, 'a')));
        Assert.Null(widget.HandleKey(Key(ConsoleKey.Y, 'y')));
        Assert.Null(widget.HandleKey(Key(ConsoleKey.Backspace, '\b')));
        Assert.Null(widget.HandleKey(Key(ConsoleKey.F5)));

        Assert.True(widget.IsActive);
        Assert.Equal(2, widget.SelectedIndex);
    }

    [Fact]
    public void PageUp_and_PageDown_are_not_consumed_as_scroll_or_decision()
    {
        // The host routes PageUp/PageDown to the transcript so it stays scrollable while an
        // approval is pending; the widget itself must treat them as inert.
        var widget = new InlineApprovalPrompt();
        widget.Begin(Request("Run a shell command", "git status"));

        Assert.Null(widget.HandleKey(Key(ConsoleKey.PageUp)));
        Assert.Null(widget.HandleKey(Key(ConsoleKey.PageDown)));
        Assert.True(widget.IsActive);
        Assert.Equal(0, widget.ScrollOffset);
    }

    [Fact]
    public void Desired_height_is_body_plus_chrome_when_it_fits()
    {
        var request = Request("Run a shell command", "git status");
        var widget = new InlineApprovalPrompt();
        widget.Begin(request);

        int width = 80;
        int bodyLines = PermissionDialogContent.BuildBodyLines(request, InlineApprovalPrompt.InnerWidth(width)).Count;

        Assert.Equal(bodyLines + InlineApprovalPrompt.ChromeRows, widget.GetDesiredHeight(width, maxHeight: 30));
    }

    [Fact]
    public void Desired_height_is_capped_at_max_height()
    {
        var commands = Enumerable.Range(0, 40).Select(i => $"echo line-{i}").ToArray();
        var widget = new InlineApprovalPrompt();
        widget.Begin(Request("Run many commands", commands));

        Assert.Equal(12, widget.GetDesiredHeight(width: 80, maxHeight: 12));
    }

    [Fact]
    public void Desired_height_is_zero_when_inactive()
    {
        var widget = new InlineApprovalPrompt();

        Assert.Equal(0, widget.GetDesiredHeight(width: 80, maxHeight: 12));
    }

    [Fact]
    public void Render_shows_title_tool_options_and_risk_colored_commands()
    {
        var theme = Andy.Cli.Themes.Theme.Current;
        var widget = new InlineApprovalPrompt();
        widget.Begin(Request("Run a shell command", "git status", "rm -rf /tmp/build"));

        var runs = Render(widget, width: 80, height: widget.GetDesiredHeight(80, 20));

        Assert.Contains(runs, r => r.Content.Contains("Permission required"));
        Assert.Contains(runs, r => r.Content.Contains("Tool: Execute Command"));
        Assert.Contains(runs, r => r.Content.Contains("$ git status") && r.Fg.Equals(theme.Info));
        Assert.Contains(runs, r => r.Content.Contains("$ rm -rf /tmp/build") && r.Fg.Equals(theme.Error));
        Assert.Contains(runs, r => r.Content.Contains("Allow once"));
        Assert.Contains(runs, r => r.Content.Contains("Allow (session)"));
        Assert.Contains(runs, r => r.Content.Contains("Deny"));
        Assert.Contains(runs, r => r.Content.Contains("Esc deny"));
    }

    [Fact]
    public void Render_stays_within_the_given_rect()
    {
        var commands = Enumerable.Range(0, 30).Select(i => $"echo line-{i}").ToArray();
        var widget = new InlineApprovalPrompt();
        widget.Begin(Request("Run many commands", commands));

        int x = 2, y = 20, w = 60, h = 10;
        var runs = Render(widget, w, h, x, y);

        Assert.NotEmpty(runs);
        Assert.All(runs, r => Assert.InRange(r.Y, y, y + h - 1));
        Assert.All(runs, r => Assert.InRange(r.X, x, x + w - 1));
    }

    [Fact]
    public void Body_scroll_clamps_between_zero_and_overflow()
    {
        var commands = Enumerable.Range(0, 20).Select(i => $"echo line-{i}").ToArray();
        var request = Request("Run many commands", commands);
        var widget = new InlineApprovalPrompt();
        widget.Begin(request);

        int width = 80, height = 10;
        int bodyLines = PermissionDialogContent.BuildBodyLines(request, InlineApprovalPrompt.InnerWidth(width)).Count;
        int visible = height - InlineApprovalPrompt.ChromeRows;
        int maxScroll = bodyLines - visible;
        Assert.True(maxScroll > 0);

        Render(widget, width, height); // render establishes the scroll bounds

        for (int i = 0; i < bodyLines + 10; i++)
        {
            widget.HandleKey(Key(ConsoleKey.DownArrow));
        }
        Assert.Equal(maxScroll, widget.ScrollOffset);

        for (int i = 0; i < bodyLines + 10; i++)
        {
            widget.HandleKey(Key(ConsoleKey.UpArrow));
        }
        Assert.Equal(0, widget.ScrollOffset);
    }

    [Fact]
    public void Scrolled_render_shows_later_body_lines_and_position_indicator()
    {
        var commands = Enumerable.Range(0, 20).Select(i => $"echo line-{i}").ToArray();
        var widget = new InlineApprovalPrompt();
        widget.Begin(Request("Run many commands", commands));

        Render(widget, 80, 10);
        widget.HandleKey(Key(ConsoleKey.DownArrow));
        widget.HandleKey(Key(ConsoleKey.DownArrow));
        var runs = Render(widget, 80, 10);

        Assert.Contains(runs, r => r.Content.StartsWith("[3-", StringComparison.Ordinal));
        Assert.Contains(runs, r => r.Content.Contains("Up/Down scroll"));
    }

    [Fact]
    public void Dismiss_clears_the_pending_request_without_a_decision()
    {
        var widget = new InlineApprovalPrompt();
        widget.Begin(Request("Run a shell command", "git status"));

        widget.Dismiss();

        Assert.False(widget.IsActive);
        Assert.Equal(0, widget.GetDesiredHeight(80, 12));
        Assert.Empty(Render(widget));
    }

    [Fact]
    public void Inline_decision_still_produces_the_transcript_audit_record()
    {
        // The decision returned by the inline prompt must feed the same PermissionDecisionItem
        // audit trail as the old dialog (issue #224).
        var request = Request("Run a shell command", "git status");
        var widget = new InlineApprovalPrompt();
        widget.Begin(request);

        var decision = widget.HandleKey(Key(ConsoleKey.Enter)); // Deny preselected

        Assert.NotNull(decision);
        var item = new PermissionDecisionItem(request, decision!);
        Assert.Equal("[denied] Execute Command", item.HeaderText);
    }
}
