using System.Reflection;

namespace PoVoiceHud.Core.Tests;

public class SmokeTests
{
    // Architecture rule A1: Core never references WinUI.
    [Fact]
    public void Core_does_not_reference_WinUI()
    {
        var refs = Assembly.Load("PoVoiceHud.Core").GetReferencedAssemblies().Select(a => a.Name!);
        Assert.DoesNotContain(refs, n => n.StartsWith("Microsoft.WinUI", StringComparison.Ordinal) || n.StartsWith("Microsoft.UI", StringComparison.Ordinal));
    }
}
