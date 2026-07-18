using UcuModManager.Core.Profiles;

namespace UcuModManager.Core.Tests;

public sealed class UcuModpackServiceTests
{
    private readonly UcuModpackService _service = new();

    [Fact]
    public void Validate_NormalizesLoadOrder()
    {
        var package = CreatePackage(
            CreateMod("Second", 1),
            CreateMod("First", 0));

        var validated = _service.Validate(package, ".UCU");

        Assert.Collection(
            validated.Mods,
            mod => Assert.Equal("First", mod.Name),
            mod => Assert.Equal("Second", mod.Name));
    }

    [Fact]
    public void Validate_RejectsNullModsCollection()
    {
        var package = CreatePackage(CreateMod("Mod", 0)) with { Mods = null! };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            _service.Validate(package, ".UCUP"));

        Assert.Contains("does not contain any mods", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_RejectsDuplicatePriorities()
    {
        var package = CreatePackage(
            CreateMod("First", 0),
            CreateMod("Second", 0));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            _service.Validate(package, ".UCU"));

        Assert.Contains("duplicate load-order priority", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_RejectsUnsupportedFormatVersion()
    {
        var package = CreatePackage(CreateMod("Mod", 0)) with { FormatVersion = 999 };

        Assert.Throws<InvalidOperationException>(() => _service.Validate(package, ".UCUP"));
    }

    [Fact]
    public void Validate_RejectsUnsafePortableArchiveName()
    {
        var mod = CreateMod("Mod", 0) with { EmbeddedArchiveFileName = "../mod.zip" };
        var package = CreatePackage(mod) with
        {
            PackageKind = UcuModpackPackage.PackageKindPortable
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            _service.Validate(package, ".UCUP"));

        Assert.Contains("invalid embedded archive name", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static UcuModpackPackage CreatePackage(params UcuModpackMod[] mods)
    {
        return new UcuModpackPackage(
            UcuModpackPackage.CurrentFormatVersion,
            UcuModpackPackage.DefaultCreatedBy,
            DateTimeOffset.UtcNow,
            "Test profile",
            mods);
    }

    private static UcuModpackMod CreateMod(string name, int priority)
    {
        return new UcuModpackMod(
            name,
            IsEnabled: true,
            priority,
            GameDomain: "scavprototype",
            NexusModId: priority + 1,
            FileId: priority + 10,
            Version: "1.0.0",
            PageUrl: null,
            SourceArchiveFileName: $"{name}.zip",
            EmbeddedArchiveFileName: $"{name}.zip");
    }
}
