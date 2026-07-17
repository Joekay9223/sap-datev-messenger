using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace NovaNein.Server;

public sealed class DatevMappingStore
{
	private readonly string _connectionString;

	public DatevMappingStore(IConfiguration configuration)
	{
		_connectionString = "Data Source=" + (configuration["Storage:DatabasePath"] ?? "data/novanein.db");
	}

	public async Task InitializeAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		string source = new SqliteConnectionStringBuilder(_connectionString).DataSource;
		string directory = Path.GetDirectoryName(source);
		if (!string.IsNullOrWhiteSpace(directory))
		{
			Directory.CreateDirectory(directory);
		}
		SqliteConnection connection = new SqliteConnection(_connectionString);
		try
		{
			await ((DbConnection)(object)connection).OpenAsync(cancellationToken);
			SqliteCommand command = connection.CreateCommand();
			((DbCommand)(object)command).CommandText = "CREATE TABLE IF NOT EXISTS datev_booking_mappings (\n  id TEXT PRIMARY KEY,\n  sap_tax_code TEXT NOT NULL,\n  datev_bu_code TEXT NOT NULL,\n  datev_account TEXT NOT NULL,\n  valid_from TEXT NOT NULL,\n  valid_to TEXT NULL,\n  approved_by TEXT NOT NULL,\n  mapping_hash TEXT NOT NULL,\n  created_at TEXT NOT NULL\n);\nCREATE INDEX IF NOT EXISTS ix_datev_booking_mappings_lookup ON datev_booking_mappings(sap_tax_code, valid_from, valid_to);";
			await ((DbCommand)(object)command).ExecuteNonQueryAsync(cancellationToken);
		}
		finally
		{
			if (connection != null)
			{
				await ((DbConnection)(object)connection).DisposeAsync();
			}
		}
	}

	public async Task<IReadOnlyList<DatevBookingMapping>> ListAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		List<DatevBookingMapping> result = new List<DatevBookingMapping>();
		SqliteConnection connection = new SqliteConnection(_connectionString);
		IReadOnlyList<DatevBookingMapping> result2;
		try
		{
			await ((DbConnection)(object)connection).OpenAsync(cancellationToken);
			SqliteCommand command = connection.CreateCommand();
			((DbCommand)(object)command).CommandText = "SELECT sap_tax_code,datev_bu_code,datev_account,valid_from,valid_to,approved_by,mapping_hash FROM datev_booking_mappings ORDER BY sap_tax_code,valid_from DESC";
			SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
			IReadOnlyList<DatevBookingMapping> readOnlyList;
			try
			{
				while (await ((DbDataReader)(object)reader).ReadAsync(cancellationToken))
				{
					result.Add(Read(reader));
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
			if (connection != null)
			{
				await ((DbConnection)(object)connection).DisposeAsync();
			}
		}
		return result2;
	}

	public async Task<DatevBookingMapping?> GetActiveAsync(string sapTaxCode, DateOnly documentDate, CancellationToken cancellationToken = default(CancellationToken))
	{
		SqliteConnection connection = new SqliteConnection(_connectionString);
		DatevBookingMapping result;
		try
		{
			await ((DbConnection)(object)connection).OpenAsync(cancellationToken);
			SqliteCommand command = connection.CreateCommand();
			((DbCommand)(object)command).CommandText = "SELECT sap_tax_code,datev_bu_code,datev_account,valid_from,valid_to,approved_by,mapping_hash\nFROM datev_booking_mappings\nWHERE sap_tax_code=$code AND valid_from <= $date\n  AND (valid_to IS NULL OR valid_to >= $date)\nORDER BY valid_from DESC LIMIT 1";
			command.Parameters.AddWithValue("$code", (object)sapTaxCode.Trim());
			command.Parameters.AddWithValue("$date", (object)documentDate.ToString("yyyy-MM-dd"));
			SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
			DatevBookingMapping datevBookingMapping;
			try
			{
				datevBookingMapping = ((await ((DbDataReader)(object)reader).ReadAsync(cancellationToken)) ? Read(reader) : null);
			}
			finally
			{
				if (reader != null)
				{
					await ((DbDataReader)(object)reader).DisposeAsync();
				}
			}
			result = datevBookingMapping;
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

	public async Task<DatevBookingMapping> UpsertAsync(DatevBookingMapping mapping, string actor, CancellationToken cancellationToken = default(CancellationToken))
	{
		if (string.IsNullOrWhiteSpace(actor))
		{
			throw new ArgumentException("Eine freigebende Person ist erforderlich.", "actor");
		}
		if (string.IsNullOrWhiteSpace(mapping.SapTaxCode) || string.IsNullOrWhiteSpace(mapping.DatevBuCode) || string.IsNullOrWhiteSpace(mapping.ApprovedBy))
		{
			throw new ArgumentException("Steuerkennzeichen, BU-Schlüssel und Freigabe sind erforderlich.", "mapping");
		}
		if (!mapping.DatevBuCode.All(char.IsDigit) || (!string.IsNullOrWhiteSpace(mapping.DatevAccount) && !mapping.DatevAccount.All(char.IsDigit)))
		{
			throw new ArgumentException("DATEV-Konto und BU-Schlüssel dürfen nur Ziffern enthalten.", "mapping");
		}
		DatevBookingMapping approved = mapping with
		{
			ApprovedBy = actor.Trim(),
			MappingHash = AccountingSourceHash.CreateMappingHash(mapping.SapTaxCode, mapping.DatevBuCode, mapping.DatevAccount, mapping.ValidFrom, mapping.ValidTo, actor)
		};
		SqliteConnection connection = new SqliteConnection(_connectionString);
		DatevBookingMapping result;
		try
		{
			await ((DbConnection)(object)connection).OpenAsync(cancellationToken);
			SqliteCommand command = connection.CreateCommand();
			((DbCommand)(object)command).CommandText = "INSERT INTO datev_booking_mappings(id,sap_tax_code,datev_bu_code,datev_account,valid_from,valid_to,approved_by,mapping_hash,created_at) VALUES($id,$code,$bu,$account,$from,$to,$by,$hash,$at)";
			command.Parameters.AddWithValue("$id", (object)Guid.NewGuid().ToString());
			command.Parameters.AddWithValue("$code", (object)approved.SapTaxCode.Trim());
			command.Parameters.AddWithValue("$bu", (object)approved.DatevBuCode.Trim());
			command.Parameters.AddWithValue("$account", (object)(approved.DatevAccount?.Trim() ?? string.Empty));
			command.Parameters.AddWithValue("$from", (object)approved.ValidFrom.ToString("yyyy-MM-dd"));
			command.Parameters.AddWithValue("$to", ((object)approved.ValidTo?.ToString("yyyy-MM-dd")) ?? ((object)DBNull.Value));
			command.Parameters.AddWithValue("$by", (object)approved.ApprovedBy);
			command.Parameters.AddWithValue("$hash", (object)approved.MappingHash);
			command.Parameters.AddWithValue("$at", (object)DateTimeOffset.UtcNow.ToString("O"));
			await ((DbCommand)(object)command).ExecuteNonQueryAsync(cancellationToken);
			result = approved;
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

	private static DatevBookingMapping Read(SqliteDataReader reader)
	{
		return new DatevBookingMapping(((DbDataReader)(object)reader).GetString(0), ((DbDataReader)(object)reader).GetString(1), ((DbDataReader)(object)reader).GetString(2), DateOnly.Parse(((DbDataReader)(object)reader).GetString(3)), ((DbDataReader)(object)reader).IsDBNull(4) ? ((DateOnly?)null) : new DateOnly?(DateOnly.Parse(((DbDataReader)(object)reader).GetString(4))), ((DbDataReader)(object)reader).GetString(5), ((DbDataReader)(object)reader).GetString(6));
	}
}
