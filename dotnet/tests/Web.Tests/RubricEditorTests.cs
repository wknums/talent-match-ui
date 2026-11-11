using Bunit;
using FluentAssertions;
using TalentMatch.Web.Client.Components;
using TalentMatch.Web.Client.Services;

namespace TalentMatch.Web.Tests;

public sealed class RubricEditorTests : BunitContext
{
    [Fact]
    public void EditableEditor_MoveDown_ReordersItemsWithinCategory()
    {
        RubricEnvelopeDto? updated = null;
        var rubric = SampleRubric();

        var cut = Render<RubricEditor>(parameters => parameters
            .Add(component => component.Value, rubric)
            .Add(component => component.Editable, true)
            .Add(component => component.OnChange, value => updated = value));

        cut.Find("button[data-action='move-down']").Click();

        updated.Should().NotBeNull();
        updated!.Items.Where(item => item.CategoryId == "cat-1")
            .OrderBy(item => item.Order)
            .Select(item => item.Id)
            .Should()
            .Equal("item-2", "item-1");
    }

    [Fact]
    public void EditableEditor_MoveToAnotherCategory_AnnouncesChange()
    {
        RubricEnvelopeDto? updated = null;
        var rubric = SampleRubric();

        var cut = Render<RubricEditor>(parameters => parameters
            .Add(component => component.Value, rubric)
            .Add(component => component.Editable, true)
            .Add(component => component.OnChange, value => updated = value));

        cut.Find("select[data-move-select='item-1']").Change("cat-2");
        cut.Find("button[data-move-apply='item-1']").Click();

        updated.Should().NotBeNull();
        updated!.Items.Single(item => item.Id == "item-1").CategoryId.Should().Be("cat-2");
        cut.Markup.Should().Contain("Moved Expert SQL experience to Delivery and Quality");
    }

    [Fact]
    public void EditableEditor_AddEditAndRemoveItem_PreservesEmptyCategory()
    {
        RubricEnvelopeDto? updated = null;
        var rubric = new RubricEnvelopeDto(
            "rubric-v2",
            null,
            [
                new RubricCategoryV2Dto("cat-1", "Technical Skills", 0.7, "Core technical match", 0),
                new RubricCategoryV2Dto("cat-2", "Delivery and Quality", 0.3, "Delivery quality", 1),
            ],
            []);

        var cut = Render<RubricEditor>(parameters => parameters
            .Add(component => component.Value, rubric)
            .Add(component => component.Editable, true)
            .Add(component => component.OnChange, value => updated = value));

        cut.Find("input[placeholder='Add item text']").Input("New manual item");
        cut.Find("button[data-add-item='cat-1']").Click();
        updated.Should().NotBeNull();
        updated!.Items.Should().ContainSingle(item => item.Text == "New manual item");

        cut = Render<RubricEditor>(parameters => parameters
            .Add(component => component.Value, updated)
            .Add(component => component.Editable, true)
            .Add(component => component.OnChange, value => updated = value));

        cut.Find("button[data-edit-item]").Click();
        cut.Find("input[value='New manual item']").Input("Edited manual item");
        cut.Find("button[data-save-edit]").Click();

        cut = Render<RubricEditor>(parameters => parameters
            .Add(component => component.Value, updated)
            .Add(component => component.Editable, true)
            .Add(component => component.OnChange, value => updated = value));

        cut.Find("button[data-remove-item]").Click();
        updated!.Items.Should().BeEmpty();
        cut.Markup.Should().Contain("No rubric items are assigned to this category.");
    }

    [Fact]
    public void EditableEditor_CanMoveCollapseAndReweightCategories()
    {
        RubricEnvelopeDto? updated = null;
        var cut = Render<RubricEditor>(parameters => parameters
            .Add(component => component.Value, SampleRubric())
            .Add(component => component.Editable, true)
            .Add(component => component.OnChange, value => updated = value));

        cut.Find("button[data-category-move-down='cat-1']").Click();

        updated.Should().NotBeNull();
        updated!.Categories.OrderBy(category => category.Order).Select(category => category.Id)
            .Should().Equal("cat-2", "cat-1");

        cut.Find("input[data-category-weight='cat-1']").Change("0.6");
        cut.Find("input[data-category-weight='cat-2']").Change("0.4");

        updated!.Categories.Single(category => category.Id == "cat-1").Weight.Should().Be(0.6);
        updated.Categories.Single(category => category.Id == "cat-2").Weight.Should().Be(0.4);
        cut.Markup.Should().Contain("Total category weight: 1");

        cut.Find("button[data-category-toggle='cat-1']").Click();
        cut.Find("section[data-drop-category='cat-1']").TextContent.Should().NotContain("Expert SQL experience");
        var categoryToggle = cut.Find("button[data-category-toggle='cat-1']");
        categoryToggle.GetAttribute("aria-expanded").Should().Be("false");
        categoryToggle.GetAttribute("aria-label").Should().Be("Expand Technical Skills");
        categoryToggle.TextContent.Should().BeEmpty();
        categoryToggle.QuerySelector(".disclosure-chevron").Should().NotBeNull();
    }

    private static RubricEnvelopeDto SampleRubric()
        => new(
            "rubric-v2",
            null,
            [
                new RubricCategoryV2Dto("cat-1", "Technical Skills", 0.7, "Core technical match", 0),
                new RubricCategoryV2Dto("cat-2", "Delivery and Quality", 0.3, "Delivery quality", 1),
            ],
            [
                new RubricItemDto("item-1", "cat-1", "Expert SQL experience", "must_have", 0, "Expert SQL experience", null, "req-1", "confirmed", "extracted"),
                new RubricItemDto("item-2", "cat-1", "Expert Python experience", "must_have", 1, "Expert Python experience", null, "req-2", "confirmed", "extracted"),
            ]);
}
