using System.Globalization;

namespace NovaNein.Server;

internal static class WorkItemOrdering
{
    private static readonly HashSet<string> SupportedFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "docNum",
        "invoiceNumber",
        "businessPartner",
        "documentDate",
        "entryDate",
        "grossAmount",
        "status"
    };

    public static IEnumerable<WorkItem> Apply(IEnumerable<WorkItem> items, string? sortBy, string? sortDirection)
    {
        var field = string.IsNullOrWhiteSpace(sortBy) ? "entryDate" : sortBy.Trim();
        var descending = string.Equals(sortDirection?.Trim(), "desc", StringComparison.OrdinalIgnoreCase);
        return items.OrderBy(item => item, new WorkItemComparer(field, descending));
    }

    public static void Validate(string? sortBy, string? sortDirection)
    {
        if (!string.IsNullOrWhiteSpace(sortBy) && !SupportedFields.Contains(sortBy.Trim()))
            throw new ArgumentException("Sortierung ist für dieses Feld nicht verfügbar.", nameof(sortBy));

        if (!string.IsNullOrWhiteSpace(sortDirection)
            && !string.Equals(sortDirection.Trim(), "asc", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(sortDirection.Trim(), "desc", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Sortierrichtung muss asc oder desc sein.", nameof(sortDirection));
    }

    private sealed class WorkItemComparer(string field, bool descending) : IComparer<WorkItem>
    {
        public int Compare(WorkItem? left, WorkItem? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return 1;
            if (right is null) return -1;

            var missing = CompareMissing(left, right);
            if (missing != 0) return missing;

            var primary = field.ToLowerInvariant() switch
            {
                "docnum" => CompareNumber(left.DocNum, right.DocNum),
                "invoicenumber" => CompareText(left.InvoiceNumber, right.InvoiceNumber),
                "businesspartner" => CompareText(left.BusinessPartner, right.BusinessPartner),
                "documentdate" => CompareNullable(left.DocumentDate, right.DocumentDate),
                "entrydate" => CompareNullable(left.EntryDate, right.EntryDate),
                "grossamount" => CompareNullable(left.GrossAmount, right.GrossAmount),
                "status" => CompareText(left.OverallLabel, right.OverallLabel),
                _ => 0
            };
            if (primary != 0) return descending ? -primary : primary;

            var direction = StringComparer.OrdinalIgnoreCase.Compare(left.Direction, right.Direction);
            return direction != 0 ? direction : left.DocNum.CompareTo(right.DocNum);
        }

        private int CompareMissing(WorkItem left, WorkItem right)
        {
            var leftMissing = IsMissing(left);
            var rightMissing = IsMissing(right);
            if (leftMissing == rightMissing) return 0;
            return leftMissing ? 1 : -1;
        }

        private bool IsMissing(WorkItem item) => field.ToLowerInvariant() switch
        {
            "docnum" => item.DocNum <= 0,
            "invoicenumber" => string.IsNullOrWhiteSpace(item.InvoiceNumber),
            "businesspartner" => string.IsNullOrWhiteSpace(item.BusinessPartner),
            "documentdate" => !item.DocumentDate.HasValue,
            "entrydate" => !item.EntryDate.HasValue,
            "grossamount" => !item.GrossAmount.HasValue,
            "status" => string.IsNullOrWhiteSpace(item.OverallLabel),
            _ => false
        };

        private static int CompareNumber(int left, int right)
        {
            if (left <= 0 || right <= 0)
            {
                if (left <= 0 && right <= 0) return 0;
                return left <= 0 ? 1 : -1;
            }
            return left.CompareTo(right);
        }

        private static int CompareText(string? left, string? right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return 0;
            return CompareNatural(left, right);
        }

        private static int CompareNatural(string left, string right)
        {
            var compareInfo = CultureInfo.GetCultureInfo("de-DE").CompareInfo;
            var leftIndex = 0;
            var rightIndex = 0;
            while (leftIndex < left.Length && rightIndex < right.Length)
            {
                if (char.IsDigit(left[leftIndex]) && char.IsDigit(right[rightIndex]))
                {
                    var leftEnd = leftIndex;
                    var rightEnd = rightIndex;
                    while (leftEnd < left.Length && char.IsDigit(left[leftEnd])) leftEnd++;
                    while (rightEnd < right.Length && char.IsDigit(right[rightEnd])) rightEnd++;

                    var leftSignificant = leftIndex;
                    var rightSignificant = rightIndex;
                    while (leftSignificant < leftEnd - 1 && left[leftSignificant] == '0') leftSignificant++;
                    while (rightSignificant < rightEnd - 1 && right[rightSignificant] == '0') rightSignificant++;
                    var leftDigits = leftEnd - leftSignificant;
                    var rightDigits = rightEnd - rightSignificant;
                    if (leftDigits != rightDigits) return leftDigits.CompareTo(rightDigits);
                    var digitCompare = string.CompareOrdinal(left, leftSignificant, right, rightSignificant, leftDigits);
                    if (digitCompare != 0) return digitCompare;

                    leftIndex = leftEnd;
                    rightIndex = rightEnd;
                    continue;
                }

                var textCompare = compareInfo.Compare(left, leftIndex, 1, right, rightIndex, 1, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace);
                if (textCompare != 0) return textCompare;
                leftIndex++;
                rightIndex++;
            }
            return (left.Length - leftIndex).CompareTo(right.Length - rightIndex);
        }

        private static int CompareNullable<T>(T? left, T? right) where T : struct, IComparable<T>
        {
            if (!left.HasValue || !right.HasValue) return 0;
            return left.Value.CompareTo(right.Value);
        }
    }
}
