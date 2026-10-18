using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace FolderLinkApp.Tests;

/// <summary>
/// The WinForms project can't be run here, but its string tables can be
/// checked as files: every key the hand-written Designer.cs asks for must
/// exist in both languages, and the Chinese text must not use a {n}
/// placeholder the English call site doesn't supply (string.Format would
/// throw at runtime on a real machine).
/// </summary>
public sealed class ResourceFileTests
{
    private static string SrcDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "FolderLink.Core")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("src folder not found");
    }

    private static Dictionary<string, string> Load(string path) =>
        XDocument.Load(path).Root!.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string)d.Element("value")!);

    private static HashSet<string> DesignerKeys(string path) =>
        Regex.Matches(File.ReadAllText(path), @"Get\(nameof\((\w+)\)\)").Select(m => m.Groups[1].Value).ToHashSet();

    private static HashSet<int> Placeholders(string text) =>
        Regex.Matches(text, @"\{(\d+)\}").Select(m => int.Parse(m.Groups[1].Value)).ToHashSet();

    [Theory]
    [InlineData("FolderLink", "Strings")]
    [InlineData("FolderLink.Core", "CoreStrings")]
    public void Every_key_exists_in_both_languages_with_compatible_placeholders(string project, string name)
    {
        var dir = Path.Combine(SrcDir(), project);
        var en = Load(Path.Combine(dir, name + ".resx"));
        var zh = Load(Path.Combine(dir, name + ".zh-TW.resx"));
        var keys = DesignerKeys(Path.Combine(dir, name + ".Designer.cs"));

        Assert.NotEmpty(keys);
        Assert.Empty(keys.Except(en.Keys));
        Assert.Empty(keys.Except(zh.Keys));
        Assert.Empty(en.Keys.Except(keys));   // no unused / unreachable strings
        Assert.Empty(zh.Keys.Except(en.Keys));

        foreach (var key in keys)
        {
            Assert.True(Placeholders(zh[key]).SetEquals(Placeholders(en[key])),
                $"{name}.{key}: placeholders differ between en and zh-TW");
            Assert.NotEqual(en[key], zh[key]);
        }
    }
}
