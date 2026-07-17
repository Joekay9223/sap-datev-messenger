using NovaNein.Datev;
using NovaNein.Domain;

namespace NovaNein.Tests;

public class DatevPackageRulesTests
{
    [Fact]
    public void Accepts_exact_incoming_three_file_package()
    {
        var result = DatevPackageRules.ValidateEntries(["document.xml", "Eingangsrechnung-2614228.xml", "Eingangsrechnung-2614228.pdf"], DocumentDirection.Incoming, 2614228);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Rejects_extra_files_and_wrong_direction()
    {
        var result = DatevPackageRules.ValidateEntries(["document.xml", "Eingangsrechnung-7.xml", "Eingangsrechnung-7.pdf", "README.txt"], DocumentDirection.Incoming, 7);
        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
        var outgoing = DatevPackageRules.ValidateEntries(["document.xml", "Eingangsrechnung-7.xml", "Eingangsrechnung-7.pdf"], DocumentDirection.Outgoing, 7);
        Assert.False(outgoing.IsValid);
    }

    [Fact]
    public void Blocks_nested_zip_paths()
    {
        var result = DatevPackageRules.ValidateEntries(["document.xml", "folder/Eingangsrechnung-7.xml", "Eingangsrechnung-7.pdf"], DocumentDirection.Incoming, 7);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Transfer_copies_to_hidden_target_then_publishes_and_removes_source()
    {
        var root = Path.Combine(Path.GetTempPath(), $"novanein-datev-transfer-{Guid.NewGuid():N}");
        var spool = Path.Combine(root, "spool");
        var watchfolder = Path.Combine(root, "watchfolder");
        Directory.CreateDirectory(spool);
        var source = Path.Combine(spool, "package.zip");
        var content = "%PDF-independent ZIP fixture"u8.ToArray();
        File.WriteAllBytes(source, content);
        try
        {
            new AtomicWatchfolderTransfer().MoveCompletedPackage(source, watchfolder, "Beleg-123.zip");

            Assert.False(File.Exists(source));
            Assert.Equal(content, File.ReadAllBytes(Path.Combine(watchfolder, "Beleg-123.zip")));
            Assert.DoesNotContain(Directory.GetFiles(watchfolder), path => Path.GetFileName(path).StartsWith('.'));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Transfer_is_idempotent_only_for_identical_content()
    {
        var root = Path.Combine(Path.GetTempPath(), $"novanein-datev-idempotent-{Guid.NewGuid():N}");
        var watchfolder = Path.Combine(root, "watchfolder");
        Directory.CreateDirectory(watchfolder);
        var target = Path.Combine(watchfolder, "Beleg-123.zip");
        File.WriteAllText(target, "original");
        try
        {
            var identical = Path.Combine(root, "identical.zip");
            File.WriteAllText(identical, "original");
            new AtomicWatchfolderTransfer().MoveCompletedPackage(identical, watchfolder, Path.GetFileName(target));
            Assert.False(File.Exists(identical));

            var conflicting = Path.Combine(root, "conflicting.zip");
            File.WriteAllText(conflicting, "different");
            Assert.Throws<IOException>(() => new AtomicWatchfolderTransfer().MoveCompletedPackage(conflicting, watchfolder, Path.GetFileName(target)));
            Assert.True(File.Exists(conflicting));
            Assert.Equal("original", File.ReadAllText(target));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
