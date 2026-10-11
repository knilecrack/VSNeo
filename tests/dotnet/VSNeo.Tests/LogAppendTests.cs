using VSNeo_Extension.Infrastructure;
using Xunit;

namespace VSNeo.Tests;

public class LogAppendTests
{
    [Fact]
    public async Task Concurrent_appends_through_separate_handles_lose_nothing()
    {
        string path = Path.Combine(Path.GetTempPath(), "vsneo-append-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            const int writers = 8, perWriter = 200;
            await Task.WhenAll(Enumerable.Range(0, writers).Select(w => Task.Run(() =>
            {
                for (int i = 0; i < perWriter; i++)
                    Log.AppendAtomic(path, System.Text.Encoding.UTF8.GetBytes($"w{w} line {i:D4}\n"));
            })));

            var lines = File.ReadAllLines(path);
            Assert.Equal(writers * perWriter, lines.Length);
            Assert.Equal(writers * perWriter, lines.Distinct().Count());
        }
        finally { File.Delete(path); }
    }
}
