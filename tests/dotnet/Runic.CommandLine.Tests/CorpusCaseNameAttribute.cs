namespace Runic.CommandLine.Tests;

/// <summary>
/// Names a corpus case <c>prefix + fixture id</c>, as the corpus rows were named before, and adds the fixture id as
/// the <c>CaseId</c> property, so <c>--treenode-filter "/*/*/*/*[CaseId=grammar.option.long-flag]"</c> selects one row.
/// A <c>$id</c> placeholder in <see cref="DisplayNameAttribute"/> would show the dots of a fixture id as middle dots.
/// </summary>
internal sealed class CorpusCaseNameAttribute(string prefix) : DisplayNameFormatterAttribute
{
    protected override string FormatDisplayName(DiscoveredTestContext context)
    {
        string id = (string)context.TestDetails.TestMethodArguments[0]!;
        context.AddProperty("CaseId", id);
        return prefix + id;
    }
}
