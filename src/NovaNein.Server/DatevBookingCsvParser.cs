using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NovaNein.Server;

public sealed class DatevBookingCsvParser
{
	public const string ParserVersion = "datev-bookings-v1";

	private static readonly string[] RequiredColumns = new string[5] { "Umsatz", "Soll/Haben", "Konto", "Gegenkonto", "Belegdatum" };

	public DatevBookingParseResult Parse(byte[] content)
	{
		if (content == null || content.Length == 0)
		{
			throw new InvalidDataException("Die DATEV-CSV ist leer.");
		}
		if (content.Length > 52428800)
		{
			throw new InvalidDataException("Die DATEV-CSV überschreitet 50 MB.");
		}
		var (text, encodingName) = Decode(content);
		if (text.IndexOf('\0') >= 0)
		{
			throw new InvalidDataException("Die DATEV-Datei enthält Binärdaten.");
		}
		char delimiter = DetectDelimiter(text);
		List<string[]> records = ParseRecords(text, delimiter);
		int headerIndex = records.FindIndex((string[] source) => source.Any((string value) => NormalizeHeader(value).Contains("UMSATZ", StringComparison.Ordinal)) && source.Any((string value) => NormalizeHeader(value).Contains("BELEGDATUM", StringComparison.Ordinal)));
		if (headerIndex < 0)
		{
			throw new InvalidDataException("Die Kopfzeile eines DATEV-Buchungsstapels wurde nicht gefunden.");
		}
		string[] header = records[headerIndex].Select(NormalizeHeader).ToArray();
		List<AccountingImportIssue> issues = new List<AccountingImportIssue>();
		string[] requiredColumns = RequiredColumns;
		foreach (string required in requiredColumns)
		{
			if (!header.Any((string value) => value.Contains(NormalizeHeader(required), StringComparison.Ordinal)))
			{
				issues.Add(new AccountingImportIssue(AccountingIssueSeverity.Error, headerIndex + 1, "Pflichtspalte '" + required + "' fehlt."));
			}
		}
		if (issues.Any((AccountingImportIssue x) => x.Severity == AccountingIssueSeverity.Error))
		{
			return new DatevBookingParseResult(Array.Empty<DatevBookingRowDraft>(), issues, null, null, encodingName, delimiter);
		}
		Dictionary<string, int> columns = BuildColumnMap(header);
		List<DatevBookingRowDraft> rows = new List<DatevBookingRowDraft>();
		for (int index = headerIndex + 1; index < records.Count; index++)
		{
			string[] fields = records[index];
			if (fields.All(string.IsNullOrWhiteSpace))
			{
				continue;
			}
			int rowNumber = index + 1;
			try
			{
				decimal amount = ParseAmount(Value(fields, columns, "amount"));
				string debitCredit = Value(fields, columns, "debitCredit").Trim().ToUpperInvariant();
				if ((!(debitCredit == "S") && !(debitCredit == "H")) || 1 == 0)
				{
					throw new InvalidDataException("Soll/Haben-Kennzeichen muss S oder H sein.");
				}
				DateOnly documentDate = ParseDate(Value(fields, columns, "documentDate"));
				string currency = Value(fields, columns, "currency").Trim().ToUpperInvariant();
				if (string.IsNullOrWhiteSpace(currency))
				{
					currency = "EUR";
				}
				if (currency.Length != 3 || currency.Any((char character) => !char.IsLetter(character)))
				{
					throw new InvalidDataException("Währung muss ein dreistelliger Code sein.");
				}
				string account = Digits(Value(fields, columns, "account"));
				string counterAccount = Digits(Value(fields, columns, "counterAccount"));
				if (string.IsNullOrEmpty(account) || string.IsNullOrEmpty(counterAccount))
				{
					throw new InvalidDataException("Konto und Gegenkonto sind erforderlich.");
				}
				string reference1 = Clean(Value(fields, columns, "reference1"), 100);
				string reference2 = Clean(Value(fields, columns, "reference2"), 100);
				string normalizedReference = NormalizeReference((!string.IsNullOrWhiteSpace(reference1)) ? reference1 : reference2);
				if (string.IsNullOrWhiteSpace(normalizedReference))
				{
					throw new InvalidDataException("Belegfeld 1 oder 2 muss eine Rechnungsreferenz enthalten.");
				}
				string bookingText = Clean(Value(fields, columns, "bookingText"), 250);
				string buCode = Digits(Value(fields, columns, "buCode"));
				var raw = fields.Select((string value, int ordinal) => new
				{
					column = ((ordinal < header.Length) ? header[ordinal] : $"SPALTE_{ordinal + 1}"),
					value = value
				}).ToArray();
				string rawJson = JsonSerializer.Serialize(raw);
				string rowHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{documentDate:yyyy-MM-dd}|{amount:F2}|{debitCredit}|{currency}|{account}|{counterAccount}|{buCode}|{reference1}|{reference2}|{bookingText}")));
				rows.Add(new DatevBookingRowDraft(rowNumber, documentDate, amount, debitCredit, currency, account, counterAccount, buCode, reference1, reference2, bookingText, normalizedReference, counterAccount, rowHash, rawJson));
			}
			catch (Exception ex) when (((ex is InvalidDataException || ex is FormatException || ex is OverflowException) ? 1 : 0) != 0)
			{
				issues.Add(new AccountingImportIssue(AccountingIssueSeverity.Error, rowNumber, ex.Message));
			}
		}
		if (rows.Count == 0 && issues.All((AccountingImportIssue x) => x.Severity != AccountingIssueSeverity.Error))
		{
			issues.Add(new AccountingImportIssue(AccountingIssueSeverity.Error, null, "Die DATEV-Datei enthält keine Buchungszeilen."));
		}
		DateOnly[] dates = rows.Select((DatevBookingRowDraft row) => row.DocumentDate).ToArray();
		return new DatevBookingParseResult(rows, issues, (dates.Length == 0) ? ((DateOnly?)null) : new DateOnly?(dates.Min()), (dates.Length == 0) ? ((DateOnly?)null) : new DateOnly?(dates.Max()), encodingName, delimiter);
	}

