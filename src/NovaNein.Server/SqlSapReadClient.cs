using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace NovaNein.Server;

public sealed class SqlSapReadClient(IConfiguration configuration, DatevMappingStore? datevMappingStore = null) : ISapSqlReadClient
{
	internal static readonly IReadOnlyDictionary<SapDocumentKind, string> AllowedDocumentTables = new Dictionary<SapDocumentKind, string>
	{
		[SapDocumentKind.PurchaseInvoice] = "OPCH",
		[SapDocumentKind.Invoice] = "OINV",
		[SapDocumentKind.PurchaseCreditNote] = "ORPC",
		[SapDocumentKind.CreditNote] = "ORIN"
	};

	internal const string MissingPdfSql = "SELECT CAST(0 AS int) AS [Kind], d.[DocEntry], d.[DocNum], d.[CreateDate], d.[AtcEntry]\nFROM [dbo].[OPCH] AS d\nWHERE d.[CreateDate] >= @fromEntryDate\n  AND d.[CreateDate] < DATEADD(day, 1, @toEntryDate)\n  AND NOT EXISTS (\n      SELECT 1 FROM [dbo].[ATC1] AS a\n      WHERE a.[AbsEntry] = d.[AtcEntry]\n        AND LOWER(LTRIM(RTRIM(a.[FileExt]))) = 'pdf')\nUNION ALL\nSELECT CAST(1 AS int), d.[DocEntry], d.[DocNum], d.[CreateDate], d.[AtcEntry]\nFROM [dbo].[OINV] AS d\nWHERE d.[CreateDate] >= @fromEntryDate\n  AND d.[CreateDate] < DATEADD(day, 1, @toEntryDate)\n  AND NOT EXISTS (\n      SELECT 1 FROM [dbo].[ATC1] AS a\n      WHERE a.[AbsEntry] = d.[AtcEntry]\n        AND LOWER(LTRIM(RTRIM(a.[FileExt]))) = 'pdf')\nUNION ALL\nSELECT CAST(2 AS int), d.[DocEntry], d.[DocNum], d.[CreateDate], d.[AtcEntry]\nFROM [dbo].[ORPC] AS d\nWHERE d.[CreateDate] >= @fromEntryDate\n  AND d.[CreateDate] < DATEADD(day, 1, @toEntryDate)\n  AND NOT EXISTS (\n      SELECT 1 FROM [dbo].[ATC1] AS a\n      WHERE a.[AbsEntry] = d.[AtcEntry]\n        AND LOWER(LTRIM(RTRIM(a.[FileExt]))) = 'pdf')\nUNION ALL\nSELECT CAST(3 AS int), d.[DocEntry], d.[DocNum], d.[CreateDate], d.[AtcEntry]\nFROM [dbo].[ORIN] AS d\nWHERE d.[CreateDate] >= @fromEntryDate\n  AND d.[CreateDate] < DATEADD(day, 1, @toEntryDate)\n  AND NOT EXISTS (\n      SELECT 1 FROM [dbo].[ATC1] AS a\n      WHERE a.[AbsEntry] = d.[AtcEntry]\n        AND LOWER(LTRIM(RTRIM(a.[FileExt]))) = 'pdf')\nORDER BY [CreateDate], [Kind], [DocNum];";

	internal const string ReadinessSql = "SELECT TOP (1) [DocEntry] FROM [dbo].[OINV] ORDER BY [DocEntry] DESC;";

	internal const string SupplierMatchingSql = """
		SELECT TOP (1000)
		       bp.[CardCode],
		       ISNULL(bp.[CardName], '') AS [CardName],
		       ISNULL(bp.[LicTradNum], '') AS [VatId],
		       ISNULL(bp.[AddID], '') AS [TaxNumber],
		       ISNULL(bank.[IBAN], '') AS [Iban],
		       ISNULL(address.[Street], '') AS [Street],
		       ISNULL(address.[ZipCode], '') AS [PostalCode],
		       ISNULL(address.[City], '') AS [City]
		FROM [dbo].[OCRD] AS bp
		OUTER APPLY (
		    SELECT TOP (1) b.[IBAN]
		    FROM [dbo].[OCRB] AS b
		    WHERE b.[CardCode] = bp.[CardCode] AND NULLIF(LTRIM(RTRIM(b.[IBAN])), '') IS NOT NULL
		    ORDER BY b.[AbsEntry]
		) AS bank
		OUTER APPLY (
		    SELECT TOP (1) a.[Street], a.[ZipCode], a.[City]
		    FROM [dbo].[CRD1] AS a
		    WHERE a.[CardCode] = bp.[CardCode]
		    ORDER BY CASE WHEN a.[AdresType] = 'B' THEN 0 WHEN a.[AdresType] = 'S' THEN 1 ELSE 2 END, a.[LineNum]
		) AS address
		WHERE bp.[CardType] = 'S' AND ISNULL(bp.[validFor], 'Y') = 'Y'
		ORDER BY bp.[CardCode];
		""";

	internal const string SupplierCodingHistorySql = """
		SELECT TOP (100)
		       ISNULL(line.[AcctCode], '') AS [Account],
		       ISNULL(line.[VatGroup], '') AS [TaxCode],
		       ISNULL(line.[Dscription], '') AS [Description],
		       COUNT_BIG(*) AS [UsageCount]
		FROM [dbo].[OPCH] AS invoice
		INNER JOIN [dbo].[PCH1] AS line ON line.[DocEntry] = invoice.[DocEntry]
		WHERE invoice.[CardCode] = @cardCode
		  AND ISNULL(invoice.[CANCELED], 'N') = 'N'
		  AND NULLIF(LTRIM(RTRIM(line.[AcctCode])), '') IS NOT NULL
		  AND NULLIF(LTRIM(RTRIM(line.[VatGroup])), '') IS NOT NULL
		GROUP BY line.[AcctCode], line.[VatGroup], line.[Dscription]
		ORDER BY COUNT_BIG(*) DESC, line.[AcctCode], line.[VatGroup];
		""";

	internal const string AccountValidationSql = """
		SELECT TOP (1)
		       LTRIM(RTRIM(ISNULL(account.[AcctCode], ''))) AS [Account],
		       ISNULL(account.[AcctName], '') AS [Name],
		       CASE WHEN ISNULL(account.[Postable], 'N') = 'Y'
		            THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END AS [ActiveAccount]
		FROM [dbo].[OACT] AS account
		WHERE LTRIM(RTRIM(account.[AcctCode])) = @account;
		""";

	internal const string PurchaseInvoiceDuplicateSql = """
		SELECT TOP (1)
		       d.[DocEntry], d.[DocNum], d.[CardCode], d.[CardName], d.[NumAtCard],
		       d.[DocDate],
		       CASE WHEN d.[DocCur] = a.[MainCurncy] OR ISNULL(d.[DocTotalFC], 0) = 0
		            THEN d.[DocTotal] ELSE d.[DocTotalFC] END AS [DocTotal],
		       d.[DocCur], d.[AtcEntry], d.[CreateDate], d.[Comments]
		FROM [dbo].[OPCH] AS d
		CROSS JOIN (SELECT TOP (1) [MainCurncy] FROM [dbo].[OADM]) AS a
		WHERE d.[CardCode] = @cardCode
		  AND LTRIM(RTRIM(ISNULL(d.[NumAtCard], ''))) = @invoiceNumber
		  AND ISNULL(d.[CANCELED], 'N') = 'N'
		ORDER BY d.[DocEntry] DESC;
		""";

