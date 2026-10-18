using System;
using System.Text;
using System.Text.Json;
using static CrowDB.Engine.Kind;

namespace CrowDB.Engine;

public record Record(string Id, string Version, string Doc, string Kind = PUT)
{
    public ReadOnlySpan<byte> Encode() => Encoding.UTF8.GetBytes(ToString());
    
    public static Record Create(string docId, ReadOnlyMemory<byte> document)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(docId);
        if (document.Length == 0)
            throw new ArgumentException("document cannot be empty", nameof(document));

        return new Record(
            Id:       docId,
            Version:  Constants.EncodeVersion,
            Doc:      Encoding.UTF8.GetString(document.ToArray()),
            Kind:     PUT);
    }
    
    public Record Delete() => this with { Kind = DELETE, Doc = Constants.DeletedDoc};
    
    public ReadOnlyMemory<byte> ToMemoryDoc() => Encoding.UTF8.GetBytes(Doc);

    public static Record Decode(byte[] recordBytes)
    {
        var record = JsonSerializer.Deserialize<Record>(recordBytes);
        return record ?? throw new Exception("empty record");
    }

    public override string ToString() => JsonSerializer.Serialize(this);
}

public static class Kind
{
    public const string DELETE = "DELETE";
    public const string PUT = "PUT";
}