using Bunit;
using FluentAssertions;
using TalentMatch.Web.Client.Components;
using TalentMatch.Web.Client.Services;

namespace TalentMatch.Web.Tests;

public sealed class ScoringRunDetailsTests : BunitContext
{
    [Fact]
    public void EvidenceUsesExplicitEvaluationMarkersForPassedAndFailedIcons()
    {
        var run = new ScoringRunDto(
            "run-1",
            1,
            75,
            """{"Reporting":100,"Safety":50}""",
            "{}",
            """
            [
              {"category":"Reporting","snippet":"Item 1/2 | PASS | Reporting damage | Candidate reported damage to a supervisor."},
              {"category":"Safety","snippet":"Item 3/3 | NOT EVIDENCED | ISO procedures | No supporting evidence found."},
              {"category":"Other","snippet":"Unstructured contextual citation"}
            ]
            """,
            """["Provide ISO evidence."]""",
            "o3",
            "prompt-1",
            0,
            0);

        using var cut = Render<ScoringRunDetails>(parameters => parameters
            .Add(component => component.Run, run));

        cut.FindAll("[aria-label='Passed']").Should().ContainSingle();
        cut.FindAll("[aria-label='Failed']").Should().ContainSingle();
        cut.FindAll("[aria-label='Not explicitly evaluated']").Should().ContainSingle();
        cut.Markup.Should().Contain("Candidate reported damage to a supervisor.");
        cut.Markup.Should().Contain("No supporting evidence found.");
    }
}