	internal static readonly IReadOnlyDictionary<SapDocumentKind, string> LineTables = new Dictionary<SapDocumentKind, string>
	{
		[SapDocumentKind.PurchaseInvoice] = "PCH1",
		[SapDocumentKind.Invoice] = "INV1",
		[SapDocumentKind.PurchaseCreditNote] = "RPC1",
		[SapDocumentKind.CreditNote] = "RIN1"
	};

	internal static readonly IReadOnlyDictionary<SapDocumentKind, string> TaxTables = new Dictionary<SapDocumentKind, string>
	{
		[SapDocumentKind.PurchaseInvoice] = "PCH4",
		[SapDocumentKind.Invoice] = "INV4",
		[SapDocumentKind.PurchaseCreditNote] = "RPC4",
		[SapDocumentKind.CreditNote] = "RIN4"
	};

	internal static string TaxBreakdownSql(SapDocumentKind kind)
	{
		if (!TaxTables.TryGetValue(kind, out var table)) throw new ArgumentOutOfRangeException(nameof(kind));
		if (!AllowedDocumentTables.TryGetValue(kind, out var headerTable)) throw new ArgumentOutOfRangeException(nameof(kind));
		return "SELECT ISNULL(t.[LineNum], 0) AS [LineNum], ISNULL(t.[StcCode], '') AS [TaxCode],\n"
			+ "       ISNULL(t.[TaxRate], 0) AS [Rate],\n"
			+ "       CASE WHEN d.[DocCur] = a.[MainCurncy] OR ISNULL(d.[DocTotalFC], 0) = 0 THEN ISNULL(t.[TaxSum], 0) ELSE ISNULL(t.[TaxSumFrgn], 0) END AS [TaxAmount],\n"
			+ "       CASE WHEN d.[DocCur] = a.[MainCurncy] OR ISNULL(d.[DocTotalFC], 0) = 0 THEN ISNULL(t.[BaseSum], 0) ELSE ISNULL(t.[BaseSumFrg], 0) END AS [NetAmount],\n"
			+ "       ISNULL(t.[TaxAcct], '') AS [TaxAccount],\n"
			+ "       COALESCE(NULLIF(t.[RvsChrgPrc], 0), ISNULL(sta.[RvsCrgPrc], 0)) AS [ReverseChargePercent],\n"
			+ "       ISNULL(t.[RvsChrgTax], 0) AS [ReverseChargeTaxAmount],\n"
			+ "       CASE WHEN ISNULL(t.[InGrossRev], 'N') = 'Y' THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END AS [IncludedInGrossRevenue],\n"
			+ "       ISNULL(d.[DocCur], '') AS [Currency]\n"
			+ "FROM [dbo].[" + table + "] AS t\n"
			+ "INNER JOIN [dbo].[" + headerTable + "] AS d ON d.[DocEntry] = t.[DocEntry]\n"
			+ "LEFT JOIN [dbo].[OSTA] AS sta ON sta.[Code] = t.[StaCode] AND sta.[Type] = t.[staType]\n"
			+ "CROSS JOIN (SELECT TOP (1) [MainCurncy] FROM [dbo].[OADM]) AS a\n"
			+ "WHERE t.[DocEntry] = @docEntry ORDER BY t.[LineNum];";
	}

	internal const string Avt1MappingSql = "SELECT TOP (1) [Code], [DatevCode]\nFROM [dbo].[AVT1]\nWHERE [Code] = @code AND [EffecDate] <= @documentDate\nORDER BY [EffecDate] DESC, [LogInstanc] DESC;";

	public async Task<SapDocumentSnapshot> GetDocumentAsync(SapDocumentKind kind, int docEntry, CancellationToken cancellationToken = default(CancellationToken))
	{
		if (docEntry <= 0)
		{
			throw new ArgumentOutOfRangeException("docEntry");
		}
		SqlConnection connection = NewReadOnlyConnection();
		SapDocumentSnapshot result;
		try
		{
			await ((DbConnection)(object)connection).OpenAsync(cancellationToken);
			SqlCommand val = new SqlCommand(DocumentSql(kind), connection);
			((DbCommand)val).CommandType = CommandType.Text;
			((DbCommand)val).CommandTimeout = CommandTimeoutSeconds();
			SqlCommand command = val;
			SapDocumentSnapshot sapDocumentSnapshot2;
			try
			{
				((DbParameter)(object)command.Parameters.Add("@docEntry", SqlDbType.Int)).Value = docEntry;
				SqlDataReader reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
				SapDocumentSnapshot sapDocumentSnapshot;
				try
				{
					if (!(await ((DbDataReader)(object)reader).ReadAsync(cancellationToken)))
					{
						throw new KeyNotFoundException($"SAP-Beleg {kind}/{docEntry} wurde nicht gefunden.");
					}
					sapDocumentSnapshot = ReadSnapshot(reader, kind);
				}
				finally
				{
					if (reader != null)
					{
						await ((DbDataReader)(object)reader).DisposeAsync();
					}
				}
				sapDocumentSnapshot2 = sapDocumentSnapshot;
			}
			finally
			{
				if (command != null)
				{
					await ((DbCommand)(object)command).DisposeAsync();
				}
			}
			result = sapDocumentSnapshot2;
		}
		finally
		{
			if (connection != null)
			{
				await ((DbConnection)(object)connection).DisposeAsync();
			}
		}
		return result;
	}

