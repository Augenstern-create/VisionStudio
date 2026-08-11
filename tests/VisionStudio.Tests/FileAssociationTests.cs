using VisionStudio.App;

namespace VisionStudio.Tests;

public sealed class FileAssociationTests
{
    [Fact]
    public void OpenCommand_QuotesExecutableAndProjectArgument()
    {
        var executable = Path.Combine("C:\\Program Files", "Vision Studio", "VisionStudio.App.exe");
        Assert.Equal($"\"{Path.GetFullPath(executable)}\" \"%1\"", WindowsFileAssociation.BuildOpenCommand(executable));
    }
}
