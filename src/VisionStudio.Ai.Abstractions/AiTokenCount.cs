using System.Globalization;

namespace VisionStudio.Ai.Abstractions;

/// <summary>
/// Parses human token budgets such as <c>128K</c>, <c>1M</c>, <c>1.5M</c> or a plain count.
/// K/M/G are binary multiples (1024/1048576/1073741824) which is what context windows quote.
/// </summary>
public static class AiTokenCount {
  private static readonly string[] Suffixes = ["", "K", "M", "G"];

  public static bool TryParse(string? text, out int tokens) {
    tokens = 0;
    if (string.IsNullOrWhiteSpace(text)) {
      return false;
    }

    string value = new([.. text.Where(character => !char.IsWhiteSpace(character) && character is not '_' and not ',')]);
    if (value.Length == 0) {
      return false;
    }

    int multiplierIndex = char.ToUpperInvariant(value[^1]) switch {
      'K' => 1,
      'M' => 2,
      'G' => 3,
      _ => 0
    };
    string number = multiplierIndex == 0 ? value : value[..^1];
    if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double amount) || amount <= 0) {
      return false;
    }

    double scaled = amount * Math.Pow(1024, multiplierIndex);
    if (scaled > int.MaxValue) {
      return false;
    }

    tokens = (int)Math.Round(scaled, MidpointRounding.AwayFromZero);
    return tokens > 0;
  }

  /// <summary>Shortens a count to <c>128K</c> / <c>1M</c> when it divides evenly.</summary>
  public static string Format(int tokens) {
    for (int index = Suffixes.Length - 1; index > 0; index--) {
      int unit = (int)Math.Pow(1024, index);
      if (tokens >= unit && tokens % unit == 0) {
        return (tokens / unit).ToString(CultureInfo.InvariantCulture) + Suffixes[index];
      }
    }

    return tokens.ToString(CultureInfo.InvariantCulture);
  }
}
