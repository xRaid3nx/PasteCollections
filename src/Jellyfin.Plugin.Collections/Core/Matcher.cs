using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Collections.Core;

/// <summary>
/// Matches parsed list lines against a library catalog. Contains no Jellyfin types.
/// </summary>
public sealed class Matcher
{
    private const int ParallelThreshold = 200;

    private readonly Dictionary<string, List<CatalogEntry>> _strict = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<CatalogEntry>> _loose = new(StringComparer.Ordinal);
    private readonly Dictionary<int, List<CatalogEntry>> _byTmdb = [];
    private readonly FuzzyIndex _fuzzy;

    /// <summary>
    /// Initializes a new instance of the <see cref="Matcher"/> class and indexes the catalog.
    /// </summary>
    /// <param name="catalog">The library items to match against.</param>
    public Matcher(IReadOnlyList<CatalogEntry> catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var seen = new HashSet<Guid>();
        foreach (var entry in catalog)
        {
            if (!seen.Add(entry.Id))
            {
                continue;
            }

            foreach (var title in new[] { entry.Name, entry.OriginalTitle, entry.SortName })
            {
                Index(_strict, TitleNormalizer.Strict(title), entry);
                Index(_loose, TitleNormalizer.Loose(title), entry);
            }

            if (entry.TmdbId is > 0)
            {
                if (!_byTmdb.TryGetValue(entry.TmdbId.Value, out var sameId))
                {
                    sameId = [];
                    _byTmdb[entry.TmdbId.Value] = sameId;
                }

                sameId.Add(entry);
            }
        }

        _fuzzy = new FuzzyIndex(_loose);
    }

    /// <summary>
    /// Matches every line. Convenience wrapper that builds the index and runs once.
    /// </summary>
    /// <param name="catalog">The library items.</param>
    /// <param name="lines">The parsed lines.</param>
    /// <param name="options">Matching options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One result per line, in line order.</returns>
    public static IReadOnlyList<MatchResult> Match(
        IReadOnlyList<CatalogEntry> catalog,
        IReadOnlyList<ParsedLine> lines,
        MatchOptions options,
        CancellationToken cancellationToken = default)
    {
        return new Matcher(catalog).Match(lines, options, cancellationToken);
    }

    /// <summary>
    /// Matches every line against the indexed catalog.
    /// </summary>
    /// <param name="lines">The parsed lines.</param>
    /// <param name="options">Matching options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One result per line, in line order.</returns>
    public IReadOnlyList<MatchResult> Match(
        IReadOnlyList<ParsedLine> lines,
        MatchOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(options);

        var results = new MatchResult[lines.Count];
        if (lines.Count >= ParallelThreshold)
        {
            Parallel.For(
                0,
                lines.Count,
                new ParallelOptions { CancellationToken = cancellationToken },
                i => results[i] = MatchLine(lines[i], options));
        }
        else
        {
            for (var i = 0; i < lines.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                results[i] = MatchLine(lines[i], options);
            }
        }

        MarkDuplicateItems(results);
        return results;
    }

    private static void Index(Dictionary<string, List<CatalogEntry>> index, string key, CatalogEntry entry)
    {
        if (key.Length == 0)
        {
            return;
        }

        if (!index.TryGetValue(key, out var list))
        {
            list = [];
            index[key] = list;
        }

        if (!list.Contains(entry))
        {
            list.Add(entry);
        }
    }

    private static void MarkDuplicateItems(MatchResult[] results)
    {
        var firstLineByItem = new Dictionary<Guid, int>();
        for (var i = 0; i < results.Length; i++)
        {
            var result = results[i];
            if (result.SelectedId is not { } id
                || result.Status is not (MatchStatus.Matched or MatchStatus.AlreadyInCollection))
            {
                continue;
            }

            if (firstLineByItem.TryGetValue(id, out var firstLine))
            {
                results[i] = result with
                {
                    IsSelectedByDefault = false,
                    DuplicateOfLineNumber = firstLine,
                    Note = string.Create(CultureInfo.InvariantCulture, $"Same item as line {firstLine}; it will only be added once.")
                };
            }
            else
            {
                firstLineByItem[id] = result.Line.LineNumber;
            }
        }
    }

    private static List<CatalogEntry> Distinct(IEnumerable<CatalogEntry> entries)
    {
        var seen = new HashSet<Guid>();
        var list = new List<CatalogEntry>();
        foreach (var entry in entries)
        {
            if (seen.Add(entry.Id))
            {
                list.Add(entry);
            }
        }

        return list;
    }