	public async Task<IReadOnlyList<SapAttachmentGap>> FindMissingPdfAttachmentsAsync(DateOnly fromEntryDate, DateOnly toEntryDate, CancellationToken cancellationToken = default(CancellationToken))
	{
		if (toEntryDate < fromEntryDate)
		{
			throw new ArgumentException("Der Endtag darf nicht vor dem Starttag liegen.");
		}
		SqlConnection connection = NewReadOnlyConnection();
		IReadOnlyList<SapAttachmentGap> result2;
		try
		{
			await ((DbConnection)(object)connection).OpenAsync(cancellationToken);
			SqlCommand val = new SqlCommand("SELECT CAST(0 AS int) AS [Kind], d.[DocEntry], d.[DocNum], d.[CreateDate], d.[AtcEntry]\nFROM [dbo].[OPCH] AS d\nWHERE d.[CreateDate] >= @fromEntryDate\n  AND d.[CreateDate] < DATEADD(day, 1, @toEntryDate)\n  AND NOT EXISTS (\n      SELECT 1 FROM [dbo].[ATC1] AS a\n      WHERE a.[AbsEntry] = d.[AtcEntry]\n        AND LOWER(LTRIM(RTRIM(a.[FileExt]))) = 'pdf')\nUNION ALL\nSELECT CAST(1 AS int), d.[DocEntry], d.[DocNum], d.[CreateDate], d.[AtcEntry]\nFROM [dbo].[OINV] AS d\nWHERE d.[CreateDate] >= @fromEntryDate\n  AND d.[CreateDate] < DATEADD(day, 1, @toEntryDate)\n  AND NOT EXISTS (\n      SELECT 1 FROM [dbo].[ATC1] AS a\n      WHERE a.[AbsEntry] = d.[AtcEntry]\n        AND LOWER(LTRIM(RTRIM(a.[FileExt]))) = 'pdf')\nUNION ALL\nSELECT CAST(2 AS int), d.[DocEntry], d.[DocNum], d.[CreateDate], d.[AtcEntry]\nFROM [dbo].[ORPC] AS d\nWHERE d.[CreateDate] >= @fromEntryDate\n  AND d.[CreateDate] < DATEADD(day, 1, @toEntryDate)\n  AND NOT EXISTS (\n      SELECT 1 FROM [dbo].[ATC1] AS a\n      WHERE a.[AbsEntry] = d.[AtcEntry]\n        AND LOWER(LTRIM(RTRIM(a.[FileExt]))) = 'pdf')\nUNION ALL\nSELECT CAST(3 AS int), d.[DocEntry], d.[DocNum], d.[CreateDate], d.[AtcEntry]\nFROM [dbo].[ORIN] AS d\nWHERE d.[CreateDate] >= @fromEntryDate\n  AND d.[CreateDate] < DATEADD(day, 1, @toEntryDate)\n  AND NOT EXISTS (\n      SELECT 1 FROM [dbo].[ATC1] AS a\n      WHERE a.[AbsEntry] = d.[AtcEntry]\n        AND LOWER(LTRIM(RTRIM(a.[FileExt]))) = 'pdf')\nORDER BY [CreateDate], [Kind], [DocNum];", connection);
			((DbCommand)val).CommandType = CommandType.Text;
			((DbCommand)val).CommandTimeout = CommandTimeoutSeconds();
			SqlCommand command = val;
			IReadOnlyList<SapAttachmentGap> readOnlyList2;
			try
			{
				((DbParameter)(object)command.Parameters.Add("@fromEntryDate", SqlDbType.Date)).Value = fromEntryDate.ToDateTime(TimeOnly.MinValue);
				((DbParameter)(object)command.Parameters.Add("@toEntryDate", SqlDbType.Date)).Value = toEntryDate.ToDateTime(TimeOnly.MinValue);
				List<SapAttachmentGap> result = new List<SapAttachmentGap>();
				SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
				IReadOnlyList<SapAttachmentGap> readOnlyList;
				try
				{
					while (await ((DbDataReader)(object)reader).ReadAsync(cancellationToken))
					{
						int attachmentOrdinal = ((DbDataReader)(object)reader).GetOrdinal("AtcEntry");
						result.Add(new SapAttachmentGap((SapDocumentKind)((DbDataReader)(object)reader).GetInt32(((DbDataReader)(object)reader).GetOrdinal("Kind")), ((DbDataReader)(object)reader).GetInt32(((DbDataReader)(object)reader).GetOrdinal("DocEntry")), ((DbDataReader)(object)reader).GetInt32(((DbDataReader)(object)reader).GetOrdinal("DocNum")), DateOnly.FromDateTime(((DbDataReader)(object)reader).GetDateTime(((DbDataReader)(object)reader).GetOrdinal("CreateDate"))), ((DbDataReader)(object)reader).IsDBNull(attachmentOrdinal) ? ((int?)null) : new int?(((DbDataReader)(object)reader).GetInt32(attachmentOrdinal))));
					}
					readOnlyList = result;
				}
				finally
				{
					if (reader != null)
					{
						await ((DbDataReader)(object)reader).DisposeAsync();
					}
				}
				readOnlyList2 = readOnlyList;
			}
			finally
			{
				if (command != null)
				{
					await ((DbCommand)(object)command).DisposeAsync();
				}
			}
			result2 = readOnlyList2;
		}
		finally
		{
			if (connection != null)
			{
				await ((DbConnection)(object)connection).DisposeAsync();
			}
		}
		return result2;
	}

	public async Task<IReadOnlyList<SapDocumentSnapshot>> ListDocumentsAsync(SapDocumentKind kind, DateOnly fromEntryDate, DateOnly toEntryDate, CancellationToken cancellationToken = default(CancellationToken))
	{
		if (toEntryDate < fromEntryDate)
		{
			throw new ArgumentException("Der Endtag darf nicht vor dem Starttag liegen.");
		}
		SqlConnection connection = NewReadOnlyConnection();
		IReadOnlyList<SapDocumentSnapshot> result2;
		try
		{
			await ((DbConnection)(object)connection).OpenAsync(cancellationToken);
			SqlCommand val = new SqlCommand(ListDocumentsSql(kind), connection);
			((DbCommand)val).CommandType = CommandType.Text;
			((DbCommand)val).CommandTimeout = CommandTimeoutSeconds();
			SqlCommand command = val;
			IReadOnlyList<SapDocumentSnapshot> readOnlyList2;
			try
			{
				((DbParameter)(object)command.Parameters.Add("@fromEntryDate", SqlDbType.Date)).Value = fromEntryDate.ToDateTime(TimeOnly.MinValue);
				((DbParameter)(object)command.Parameters.Add("@toEntryDate", SqlDbType.Date)).Value = toEntryDate.ToDateTime(TimeOnly.MinValue);
				List<SapDocumentSnapshot> result = new List<SapDocumentSnapshot>();
				SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
				IReadOnlyList<SapDocumentSnapshot> readOnlyList;
				try
				{
					while (await ((DbDataReader)(object)reader).ReadAsync(cancellationToken))
					{
						result.Add(ReadSnapshot(reader, kind));
					}
					readOnlyList = result;
				}
				finally
				{
					if (reader != null)
					{
						await ((DbDataReader)(object)reader).DisposeAsync();
					}
				}
				readOnlyList2 = readOnlyList;
			}
			finally
			{
				if (command != null)
				{
					await ((DbCommand)(object)command).DisposeAsync();
				}
			}
			result2 = readOnlyList2;
		}
		finally
		{
			if (connection != null)
			{
				await ((DbConnection)(object)connection).DisposeAsync();
			}
		}
		return result2;
	}

	public async Task<SapDocumentSnapshot?> FindDocumentByDocNumAsync(SapDocumentKind kind, int docNum, CancellationToken cancellationToken = default(CancellationToken))
	{
		if (docNum <= 0)
		{
			throw new ArgumentOutOfRangeException("docNum");
		}
		SqlConnection connection = NewReadOnlyConnection();
		SapDocumentSnapshot result;
		try
		{
			await ((DbConnection)(object)connection).OpenAsync(cancellationToken);
			SqlCommand val = new SqlCommand(DocumentSql(kind).Replace("WHERE d.[DocEntry] = @docEntry", "WHERE d.[DocNum] = @docNum", StringComparison.Ordinal), connection);
			((DbCommand)val).CommandType = CommandType.Text;
			((DbCommand)val).CommandTimeout = CommandTimeoutSeconds();
			SqlCommand command = val;
			SapDocumentSnapshot sapDocumentSnapshot2;
			try
			{
				((DbParameter)(object)command.Parameters.Add("@docNum", SqlDbType.Int)).Value = docNum;
				SqlDataReader reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
				SapDocumentSnapshot sapDocumentSnapshot;
				try
				{
					sapDocumentSnapshot = ((await ((DbDataReader)(object)reader).ReadAsync(cancellationToken)) ? ReadSnapshot(reader, kind) : null);
				}
				finally
				{
					if (reader != null)
					{
						await ((DbDataReader)(object)reader).DisposeAsync();
					}
				}
				sapDocumentSnapshot2 = sapDocumentSnapshot;
			}
			finally
			{
				if (command != null)
				{
					await ((DbCommand)(object)command).DisposeAsync();
				}
			}
			result = sapDocumentSnapshot2;
		}
		finally
		{
			if (connection != null)
			{
				await ((DbConnection)(object)connection).DisposeAsync();
			}
		}
		return result;
	}

