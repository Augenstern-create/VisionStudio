using System.Text.RegularExpressions;
using VisionStudio.Core;

namespace VisionStudio.Ai;

public sealed record OperatorSearchResult(OperatorDescriptor Descriptor, double Score, IReadOnlyList<string> Matches);

public sealed class OperatorSearchService {
  public IReadOnlyList<OperatorSearchResult> Search(IEnumerable<OperatorDescriptor> descriptors, string query, int limit = 8) {
    ArgumentException.ThrowIfNullOrWhiteSpace(query);
    if (limit is < 1 or > 50) {
      throw new ArgumentOutOfRangeException(nameof(limit));
    }

    var terms = Tokenize(query);
    return [.. descriptors.Select(descriptor => Score(descriptor, terms))
        .Where(result => result.Score > 0)
        .OrderByDescending(result => result.Score)
        .ThenBy(result => result.Descriptor.Type, StringComparer.Ordinal)
        .Take(limit)];
  }

  private static OperatorSearchResult Score(OperatorDescriptor descriptor, IReadOnlyList<string> terms) {
    var fields = new List<(string Text, double Weight)>
    {
            (descriptor.Type, 5), (descriptor.DisplayName, 5), (descriptor.EnglishName ?? "", 4),
            (descriptor.Category, 3), (descriptor.Description ?? "", 2), (descriptor.EnglishDescription ?? "", 2)
        };
    fields.AddRange((descriptor.Aliases ?? []).Select(value => (value, 5d)));
    fields.AddRange((descriptor.UseCases ?? []).Select(value => (value, 4d)));
    fields.AddRange((descriptor.Keywords ?? []).Select(value => (value, 3d)));
    var matches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    double score = 0d;
    foreach (string term in terms) {
      foreach (var (Text, Weight) in fields) {
        if (string.IsNullOrWhiteSpace(Text) || !Text.Contains(term, StringComparison.OrdinalIgnoreCase)) {
          continue;
        }

        score += Weight;
        matches.Add(term);
      }
    }

    return new(descriptor, score, [.. matches.Order(StringComparer.OrdinalIgnoreCase)]);
  }

  private static IReadOnlyList<string> Tokenize(string query) {
    string normalized = query.Trim().ToLowerInvariant();
    var tokens = Regex.Matches(normalized, @"[a-z0-9_.]+|[\p{IsCJKUnifiedIdeographs}]{1,4}")
        .Select(match => match.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    foreach (string? keyword in new[] { "裂纹", "裂缝", "数量", "长度", "像素", "crack", "length", "count" }) {
      if (normalized.Contains(keyword, StringComparison.OrdinalIgnoreCase)) {
        tokens.Add(keyword);
      }
    }

    return [.. tokens.Distinct(StringComparer.OrdinalIgnoreCase)];
  }
}