    private static List<CatalogEntry> Ordered(IEnumerable<CatalogEntry> entries)
    {
        return [.. entries
            .OrderBy(e => e.Year ?? int.MaxValue)
            .ThenBy(e => e.Kind)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private MatchResult MatchLine(ParsedLine line, MatchOptions options)
    {
        if (line.TmdbId is > 0 && MatchTmdbId(line, line.TmdbId.Value, options) is { } byId)
        {
            return byId;
        }

        var result = MatchTitle(line, line.Title, line.Year, options);
        if (result is null && line.BareYearTitle is not null && line.BareYear is not null)
        {
            result = MatchTitle(line, line.BareYearTitle, line.BareYear, options);
        }

        if (result is null && options.Fuzzy)
        {
            result = MatchFuzzy(line, line.Title, line.Year, options);
            if (result is null && line.BareYearTitle is not null && line.BareYear is not null)
            {
                result = MatchFuzzy(line, line.BareYearTitle, line.BareYear, options);
            }
        }

        return result ?? new MatchResult(line, MatchStatus.NotFound, null, [], null, false, null, null);
    }

    private MatchResult? MatchTmdbId(ParsedLine line, int tmdbId, MatchOptions options)
    {
        if (!_byTmdb.TryGetValue(tmdbId, out var hits) || hits.Count == 0)
        {
            return null;
        }

        if (line.RemoteKind is { } kind)
        {
            hits = [.. hits.Where(entry => entry.Kind == kind)];
            if (hits.Count == 0)
            {
                return null;
            }
        }

        var pool = Ordered(Distinct(hits));
        return pool.Count == 1
            ? Selected(line, pool[0], options)
            : Build(line, MatchStatus.Ambiguous, null, pool, options, note: "Several library items share this TMDB id. Choose one.");
    }

    private MatchResult? MatchTitle(ParsedLine line, string title, int? year, MatchOptions options)
    {
        var strict = _strict.GetValueOrDefault(TitleNormalizer.Strict(title)) ?? [];
        var loose = _loose.GetValueOrDefault(TitleNormalizer.Loose(title)) ?? [];
        if (line.RemoteKind is { } kind)
        {
            strict = [.. strict.Where(entry => entry.Kind == kind)];
            loose = [.. loose.Where(entry => entry.Kind == kind)];
        }

        if (strict.Count == 0 && loose.Count == 0)
        {
            return null;
        }

        if (year is null)
        {
            var pool = Ordered(Distinct(strict.Count > 0 ? strict : loose));
            return pool.Count == 1
                ? Selected(line, pool[0], options)
                : Build(line, MatchStatus.Ambiguous, null, pool, options, note: "Several items have this title. Choose one, or add all.");
        }

        // A candidate with the same title and the same year wins over everything else.
        var exact = Distinct(strict.Where(e => e.Year == year));
        if (exact.Count == 0)
        {
            exact = Distinct(loose.Where(e => e.Year == year));
        }

        if (exact.Count == 1)
        {
            return Selected(line, exact[0], options);
        }

        if (exact.Count > 1)
        {
            return Build(line, MatchStatus.Ambiguous, null, Ordered(exact), options, note: "Several items have this title and year. Choose one, or add all.");
        }

        var titled = Ordered(Distinct(strict.Concat(loose)));

        var offByOne = titled.Where(e => e.Year is { } y && Math.Abs(y - year.Value) == 1).ToList();
        if (options.YearTolerance >= 1 && offByOne.Count > 0)
        {
            return Build(
                line,
                MatchStatus.PossibleYearOffByOne,
                offByOne.Count == 1 ? offByOne[0].Id : null,
                offByOne,
                options,
                note: string.Create(CultureInfo.InvariantCulture, $"Library year is {offByOne[0].Year}, list says {year}."));
        }

        var noYear = titled.Where(e => e.Year is null).ToList();
        if (noYear.Count > 0)
        {
            return Build(line, MatchStatus.Ambiguous, null, noYear, options, note: "The library item has no year, so the year can't be confirmed. Choose it to add it.");
        }

        var years = string.Join(", ", titled.Select(e => e.Year!.Value.ToString(CultureInfo.InvariantCulture)).Distinct());
        return Build(
            line,
            MatchStatus.YearMismatch,
            null,
            titled,
            options,
            note: string.Create(CultureInfo.InvariantCulture, $"Library has this title from {years}, not {year}."));
    }

    private MatchResult? MatchFuzzy(ParsedLine line, string title, int? year, MatchOptions options)
    {
        var hits = _fuzzy.Find(TitleNormalizer.Loose(title), options.FuzzyThreshold);
        var accepted = new List<MatchCandidate>();
        var seen = new HashSet<Guid>();
        foreach (var (entry, similarity) in hits)
        {
            if (line.RemoteKind is { } kind && entry.Kind != kind)
            {
                continue;
            }

            if (year is { } y && entry.Year is { } ey && Math.Abs(ey - y) > 1)
            {
                continue;
            }

            if (seen.Add(entry.Id))
            {
                accepted.Add(new MatchCandidate(entry, options.ExistingMemberIds.Contains(entry.Id), similarity));
            }
        }

        if (accepted.Count == 0)
        {
            return null;
        }

        var best = accepted[0];
        return new MatchResult(
            line,
            MatchStatus.PossibleFuzzy,
            best.Entry.Id,
            accepted,
            best.Similarity,
            false,
            null,
            string.Create(CultureInfo.InvariantCulture, $"Close spelling ({best.Similarity:P0} similar). Check it before adding."));
    }

    private static MatchResult Selected(ParsedLine line, CatalogEntry entry, MatchOptions options)
    {
        var inCollection = options.ExistingMemberIds.Contains(entry.Id);
        return new MatchResult(
            line,
            inCollection ? MatchStatus.AlreadyInCollection : MatchStatus.Matched,
            entry.Id,
            [new MatchCandidate(entry, inCollection)],
            null,
            !inCollection,
            null,
            inCollection ? "Already in this collection." : null);
    }

    private static MatchResult Build(
        ParsedLine line,
        MatchStatus status,
        Guid? selectedId,
        IReadOnlyList<CatalogEntry> entries,
        MatchOptions options,
        string? note)
    {
        var candidates = entries
            .Select(e => new MatchCandidate(e, options.ExistingMemberIds.Contains(e.Id)))
            .ToList();
        return new MatchResult(line, status, selectedId, candidates, null, false, null, note);
    }
}
