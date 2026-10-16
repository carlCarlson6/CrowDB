using System.Text.Json;

namespace CrowDB.Engine;

public class DocumentEncoder
{
    private readonly string _docId;
    private readonly ReadOnlyMemory<byte> _document;
    
    public DocumentEncoder(string docId, ReadOnlyMemory<byte> document)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(docId);
        if (document.Length == 0)
            throw new ArgumentException("document cannot be empty", nameof(document));
        
        _docId = docId;
        _document = document;
    }

    public ReadOnlySpan<byte> ToEncodedSpan()
    {
        var encoded = ToString();
        return new ReadOnlySpan<byte>(System.Text.Encoding.UTF8.GetBytes(encoded));
    }
    
    public override string ToString() => JsonSerializer.Serialize(new
    {
        version = Constants.ENCODE_VERSION,
        id = _docId,
        doc = System.Text.Encoding.Default.GetString(_document.ToArray())
    })+"\n";
}