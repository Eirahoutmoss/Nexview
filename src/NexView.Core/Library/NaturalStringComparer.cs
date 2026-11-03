using System.Globalization;

namespace NexView.Core.Library;

/// <summary>"img2" &lt; "img10" sıralaması (Windows Explorer'a benzer).</summary>
public sealed class NaturalStringComparer : IComparer<string>
{
    public static readonly NaturalStringComparer Instance = new();
    private static readonly CompareInfo Compare = CultureInfo.InvariantCulture.CompareInfo;

    int IComparer<string>.Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                int si = i, sj = j;
                while (si < x.Length && x[si] == '0') si++;
                while (sj < y.Length && y[sj] == '0') sj++;
                int ei = si, ej = sj;
                while (ei < x.Length && char.IsDigit(x[ei])) ei++;
                while (ej < y.Length && char.IsDigit(y[ej])) ej++;
                int lenCmp = (ei - si).CompareTo(ej - sj);
                if (lenCmp != 0) return lenCmp;
                int c = string.CompareOrdinal(x, si, y, sj, ei - si);
                if (c != 0) return c;
                i = ei; j = ej;
            }
            else
            {
                int ei = i, ej = j;
                while (ei < x.Length && !char.IsDigit(x[ei])) ei++;
                while (ej < y.Length && !char.IsDigit(y[ej])) ej++;
                int c = Compare.Compare(x, i, ei - i, y, j, ej - j, CompareOptions.IgnoreCase);
                if (c != 0) return c;
                i = ei; j = ej;
            }
        }
        return (x.Length - i).CompareTo(y.Length - j);
    }
}
