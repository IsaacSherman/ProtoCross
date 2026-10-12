using ProtoCross.Diagnostics;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>Reaching a map through a oneof member unsets its sibling before a key is read.</summary>
public class MapPresenceReviewTests
{
    [Theory]
    [InlineData("target.values[source.key] = 9;")]
    [InlineData("target.items[source.key].key = 9;")]
    public void AKeyCannotReadAOneofSiblingUnsetByReachingItsMap(string statement)
    {
        var schemas = TestPaths.CreateTempDirectory();
        File.WriteAllText(Path.Combine(schemas, "map_presence.proto"), """
            syntax = "proto3";
            package map_presence_review;
            message Item {
              int64 key = 1;
              map<int64, int64> values = 2;
              map<int64, Item> items = 3;
            }
            message Root {
              oneof choice { Item target = 1; Item source = 2; }
            }
            """);
        var result = Compilation.Compile(TestPaths.WriteTempScript($$"""
            import proto "map_presence.proto";
            extend Root {
                mut fn change() {
                    if has source {
                        {{statement}}
                    }
                }
            }
            """), [schemas]);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == DiagnosticCodes.MessageFieldMayBeUnset.Code);
    }
}
