using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Genie.App.ViewModels;
using Xunit;

namespace Genie.App.Tests;

/// <summary>
/// Command-bar recall history, and the Down-arrow clear (#262).
///
/// <para>
/// Genie 4's <c>ComponentTextBox.KeyDownHistory</c> clears the input when the
/// user is not mid-recall — its own source marks the branch
/// <c>else // On Request from Fatal (Down Clears)</c>. Genie 5 returned early
/// instead, so Down did nothing on a freshly typed line and nothing else cleared
/// the box either. These tests pin both halves: the clear, and the history walk
/// that must keep working around it.
/// </para>
/// </summary>
public class CommandHistoryTests
{
    /// <summary>A view model whose send is a no-op, so Submit only exercises the
    /// history bookkeeping. Returns the sent lines for the masking check.</summary>
    private static (CommandViewModel Vm, List<string> Sent) NewVm()
    {
        var sent = new List<string>();
        var vm = new CommandViewModel(line => { sent.Add(line); return Task.CompletedTask; });
        return (vm, sent);
    }

    private static void Submit(CommandViewModel vm, string line)
    {
        vm.CommandText = line;
        vm.SubmitCommand.Execute().Subscribe();
    }

    // ── #262: Down clears when not recalling ────────────────────────────────

    [Fact]
    public void Down_clears_a_freshly_typed_line()
    {
        var (vm, _) = NewVm();
        Submit(vm, "look");
        vm.CommandText = "half-typed thought";

        vm.HistoryDown();

        Assert.Equal("", vm.CommandText);
    }

    [Fact]
    public void Down_clears_even_before_anything_has_been_sent()
    {
        // Deliberate divergence from Genie 4, which guards the whole method on a
        // non-empty history. Honouring that guard would make the fix look broken
        // to anyone testing it on a fresh session.
        var (vm, _) = NewVm();
        vm.CommandText = "first thing I typed";

        vm.HistoryDown();

        Assert.Equal("", vm.CommandText);
    }

    [Fact]
    public void Down_on_an_already_empty_box_is_harmless()
    {
        var (vm, _) = NewVm();

        vm.HistoryDown();
        vm.HistoryDown();

        Assert.Equal("", vm.CommandText);
    }

    // ── The history walk still works around the clear ───────────────────────

    [Fact]
    public void Up_walks_backwards_through_history_newest_first()
    {
        var (vm, _) = NewVm();
        Submit(vm, "one");
        Submit(vm, "two");
        Submit(vm, "three");

        vm.HistoryUp();
        Assert.Equal("three", vm.CommandText);
        vm.HistoryUp();
        Assert.Equal("two", vm.CommandText);
        vm.HistoryUp();
        Assert.Equal("one", vm.CommandText);
    }

    [Fact]
    public void Up_stops_at_the_oldest_entry_rather_than_wrapping()
    {
        var (vm, _) = NewVm();
        Submit(vm, "only");

        vm.HistoryUp();
        vm.HistoryUp();
        vm.HistoryUp();

        Assert.Equal("only", vm.CommandText);
    }

    [Fact]
    public void Up_then_Down_walks_back_forward_through_history()
    {
        // The regression this fix could plausibly cause: Down must still walk the
        // history when the user IS mid-recall, not clear out from under them.
        var (vm, _) = NewVm();
        Submit(vm, "one");
        Submit(vm, "two");
        Submit(vm, "three");

        vm.HistoryUp();                       // three
        vm.HistoryUp();                       // two
        vm.HistoryUp();                       // one

        vm.HistoryDown();
        Assert.Equal("two", vm.CommandText);
        vm.HistoryDown();
        Assert.Equal("three", vm.CommandText);
    }

    [Fact]
    public void Walking_forward_past_the_newest_entry_ends_on_an_empty_box()
    {
        var (vm, _) = NewVm();
        Submit(vm, "one");
        Submit(vm, "two");

        vm.HistoryUp();      // two
        vm.HistoryUp();      // one
        vm.HistoryDown();    // two
        vm.HistoryDown();    // past the end

        Assert.Equal("", vm.CommandText);
    }

    [Fact]
    public void Recall_restarts_from_the_newest_entry_after_walking_off_the_end()
    {
        // Walking past the end resets the cursor, so the next Up must start over
        // at the newest entry rather than resuming mid-list.
        var (vm, _) = NewVm();
        Submit(vm, "one");
        Submit(vm, "two");

        vm.HistoryUp();      // two
        vm.HistoryDown();    // off the end -> ""
        vm.HistoryUp();

        Assert.Equal("two", vm.CommandText);
    }

    [Fact]
    public void Submitting_clears_the_box_and_resets_the_recall_position()
    {
        var (vm, _) = NewVm();
        Submit(vm, "one");
        vm.HistoryUp();               // mid-recall on "one"

        Submit(vm, "two");

        Assert.Equal("", vm.CommandText);
        vm.HistoryUp();
        Assert.Equal("two", vm.CommandText);   // newest, not resumed mid-walk
    }

    [Fact]
    public void A_whitespace_only_line_is_never_sent_or_recorded()
    {
        // SubmitCommand's canExecute rejects whitespace, so nothing is sent and
        // nothing enters history. The box deliberately keeps its text — the user
        // gets their line back rather than having it eaten by a stray Enter.
        var (vm, sent) = NewVm();
        Submit(vm, "   ");

        Assert.Empty(sent);
        Assert.Equal("   ", vm.CommandText);

        vm.HistoryUp();                       // nothing to recall
        Assert.Equal("   ", vm.CommandText);
    }

    // ── Consecutive duplicates collapse in history ───────────────────────────

    [Fact]
    public void Immediate_duplicate_submissions_add_only_one_history_entry()
    {
        // "look" before the run of duplicates proves the count: with dedup, one
        // Up reaches "attack" and a second reaches "look" directly. Without
        // dedup it would take three Ups to get past the repeated "attack"s.
        var (vm, sent) = NewVm();
        Submit(vm, "look");
        Submit(vm, "attack");
        Submit(vm, "attack");
        Submit(vm, "attack");

        Assert.Equal(new[] { "look", "attack", "attack", "attack" }, sent);

        vm.HistoryUp();
        Assert.Equal("attack", vm.CommandText);
        vm.HistoryUp();
        Assert.Equal("look", vm.CommandText);
        vm.HistoryUp();
        Assert.Equal("look", vm.CommandText);   // stops at the oldest entry
    }

    [Fact]
    public void Non_consecutive_duplicates_are_all_recorded()
    {
        var (vm, _) = NewVm();
        Submit(vm, "attack");
        Submit(vm, "look");
        Submit(vm, "attack");

        vm.HistoryUp();
        Assert.Equal("attack", vm.CommandText);
        vm.HistoryUp();
        Assert.Equal("look", vm.CommandText);
        vm.HistoryUp();
        Assert.Equal("attack", vm.CommandText);
    }

    [Fact]
    public void Connect_passwords_are_masked_in_recall_but_sent_intact()
    {
        // Pre-existing behaviour worth a guard while we're in here: Up must not
        // surface a plaintext password, but the game still has to receive one.
        var (vm, sent) = NewVm();
        Submit(vm, "#connect myaccount hunter2 Renucci DR");

        vm.HistoryUp();

        Assert.DoesNotContain("hunter2", vm.CommandText);
        Assert.Contains("hunter2", sent[0]);
    }
}
