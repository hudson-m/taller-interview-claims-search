using Microsoft.Data.Sqlite;

namespace Claims;

public class ClaimsRepository
{
    private readonly SqliteConnection _db;

    public ClaimsRepository(SqliteConnection db) => _db = db;

    // MYT-3108: only these fixed SQL fragments can reach ORDER BY; user input is used as a lookup key, never as SQL text.
    private static readonly IReadOnlyDictionary<string, string> SortColumns = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["date"] = "submitted_at",
        ["amount"] = "amount",
        ["status"] = "status",
    };

    private static readonly IReadOnlyDictionary<string, string> SortDirections = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["asc"] = "ASC",
        ["desc"] = "DESC",
    };

    // Search the claims of one member. Called by the claims page of the member website and by the support tool.
    public IReadOnlyList<Claim> Search(int memberId, string? text, string? sortBy = "date", string? sortDir = "desc", int page = 0)
    {
        // MYT-3108: sort/direction come from allow-lists; the search text only ever travels as a parameter.
        var column = SortColumns.TryGetValue(sortBy ?? "", out var col)
            ? col : throw new ArgumentException("sortBy must be 'date', 'amount' or 'status'.", nameof(sortBy));
        var direction = SortDirections.TryGetValue(sortDir ?? "", out var dir)
            ? dir : throw new ArgumentException("sortDir must be 'asc' or 'desc'.", nameof(sortDir));
        if (page < 0) throw new ArgumentOutOfRangeException(nameof(page), "page must be 0 or greater.");

        using var cmd = _db.CreateCommand();
        cmd.CommandText =
            "SELECT id, provider, description, amount, status, submitted_at FROM claims " +
            "WHERE member_id = @member AND (description LIKE @pattern ESCAPE '\\' OR provider LIKE @pattern ESCAPE '\\') " +
            $"ORDER BY {column} {direction}, id ASC LIMIT 20 OFFSET @offset";
        cmd.Parameters.AddWithValue("@member", memberId);
        cmd.Parameters.AddWithValue("@pattern", ContainsPattern(text));
        cmd.Parameters.AddWithValue("@offset", page * 20);

        var results = new List<Claim>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add(new Claim(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                                  reader.GetDouble(3), reader.GetString(4), reader.GetString(5)));
        return results;
    }

    // "Contains" pattern where %, _ and \ typed by the user are literal characters, not wildcards.
    private static string ContainsPattern(string? text)
    {
        var escaped = (text ?? "").Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
        return "%" + escaped + "%";
    }
}
