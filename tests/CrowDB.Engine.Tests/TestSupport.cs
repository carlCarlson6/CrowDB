using System.Text;
using CrowDB.Engine;

namespace CrowDB.Engine.Tests;

/// <summary>Helpers shared by the engine tests.</summary>
internal static class TestSupport
{
    /// <summary>Document bytes as the engine sees them: UTF-8, never parsed.</summary>
    public static byte[] Bytes(string json) => Encoding.UTF8.GetBytes(json);

    /// <summary>Reads a document, failing the test when the id is missing.</summary>
    public static byte[] Read(DocumentStore store, string id)
    {
        Assert.True(store.TryGet(id, out var document), $"expected a document for id '{id}'");
        return document.ToArray();
    }
}
