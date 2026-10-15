// [MS-ONE] 1.3.4 and 2.1.15: a notebook is a folder of section files described by a table of
// contents (.onetoc2), with section groups as subfolders. A .onepkg is the same folder packed
// into a Microsoft Cabinet ([MS-CAB]).

using XRay.Content.Internal.Cab;
using XRay.Content.OneNote.Store;
using P = XRay.Content.OneNote.Store.OneNotePropertyNames;

namespace XRay.Content.OneNote;

/// <summary>One entry of a table of contents: a section file or a section-group folder.</summary>
public sealed record OneNoteTocEntry(string Name, Guid? FileId, uint? Order, OneNoteColor? Color)
{
    public bool IsSection => Name.EndsWith(".one", StringComparison.OrdinalIgnoreCase);
}

/// <summary>[MS-ONE] 2.2.14: a notebook's table of contents.</summary>
public sealed class OneNoteTableOfContents
{
    public Guid FileId { get; init; }
    public OneStoreEncoding Encoding { get; init; }
    public OneNoteColor? Color { get; init; }
    public bool? EnableHistory { get; init; }
    public List<OneNoteTocEntry> Entries { get; } = new();
    public List<string> Warnings { get; } = new();

    public static OneNoteTableOfContents Read(byte[] data) => Read(OneStore.Read(data));

    public static OneNoteTableOfContents Read(OneStore store)
    {
        if (store.Kind != OneStoreFileKind.TableOfContents) throw new OneNoteFormatException("The file is a section, not a table of contents.");
        var rev = store.Root.Current;
        var root = rev?.GetRoot(RevisionRoles.DefaultContent);
        var toc = new OneNoteTableOfContents
        {
            FileId = store.FileGuid,
            Encoding = store.Encoding,
            Color = root is null ? null : OneNoteColor.FromNotebookColor(root.Properties.GetUInt32(P.NotebookColor)),
            EnableHistory = root?.Properties.GetBool(P.EnableHistory),
        };
        toc.Warnings.AddRange(store.Warnings);
        if (rev is null || root is null)
        {
            toc.Warnings.Add("The table of contents has no content root.");
            return toc;
        }
        foreach (var id in root.Properties.GetObjectIds(P.TOCEntryIndex_OidIndex))
        {
            var entry = rev.GetObject(id);
            if (entry is null) continue;
            var p = entry.Properties;
            string? name = p.GetString(P.FolderChildFilename);
            if (string.IsNullOrEmpty(name)) continue;
            toc.Entries.Add(new OneNoteTocEntry(name, p.GetGuid(P.FileIdentityGuid), p.GetUInt32(P.NotebookElementOrderingID), OneNoteColor.FromNotebookColor(p.GetUInt32(P.NotebookColor))));
        }
        return toc;
    }
}

/// <summary>Reads whole notebooks: a folder on disk, a <c>.onepkg</c> package, or any set of
/// files keyed by their path inside the notebook.</summary>
public static class OneNoteNotebookReader
{
    public const string RecycleBinFolder = "OneNote_RecycleBin";

