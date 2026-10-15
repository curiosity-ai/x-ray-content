// Not a port: upstream xberg has no OneNote support. Reads [MS-ONESTORE] / [MS-ONE] sections
// (.one), tables of contents (.onetoc2) and packages (.onepkg, an [MS-CAB] cabinet) through
// XRay.Content.OneNote, and generic cabinets through the same CAB reader.

using XRay.Content.Core;
using XRay.Content.Internal.Archive;
using XRay.Content.Internal.Cab;
using XRay.Content.OneNote;
using XRay.Content.OneNote.Store;
using XRay.Content.Types;

namespace XRay.Content.Extractors;

/// <summary>Extracts OneNote sections, tables of contents and packages; and the members of
/// any other Microsoft Cabinet.</summary>
public sealed class OneNoteExtractor : IExtractor
{
    public IEnumerable<string> SupportedMimeTypes => new[]
    {
        Mime.OneNoteMimeType,
        "application/msonenote",
        "application/x-onenote",
        Mime.CabinetMimeType,
        "application/x-cab",
        "application/cab",
    };

    public int Priority => 50;

    public InternalDocument Extract(ReadOnlySpan<byte> content, string mimeType, ExtractionConfig config)
    {
        var bytes = content.ToArray();
        var options = new OneNoteReadOptions { IncludeVersionHistory = config.Options.OneNoteIncludeVersionHistory };

        if (OneStore.IsOneStore(bytes))
        {
            var store = OneStore.Read(bytes);
            if (store.Kind == OneStoreFileKind.Section)
            {
                var section = OneNoteSectionReader.Read(store, config.SourceName, options);
                return OneNoteDocumentBuilder.Build(new[] { section }, mimeType, config);
            }
            return BuildTableOfContents(OneNoteTableOfContents.Read(store), mimeType, config);
        }

        if (CabinetReader.IsCabinet(bytes))
        {
            var cab = CabinetReader.Read(bytes);
            if (cab.Entries.Any(e => e.Path.EndsWith(".one", StringComparison.OrdinalIgnoreCase) || e.Path.EndsWith(".onetoc2", StringComparison.OrdinalIgnoreCase)))
            {
                var notebook = OneNoteNotebookReader.ReadPackage(bytes, config.SourceName, options);
                var all = notebook.AllSections().ToList();
                if (notebook.RecycleBin is { } bin) all.AddRange(bin.AllSections());
                return OneNoteDocumentBuilder.Build(all, mimeType, config, notebook);
            }
            return ArchiveDocument.Build(ReadCabinet(cab, config), mimeType, config);
        }

        throw new OneNoteFormatException("The content is neither a OneNote file nor a cabinet.");
    }

    private static InternalDocument BuildTableOfContents(OneNoteTableOfContents toc, string mimeType, ExtractionConfig config)
    {
        var b = new InternalDocumentBuilder("onenote");
        b.SetMimeType(mimeType);
        string title = config.SourceName is { } name ? Path.GetFileNameWithoutExtension(name) : "Notebook";
        b.PushTitle(title, null, null);
        foreach (var entry in toc.Entries.OrderBy(e => e.Order ?? uint.MaxValue))
            b.PushParagraph(entry.IsSection ? Path.GetFileNameWithoutExtension(entry.Name) : entry.Name + "/", new(), null, null);
        foreach (var w in toc.Warnings) b.AddWarning(new ProcessingWarning { Source = "onenote", Message = w });
        var doc = b.Build();
        doc.Metadata = new Metadata { Title = title };
        doc.Metadata.Additional["onenote_toc"] = System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            id = toc.FileId.ToString("D"),
            color = toc.Color?.ToHex(),
            entries = toc.Entries.Select(e => new { name = e.Name, id = e.FileId?.ToString("D"), order = e.Order, color = e.Color?.ToHex(), isSection = e.IsSection }),
        });
        return doc;
    }

    private static ArchiveReadResult ReadCabinet(CabinetArchive cab, ExtractionConfig config)
    {
        var read = new ArchiveReadResult();
        read.Info.Format = "CAB";
        foreach (var entry in cab.Entries)
        {
            read.Info.FileList.Add(new ArchiveFileEntry(entry.Path, (ulong)entry.Size, false));
            read.Info.TotalSize += (ulong)entry.Size;
            byte[] data;
            try
            {
                data = cab.Extract(entry);
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
            {
                continue;
            }
            if (ArchiveConstants.IsTextFile(entry.Path))
                read.TextContents.Add(new(entry.Path, System.Text.Encoding.UTF8.GetString(data)));
            read.FileBytes.Add(new(entry.Path, data));
        }
        read.Info.FileCount = read.Info.FileList.Count;
        return read;
    }
}
