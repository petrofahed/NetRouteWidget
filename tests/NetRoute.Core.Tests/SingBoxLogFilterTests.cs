namespace NetRoute.Core.Tests;

public class SingBoxLogFilterTests
{
    const string Esc = "\u001b";

    [Theory]
    [InlineData("+0300 2026-10-02 16:50:47 INFO [123 3ms] dns: exchanged www.google.com. NOERROR 300")]
    [InlineData("+0300 2026-10-02 16:50:47 DEBUG [123 3ms] dns: cached x NOERROR")]
    [InlineData("+0300 2026-10-02 16:12:35 INFO [220090278 3ms] outbound/direct[phone]: outbound connection to 104.18.20.213:80")]
    [InlineData("")]
    public void Ordinary_lines_are_not_noteworthy(string line) => Assert.False(SingBoxLogFilter.IsNoteworthy(line));

    [Theory]
    [InlineData("+0300 2026-10-02 16:47:37 ERROR [123 5.0s] connection: open connection to 1.2.3.4:443 using outbound/direct[lan]: dial tcp: i/o timeout")]
    [InlineData("+0300 2026-10-02 16:47:37 WARN [123 5.0s] something odd")]
    [InlineData("FATAL[0000] decode config at stdin: unknown field")]
    public void Error_warn_and_fatal_levels_are_noteworthy(string line) => Assert.True(SingBoxLogFilter.IsNoteworthy(line));

    [Fact]
    public void Ansi_coloured_levels_are_noteworthy()
    {
        Assert.True(SingBoxLogFilter.IsNoteworthy($"+0300 2026-10-02 16:50:47 {Esc}[31mERROR{Esc}[0m [{Esc}[38;5;151m123{Esc}[0m 150ms] connection: open connection failed"));
        Assert.True(SingBoxLogFilter.IsNoteworthy($"+0300 2026-10-02 16:50:47 {Esc}[33mWARN{Esc}[0m [1 2ms] x"));
    }

    [Fact]
    public void Ansi_coloured_noerror_is_still_not_noteworthy()
    {
        Assert.False(SingBoxLogFilter.IsNoteworthy($"+0300 2026-10-02 16:50:47 {Esc}[36mINFO{Esc}[0m [1 2ms] dns: exchanged a.com. {Esc}[32mNOERROR{Esc}[0m 300"));
    }

    [Fact]
    public void Lowercase_level_words_do_not_count()
    {
        Assert.False(SingBoxLogFilter.IsNoteworthy("INFO [1 2ms] no error here, just a warning word"));
    }
}
