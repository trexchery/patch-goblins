using PatchGoblins;
using Xunit;

namespace PatchGoblins.Tests;

public class DefaultProfileSafetyTests
{
    [Fact]
    public void YieldPreflightTreatsLiveAndUnreadableRecordsAsBusy()
    {
        var dir = Path.Combine(Path.GetTempPath(), "goblins-default-" + Guid.NewGuid());
        try
        {
            Assert.False(ResumeStopped.DefaultProfileClear(dir));
            var sessions = Directory.CreateDirectory(Path.Combine(dir, "sessions")).FullName;
            Assert.True(ResumeStopped.DefaultProfileClear(dir));
            var record = Path.Combine(sessions, "session.json");
            File.WriteAllText(record, "{");
            Assert.False(ResumeStopped.DefaultProfileClear(dir));
            File.WriteAllText(record, $$"""{"pid":{{Environment.ProcessId}}}""");
            Assert.False(ResumeStopped.DefaultProfileClear(dir));
            File.Delete(record);
            var key = Path.Combine(sessions, $"{Environment.ProcessId}.hash.key");
            File.WriteAllText(key, "");
            Assert.False(ResumeStopped.DefaultProfileClear(dir));
            File.Delete(key);
            Assert.True(ResumeStopped.DefaultProfileClear(dir));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }
}
