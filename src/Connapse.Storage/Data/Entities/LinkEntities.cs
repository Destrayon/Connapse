namespace Connapse.Storage.Data.Entities;

/// <summary>
/// One kind, name or summary a document gave a node. Document ids are text, not foreign keys: the rows a document
/// asserted are removed by a trigger when the document is deleted.
/// </summary>
public class LinkNodeFactEntity
{
    public long Id { get; set; }
    public string NodeId { get; set; } = string.Empty;
    public short Field { get; set; }
    public string Text { get; set; } = string.Empty;
    public string DocumentId { get; set; } = string.Empty;
    public int Ordinal { get; set; }
}

/// <summary>A document that stands for a node: the issue whose key is the node id.</summary>
public class LinkNodeDocumentEntity
{
    public string NodeId { get; set; } = string.Empty;
    public string DocumentId { get; set; } = string.Empty;
}

/// <summary>One link as one document asserted it. A link several documents assert has a row from each.</summary>
public class LinkEdgeEntity
{
    public long Id { get; set; }
    public string EdgeId { get; set; } = string.Empty;
    public string FromNode { get; set; } = string.Empty;
    public string ToNode { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string DocumentId { get; set; } = string.Empty;
    public string Evidence { get; set; } = string.Empty;
    public int Ordinal { get; set; }
}
