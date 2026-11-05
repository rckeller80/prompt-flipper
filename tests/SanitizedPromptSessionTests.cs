using Xunit;

namespace PromptFlipper.Tests;

public sealed class SanitizedPromptSessionTests
{
    private static readonly SanitizerRule Token = new("token", "secret");

    private static SanitizedPromptSession Create(string text)
    {
        var session = new SanitizedPromptSession();
        session.Load(text);
        Sanitize(session);
        return session;
    }

    private static void Sanitize(SanitizedPromptSession session)
    {
        Assert.True(Sanitizer.TrySanitize(session.PromptText, new[] { Token }, out var text, out var spans));
        Assert.True(session.ApplySanitized(text!, spans));
    }

    private static void Edit(SanitizedPromptSession session, int start, int removed, string inserted)
    {
        Assert.True(session.TryEdit(session.Display.Remove(start, removed).Insert(start, inserted), out _));
    }

    [Theory]
    [InlineData("{secret", false, 1, "")]
    [InlineData("secret}", false, 0, "{")]
    [InlineData("secret}", true, 1, "")]
    public void EqualCharactersAllowAnAlignmentOutsideTheSpan(string text, bool atEnd, int removed, string inserted)
    {
        var session = Create(text);
        Edit(session, atEnd ? session.Display.Length - 1 : 0, removed, inserted);
        var sourceStart = atEnd ? text.Length - 1 : 0;
        Assert.Equal(text.Remove(sourceStart, removed).Insert(sourceStart, inserted), session.PromptText);
        Assert.Single(session.Spans);
    }

    [Theory]
    [InlineData(2, 0, "x")]
    [InlineData(1, 1, "")]
    [InlineData(0, 3, "z")]
    public void RejectedEditsPreserveTextSpansAndReversalRange(int offset, int removed, string inserted)
    {
        var session = Create("a secret b");
        var before = session.Display;
        var span = Assert.Single(session.Spans);
        var after = before.Remove(span.Start + offset, removed).Insert(span.Start + offset, inserted);
        Assert.False(session.TryEdit(after, out var edit));
        Assert.Equal(before, session.Display);
        Assert.Equal(span, Assert.Single(session.Spans));
        Assert.Equal(before, after.Remove(edit.Start, edit.Inserted).Insert(edit.Start, before.Substring(edit.Start, edit.Removed)));
    }

    [Fact]
    public void SurroundingEditsShiftEveryLaterSpanAndRestoreOnlyRecordedValues()
    {
        var session = Create("a secret b secret c");
        Edit(session, 0, 2, "prefix ");
        Assert.Equal(7, session.Spans[0].Start);
        var secondStart = session.Spans[1].Start;
        Edit(session, session.Spans[0].End, 0, "!");
        Assert.Equal(secondStart + 1, session.Spans[1].Start);
        Edit(session, session.Display.Length, 0, " " + Token.Placeholder);
        Assert.Equal("prefix secret! b secret c " + Token.Placeholder, session.PromptText);
        Assert.True(session.Desanitize());
        Assert.Equal(session.PromptText, session.Display);
        Assert.False(session.IsSanitized);
        Assert.False(session.Desanitize());
    }

    [Theory]
    [InlineData("head\r\nsecret\r\ntail", "\r\n")]
    [InlineData("head\nsecret\ntail", "\n")]
    [InlineData("secret", "\r\n")]
    public void CoordinatesAndRestorationPreserveSourceLineEndings(string source, string newline)
    {
        var session = Create(source);
        var span = Assert.Single(session.Spans);
        Assert.Equal(session.Display.IndexOf(Token.Placeholder, StringComparison.Ordinal), span.Start);
        Assert.DoesNotContain('\r', session.Display);
        Assert.Equal(source, session.PromptText);
        Edit(session, session.Display.Length, 0, "\nmore");
        Assert.Equal(source + newline + "more", session.PromptText);
        session.Desanitize();
        Assert.Equal(source + newline + "more", session.PromptText);
    }

    [Fact]
    public void MixedSourceLineEndingsRoundTripUntilAnEdit()
    {
        var source = "secret\r\nx\ny\rz";
        var session = Create(source);
        Assert.Equal(source, session.PromptText);
        Assert.True(session.Desanitize());
        Assert.Equal(source, session.PromptText);
    }

    [Fact]
    public void MultilineOriginalAndMultipleSourceOffsetsRestoreExactly()
    {
        var session = new SanitizedPromptSession();
        var source = "a\r\nsecret\r\nvalue and secret\r\nvalue";
        session.Load(source);
        var rule = new SanitizerRule("block", "secret\r\nvalue");
        Assert.True(Sanitizer.TrySanitize(source, new[] { rule }, out var output, out var spans));
        Assert.True(session.ApplySanitized(output!, spans));
        Assert.Equal(2, session.Spans[0].Start);
        Assert.Equal(source, session.PromptText);
        Assert.Equal("secret\nvalue", session.Spans[0].Original);
        Assert.True(session.Desanitize());
        Assert.Equal(source, session.PromptText);
    }

    [Fact]
    public void SnapshotsRemainIndependentOfLaterEditsAndRequireMatchingPrompt()
    {
        var session = Create("secret tail");
        var view = session.CaptureView();
        var originalSpan = Assert.Single(view!.Spans);
        Edit(session, 0, 0, "prefix ");
        Assert.Equal(originalSpan, Assert.Single(view.Spans));
        Assert.False(session.RestoreView(view));
        session.Load(view.PromptText);
        Assert.True(session.RestoreView(view));
        Assert.Equal(view.Display, session.Display);
        Edit(session, 0, 0, "again ");
        Assert.Equal(originalSpan, Assert.Single(view.Spans));
        session.Load("another card");
        Assert.False(session.IsSanitized);
        Assert.Empty(session.Spans);
        Assert.Null(session.CaptureView());
        Assert.False(session.RestoreView(view));
        Assert.False(session.RestoreView(null));
    }

    [Fact]
    public void InvalidViewsLeaveTheCurrentSessionIntact()
    {
        var session = Create("secret");
        var before = session.Display;
        Assert.False(session.ApplySanitized("wrong", new[] { new SanitizedSpan(0, Token.Placeholder, "secret") }));
        Assert.False(session.ApplySanitized(before, new[] { new SanitizedSpan(-1, Token.Placeholder, "secret") }));
        Assert.False(session.ApplySanitized(before, new[] { new SanitizedSpan(0, Token.Placeholder, "secret"), new SanitizedSpan(0, Token.Placeholder, "secret") }));
        Assert.Equal(before, session.Display);
        Assert.Equal("secret", session.PromptText);
    }

    [Fact]
    public void RepeatedSanitizeUsesSurroundingEditsAndLoadEndsRestoration()
    {
        var session = Create("secret");
        Edit(session, session.Display.Length, 0, " secret");
        Sanitize(session);
        Assert.Equal(2, session.Spans.Count);
        Assert.Equal("secret secret", session.PromptText);
        session.Load(Token.Placeholder);
        Assert.False(session.IsSanitized);
        Assert.Equal(Token.Placeholder, session.PromptText);
        Assert.False(session.Desanitize());
        session.Load(null);
        Assert.Equal(string.Empty, session.PromptText);
    }
}
