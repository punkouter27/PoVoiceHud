using PoVoiceHud.Core.Config;


namespace PoVoiceHud.Core.Tests.Config;

[Trait("Category", "Integration")]
public class CredentialStoreTests
{
    [Fact]
    public void Write_read_delete_round_trip()
    {
        var target = "PoVoiceHud/Test-" + Guid.NewGuid().ToString("N");
        try
        {
            CredentialStore.Write(target, "s3cret-πat");
            Assert.Equal("s3cret-πat", CredentialStore.Read(target));

            CredentialStore.Write(target, "rotated");
            Assert.Equal("rotated", CredentialStore.Read(target));
        }
        finally
        {
            CredentialStore.Delete(target);
        }
        Assert.Null(CredentialStore.Read(target));
    }

    [Fact]
    public void Missing_credential_reads_null_and_delete_is_idempotent()
    {
        var target = "PoVoiceHud/Missing-" + Guid.NewGuid().ToString("N");
        Assert.Null(CredentialStore.Read(target));
        CredentialStore.Delete(target);
    }
}
