using Enlist.ControlPlane.Contracts;

namespace Enlist.ControlPlane.Tests;

/// <summary>The cron dialect enList accepts, pinned where it is defined: five fields or six, '?' for '*', and a refusal that names what was wrong.</summary>
public sealed class CronExpressionsTests
{
    private static readonly DateTimeOffset Noon = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Five_and_six_field_forms_of_the_same_schedule_agree_on_the_next_occurrence()
    {
        var standard = CronExpressions.Parse("0 3 * * *").GetNextOccurrence(Noon, TimeZoneInfo.Utc);
        var withSeconds = CronExpressions.Parse("0 0 3 * * *").GetNextOccurrence(Noon, TimeZoneInfo.Utc);

        Assert.Equal(new DateTimeOffset(2026, 9, 10, 3, 0, 0, TimeSpan.Zero), standard);
        Assert.Equal(standard, withSeconds);
    }

    [Fact]
    public void A_Quartz_style_question_mark_is_read_as_a_star()
    {
        var next = CronExpressions.Parse("0 0 5 * * ?").GetNextOccurrence(Noon, TimeZoneInfo.Utc);

        Assert.Equal(new DateTimeOffset(2026, 9, 10, 5, 0, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void The_wrong_number_of_fields_is_refused_with_the_count()
    {
        var error = CronExpressions.Validate("0 0 5 *");

        Assert.NotNull(error);
        Assert.Contains("has 4 field(s)", error);
        Assert.Contains("five fields", error);
    }

    [Fact]
    public void A_value_out_of_range_is_refused_with_the_reason()
    {
        var error = CronExpressions.Validate("0 0 25 * * *");

        Assert.NotNull(error);
        Assert.Contains("not a valid cron expression", error);
    }

    [Fact]
    public void A_valid_expression_validates_to_nothing()
    {
        Assert.Null(CronExpressions.Validate("*/2 * * * * *"));
        Assert.Null(CronExpressions.Validate("30 4 1 * *"));
    }
}
