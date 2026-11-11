using FluentAssertions;
using TalentMatch.Web.Client.Services;

namespace TalentMatch.Web.Tests;

public sealed class ScoringGateSummaryTests
{
    [Fact]
    public void Build_AggregatesEveryRunAndPreservesFailedRequirementVotes()
    {
        var result = ScoringGateSummary.Build(
        [
            """
            {"passed":true,"details":{"entries":[
              {"criterion":"Registered engineer","passed":true,"evidence":"Registration listed"},
              {"criterion":"Five years experience","passed":true,"evidence":"Six years listed"}
            ]}}
            """,
            """
            {"passed":false,"missing_criteria":["Registered engineer"],"details":{"entries":[
              {"criterion":"Registered engineer","passed":false,"evidence":"Registration could not be verified"},
              {"criterion":"Five years experience","passed":true,"evidence":"Six years listed"}
            ]}}
            """,
            """
            {"passed":false,"missing_criteria":["Registered engineer"],"details":{"entries":[
              {"criterion":"Registered engineer","passed":false,"evidence":"No registration number"},
              {"criterion":"Five years experience","passed":true,"evidence":"Six years listed"}
            ]}}
            """
        ],
        ["Registered engineer", "Five years experience"]);

        result.Should().NotBeNull();
        result!.Passed.Should().BeFalse();
        result.PassedVotes.Should().Be(1);
        result.FailedVotes.Should().Be(2);
        result.Entries.Should().ContainEquivalentOf(new AggregatedGateEntryDto(
            "Registered engineer",
            false,
            1,
            2,
            "Registration could not be verified"));
        result.Entries.Should().ContainEquivalentOf(new AggregatedGateEntryDto(
            "Five years experience",
            true,
            3,
            0,
            "Six years listed"));
    }

    [Fact]
    public void Build_UsesMissingCriteriaWhenEntriesAreNotReturned()
    {
        var result = ScoringGateSummary.Build(
        [
            """{"passed":false,"missing_criteria":["Security clearance"]}""",
            """{"passed":true,"missing_criteria":[]}"""
        ],
        ["Security clearance", "Degree"]);

        result.Should().NotBeNull();
        result!.Entries.Single(entry => entry.Criterion == "Security clearance")
            .FailedVotes.Should().Be(1);
        result.Entries.Single(entry => entry.Criterion == "Degree")
            .PassedVotes.Should().Be(2);
    }

    [Fact]
    public void Parse_UsesCaseInsensitiveFieldsAndFlexibleStatuses()
    {
        var result = ScoringGateSummary.Parse(
            """
            {
              "Passed": true,
              "MissingCriteria": [],
              "Details": {
                "Entries": [
                  {
                    "Requirement": "Basic literacy",
                    "Status": "Met",
                    "Supporting_Evidence": "Completed written qualifications"
                  }
                ]
              }
            }
            """);

        result.Should().NotBeNull();
        result!.Passed.Should().BeTrue();
        result.Entries.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new ScoringGateEntryDto(
                "Basic literacy",
                true,
                "Completed written qualifications"));
    }
}