	public async Task CheckReadinessAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		SqlConnection connection = NewReadOnlyConnection();
		try
		{
			await ((DbConnection)(object)connection).OpenAsync(cancellationToken);
			SqlCommand val = new SqlCommand("SELECT TOP (1) [DocEntry] FROM [dbo].[OINV] ORDER BY [DocEntry] DESC;", connection);
			((DbCommand)val).CommandType = CommandType.Text;
			((DbCommand)val).CommandTimeout = CommandTimeoutSeconds();
			SqlCommand command = val;
			try
			{
				await ((DbCommand)(object)command).ExecuteScalarAsync(cancellationToken);
			}
			finally
			{
				if (command != null)
				{
					await ((DbCommand)(object)command).DisposeAsync();
				}
			}
		}
		finally
		{
			if (connection != null)
			{
				await ((DbConnection)(object)connection).DisposeAsync();
			}
		}
	}

	public async Task<IReadOnlyList<SapSupplierCandidate>> FindSuppliersAsync(
		string name,
		string? vatId,
		string? taxNumber,
		string? iban,
		string? street,
		string? postalCode,
		string? city,
		CancellationToken cancellationToken = default)
	{
		await using var connection = NewReadOnlyConnection();
		await connection.OpenAsync(cancellationToken);
		await using var command = new SqlCommand(SupplierMatchingSql, connection)
		{
			CommandType = CommandType.Text,
			CommandTimeout = CommandTimeoutSeconds()
		};
		var candidates = new List<SapSupplierCandidate>();
		await using var reader = await command.ExecuteReaderAsync(cancellationToken);
		while (await reader.ReadAsync(cancellationToken))
		{
			var cardCode = ReadString(reader, "CardCode");
			var cardName = ReadString(reader, "CardName");
			var candidateVat = ReadString(reader, "VatId");
			var candidateTax = ReadString(reader, "TaxNumber");
			var candidateIban = ReadString(reader, "Iban");
			var candidateStreet = ReadString(reader, "Street");
			var candidatePostalCode = ReadString(reader, "PostalCode");
			var candidateCity = ReadString(reader, "City");
			var reasons = new List<string>();
			decimal score = 0m;
			AddExactScore(vatId, candidateVat, 100m, "USt-ID", reasons, ref score);
			AddExactScore(taxNumber, candidateTax, 90m, "Steuernummer", reasons, ref score);
			AddExactScore(iban, candidateIban, 85m, "IBAN", reasons, ref score);
			AddExactScore(name, cardName, 45m, "Name", reasons, ref score);
			AddExactScore(postalCode, candidatePostalCode, 20m, "PLZ", reasons, ref score);
			AddExactScore(city, candidateCity, 15m, "Ort", reasons, ref score);
			AddExactScore(street, candidateStreet, 10m, "Straße", reasons, ref score);
			if (!string.IsNullOrWhiteSpace(cardCode) && score > 0m)
				candidates.Add(new SapSupplierCandidate(
					cardCode, cardName, candidateVat, candidateTax, candidateIban,
					candidateStreet, candidatePostalCode, candidateCity, score, reasons));
		}
		return candidates
			.OrderByDescending(candidate => candidate.MatchScore)
			.ThenBy(candidate => candidate.CardCode, StringComparer.OrdinalIgnoreCase)
			.ToArray();
	}

	public async Task<IReadOnlyList<SapCodingCandidate>> GetSupplierCodingHistoryAsync(string cardCode, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(cardCode);
		await using var connection = NewReadOnlyConnection();
		await connection.OpenAsync(cancellationToken);
		await using var command = new SqlCommand(SupplierCodingHistorySql, connection)
		{
			CommandType = CommandType.Text,
			CommandTimeout = CommandTimeoutSeconds()
		};
		command.Parameters.Add("@cardCode", SqlDbType.NVarChar, 15).Value = cardCode.Trim();
		var result = new List<SapCodingCandidate>();
		await using var reader = await command.ExecuteReaderAsync(cancellationToken);
		while (await reader.ReadAsync(cancellationToken))
		{
			var usageCount = Convert.ToInt32(reader.GetValue(reader.GetOrdinal("UsageCount")), CultureInfo.InvariantCulture);
			result.Add(new SapCodingCandidate(
				ReadString(reader, "Account"),
				ReadString(reader, "TaxCode"),
				ReadString(reader, "Description"),
				usageCount,
				Math.Min(0.98m, 0.60m + usageCount * 0.05m),
				"SAP-Historie"));
		}
		return result;
	}

	public async Task<SapAccountValidation> ValidateAccountAsync(string account, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(account))
			return new SapAccountValidation(account, false, false, false, null, "Sachkonto fehlt.");

		var normalizedAccount = account.Trim();
		await using var connection = NewReadOnlyConnection();
		await connection.OpenAsync(cancellationToken);
		await using var command = new SqlCommand(AccountValidationSql, connection)
		{
			CommandType = CommandType.Text,
			CommandTimeout = CommandTimeoutSeconds()
		};
		command.Parameters.Add("@account", SqlDbType.NVarChar, 15).Value = normalizedAccount;
		await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
		if (!await reader.ReadAsync(cancellationToken))
			return new SapAccountValidation(normalizedAccount, false, false, false, null, "Sachkonto existiert nicht.");

		var dimensions = configuration.GetSection("Sap:AccountsRequiringDimensions").Get<string[]>() ?? [];
		var requiresDimensions = dimensions.Contains(normalizedAccount, StringComparer.OrdinalIgnoreCase);
		return new SapAccountValidation(
			ReadString(reader, "Account"),
			true,
			ReadBoolean(reader, "ActiveAccount"),
			requiresDimensions,
			ReadString(reader, "Name"),
			null);
	}

	public async Task<SapDocumentSnapshot?> FindPurchaseInvoiceDuplicateAsync(string cardCode, string invoiceNumber, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(cardCode);
		ArgumentException.ThrowIfNullOrWhiteSpace(invoiceNumber);
		await using var connection = NewReadOnlyConnection();
		await connection.OpenAsync(cancellationToken);
		await using var command = new SqlCommand(PurchaseInvoiceDuplicateSql, connection)
		{
			CommandType = CommandType.Text,
			CommandTimeout = CommandTimeoutSeconds()
		};
		command.Parameters.Add("@cardCode", SqlDbType.NVarChar, 15).Value = cardCode.Trim();
		command.Parameters.Add("@invoiceNumber", SqlDbType.NVarChar, 100).Value = invoiceNumber.Trim();
		await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
		return await reader.ReadAsync(cancellationToken)
			? ReadSnapshot(reader, SapDocumentKind.PurchaseInvoice)
			: null;
	}

	public async Task<SapAccountingDocument?> GetAccountingDocumentAsync(SapDocumentKind kind, int docEntry, CancellationToken cancellationToken = default(CancellationToken))
	{
		if (docEntry <= 0)
		{
			throw new ArgumentOutOfRangeException("docEntry");
		}
		SqlConnection connection = NewReadOnlyConnection();
		SapAccountingDocument result;
		try
		{
			await ((DbConnection)(object)connection).OpenAsync(cancellationToken);
			(SapDocumentSnapshot Snapshot, int? TransId, string? PartnerAccountNumber, string? PartnerVatId, string? PartnerStreet, string? PartnerZip, string? PartnerCity, string? CompanyName, string? CompanyVatId, string? CompanyStreet, string? CompanyZip, string? CompanyCity)? header = await ReadAccountingHeaderAsync(connection, kind, docEntry, cancellationToken);
			if (!header.HasValue)
			{
				result = null;
			}
			else
			{
				IReadOnlyList<SapDocumentLine> lines = await ReadDocumentLinesAsync(connection, kind, docEntry, header.Value.TransId, cancellationToken);
				IReadOnlyList<SapTaxBreakdown> taxes = await ReadTaxBreakdownAsync(connection, kind, docEntry, cancellationToken);
				if (taxes.Count == 0) taxes = BuildTaxBreakdownFromLines(lines);
				lines = ApplyTaxMetadata(lines, taxes);
				IReadOnlyList<SapJournalLine> journal = await ReadJournalLinesAsync(connection, header.Value.TransId, cancellationToken);
				IReadOnlyList<DatevBookingMapping> mappings = await ReadDatevMappingsAsync(connection, taxes, header.Value.Snapshot.DocumentDate, datevMappingStore, cancellationToken);
				string sourceHash = AccountingSourceHash.Create(header.Value.Snapshot, lines, taxes, journal, mappings);
				result = new SapAccountingDocument(header.Value.Snapshot, header.Value.TransId, header.Value.PartnerAccountNumber, header.Value.PartnerVatId, header.Value.PartnerStreet, header.Value.PartnerZip, header.Value.PartnerCity, header.Value.Rest.Item1, header.Value.Rest.Item2, header.Value.Rest.Item3, header.Value.Rest.Item4, header.Value.Rest.Item5, lines, taxes, journal, mappings, sourceHash);
			}
		}
		finally
		{
			if (connection != null)
			{
				await ((DbConnection)(object)connection).DisposeAsync();
			}
		}
		return result;
	}

	internal static IReadOnlyList<SapTaxBreakdown> BuildTaxBreakdownFromLines(IReadOnlyList<SapDocumentLine> lines)
	{
		ArgumentNullException.ThrowIfNull(lines);
		return lines
			.Where(line => !string.IsNullOrWhiteSpace(line.TaxCode))
			.GroupBy(line => (
				TaxCode: line.TaxCode.Trim().ToUpperInvariant(),
				TaxRate: line.TaxRate,
				Currency: line.Currency?.Trim().ToUpperInvariant() ?? string.Empty))
			.Select(group => new SapTaxBreakdown(
				group.Min(line => line.LineNum),
				group.Key.TaxCode,
				group.Key.TaxRate,
				decimal.Round(group.Sum(line => line.NetAmount), 2, MidpointRounding.AwayFromZero),
				decimal.Round(group.Sum(line => line.TaxAmount), 2, MidpointRounding.AwayFromZero),
				group.Key.Currency))
			.OrderBy(tax => tax.LineNum)
			.ToArray();
	}

	internal static IReadOnlyList<SapDocumentLine> ApplyTaxMetadata(IReadOnlyList<SapDocumentLine> lines, IReadOnlyList<SapTaxBreakdown> taxes)
	{
		ArgumentNullException.ThrowIfNull(lines);
		ArgumentNullException.ThrowIfNull(taxes);
		var reverseCodes = taxes
			.Where(tax => tax.IsReverseCharge && !string.IsNullOrWhiteSpace(tax.TaxCode))
			.Select(tax => tax.TaxCode.Trim())
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		return lines
			.Select(line => line with { IsReverseCharge = line.IsReverseCharge || reverseCodes.Contains(line.TaxCode?.Trim() ?? string.Empty) })
			.ToArray();
	}

	private async Task<(SapDocumentSnapshot Snapshot, int? TransId, string? PartnerAccountNumber, string? PartnerVatId, string? PartnerStreet, string? PartnerZip, string? PartnerCity, string? CompanyName, string? CompanyVatId, string? CompanyStreet, string? CompanyZip, string? CompanyCity)?> ReadAccountingHeaderAsync(SqlConnection connection, SapDocumentKind kind, int docEntry, CancellationToken cancellationToken)
	{
		if (!AllowedDocumentTables.TryGetValue(kind, out string table))
		{
			throw new ArgumentOutOfRangeException("kind");
		}
		string sql = "SELECT TOP (1) d.[DocEntry], d.[DocNum], d.[CardCode], d.[CardName], d.[NumAtCard],\n       d.[DocDate], CASE WHEN d.[DocCur] = oa.[MainCurncy] OR ISNULL(d.[DocTotalFC], 0) = 0 THEN d.[DocTotal] ELSE d.[DocTotalFC] END AS [DocTotal], d.[DocCur], d.[AtcEntry], d.[CreateDate], d.[Comments], d.[TransId],\n       d.[CardCode] AS [PartnerAccountNumber], bp.[LicTradNum] AS [PartnerVatId], ad.[Street] AS [PartnerStreet],\n       ad.[ZipCode] AS [PartnerZip], ad.[City] AS [PartnerCity],\n       (SELECT TOP (1) [CompnyName] FROM [dbo].[OADM]) AS [CompanyName],\n       (SELECT TOP (1) [TaxIdNum] FROM [dbo].[OADM]) AS [CompanyVatId],\n       (SELECT TOP (1) [Street] FROM [dbo].[ADM1]) AS [CompanyStreet],\n       (SELECT TOP (1) [ZipCode] FROM [dbo].[ADM1]) AS [CompanyZip],\n       (SELECT TOP (1) [City] FROM [dbo].[ADM1]) AS [CompanyCity]\nFROM [dbo].[" + table + "] AS d\nCROSS JOIN (SELECT TOP (1) [MainCurncy] FROM [dbo].[OADM]) AS oa\nLEFT JOIN [dbo].[OCRD] AS bp ON bp.[CardCode] = d.[CardCode]\nOUTER APPLY (SELECT TOP (1) a.[Street], a.[ZipCode], a.[City]\n             FROM [dbo].[CRD1] AS a\n             WHERE a.[CardCode] = d.[CardCode]\n             ORDER BY CASE WHEN a.[AdresType] = 'B' THEN 0 WHEN a.[AdresType] = 'S' THEN 1 ELSE 2 END, a.[LineNum]) AS ad\nWHERE d.[DocEntry] = @docEntry;";
		SqlCommand val = new SqlCommand(sql, connection);
		((DbCommand)val).CommandType = CommandType.Text;
		((DbCommand)val).CommandTimeout = CommandTimeoutSeconds();
		SqlCommand command = val;
		(SapDocumentSnapshot Snapshot, int? TransId, string? PartnerAccountNumber, string? PartnerVatId, string? PartnerStreet, string? PartnerZip, string? PartnerCity, string? CompanyName, string? CompanyVatId, string? CompanyStreet, string? CompanyZip, string? CompanyCity)? result;
		try
		{
			((DbParameter)(object)command.Parameters.Add("@docEntry", SqlDbType.Int)).Value = docEntry;
			SqlDataReader reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
			(SapDocumentSnapshot Snapshot, int? TransId, string? PartnerAccountNumber, string? PartnerVatId, string? PartnerStreet, string? PartnerZip, string? PartnerCity, string? CompanyName, string? CompanyVatId, string? CompanyStreet, string? CompanyZip, string? CompanyCity)? tuple;
			try
			{
				if (!(await ((DbDataReader)(object)reader).ReadAsync(cancellationToken)))
				{
					tuple = null;
				}
				else
				{
					SapDocumentSnapshot snapshot = ReadSnapshot(reader, kind);
					int? transId = ReadNullableInt(reader, "TransId");
					tuple = (snapshot with
					{
						TransId = transId
					}, ReadNullableInt(reader, "TransId"), ReadNullableString(reader, "PartnerAccountNumber"), ReadNullableString(reader, "PartnerVatId"), ReadNullableString(reader, "PartnerStreet"), ReadNullableString(reader, "PartnerZip"), ReadNullableString(reader, "PartnerCity"), ReadNullableString(reader, "CompanyName"), ReadNullableString(reader, "CompanyVatId"), ReadNullableString(reader, "CompanyStreet"), ReadNullableString(reader, "CompanyZip"), ReadNullableString(reader, "CompanyCity"));
				}
			}
			finally
			{
				if (reader != null)
				{
					await ((DbDataReader)(object)reader).DisposeAsync();
				}
			}
			result = tuple;
		}
		finally
		{
			if (command != null)
			{
				await ((DbCommand)(object)command).DisposeAsync();
			}
		}
		return result;
	}

	private async Task<IReadOnlyList<SapDocumentLine>> ReadDocumentLinesAsync(SqlConnection connection, SapDocumentKind kind, int docEntry, int? transId, CancellationToken cancellationToken)
	{
		if (!LineTables.TryGetValue(kind, out string table))
		{
			throw new ArgumentOutOfRangeException("kind");
		}
		if (!AllowedDocumentTables.TryGetValue(kind, out string headerTable)) throw new ArgumentOutOfRangeException(nameof(kind));
		string sql = "SELECT l.[LineNum], ISNULL(l.[Dscription], '') AS [Description], ISNULL(l.[Quantity], 1) AS [Quantity],\n"
			+ "       CASE WHEN d.[DocCur] = a.[MainCurncy] OR ISNULL(d.[DocTotalFC], 0) = 0 THEN ISNULL(l.[LineTotal], 0) ELSE ISNULL(l.[TotalFrgn], 0) END AS [NetAmount],\n"
			+ "       CASE WHEN d.[DocCur] = a.[MainCurncy] OR ISNULL(d.[DocTotalFC], 0) = 0 THEN ISNULL(l.[VatSum], 0) ELSE ISNULL(l.[VatSumFrgn], 0) END AS [TaxAmount],\n"
			+ "       ISNULL(l.[VatGroup], '') AS [TaxCode], ISNULL(l.[VatPrcnt], 0) AS [TaxRate],\n"
			+ "       ISNULL(l.[AcctCode], '') AS [Account], ISNULL(d.[DocCur], '') AS [Currency]\n"
			+ "FROM [dbo].[" + table + "] AS l\n"
			+ "INNER JOIN [dbo].[" + headerTable + "] AS d ON d.[DocEntry] = l.[DocEntry]\n"
			+ "CROSS JOIN (SELECT TOP (1) [MainCurncy] FROM [dbo].[OADM]) AS a\n"
			+ "WHERE l.[DocEntry] = @docEntry ORDER BY l.[LineNum];";
		SqlCommand val = new SqlCommand(sql, connection);
		((DbCommand)val).CommandType = CommandType.Text;
		((DbCommand)val).CommandTimeout = CommandTimeoutSeconds();
		SqlCommand command = val;
		IReadOnlyList<SapDocumentLine> result2;
		try
		{
			((DbParameter)(object)command.Parameters.Add("@docEntry", SqlDbType.Int)).Value = docEntry;
			List<SapDocumentLine> result = new List<SapDocumentLine>();
			SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
			IReadOnlyList<SapDocumentLine> readOnlyList;
			try
			{
				while (await ((DbDataReader)(object)reader).ReadAsync(cancellationToken))
				{
					result.Add(new SapDocumentLine(((DbDataReader)(object)reader).GetInt32(((DbDataReader)(object)reader).GetOrdinal("LineNum")), ReadString(reader, "Description"), ReadDecimal(reader, "Quantity"), ReadDecimal(reader, "NetAmount"), ReadDecimal(reader, "TaxAmount"), ReadString(reader, "TaxCode"), ReadDecimal(reader, "TaxRate"), ReadString(reader, "Account"), ReadString(reader, "Currency")));
				}
				readOnlyList = result;
			}
			finally
			{
				if (reader != null)
				{
					await ((DbDataReader)(object)reader).DisposeAsync();
				}
			}
			result2 = readOnlyList;
		}
		finally
		{
			if (command != null)
			{
				await ((DbCommand)(object)command).DisposeAsync();
			}
		}
		return result2;
	}

	private async Task<IReadOnlyList<SapTaxBreakdown>> ReadTaxBreakdownAsync(SqlConnection connection, SapDocumentKind kind, int docEntry, CancellationToken cancellationToken)
	{
		string sql = TaxBreakdownSql(kind);
		SqlCommand val = new SqlCommand(sql, connection);
		((DbCommand)val).CommandType = CommandType.Text;
		((DbCommand)val).CommandTimeout = CommandTimeoutSeconds();
		SqlCommand command = val;
		IReadOnlyList<SapTaxBreakdown> result2;
		try
		{
			((DbParameter)(object)command.Parameters.Add("@docEntry", SqlDbType.Int)).Value = docEntry;
			List<SapTaxBreakdown> result = new List<SapTaxBreakdown>();
			SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
			IReadOnlyList<SapTaxBreakdown> readOnlyList;
			try
			{
				while (await ((DbDataReader)(object)reader).ReadAsync(cancellationToken))
				{
					result.Add(new SapTaxBreakdown(
						((DbDataReader)(object)reader).GetInt32(((DbDataReader)(object)reader).GetOrdinal("LineNum")),
						ReadString(reader, "TaxCode"),
						ReadDecimal(reader, "Rate"),
						ReadDecimal(reader, "NetAmount"),
						ReadDecimal(reader, "TaxAmount"),
						ReadString(reader, "Currency"),
						ReadString(reader, "TaxAccount"),
						ReadDecimal(reader, "ReverseChargePercent"),
						ReadDecimal(reader, "ReverseChargeTaxAmount"),
						ReadBoolean(reader, "IncludedInGrossRevenue")));
				}
				readOnlyList = result;
			}
			finally
			{
				if (reader != null)
				{
					await ((DbDataReader)(object)reader).DisposeAsync();
				}
			}
			result2 = readOnlyList;
		}
		finally
		{
			if (command != null)
			{
				await ((DbCommand)(object)command).DisposeAsync();
			}
		}
		return result2;
	}

	private async Task<IReadOnlyList<SapJournalLine>> ReadJournalLinesAsync(SqlConnection connection, int? transId, CancellationToken cancellationToken)
	{
		if (!transId.HasValue)
		{
			return Array.Empty<SapJournalLine>();
		}
		SqlCommand val = new SqlCommand("SELECT [Line_ID], ISNULL([Account], '') AS [Account], ISNULL([ContraAct], '') AS [CounterAccount],\n       ISNULL([Debit], 0) AS [Debit], ISNULL([Credit], 0) AS [Credit], ISNULL([FCCurrency], '') AS [Currency],\n       ISNULL([ProfitCode], '') AS [CostCenter], ISNULL([Project], '') AS [Project]\nFROM [dbo].[JDT1] WHERE [TransId] = @transId ORDER BY [Line_ID];", connection);
		((DbCommand)val).CommandType = CommandType.Text;
		((DbCommand)val).CommandTimeout = CommandTimeoutSeconds();
		SqlCommand command = val;
		IReadOnlyList<SapJournalLine> result2;
		try
		{
			((DbParameter)(object)command.Parameters.Add("@transId", SqlDbType.Int)).Value = transId.Value;
			List<SapJournalLine> result = new List<SapJournalLine>();
			SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
			IReadOnlyList<SapJournalLine> readOnlyList;
			try
			{
				while (await ((DbDataReader)(object)reader).ReadAsync(cancellationToken))
				{
					decimal debit = ReadDecimal(reader, "Debit");
					decimal credit = ReadDecimal(reader, "Credit");
					result.Add(new SapJournalLine(((DbDataReader)(object)reader).GetInt32(((DbDataReader)(object)reader).GetOrdinal("Line_ID")), ReadString(reader, "Account"), ReadString(reader, "CounterAccount"), (debit > 0m) ? "S" : ((credit > 0m) ? "H" : string.Empty), debit, credit, ReadString(reader, "Currency"), ReadNullableString(reader, "CostCenter"), ReadNullableString(reader, "Project")));
				}
				readOnlyList = result;
			}
			finally
			{
				if (reader != null)
				{
					await ((DbDataReader)(object)reader).DisposeAsync();
				}
			}
			result2 = readOnlyList;
		}
		finally
		{
			if (command != null)
			{
				await ((DbCommand)(object)command).DisposeAsync();
			}
		}
		return result2;
	}

	private async Task<IReadOnlyList<DatevBookingMapping>> ReadDatevMappingsAsync(SqlConnection connection, IReadOnlyList<SapTaxBreakdown> taxes, DateOnly documentDate, DatevMappingStore? mappingStore, CancellationToken cancellationToken)
	{
		string[] taxCodes = (from t in taxes
			select t.TaxCode into code
			where !string.IsNullOrWhiteSpace(code)
			select code).Distinct<string>(StringComparer.OrdinalIgnoreCase).ToArray();
		if (taxCodes.Length == 0)
		{
			return Array.Empty<DatevBookingMapping>();
		}
		List<DatevBookingMapping> result = new List<DatevBookingMapping>();
		string[] array = taxCodes;
		foreach (string taxCode in array)
		{
			SqlCommand val = new SqlCommand(Avt1MappingSql, connection);
			((DbCommand)val).CommandType = CommandType.Text;
			((DbCommand)val).CommandTimeout = CommandTimeoutSeconds();
			SqlCommand command = val;
			try
			{
				((DbParameter)(object)command.Parameters.Add("@code", SqlDbType.NVarChar, 20)).Value = taxCode;
				((DbParameter)(object)command.Parameters.Add("@documentDate", SqlDbType.Date)).Value = documentDate.ToDateTime(TimeOnly.MinValue);
				SqlDataReader reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
				try
				{
					if (await ((DbDataReader)(object)reader).ReadAsync(cancellationToken))
					{
						string datevCode = ReadNullableString(reader, "DatevCode");
						DatevBookingMapping? approved = mappingStore is null
							? null
							: await mappingStore.GetActiveAsync(taxCode, documentDate, cancellationToken);
						var resolved = ResolveSapAvt1Mapping(taxCode, datevCode, documentDate, approved);
						if (resolved is not null)
						{
							result.Add(resolved);
							continue;
						}
						goto end_IL_022f;
					}
					end_IL_022f:;
				}
				finally
				{
					if (reader != null)
					{
						await ((DbDataReader)(object)reader).DisposeAsync();
					}
				}
			}
			finally
			{
				if (command != null)
				{
					await ((DbCommand)(object)command).DisposeAsync();
				}
			}
		}
		return result;
	}

	internal static DatevBookingMapping? ResolveSapAvt1Mapping(string taxCode, string? datevCode, DateOnly documentDate, DatevBookingMapping? approved)
	{
		if (string.IsNullOrWhiteSpace(taxCode)) return null;
		// A locally approved mapping is the explicit escape hatch for historic SAP
		// AVT1 rows whose DATEV field is empty. A populated SAP value remains the
		// primary source and may never be silently overridden.
		if (string.IsNullOrWhiteSpace(datevCode)) return approved;
		if (!datevCode.All(char.IsDigit)) return null;
		if (approved is not null)
		{
			// Eine bestehende lokale Freigabe darf eine abweichende SAP-AVT1-Zuordnung nicht still überstimmen.
			return string.Equals(approved.DatevBuCode, datevCode, StringComparison.OrdinalIgnoreCase) ? approved : null;
		}
		const string source = "SAP AVT1";
		return new DatevBookingMapping(
			taxCode.Trim(),
			datevCode.Trim(),
			string.Empty,
			documentDate,
			null,
			source,
			AccountingSourceHash.CreateMappingHash(taxCode.Trim(), datevCode.Trim(), string.Empty, documentDate, null, source));
	}

	internal static string DocumentSql(SapDocumentKind kind)
	{
		if (!AllowedDocumentTables.TryGetValue(kind, out string table))
		{
			throw new ArgumentOutOfRangeException("kind");
		}
		return "SELECT TOP (1)\n    d.[DocEntry], d.[DocNum], d.[CardCode], d.[CardName], d.[NumAtCard],\n"
			+ "    d.[DocDate], CASE WHEN d.[DocCur] = a.[MainCurncy] OR ISNULL(d.[DocTotalFC], 0) = 0 THEN d.[DocTotal] ELSE d.[DocTotalFC] END AS [DocTotal],\n"
			+ "    d.[DocCur], d.[AtcEntry], d.[CreateDate], d.[Comments]\nFROM [dbo].[" + table + "] AS d\n"
			+ "CROSS JOIN (SELECT TOP (1) [MainCurncy] FROM [dbo].[OADM]) AS a\nWHERE d.[DocEntry] = @docEntry;";
	}

	internal static string ListDocumentsSql(SapDocumentKind kind)
	{
		if (!AllowedDocumentTables.TryGetValue(kind, out string table))
		{
			throw new ArgumentOutOfRangeException("kind");
		}
		return "SELECT d.[DocEntry], d.[DocNum], d.[CardCode], d.[CardName], d.[NumAtCard],\n"
			+ "       d.[DocDate], CASE WHEN d.[DocCur] = a.[MainCurncy] OR ISNULL(d.[DocTotalFC], 0) = 0 THEN d.[DocTotal] ELSE d.[DocTotalFC] END AS [DocTotal],\n"
			+ "       d.[DocCur], d.[AtcEntry], d.[CreateDate], d.[Comments]\nFROM [dbo].[" + table + "] AS d\n"
			+ "CROSS JOIN (SELECT TOP (1) [MainCurncy] FROM [dbo].[OADM]) AS a\n"
			+ "WHERE d.[CreateDate] >= @fromEntryDate\n  AND d.[CreateDate] < DATEADD(day, 1, @toEntryDate)\nORDER BY d.[CreateDate], d.[DocNum];";
	}

	internal static string InvoiceNumber(SapDocumentKind kind, int docNum, string? supplierReference)
	{
		string text;
		if ((kind != SapDocumentKind.Invoice && kind != SapDocumentKind.CreditNote) || 1 == 0)
		{
			text = supplierReference;
			if (text == null)
			{
				return string.Empty;
			}
		}
		else
		{
			text = docNum.ToString(CultureInfo.InvariantCulture);
		}
		return text;
	}

	private static SapDocumentSnapshot ReadSnapshot(SqlDataReader reader, SapDocumentKind kind)
	{
		int sapDocNum = ((DbDataReader)(object)reader).GetInt32(((DbDataReader)(object)reader).GetOrdinal("DocNum"));
		int supplierReferenceOrdinal = ((DbDataReader)(object)reader).GetOrdinal("NumAtCard");
		string supplierReference = (((DbDataReader)(object)reader).IsDBNull(supplierReferenceOrdinal) ? string.Empty : ((DbDataReader)(object)reader).GetString(supplierReferenceOrdinal));
		int attachmentOrdinal = ((DbDataReader)(object)reader).GetOrdinal("AtcEntry");
		int entryDateOrdinal = ((DbDataReader)(object)reader).GetOrdinal("CreateDate");
		return new SapDocumentSnapshot(kind, ((DbDataReader)(object)reader).GetInt32(((DbDataReader)(object)reader).GetOrdinal("DocEntry")), sapDocNum, ReadString(reader, "CardCode"), ReadString(reader, "CardName"), InvoiceNumber(kind, sapDocNum, supplierReference), DateOnly.FromDateTime(((DbDataReader)(object)reader).GetDateTime(((DbDataReader)(object)reader).GetOrdinal("DocDate"))), ((DbDataReader)(object)reader).GetDecimal(((DbDataReader)(object)reader).GetOrdinal("DocTotal")), ReadString(reader, "DocCur"), ((DbDataReader)(object)reader).IsDBNull(attachmentOrdinal) ? ((int?)null) : new int?(((DbDataReader)(object)reader).GetInt32(attachmentOrdinal)), ((DbDataReader)(object)reader).IsDBNull(entryDateOrdinal) ? ((DateOnly?)null) : new DateOnly?(DateOnly.FromDateTime(((DbDataReader)(object)reader).GetDateTime(entryDateOrdinal))), Comments: ReadNullableString(reader, "Comments"));
	}

	internal static string BuildReadOnlyConnectionString(string configured)
	{
		//IL_0001: Unknown result type (might be due to invalid IL or missing references)
		//IL_0006: Unknown result type (might be due to invalid IL or missing references)
		//IL_000d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0019: Expected O, but got Unknown
		SqlConnectionStringBuilder connectionString = new SqlConnectionStringBuilder(configured)
		{
			ApplicationIntent = (ApplicationIntent)1,
			ApplicationName = "NovaNein SQL read-only"
		};
		return ((DbConnectionStringBuilder)(object)connectionString).ConnectionString;
	}

	private SqlConnection NewReadOnlyConnection()
	{
		//IL_002a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0030: Expected O, but got Unknown
		string configured = configuration["Sap:Sql:ConnectionString"];
		if (string.IsNullOrWhiteSpace(configured))
		{
			throw new InvalidOperationException("Sap:Sql:ConnectionString fehlt für Sap:ReadMode=sql-read-only.");
		}
		return new SqlConnection(BuildReadOnlyConnectionString(configured));
	}

	private int CommandTimeoutSeconds()
	{
		int configured = configuration.GetValue("Sap:Sql:CommandTimeoutSeconds", 20);
		return Math.Clamp(configured, 1, 120);
	}

	private static void AddExactScore(
		string? expected,
		string? actual,
		decimal points,
		string reason,
		ICollection<string> reasons,
		ref decimal score)
	{
		if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(actual)) return;
		if (!string.Equals(NormalizeMatchValue(expected), NormalizeMatchValue(actual), StringComparison.Ordinal)) return;
		score += points;
		reasons.Add(reason);
	}

	private static string NormalizeMatchValue(string value)
		=> new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

	private static string ReadString(SqlDataReader reader, string name)
	{
		int ordinal = ((DbDataReader)(object)reader).GetOrdinal(name);
		if (!((DbDataReader)(object)reader).IsDBNull(ordinal))
		{
			return ((DbDataReader)(object)reader).GetString(ordinal);
		}
		return string.Empty;
	}

	private static string? ReadNullableString(SqlDataReader reader, string name)
	{
		int ordinal = ((DbDataReader)(object)reader).GetOrdinal(name);
		if (!((DbDataReader)(object)reader).IsDBNull(ordinal))
		{
			return ((DbDataReader)(object)reader).GetValue(ordinal)?.ToString();
		}
		return null;
	}

	private static int? ReadNullableInt(SqlDataReader reader, string name)
	{
		int ordinal = ((DbDataReader)(object)reader).GetOrdinal(name);
		if (!((DbDataReader)(object)reader).IsDBNull(ordinal))
		{
			return Convert.ToInt32(((DbDataReader)(object)reader).GetValue(ordinal), CultureInfo.InvariantCulture);
		}
		return null;
	}

	private static decimal ReadDecimal(SqlDataReader reader, string name)
	{
		int ordinal = ((DbDataReader)(object)reader).GetOrdinal(name);
		if (!((DbDataReader)(object)reader).IsDBNull(ordinal))
		{
			return Convert.ToDecimal(((DbDataReader)(object)reader).GetValue(ordinal), CultureInfo.InvariantCulture);
		}
		return 0m;
	}

	private static bool ReadBoolean(SqlDataReader reader, string name)
	{
		int ordinal = ((DbDataReader)(object)reader).GetOrdinal(name);
		if (!((DbDataReader)(object)reader).IsDBNull(ordinal))
		{
			return Convert.ToBoolean(((DbDataReader)(object)reader).GetValue(ordinal), CultureInfo.InvariantCulture);
		}
		return false;
	}
}