    /// <summary>Reads a notebook folder from disk.</summary>
    public static OneNoteNotebook ReadDirectory(string directory, OneNoteReadOptions? options = null)
    {
        var root = Path.GetFullPath(directory);
        var files = new Dictionary<string, Func<byte[]>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            string path = file;
            files[rel] = () => File.ReadAllBytes(path);
        }
        return Read(files, Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), options);
    }

    /// <summary>Reads a <c>.onepkg</c>. The notebook is named after the package unless a name is given.</summary>
    public static OneNoteNotebook ReadPackage(byte[] package, string? name = null, OneNoteReadOptions? options = null)
    {
        CabinetArchive cab;
        try
        {
            cab = CabinetReader.Read(package);
        }
        catch (InvalidDataException ex)
        {
            throw new OneNoteFormatException($"The package is not a valid cabinet: {ex.Message}", ex);
        }
        var files = new Dictionary<string, Func<byte[]>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in cab.Entries)
        {
            var e = entry;
            files[e.Path.TrimStart('/')] = () => cab.Extract(e);
        }
        return Read(files, name is null ? "" : Path.GetFileNameWithoutExtension(name), options);
    }

    /// <summary>Lists the files of a <c>.onepkg</c> without reading them.</summary>
    public static IReadOnlyList<(string Path, long Size, DateTime? LastModified)> ListPackage(byte[] package)
    {
        var cab = CabinetReader.Read(package);
        return cab.Entries.Select(e => (e.Path, e.Size, e.LastModified)).ToList();
    }

    /// <summary>Reads a notebook from files keyed by their path relative to the notebook
    /// root (<c>/</c> separators). This is the entry point for files synced from SharePoint or
    /// OneDrive: pass the notebook folder's children.</summary>
    public static OneNoteNotebook Read(IReadOnlyDictionary<string, Func<byte[]>> files, string displayName, OneNoteReadOptions? options = null)
    {
        options ??= OneNoteReadOptions.Default;
        var notebook = new OneNoteNotebook { DisplayName = displayName };
        var all = files.Keys.Select(k => k.Replace('\\', '/').Trim('/')).ToList();
        var lookup = files.ToDictionary(kv => kv.Key.Replace('\\', '/').Trim('/'), kv => kv.Value, StringComparer.OrdinalIgnoreCase);

        var rootToc = ReadFolder("", all, lookup, options, notebook.Warnings, out var sections, out var groups, out var recycle);
        notebook.Sections.AddRange(sections);
        notebook.SectionGroups.AddRange(groups);
        notebook.RecycleBin = recycle;
        if (rootToc is not null)
        {
            notebook.Id = rootToc.FileId;
            notebook.Color = rootToc.Color;
            notebook.EnableHistory = rootToc.EnableHistory;
            notebook.Encoding = rootToc.Encoding;
        }
        notebook.Encoding ??= notebook.AllSections().FirstOrDefault()?.Encoding;
        if (string.IsNullOrEmpty(notebook.DisplayName)) notebook.DisplayName = "Notebook";
        return notebook;
    }

    private static OneNoteTableOfContents? ReadFolder(
        string folder,
        List<string> all,
        Dictionary<string, Func<byte[]>> files,
        OneNoteReadOptions options,
        List<string> warnings,
        out List<OneNoteSection> sections,
        out List<OneNoteSectionGroup> groups,
        out OneNoteSectionGroup? recycleBin)
    {
        sections = new List<OneNoteSection>();
        groups = new List<OneNoteSectionGroup>();
        recycleBin = null;
        string prefix = folder.Length == 0 ? "" : folder + "/";
        var direct = all.Where(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && p.IndexOf('/', prefix.Length) < 0).ToList();
        var subfolders = all.Where(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && p.IndexOf('/', prefix.Length) > 0)
            .Select(p => p[..p.IndexOf('/', prefix.Length)])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        OneNoteTableOfContents? toc = null;
        var tocFiles = direct.Where(p => p.EndsWith(".onetoc2", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var tocPath in tocFiles.OrderByDescending(p => Path.GetFileName(p).Equals("Open Notebook.onetoc2", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var candidate = OneNoteTableOfContents.Read(files[tocPath]());
                warnings.AddRange(candidate.Warnings.Select(w => $"{tocPath}: {w}"));
                if (toc is null || candidate.Entries.Count > toc.Entries.Count) toc = candidate;
            }
            catch (Exception ex) when (ex is OneNoteFormatException or InvalidDataException or NotSupportedException)
            {
                warnings.Add($"{tocPath}: the table of contents could not be read: {ex.Message}");
            }
        }

        foreach (var path in direct.Where(p => p.EndsWith(".one", StringComparison.OrdinalIgnoreCase)))
        {
            string name = Path.GetFileName(path);
            string onefiles = prefix + Path.GetFileNameWithoutExtension(name) + "_onefiles/";
            var sectionOptions = new OneNoteReadOptions
            {
                IncludeVersionHistory = options.IncludeVersionHistory,
                IncludeConflictPages = options.IncludeConflictPages,
                IncludeDeletedPages = options.IncludeDeletedPages,
                IncludeRecycleBin = options.IncludeRecycleBin,
                ExternalFileResolver = options.ExternalFileResolver ?? (file => files.TryGetValue(onefiles + file, out var read) ? read() : null),
            };
            OneNoteSection section;
            try
            {
                section = OneNoteSectionReader.Read(files[path](), name, sectionOptions);
            }
            catch (Exception ex) when (ex is OneNoteFormatException or InvalidDataException or NotSupportedException)
            {
                section = new OneNoteSection { DisplayName = Path.GetFileNameWithoutExtension(name), FileName = name };
                section.Warnings.Add($"The section could not be read: {ex.Message}");
            }
            section.RelativePath = path;
            var entry = toc?.Entries.FirstOrDefault(e => e.FileId is { } id && id != Guid.Empty && (id == section.Id || id == section.FileId))
                ?? toc?.Entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
            if (entry is not null)
            {
                section.Order = entry.Order;
                section.Color ??= entry.Color;
            }
            sections.Add(section);
        }

        foreach (var sub in subfolders)
        {
            string subName = sub[(prefix.Length)..];
            if (subName.EndsWith("_onefiles", StringComparison.OrdinalIgnoreCase)) continue;
            bool isRecycle = subName.Equals(RecycleBinFolder, StringComparison.OrdinalIgnoreCase);
            if (isRecycle && !options.IncludeRecycleBin) continue;
            var subToc = ReadFolder(sub, all, files, options, warnings, out var subSections, out var subGroups, out var nestedRecycle);
            if (subSections.Count == 0 && subGroups.Count == 0 && subToc is null) continue;
            var group = new OneNoteSectionGroup
            {
                DisplayName = subName,
                RelativePath = sub,
                Id = subToc?.FileId,
                IsRecycleBin = isRecycle,
            };
            group.Sections.AddRange(subSections);
            group.SectionGroups.AddRange(subGroups);
            if (nestedRecycle is not null) group.SectionGroups.Add(nestedRecycle);
            var entry = toc?.Entries.FirstOrDefault(e => e.FileId is { } id && subToc is not null && id == subToc.FileId)
                ?? toc?.Entries.FirstOrDefault(e => string.Equals(e.Name, subName, StringComparison.OrdinalIgnoreCase));
            if (entry is not null)
            {
                group.Order = entry.Order;
                group.Color = entry.Color;
            }
            if (isRecycle) recycleBin = group;
            else groups.Add(group);
        }

        if (toc is not null)
        {
            foreach (var e in toc.Entries)
            {
                if (e.Name.Equals(RecycleBinFolder, StringComparison.OrdinalIgnoreCase)) continue;
                bool present = e.IsSection
                    ? sections.Any(s => string.Equals(s.FileName, e.Name, StringComparison.OrdinalIgnoreCase) || e.FileId is { } id && (s.Id == id || s.FileId == id))
                    : groups.Any(g => string.Equals(g.DisplayName, e.Name, StringComparison.OrdinalIgnoreCase));
                if (!present) warnings.Add($"{prefix}{e.Name} is listed in the table of contents but was not provided.");
            }
        }

        sections.Sort((a, b) => CompareOrder(a.Order, b.Order, a.FileName, b.FileName));
        groups.Sort((a, b) => CompareOrder(a.Order, b.Order, a.DisplayName, b.DisplayName));
        return toc;
    }

    private static int CompareOrder(uint? a, uint? b, string? nameA, string? nameB)
    {
        if (a is { } x && b is { } y && x != y) return x.CompareTo(y);
        if (a is not null && b is null) return -1;
        if (a is null && b is not null) return 1;
        return string.Compare(nameA, nameB, StringComparison.OrdinalIgnoreCase);
    }
}