	public static string NormalizeReference(string value)
	{
		return new string((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
	}

	private static (string Text, string Name) Decode(byte[] bytes)
	{
		if (bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble))
		{
			return (Text: Encoding.UTF8.GetString(bytes, Encoding.UTF8.Preamble.Length, bytes.Length - Encoding.UTF8.Preamble.Length), Name: "utf-8-bom");
		}
		try
		{
			UTF8Encoding strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
			return (Text: strictUtf8.GetString(bytes), Name: "utf-8");
		}
		catch (DecoderFallbackException)
		{
			Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
			return (Text: Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetString(bytes), Name: "windows-1252");
		}
	}

	private static char DetectDelimiter(string text)
	{
		string line = text.Split(new char[2] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault((string value) => value.Contains("Umsatz", StringComparison.OrdinalIgnoreCase) || value.Contains("Belegdatum", StringComparison.OrdinalIgnoreCase)) ?? text.Split(new char[2] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
		if (line.Count((char character) => character == ';') < line.Count((char character) => character == ','))
		{
			return ',';
		}
		return ';';
	}

	private static List<string[]> ParseRecords(string text, char delimiter)
	{
		List<string[]> records = new List<string[]>();
		List<string> row = new List<string>();
		StringBuilder field = new StringBuilder();
		bool quoted = false;
		for (int index = 0; index < text.Length; index++)
		{
			char current = text[index];
			if (current == '"')
			{
				if (quoted && index + 1 < text.Length && text[index + 1] == '"')
				{
					field.Append('"');
					index++;
				}
				else
				{
					quoted = !quoted;
				}
			}
			else if (current == delimiter && !quoted)
			{
				row.Add(field.ToString());
				field.Clear();
			}
			else if ((current == '\r' || current == '\n') && !quoted)
			{
				if (current == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
				{
					index++;
				}
				row.Add(field.ToString());
				field.Clear();
				records.Add(row.ToArray());
				row.Clear();
			}
			else
			{
				field.Append(current);
			}
		}
		if (quoted)
		{
			throw new InvalidDataException("Die DATEV-CSV enthält ein nicht geschlossenes Anführungszeichen.");
		}
		if (field.Length > 0 || row.Count > 0)
		{
			row.Add(field.ToString());
			records.Add(row.ToArray());
		}
		return records;
	}

	private static Dictionary<string, int> BuildColumnMap(string[] header)
	{
		Dictionary<string, int> dictionary = new Dictionary<string, int>();
		dictionary["amount"] = Find(new string[2] { "UMSATZOHNESOLLHABENKZ", "UMSATZ" });
		dictionary["debitCredit"] = Find(new string[2] { "SOLLHABENKENNZEICHEN", "SOLLHABENKZ" });
		dictionary["currency"] = Find(new string[3] { "WKZUMSATZ", "WAEHRUNG", "WKZ" });
		dictionary["account"] = Find(new string[1] { "KONTO" });
		dictionary["counterAccount"] = Find(new string[2] { "GEGENKONTOOHNEBUSCHLUESSEL", "GEGENKONTO" });
		dictionary["buCode"] = Find(new string[2] { "BUSCHLUESSEL", "BU" });
		dictionary["documentDate"] = Find(new string[1] { "BELEGDATUM" });
		dictionary["reference1"] = Find(new string[1] { "BELEGFELD1" });
		dictionary["reference2"] = Find(new string[1] { "BELEGFELD2" });
		dictionary["bookingText"] = Find(new string[1] { "BUCHUNGSTEXT" });
		return dictionary;
		int Find(string[] candidates)
		{
			string[] array = candidates;
			foreach (string candidate in array)
			{
				int exact = Array.FindIndex(header, (string value) => string.Equals(value, candidate, StringComparison.Ordinal));
				if (exact >= 0)
				{
					return exact;
				}
			}
			return Array.FindIndex(header, (string value) => candidates.Any((string value2) => value.StartsWith(value2, StringComparison.Ordinal)));
		}
	}

	private static string Value(string[] fields, IReadOnlyDictionary<string, int> columns, string key)
	{
		if (!columns.TryGetValue(key, out var index) || index < 0 || index >= fields.Length)
		{
			return string.Empty;
		}
		return fields[index];
	}

	private static string NormalizeHeader(string value)
	{
		return new string((from character in (value ?? string.Empty).Normalize(NormalizationForm.FormD)
			where CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(character)
			select character).Select(char.ToUpperInvariant).ToArray()).Replace("SCHLUSSEL", "SCHLUESSEL", StringComparison.Ordinal);
	}

	private static decimal ParseAmount(string value)
	{
		string compact = (value ?? string.Empty).Trim().Replace(" ", string.Empty);
		if (compact.Contains(',') && compact.Contains('.'))
		{
			compact = ((compact.LastIndexOf(',') > compact.LastIndexOf('.')) ? compact.Replace(".", string.Empty).Replace(',', '.') : compact.Replace(",", string.Empty));
		}
		else if (compact.Contains(','))
		{
			compact = compact.Replace(',', '.');
		}
		if (!decimal.TryParse(compact, NumberStyles.Number, CultureInfo.InvariantCulture, out var result) || result < 0m)
		{
			throw new InvalidDataException("Umsatz ist kein gültiger positiver Betrag.");
		}
		return decimal.Round(result, 2, MidpointRounding.AwayFromZero);
	}

	private static DateOnly ParseDate(string value)
	{
		string trimmed = value.Trim();
		if (trimmed.Length == 4 && int.TryParse(trimmed.Substring(0, 2), out var day))
		{
			string text = trimmed;
			if (int.TryParse(text.Substring(2, text.Length - 2), out var month))
			{
				try
				{
					return new DateOnly(DateTime.Today.Year, month, day);
				}
				catch (ArgumentOutOfRangeException)
				{
					throw new InvalidDataException("Belegdatum ist ungültig.");
				}
			}
		}
		string[] formats = new string[3] { "ddMMyyyy", "dd.MM.yyyy", "yyyy-MM-dd" };
		string[] array = formats;
		foreach (string format in array)
		{
			if (DateOnly.TryParseExact(trimmed, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
			{
				return parsed;
			}
		}
		throw new InvalidDataException("Belegdatum ist ungültig.");
	}

	private static string Digits(string value)
	{
		return new string((value ?? string.Empty).Where(char.IsDigit).ToArray());
	}

	private static string Clean(string value, int maximum)
	{
		string clean = string.Join(' ', (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
		if (clean.Length > maximum)
		{
			return clean.Substring(0, maximum);
		}
		return clean;
	}
}
