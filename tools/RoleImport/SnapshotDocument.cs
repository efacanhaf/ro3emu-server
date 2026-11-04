using System.Text.Json;
using Google.Protobuf;

namespace RoleImport;

/// <summary>
/// One character snapshot: every AUTO_SYNC dataset the exporter read from a client, plus the
/// character's id/name/account and the export time (ro3/bot/ferramentas/exportar-personagem.ps1).
/// </summary>
public sealed class SnapshotDocument : IDisposable
{
    private readonly JsonDocument _document;

    private SnapshotDocument(JsonDocument document)
    {
        _document = document;
        Root = document.RootElement;
        if (!Root.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Snapshot has no 'messages' object.");
        Messages = messages;
    }

    public JsonElement Root { get; }
    public JsonElement Messages { get; }

    public static SnapshotDocument Load(string path) =>
        new(JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { AllowTrailingCommas = true }));

    public string? ExportedAtText =>
        Root.TryGetProperty("exportedAtText", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public bool Has(string message) => Messages.TryGetProperty(message, out _);

    public bool TryMessage(string message, out JsonElement value) =>
        Messages.TryGetProperty(message, out value) && value.ValueKind == JsonValueKind.Object;

    public JsonElement Message(string message) =>
        TryMessage(message, out var value) ? value : throw new InvalidDataException($"Snapshot has no '{message}' message.");

    public int Count => Messages.EnumerateObject().Count();

    public IEnumerable<string> Names => Messages.EnumerateObject().Select(p => p.Name);

    public void Dispose() => _document.Dispose();
}
